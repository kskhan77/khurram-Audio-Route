using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Buffers;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using KhurramAudioRoute.Core.Spatial;
using ManagedBass;
using ManagedBass.Mix;
using ManagedBass.Fx;
using ManagedBass.Wasapi;

namespace KhurramAudioRoute.Core
{
    /// <summary>
    /// The high-end audio engine powered by BASS.
    /// Handles professional EQ, multi-device synchronization, and 7.1/Atmos foundations.
    /// </summary>
    public static class BassEngine
    {
        private static readonly List<int> _activeDevices = new();

        static BassEngine()
        {
            // Initialize BASS with "no sound" device (0) to ensure the engine is 
            // available globally. BASSWASAPI requires BASS to be initialized.
            try
            {
                Bass.Init(0);
            }
            catch { }
        }

        /// <summary>
        /// Checks if the required BASS native DLLs are present in the application directory.
        /// </summary>
        public static bool CheckNativeDlls(out string missingFiles)
        {
            var required = new[] { "bass.dll", "bassmix.dll", "bass_fx.dll" };
            var missing = new List<string>();
            var appDir = AppDomain.CurrentDomain.BaseDirectory;

            foreach (var dll in required)
            {
                if (!File.Exists(Path.Combine(appDir, dll)))
                {
                    missing.Add(dll);
                }
            }

            missingFiles = string.Join(", ", missing);
            return missing.Count == 0;
        }

        /// <summary>
        /// Initializes the BASS engine for a specific device.
        /// </summary>
        public static bool InitializeDevice(int deviceIndex)
        {
            // Skip devices that have already failed init - retrying them every meter
            // tick produces hundreds of identical error lines per minute.
            if (_bassInitFailedDevices.Contains(deviceIndex)) return false;

            try
            {
                if (!CheckNativeDlls(out _)) return false;

                if (!Bass.Init(deviceIndex))
                {
                    var error = Bass.LastError;
                    if (error != Errors.Already)
                    {
                        Debug.WriteLine($"BASS: Failed to init device {deviceIndex}. Error: {error} (will not retry)");
                        _bassInitFailedDevices.Add(deviceIndex);
                        return false;
                    }
                }

                if (!_activeDevices.Contains(deviceIndex))
                    _activeDevices.Add(deviceIndex);

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BASS: Critical error during init: {ex.Message}");
                _bassInitFailedDevices.Add(deviceIndex);
                return false;
            }
        }

        /// <summary>
        /// Frees all BASS resources.
        /// </summary>
        public static void Free()
        {
            StopBridge();
            
            foreach (var pipeline in _spatialPipelines.Values)
                DisposePipeline(pipeline);
            _spatialPipelines.Clear();
            _spatialScratch.Clear();
            _captureFormats.Clear();

            foreach (var dev in _activeDevices)
            {
                Bass.CurrentDevice = dev;
                Bass.Free();
            }
            _activeDevices.Clear();
        }

        private static readonly Dictionary<string, int> _deviceStreams = new();
        private static readonly Dictionary<string, int[]> _deviceEqHandles = new();
        private static readonly Dictionary<string, int> _loopbackHandles = new();
        private static int _testToneStream;

        // Bridge state. Topology:
        //   WASAPI loopback (source) ──► push stream (source format)
        //                                     │
        //                                     ▼
        //                                master mixer (48k/2 + EQ + soft limiter)
        //                                     │
        //              ┌──────────────────────┼──────────────────────┐
        //              ▼                      ▼                      ▼
        //         split #1              split #2               split #N
        //              │                      │                      │
        //              ▼                      ▼                      ▼
        //         WASAPI play           WASAPI play            WASAPI play
        // Splits give each target an independent read position so they don't
        // compete for the same bytes the way MixerAddChannel(target, master) does.
        private static string? _bridgeSourceId;
        private static readonly HashSet<string> _bridgeTargetIds = new();
        private static int _bridgePushStream;     // Push stream in source's native format
        private static int _bridgeMasterMixer;    // 48k/2 mixer; EQ FX + soft limit live here
        private static readonly Dictionary<string, int> _bridgeTargetSplits = new();
        // Optional per-target conversion mixer used when the device WASAPI session
        // negotiates a format different from 48k/2. The split feeds this mixer,
        // which auto-resamples/channel-converts to the target's format, and the
        // WASAPI playback callback pulls from the mixer instead of the split.
        private static readonly Dictionary<string, int> _bridgeTargetConvert = new();
        private static readonly Dictionary<string, int> _bridgeDelayHandles = new();
        private static readonly Dictionary<string, WasapiProcedure> _bridgeTargetProcs = new();
        private static int _bridgeLimiterFx;
        private static WasapiProcedure? _bridgeSourceProc;
        /// <summary>Persisted Tools matrix channel order; snapshot when <see cref="StartBridge"/> begins.</summary>
        private static string _bridgeMatrixChannelOrder = MatrixBridgeChannelReorder.OrderAuto;

        /// <summary>True when bridge master EQ uses DX8 Param EQ (BASS_FX PeakEQ rejected this stream).</summary>
        private static bool _bridgeMasterUsesDxPeakEq;

        /// <summary>
        /// DSP-based EQ for the bridge master mixer. BASS FX (both PeakEQ and
        /// DX8 ParamEQ) silently no-op on decode mixers, so we run a 10-band
        /// biquad chain via <see cref="Bass.ChannelSetDSP"/> instead. Replaces
        /// the legacy FX path on the bridge only — non-bridge per-device EQ
        /// still uses BASS FX (those mixers are playback streams).
        /// </summary>
        private static MasterEqDsp? _bridgeEqDsp;

        // Diagnostic counters for the source loopback callback. If
        // _bridgeSourceCallbacks stays at 0 after the bridge has been running
        // for a few seconds, WASAPI loopback on the bus device is not
        // delivering data — the bridge is fanning out silence regardless of
        // what apps are doing. _bridgeSourceMaxAbsSample tracks the loudest
        // float magnitude observed in the captured buffer; a value persistently
        // near 0 means loopback is delivering empty buffers.
        private static long _bridgeSourceCallbacks;
        private static long _bridgeSourceFrames;
        private static float _bridgeSourceMaxAbsSample;

        // Diagnostic counters for Bass.StreamPutData on the push stream.
        // _bridgePushOk: number of successful StreamPutData calls.
        // _bridgePushFailures: number of calls that returned -1 or queued less
        //   than the requested length.
        // _bridgePushLastError: the most recent Bass.LastError after a failed
        //   StreamPutData; lets us tell BUFLOST from HANDLE from MEM, etc.
        // _bridgePushBytesAttempted / _bridgePushBytesQueued: cumulative counts.
        private static long _bridgePushOk;
        private static long _bridgePushFailures;
        private static long _bridgePushBytesAttempted;
        private static long _bridgePushBytesQueued;
        private static int _bridgePushLastError;

        // Circuit breakers: UpdateEqualizer is called at the meter-tick interval
        // (~5/sec). Without these, a missing basswasapi.dll or a device that won't
        // init floods the debug console with the same error every tick.
        private static bool _bassWasapiAvailable = true;
        private static readonly HashSet<int> _bassInitFailedDevices = new();
        private static readonly HashSet<string> _wasapiInitFailedDevices = new();

        // Per-device spatial pipeline. Set by the UI via SetSpatialPreset; read on
        // the BASS WASAPI capture thread inside the loopback callback. ConcurrentDictionary
        // gives lock-free reads on the hot path; the scratch buffer is owned by the audio
        // thread and resized on the first callback after a preset change.
        private static readonly ConcurrentDictionary<string, SpatialPipeline> _spatialPipelines = new();
        private static readonly ConcurrentDictionary<string, float[]> _spatialScratch = new();

        // Actual capture format negotiated by BassWasapi.Init for each device. Windows
        // shared-mode picks the device's mix format (often 48 kHz stereo, but 44.1 kHz
        // and 5.1/7.1 are valid). Read on the audio thread; written once after init.
        private static readonly ConcurrentDictionary<string, (int Frequency, int Channels)> _captureFormats = new();

        public static bool StartBridge(string sourceId, IEnumerable<string> targetIds, float[] gains)
        {
            try
            {
                StopBridge();

                // Reset source-callback diagnostics so DumpBridgeDiagnostics shows
                // counts only for the live bridge run, not accumulation across runs.
                System.Threading.Interlocked.Exchange(ref _bridgeSourceCallbacks, 0);
                System.Threading.Interlocked.Exchange(ref _bridgeSourceFrames, 0);
                _bridgeSourceMaxAbsSample = 0f;
                System.Threading.Interlocked.Exchange(ref _bridgePushOk, 0);
                System.Threading.Interlocked.Exchange(ref _bridgePushFailures, 0);
                System.Threading.Interlocked.Exchange(ref _bridgePushBytesAttempted, 0);
                System.Threading.Interlocked.Exchange(ref _bridgePushBytesQueued, 0);
                _bridgePushLastError = 0;

                _bridgeMatrixChannelOrder = UserSettings.GetMatrixBridgeChannelOrder();

                int sourceIndex = GetDeviceIndex(sourceId);
                int sourceWasapiIndex = GetWasapiDeviceIndex(sourceId, true); // Loopback
                if (sourceIndex == -1 || sourceWasapiIndex == -1)
                {
                    Debug.WriteLine($"BASS BRIDGE: source device not found ({sourceId})");
                    return false;
                }

                // Clear the failed-device cache for the indices we are about to
                // try. Earlier in the process, legacy per-device init paths can
                // call InitializeDevice on these indices and fail (e.g. when a
                // WASAPI session was held by a previous app instance), which
                // poisons the cache for the rest of the process lifetime. The
                // bridge must always be allowed to retry on each engage.
                _bassInitFailedDevices.Remove(sourceIndex);
                _bassInitFailedDevices.Remove(0);

                // VB-CABLE / some loopback endpoints don’t expose a usable DirectSound device for
                // BASS_Init(deviceIndex), which breaks Bass.CurrentDevice + stream creation. WASAPI
                // loopback still works via BassWasapi indices — build decode-only streams on device 0.
                int graphDeviceIndex = sourceIndex;
                if (!InitializeDevice(sourceIndex))
                {
                    Debug.WriteLine($"BASS BRIDGE: BASS.Init({sourceIndex}) failed (LastError={Bass.LastError}) — decode graph on device 0");
                    graphDeviceIndex = 0;
                    if (!InitializeDevice(0))
                    {
                        Debug.WriteLine($"BASS BRIDGE: BASS.Init(0) failed (LastError={Bass.LastError})");
                        StopBridge();
                        return false;
                    }
                }

                _bridgeSourceId = sourceId;

                // 1. Init source loopback first so we can read the negotiated capture format,
                // then create matching streams. Keep the delegate rooted in a static field
                // so the GC doesn't collect it while WASAPI holds the native function pointer.
                _bridgeSourceProc = BridgeSourceCallback;
                // Don't pre-set CurrentDevice here — BassWasapi.Init takes the index
                // directly, and setting CurrentDevice on an uninitialized session
                // throws BassException.
                bool sourceOk = BassWasapi.Init(
                    sourceWasapiIndex, 0, 0,
                    WasapiInitFlags.AutoFormat | WasapiInitFlags.Buffer,
                    0.1f, 0.05f,
                    _bridgeSourceProc);
                if (!sourceOk)
                {
                    Debug.WriteLine($"BASS BRIDGE: source WASAPI init failed: {Bass.LastError}");
                    StopBridge();
                    return false;
                }

                var info = BassWasapi.Info;
                int captureFreq = info.Frequency;
                int captureChans = info.Channels;
                _captureFormats[sourceId] = (captureFreq, captureChans);

                bool devOk = TrySetBassCurrentDevice(graphDeviceIndex);
                if (!devOk && graphDeviceIndex != 0)
                {
                    graphDeviceIndex = 0;
                    InitializeDevice(0);
                    TrySetBassCurrentDevice(0);
                }

                // 2. Push stream in the source's native float format. Decode + Float so the
                // mixer can pull. WASAPI capture in AutoFormat is 32-bit float.
                _bridgePushStream = Bass.CreateStream(captureFreq, captureChans,
                    BassFlags.Decode | BassFlags.Float, StreamProcedureType.Push);
                if (_bridgePushStream == 0)
                {
                    Debug.WriteLine($"BASS BRIDGE: push stream create failed: {Bass.LastError}");
                    StopBridge();
                    return false;
                }

                // 3. Master mixer @ 48k/2 float. The mixer auto-resamples and downmixes the
                // push stream. EQ FX run once here for every target.
                _bridgeMasterMixer = BassMix.CreateMixerStream(48000, 2,
                    BassFlags.Decode | BassFlags.Float | BassFlags.MixerNonStop);
                if (_bridgeMasterMixer == 0)
                {
                    Debug.WriteLine($"BASS BRIDGE: master mixer create failed: {Bass.LastError}");
                    StopBridge();
                    return false;
                }
                // CRITICAL: do NOT pass BassFlags.MixerNonStop here. The flag bit
                // 0x20000 is BASS_MIXER_NONSTOP at stream-create time but
                // BASS_MIXER_CHAN_PAUSE when adding a channel — same numeric
                // value, opposite meaning. ManagedBass aliases both names to
                // 0x20000 so the C# code compiles, but BASSmix sees PAUSE here
                // and the push stream is added with processing disabled. The
                // mixer then pulls silence forever and every split downstream
                // returns zeros to the WASAPI target proc. Keep the flag list
                // to channel-only flags (MixerChanDownMix is the only one
                // needed for our 2ch source -> 2ch mixer layout).
                BassMix.MixerAddChannel(_bridgeMasterMixer, _bridgePushStream,
                    BassFlags.MixerChanDownMix);

                // Master mixer is a *decoding* mixer (same as push source). Decode streams cannot use
                // ChannelPlay — they advance only when downstream splits/WASAPI pull from the mixer.
                // Do not switch this to a playback mixer on the VB-CABLE device: ChannelPlay would feed
                // processed audio back into the cable and fight loopback capture.

                float[] eqGains = gains ?? Array.Empty<float>();
                // BASS FX (both BASS_FX PeakEQ and DX8 ParamEQ) silently no-op on BASSmix decode
                // streams. We use our own 10-band biquad EQ as a Bass.ChannelSetDSP callback
                // instead — and we attach it to each PER-TARGET SPLIT below, not to the master
                // mixer or the source push stream. BASSmix splits read directly from the mixer's
                // internal pre-DSP buffer, so DSP on the mixer or source never reaches the audio
                // that WASAPI consumes. The splits ARE the channels WASAPI pulls, so DSP on them
                // always runs. Filter state lives in MasterEqDsp.AttachedFilters per split.
                _bridgeEqDsp?.Dispose();
                _bridgeEqDsp = new MasterEqDsp(eqGains);
                _bridgeMasterUsesDxPeakEq = false;
                _deviceEqHandles["BRIDGE_MASTER"] = new[] { 1,1,1,1,1,1,1,1,1,1 };

                _bridgeLimiterFx = MasterEngine.AttachBusSoftLimiterFx(_bridgeMasterMixer);

                // 4. Per-target splits + WASAPI playback. Each split has its own read
                // position into the master mixer so all targets get the same audio.
                // NOTE: we deliberately do NOT call Bass.Init(targetIndex) here. That
                // opens a DirectSound/Wave output on the device which we don't need
                // (the bridge drives targets exclusively through BASSWASAPI), and on
                // some drivers it leaves the device in a state where the subsequent
                // BASSWASAPI shared-mode session never delivers data.
                foreach (var targetId in targetIds)
                {
                    if (targetId == sourceId) continue;
                    int targetWasapiIndex = GetWasapiDeviceIndex(targetId, false);
                    if (targetWasapiIndex == -1)
                    {
                        Debug.WriteLine($"BASS BRIDGE: target {targetId} not found in WASAPI device list");
                        continue;
                    }

                    int split = BassMix.CreateSplitStream(_bridgeMasterMixer, BassFlags.Decode | BassFlags.Float, null);
                    if (split == 0)
                    {
                        Debug.WriteLine($"BASS BRIDGE: split create failed for {targetId}: {Bass.LastError}");
                        continue;
                    }
                    _bridgeTargetSplits[targetId] = split;

                    // Register the split for inline EQ processing. The EQ runs inside
                    // the WASAPI proc below, on the bytes returned by ChannelGetData,
                    // because BASSmix splits don't surface DSP modifications via
                    // ChannelGetData (Bass.ChannelSetDSP runs but its output is hidden
                    // from the data caller). Format = master mixer's 48k/2 Float for
                    // stereo targets; matrix mode uses the WASAPI's native channel count.
                    _bridgeEqDsp?.RegisterTarget(split, 2, 48000);

                    // Try the master mixer's format (48k/2) first — Windows shared-mode
                    // SRC handles the conversion to the device. If the driver rejects
                    // that, fall back to AutoFormat and insert a per-target conversion
                    // mixer between the split and the WASAPI callback so the data we
                    // hand WASAPI matches the negotiated format exactly.
                    int sourceForCallback = split;
                    int convertMixer = 0;

                    int splitForEq = split;
                    WasapiProcedure proc = (buf, len, user) =>
                    {
                        int got = Bass.ChannelGetData(sourceForCallback, buf, len);
                        // Clamp negative (error) returns — WASAPI interprets a negative as
                        // a huge unsigned write count and that's the AV-trigger we hit.
                        if (got <= 0) return 0;
                        // Apply master EQ in-place on the bytes we hand WASAPI. This is the
                        // only attachment point that affects audible output — see
                        // MasterEqDsp's class-level remarks.
                        _bridgeEqDsp?.ProcessInline(splitForEq, buf, got);
                        return got;
                    };
                    _bridgeTargetProcs[targetId] = proc;

                    bool targetOk = BassWasapi.Init(
                        targetWasapiIndex, 48000, 2,
                        WasapiInitFlags.Buffer,
                        0.1f, 0.05f,
                        proc);

                    if (!targetOk)
                    {
                        var firstErr = Bass.LastError;
                        Debug.WriteLine($"BASS BRIDGE: target {targetId} 48k/2 init failed ({firstErr}), retrying with AutoFormat");
                        targetOk = BassWasapi.Init(
                            targetWasapiIndex, 0, 0,
                            WasapiInitFlags.AutoFormat | WasapiInitFlags.Buffer,
                            0.1f, 0.05f,
                            proc);
                    }

                    if (targetOk)
                    {
                        try { BassWasapi.CurrentDevice = targetWasapiIndex; } catch { }
                        var tInfo = BassWasapi.Info;
                        Debug.WriteLine($"BASS BRIDGE: target {targetId} initialised at {tInfo.Frequency}Hz/{tInfo.Channels}ch");

                        bool matrixHandled = false;
                        if (UserSettings.GetMatrixSurroundBridgeUpmix()
                            && tInfo.Frequency == 48000
                            && (tInfo.Channels == 6 || tInfo.Channels == 8))
                        {
                            try { BassWasapi.Stop(true); BassWasapi.Free(); } catch { }

                            int outCh = tInfo.Channels;
                            WasapiProcedure matrixProc = CreateMatrixBridgePullProc(split, outCh, _bridgeMatrixChannelOrder);
                            _bridgeTargetProcs[targetId] = matrixProc;

                            bool mOk = BassWasapi.Init(
                                targetWasapiIndex, 48000, outCh,
                                WasapiInitFlags.Buffer,
                                0.1f, 0.05f,
                                matrixProc);

                            if (mOk)
                            {
                                try { BassWasapi.CurrentDevice = targetWasapiIndex; } catch { }
                                Debug.WriteLine($"BASS BRIDGE: target {targetId} matrix Hafler upmix → {outCh}ch @48kHz");
                                matrixHandled = true;
                            }
                            else
                            {
                                Debug.WriteLine($"BASS BRIDGE: target {targetId} matrix init failed ({Bass.LastError}), reverting to stereo path");
                                WasapiProcedure stereoProc = (buf, len, user) =>
                                {
                                    int got = Bass.ChannelGetData(sourceForCallback, buf, len);
                                    return got < 0 ? 0 : got;
                                };
                                _bridgeTargetProcs[targetId] = stereoProc;
                                targetOk = BassWasapi.Init(
                                    targetWasapiIndex, 48000, 2,
                                    WasapiInitFlags.Buffer,
                                    0.1f, 0.05f,
                                    stereoProc);
                                if (!targetOk)
                                {
                                    targetOk = BassWasapi.Init(
                                        targetWasapiIndex, 0, 0,
                                        WasapiInitFlags.AutoFormat | WasapiInitFlags.Buffer,
                                        0.1f, 0.05f,
                                        stereoProc);
                                }

                                if (!targetOk)
                                {
                                    Debug.WriteLine($"BASS BRIDGE: target {targetId} matrix fallback init failed");
                                    try { Bass.StreamFree(split); } catch { }
                                    _bridgeTargetSplits.Remove(targetId);
                                    _bridgeTargetProcs.Remove(targetId);
                                    continue;
                                }

                                try { BassWasapi.CurrentDevice = targetWasapiIndex; } catch { }
                                tInfo = BassWasapi.Info;
                                Debug.WriteLine($"BASS BRIDGE: target {targetId} post-matrix fallback at {tInfo.Frequency}Hz/{tInfo.Channels}ch");
                            }
                        }

                        // If the device negotiated a different format than the master
                        // mixer's 48k/2, the split's bytes won't match what WASAPI
                        // expects. Insert a conversion mixer that auto-SRCs/upmixes
                        // from the split into the device's format.
                        if (!matrixHandled && (tInfo.Frequency != 48000 || tInfo.Channels != 2))
                        {
                            convertMixer = BassMix.CreateMixerStream(tInfo.Frequency, tInfo.Channels,
                                BassFlags.Decode | BassFlags.Float | BassFlags.MixerNonStop);
                            if (convertMixer == 0)
                            {
                                Debug.WriteLine($"BASS BRIDGE: target {targetId} convert mixer create failed: {Bass.LastError}");
                                try { BassWasapi.Free(); } catch { }
                                Bass.StreamFree(split);
                                _bridgeTargetSplits.Remove(targetId);
                                _bridgeTargetProcs.Remove(targetId);
                                continue;
                            }
                            BassMix.MixerAddChannel(convertMixer, split,
                                BassFlags.MixerChanDownMix | BassFlags.MixerNonStop);
                            _bridgeTargetConvert[targetId] = convertMixer;
                            sourceForCallback = convertMixer;
                            Debug.WriteLine($"BASS BRIDGE: target {targetId} using conversion mixer (master 48k/2 → device {tInfo.Frequency}/{tInfo.Channels})");
                        }

                        try { BassWasapi.Start(); }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"BASS BRIDGE: target {targetId} Start() threw: {ex.Message}");
                            try { BassWasapi.Free(); } catch { }
                            if (convertMixer != 0) Bass.StreamFree(convertMixer);
                            Bass.StreamFree(split);
                            _bridgeTargetSplits.Remove(targetId);
                            _bridgeTargetConvert.Remove(targetId);
                            _bridgeTargetProcs.Remove(targetId);
                            continue;
                        }
                        _bridgeTargetIds.Add(targetId);
                        Debug.WriteLine($"BASS BRIDGE: target {targetId} started");
                    }
                    else
                    {
                        Debug.WriteLine($"BASS BRIDGE: target {targetId} init failed (both formats): {Bass.LastError}");
                        Bass.StreamFree(split);
                        _bridgeTargetSplits.Remove(targetId);
                        _bridgeTargetProcs.Remove(targetId);
                    }
                }

                if (_bridgeTargetIds.Count == 0)
                {
                    Debug.WriteLine("BASS BRIDGE: no target devices started");
                    StopBridge();
                    return false;
                }

                // 5. Start source last so the loopback callback always has somewhere to push.
                try { BassWasapi.CurrentDevice = sourceWasapiIndex; } catch { }
                try { BassWasapi.Start(); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"BASS BRIDGE: source Start() threw: {ex.Message}");
                    StopBridge();
                    return false;
                }

                Debug.WriteLine($"BASS BRIDGE: started {sourceId} ({captureFreq} Hz / {captureChans}ch) → {_bridgeTargetIds.Count} target(s)");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BASS BRIDGE: exception in StartBridge: {ex}");
                StopBridge();
                return false;
            }
        }

        // Audio thread. Must never throw — an unhandled exception here tears down
        // the WASAPI capture thread and can take the host process with it.
        private static int BridgeSourceCallback(IntPtr buffer, int length, IntPtr user)
        {
            try
            {
                int push = _bridgePushStream;
                if (push == 0) return length;

                // Bump callback diagnostics first — even a 0-byte buffer is
                // useful evidence that WASAPI loopback at least fired.
                System.Threading.Interlocked.Increment(ref _bridgeSourceCallbacks);

                if (length > 0)
                {
                    int floatCount = length / sizeof(float);
                    var (sr, ch) = _captureFormats.TryGetValue(_bridgeSourceId ?? string.Empty, out var fmt)
                        ? fmt : (48000, 2);
                    int frames = ch > 0 ? floatCount / ch : 0;
                    if (frames > 0)
                        System.Threading.Interlocked.Add(ref _bridgeSourceFrames, frames);

                    // Cheap loudness probe: scan the buffer for the loudest
                    // sample. Stays branch-light so we don't drag the audio
                    // thread. Sub-10^-6 = essentially silence.
                    unsafe
                    {
                        float* p = (float*)buffer.ToPointer();
                        float peak = 0f;
                        for (int i = 0; i < floatCount; i++)
                        {
                            float v = p[i];
                            if (v < 0f) v = -v;
                            if (v > peak) peak = v;
                        }
                        // Decay slightly so transient peaks don't pin the value forever.
                        float prev = _bridgeSourceMaxAbsSample * 0.995f;
                        _bridgeSourceMaxAbsSample = peak > prev ? peak : prev;
                    }
                }

                var sourceId = _bridgeSourceId;
                if (sourceId != null)
                    ApplySpatialIfActive(sourceId, buffer, length);

                int queued = Bass.StreamPutData(push, buffer, length);
                if (length > 0)
                    System.Threading.Interlocked.Add(ref _bridgePushBytesAttempted, length);
                if (queued < 0)
                {
                    // Failure — most commonly BASS_ERROR_BUFLOST when the stream's
                    // queue is full because nothing downstream pulled. The data is
                    // dropped on the floor and the splits read silence.
                    System.Threading.Interlocked.Increment(ref _bridgePushFailures);
                    _bridgePushLastError = (int)Bass.LastError;
                }
                else
                {
                    System.Threading.Interlocked.Increment(ref _bridgePushOk);
                    if (queued > 0)
                        System.Threading.Interlocked.Add(ref _bridgePushBytesQueued, queued);
                    if (queued < length)
                    {
                        // Partial queue — buffer was nearly full. Treat as a soft
                        // failure for diagnostics so we notice it.
                        System.Threading.Interlocked.Increment(ref _bridgePushFailures);
                        _bridgePushLastError = (int)Bass.LastError;
                    }
                }

                // Nudge the decoding mixer so FX (peak EQ, limiter) stay aligned with the push cadence;
                // downstream WASAPI targets still drive the real pull.
                int mix = _bridgeMasterMixer;
                if (mix != 0)
                    Bass.ChannelUpdate(mix, length);
            }
            catch
            {
                // swallow — see comment above
            }
            return length;
        }

        public static void StopBridge()
        {
            // Stop the source first so no more data flows in.
            // Wrap every native call: if the bridge never fully started (or this is
            // a defensive cleanup before a fresh start) the WASAPI session for the
            // computed index may not exist, and the CurrentDevice setter throws
            // BassException in that case.
            if (_bridgeSourceId != null)
            {
                int sourceWasapiIndex = GetWasapiDeviceIndex(_bridgeSourceId, true);
                if (sourceWasapiIndex != -1)
                {
                    try { BassWasapi.CurrentDevice = sourceWasapiIndex; } catch { }
                    try { BassWasapi.Stop(true); } catch { }
                    try { BassWasapi.Free(); } catch { }
                }
                _bridgeSourceId = null;
            }
            _bridgeSourceProc = null;

            foreach (var targetId in _bridgeTargetIds)
            {
                int targetWasapiIndex = GetWasapiDeviceIndex(targetId, false);
                if (targetWasapiIndex != -1)
                {
                    try { BassWasapi.CurrentDevice = targetWasapiIndex; } catch { }
                    try { BassWasapi.Stop(true); } catch { }
                    try { BassWasapi.Free(); } catch { }
                }
            }
            _bridgeTargetIds.Clear();
            _bridgeTargetProcs.Clear();

            // Free per-target conversion mixers, then splits, then master mixer
            // (which auto-frees its FX), then push stream.
            foreach (var convert in _bridgeTargetConvert.Values)
            {
                try { Bass.StreamFree(convert); } catch { }
            }
            _bridgeTargetConvert.Clear();

            foreach (var split in _bridgeTargetSplits.Values)
            {
                try { Bass.StreamFree(split); } catch { }
            }
            _bridgeTargetSplits.Clear();
            _bridgeDelayHandles.Clear();

            if (_bridgeMasterMixer != 0)
            {
                _bridgeMasterUsesDxPeakEq = false;
                StopBridgeTestTone();
                _bridgeEqDsp?.Dispose();
                _bridgeEqDsp = null;
                MasterEngine.RemoveFx(_bridgeMasterMixer, ref _bridgeLimiterFx);
                try { Bass.StreamFree(_bridgeMasterMixer); } catch { }
                _bridgeMasterMixer = 0;
            }

            if (_bridgePushStream != 0)
            {
                try { Bass.StreamFree(_bridgePushStream); } catch { }
                _bridgePushStream = 0;
            }

            _deviceEqHandles.Remove("BRIDGE_MASTER");
        }

        /// <summary>
        /// Snapshot of the BASS bridge for the Tools-page Diagnose button. Returns
        /// a multi-line string with mixer / push / EQ / target state so the user
        /// can paste it back when reporting an audio issue.
        /// </summary>
        public static string DumpBridgeDiagnostics()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== BASS BRIDGE DIAGNOSTICS ===");
            sb.AppendLine($"  IsBridgeRunning      : {IsBridgeRunning}");
            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                using var defaultDev = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console);
                sb.AppendLine($"  Windows default out  : {defaultDev.FriendlyName} ({defaultDev.ID})");
            }
            catch (Exception ex) { sb.AppendLine($"  Windows default out  : (error: {ex.Message})"); }
            sb.AppendLine($"  Source ID            : {_bridgeSourceId ?? "(none)"}");
            sb.AppendLine($"  Source loopback hits : {System.Threading.Interlocked.Read(ref _bridgeSourceCallbacks)} (must be > 0 — if 0, WASAPI loopback isn't delivering)");
            sb.AppendLine($"  Source loopback frms : {System.Threading.Interlocked.Read(ref _bridgeSourceFrames)}");
            sb.AppendLine($"  Source peak |sample| : {_bridgeSourceMaxAbsSample:0.000000} (near 0 = silence captured; ≥ 0.001 = real audio captured)");
            sb.AppendLine($"  StreamPutData ok     : {System.Threading.Interlocked.Read(ref _bridgePushOk)}");
            sb.AppendLine($"  StreamPutData fail   : {System.Threading.Interlocked.Read(ref _bridgePushFailures)} (last Bass.LastError = {(Errors)_bridgePushLastError})");
            sb.AppendLine($"  Push bytes attempted : {System.Threading.Interlocked.Read(ref _bridgePushBytesAttempted)}");
            sb.AppendLine($"  Push bytes queued    : {System.Threading.Interlocked.Read(ref _bridgePushBytesQueued)} (gap from attempted = bytes dropped)");
            try
            {
                int pushQueued = _bridgePushStream != 0
                    ? Bass.ChannelGetData(_bridgePushStream, IntPtr.Zero, (int)DataFlags.Available)
                    : -1;
                sb.AppendLine($"  Push queued now      : {pushQueued} bytes (instantaneous; high & growing = nothing downstream pulling)");
            }
            catch (Exception ex) { sb.AppendLine($"  Push queued now      : (error: {ex.Message})"); }
            try
            {
                int mixQueued = _bridgeMasterMixer != 0
                    ? Bass.ChannelGetData(_bridgeMasterMixer, IntPtr.Zero, (int)DataFlags.Available)
                    : -1;
                sb.AppendLine($"  Mixer queued now     : {mixQueued} bytes");
            }
            catch (Exception ex) { sb.AppendLine($"  Mixer queued now     : (error: {ex.Message})"); }
            sb.AppendLine($"  Push stream handle   : {_bridgePushStream}");
            sb.AppendLine($"  Master mixer handle  : {_bridgeMasterMixer}");
            sb.AppendLine($"  Limiter FX handle    : {_bridgeLimiterFx}");
            sb.AppendLine($"  EQ mode              : {(_bridgeEqDsp is not null ? "DSP biquad chain (per-split)" : (_bridgeMasterUsesDxPeakEq ? "DX8 ParamEQ (fallback)" : "BASS_FX PeakEQ"))}");
            sb.AppendLine($"  EQ DSP attachments   : {(_bridgeEqDsp?.AttachedCount ?? 0)} (one per active split)");
            if (_bridgeEqDsp is not null)
            {
                sb.AppendLine($"  EQ DSP total calls   : {_bridgeEqDsp.TotalCallbackCount} (must be > 0 for DSP to be running)");
                sb.AppendLine($"  EQ DSP total frames  : {_bridgeEqDsp.TotalFrameCount}");
                foreach (var (ch, cb, fr) in _bridgeEqDsp.AttachmentStats())
                    sb.AppendLine($"    split={ch}: callbacks={cb}, frames={fr}");
                foreach (var (ch, pin, pout, nans) in _bridgeEqDsp.AmplitudeStats())
                    sb.AppendLine($"    split={ch}: peakIn={pin:0.000000}, peakOut={pout:0.000000}, nanResets={nans} (peakOut near 0 with peakIn > 0 means EQ killed the audio)");
                sb.AppendLine($"  EQ TestKillFactor    : {_bridgeEqDsp.TestKillFactor:F2} (1.0 = no test override)");
            }
            sb.AppendLine($"  Last gain signature  : [{_bridgeEqLastSig}]");

            if (_bridgeMasterMixer != 0)
            {
                try
                {
                    var info = Bass.ChannelGetInfo(_bridgeMasterMixer);
                    sb.AppendLine($"  Master mixer format  : {info.Frequency}Hz / {info.Channels}ch / flags={info.Flags}");
                }
                catch (Exception ex) { sb.AppendLine($"  Master mixer info    : (error: {ex.Message})"); }

                try
                {
                    bool isActive = Bass.ChannelIsActive(_bridgeMasterMixer) != PlaybackState.Stopped;
                    sb.AppendLine($"  Master mixer active? : {isActive}");
                }
                catch { /* ignore */ }
            }

            sb.AppendLine($"  Target splits        : {_bridgeTargetSplits.Count}");
            foreach (var kvp in _bridgeTargetSplits)
            {
                string name = "(unknown)";
                try
                {
                    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    using var dev = enumerator.GetDevice(kvp.Key);
                    name = dev.FriendlyName ?? "(unnamed)";
                }
                catch { /* device may have been unplugged */ }
                sb.AppendLine($"    split={kvp.Value} ← {name}");
                sb.AppendLine($"      id={kvp.Key}");
            }

            sb.AppendLine("=== END DIAGNOSTICS ===");
            return sb.ToString();
        }

        /// <summary>
        /// Temporary diagnostic helper. Sets the master EQ DSP kill factor.
        /// 1.0 = passthrough, 0.5 = -6 dB, 0.0 = silence. If audibly affects
        /// output, the DSP path is reaching the speakers; if not, the audio
        /// flows around our DSP attachment point.
        /// </summary>
        public static void SetEqTestKillFactor(float factor)
        {
            if (_bridgeEqDsp is null) return;
            _bridgeEqDsp.TestKillFactor = factor;
            Debug.WriteLine($"BASS BRIDGE: EQ TestKillFactor = {factor:F2}");
        }

        // ── Bridge test tone state ────────────────────────────────────────────
        private static int _bridgeTestToneStream;
        private static double _bridgeTestTonePhase;

        /// <summary>
        /// Injects a 1 kHz sine test tone directly into the master bridge for
        /// <paramref name="durationMs"/>. Audio runs through the master EQ +
        /// spatial chain and out to every ACTIVE target. The user hears a
        /// short beep on every active device — proves the bridge fan-out is
        /// working without depending on any external app routing.
        /// </summary>
        public static bool PlayBridgeTestTone(int durationMs = 2500)
        {
            if (!IsBridgeRunning || _bridgeMasterMixer == 0)
            {
                Debug.WriteLine("BASS BRIDGE: PlayBridgeTestTone — bridge not running");
                return false;
            }

            try
            {
                StopBridgeTestTone();

                _bridgeTestTonePhase = 0;
                const int toneRate = 48000;
                const int toneChannels = 2;
                const float toneFreq = 1000f;
                const float toneAmp = 0.25f; // ~ -12 dBFS

                _bridgeTestToneStream = Bass.CreateStream(toneRate, toneChannels,
                    BassFlags.Decode | BassFlags.Float, (handle, buffer, length, user) =>
                {
                    int sampleCount = length / 4;
                    int frames = sampleCount / toneChannels;
                    var pool = ArrayPool<float>.Shared;
                    float[] tmp = pool.Rent(sampleCount);
                    try
                    {
                        double dt = 2.0 * Math.PI * toneFreq / toneRate;
                        for (int f = 0; f < frames; f++)
                        {
                            float v = (float)Math.Sin(_bridgeTestTonePhase) * toneAmp;
                            _bridgeTestTonePhase += dt;
                            int i = f * toneChannels;
                            for (int c = 0; c < toneChannels; c++) tmp[i + c] = v;
                        }
                        if (_bridgeTestTonePhase > 1e6) _bridgeTestTonePhase %= 2.0 * Math.PI;
                        Marshal.Copy(tmp, 0, buffer, sampleCount);
                    }
                    finally { pool.Return(tmp); }
                    return length;
                });

                if (_bridgeTestToneStream == 0)
                {
                    Debug.WriteLine($"BASS BRIDGE: test tone stream create failed: {Bass.LastError}");
                    return false;
                }

                bool added = BassMix.MixerAddChannel(_bridgeMasterMixer, _bridgeTestToneStream,
                    BassFlags.MixerChanDownMix | BassFlags.MixerNonStop);
                if (!added)
                {
                    Debug.WriteLine($"BASS BRIDGE: test tone MixerAddChannel failed: {Bass.LastError}");
                    Bass.StreamFree(_bridgeTestToneStream);
                    _bridgeTestToneStream = 0;
                    return false;
                }

                int handleSnapshot = _bridgeTestToneStream;
                System.Threading.Tasks.Task.Delay(durationMs).ContinueWith(_ =>
                {
                    if (_bridgeTestToneStream == handleSnapshot) StopBridgeTestTone();
                });

                Debug.WriteLine($"BASS BRIDGE: test tone playing for {durationMs} ms");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BASS BRIDGE: PlayBridgeTestTone failed: {ex.Message}");
                return false;
            }
        }

        public static void StopBridgeTestTone()
        {
            if (_bridgeTestToneStream == 0) return;
            try { BassMix.MixerRemoveChannel(_bridgeTestToneStream); } catch { }
            try { Bass.StreamFree(_bridgeTestToneStream); } catch { }
            _bridgeTestToneStream = 0;
            _bridgeTestTonePhase = 0;
        }

        public static void UpdateBridgeEqualizer(float[] gains)
        {
            float[] g = gains ?? Array.Empty<float>();
            string sig = string.Join(",", g.Select(v => v.ToString("+0.0;-0.0;0", System.Globalization.CultureInfo.InvariantCulture)));
            if (!string.Equals(sig, _bridgeEqLastSig, StringComparison.Ordinal))
            {
                Debug.WriteLine($"BASS BRIDGE: UpdateBridgeEqualizer mixer={_bridgeMasterMixer}, mode=DSP, gains=[{sig}]");
                _bridgeEqLastSig = sig;
            }

            _bridgeEqDsp?.UpdateGains(g);
        }
        private static string _bridgeEqLastSig = "";

        private static bool TrySetBassCurrentDevice(int deviceIndex)
        {
            try
            {
                Bass.CurrentDevice = deviceIndex;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BASS BRIDGE: Bass.CurrentDevice={deviceIndex}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// True while the BASS bridge is alive (a source is being captured and
        /// fanned out to one or more targets). Used by the master engine
        /// orchestrator in <c>MainViewModel</c> to decide between starting the
        /// bridge fresh and live-updating it.
        /// </summary>
        public static bool IsBridgeRunning => _bridgeMasterMixer != 0 && _bridgeSourceId != null;

        public static bool IsBridgeEndpoint(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            if (_bridgeSourceId != null
                && string.Equals(deviceId, _bridgeSourceId, StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var targetId in _bridgeTargetIds)
            {
                if (string.Equals(deviceId, targetId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The endpoint id currently driving the bridge as the capture source,
        /// or <c>null</c> when no bridge is running. Used so the orchestrator
        /// can target the same id when changing master spatial.
        /// </summary>
        public static string? BridgeSourceId => _bridgeSourceId;

        public static void UpdateBridgeTargetLatency(string targetId, int offsetMs)
        {
            if (!_bridgeTargetSplits.TryGetValue(targetId, out int split)) return;

            if (_bridgeDelayHandles.TryGetValue(targetId, out int oldFx))
            {
                Bass.ChannelRemoveFX(split, oldFx);
                _bridgeDelayHandles.Remove(targetId);
            }

            if (offsetMs > 0)
            {
                int fx = Bass.ChannelSetFX(split, EffectType.Echo, 1);
                var echo = new EchoParameters
                {
                    fDryMix = 0,
                    fWetMix = 1,
                    fFeedback = 0,
                    fDelay = offsetMs / 1000f,
                    bStereo = 1
                };
                Bass.FXSetParameters(fx, echo);
                _bridgeDelayHandles[targetId] = fx;
            }
        }

        /// <summary>Last-applied master stereo-width — applied to fresh pipelines on creation.</summary>
        private static float _masterStereoWidth = 1.0f;

        /// <summary>
        /// Selects a spatial preset for the given device. <see cref="SpatialPreset.Off"/>
        /// removes any active pipeline. Takes effect on the next WASAPI callback.
        /// </summary>
        public static void SetSpatialPreset(string deviceId, SpatialPreset preset)
        {
            if (preset == SpatialPreset.Off)
            {
                if (_spatialPipelines.TryRemove(deviceId, out var old))
                    DisposePipeline(old);
            }
            else
            {
                var fresh = SpatialPipelineFactory.Create(preset, _masterStereoWidth);
                if (_spatialPipelines.TryGetValue(deviceId, out var old))
                {
                    _spatialPipelines[deviceId] = fresh;
                    DisposePipeline(old);
                }
                else
                {
                    _spatialPipelines[deviceId] = fresh;
                }
            }
            _spatialScratch.TryRemove(deviceId, out _);
        }

        /// <summary>
        /// Live-tune the Master Stereo Width stage on every active spatial
        /// pipeline. New pipelines created after this call inherit the value.
        /// No effect when a pipeline's preset is <see cref="SpatialPreset.Off"/>
        /// (no pipeline exists for that device).
        /// </summary>
        public static void SetMasterStereoWidth(float width)
        {
            _masterStereoWidth = Math.Clamp(width, StereoWidthStage.MinWidth, StereoWidthStage.MaxWidth);
            foreach (var pipeline in _spatialPipelines.Values)
            {
                foreach (var stage in pipeline.Stages)
                {
                    if (stage is StereoWidthStage sw && sw.Name == StereoWidthStage.MasterStageName)
                        sw.Width = _masterStereoWidth;
                }
            }
        }

        private static void DisposePipeline(SpatialPipeline pipeline)
        {
            foreach (var stage in pipeline.Stages)
                if (stage is IDisposable d) d.Dispose();
        }

        // Capture-thread hot path. Mutates the WASAPI capture buffer in place when a
        // pipeline is active. Capture format (sample rate, channels) is whatever
        // BassWasapi.Init negotiated with Windows shared-mode and was cached into
        // _captureFormats right after init. Bytes are 32-bit float (Float flag in
        // WasapiInitFlags). Channel-count-preserving stages can write back to the
        // same IntPtr; stages that change channel count (future real upmix →
        // speakers) fall through with the dry buffer until a matching mixer exists.
        private static void ApplySpatialIfActive(string deviceId, IntPtr buffer, int length)
        {
            if (!_spatialPipelines.TryGetValue(deviceId, out var pipeline)) return;

            int floatCount = length / sizeof(float);
            if (floatCount == 0) return;

            var (sampleRate, channels) = _captureFormats.TryGetValue(deviceId, out var fmt)
                ? fmt
                : (48000, 2);
            if (channels <= 0) return;

            if (!_spatialScratch.TryGetValue(deviceId, out var scratch) || scratch.Length != floatCount)
            {
                scratch = new float[floatCount];
                _spatialScratch[deviceId] = scratch;
            }

            Marshal.Copy(buffer, scratch, 0, floatCount);

            try
            {
                var input  = new SpatialBuffer(scratch, channels, sampleRate, LayoutFor(channels));
                var output = pipeline.Process(input);

                if (output.ChannelCount == channels && output.Samples.Length == floatCount)
                {
                    Marshal.Copy(output.Samples, 0, buffer, floatCount);
                }
                else if (channels == 2
                         && (output.ChannelCount == 6 || output.ChannelCount == 8)
                         && output.FrameCount == floatCount / 2
                         && SpatialFoldDown.TryFoldSurroundToStereo(output.Samples, output.ChannelCount, output.FrameCount, scratch))
                {
                    Marshal.Copy(scratch, 0, buffer, floatCount);
                }
                // else: pipeline changed channel count or length; leave the
                // original buffer untouched and let EQ run on dry capture.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Spatial pipeline error on {deviceId}: {ex.Message}");
            }
        }

        private static ChannelLayout LayoutFor(int channels) => channels switch
        {
            1 => ChannelLayout.Mono,
            2 => ChannelLayout.Stereo,
            4 => ChannelLayout.Quad,
            6 => ChannelLayout.Surround_5_1,
            8 => ChannelLayout.Surround_7_1,
            _ => ChannelLayout.Stereo,
        };

        /// <summary>
        /// Frees BASS WASAPI loopback + mixer EQ for one Windows endpoint. Call before NAudio
        /// <see cref="DuplicationManager"/> captures the same source; otherwise EQ updates hit
        /// idle pipelines or fight for exclusive loopback access.
        /// </summary>
        public static void StopStandaloneDeviceProcessing(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;

            try
            {
                SetSpatialPreset(deviceId, SpatialPreset.Off);

                if (_loopbackHandles.TryGetValue(deviceId, out int wasapiIndex))
                {
                    _loopbackHandles.Remove(deviceId);
                    try
                    {
                        BassWasapi.CurrentDevice = wasapiIndex;
                        BassWasapi.Stop(true);
                        BassWasapi.Free();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"BASS WASAPI teardown for {deviceId}: {ex.Message}");
                    }
                }

                _captureFormats.TryRemove(deviceId, out _);

                int devIndex = GetDeviceIndex(deviceId);
                if (devIndex != -1)
                {
                    // Silently swallow "Init" errors — this device may simply have never been
                    // BASS-initialised in this session (we don't Bass.Init() bridge targets), and
                    // the cleanup is harmless. Only log unexpected errors.
                    try { Bass.CurrentDevice = devIndex; }
                    catch (BassException bex) when (bex.ErrorCode == Errors.Init) { /* expected during cleanup */ }
                    catch (Exception ex) { Debug.WriteLine($"BASS CurrentDevice ({deviceId}): {ex.Message}"); }
                }

                if (_deviceStreams.TryGetValue(deviceId, out int mixerStream))
                {
                    _deviceStreams.Remove(deviceId);
                    try { Bass.ChannelStop(mixerStream); }
                    catch (Exception ex) { Debug.WriteLine($"BASS mixer stop ({deviceId}): {ex.Message}"); }

                    try { Bass.StreamFree(mixerStream); }
                    catch (Exception ex) { Debug.WriteLine($"BASS mixer free ({deviceId}): {ex.Message}"); }
                }

                _deviceEqHandles.Remove(deviceId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StopStandaloneDeviceProcessing error for {deviceId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies high-precision EQ to a device. 
        /// For single-device mode, we create a loopback capture to intercept Windows audio.
        /// </summary>
        public static void UpdateEqualizer(string deviceId, float[] gains)
        {
            try
            {
                // The master bridge owns EQ/spatial for its source and target endpoints.
                // Re-opening the legacy self-EQ loopback during a live bridge can leave
                // WASAPI targets busy and makes slider changes appear to do nothing.
                if (IsBridgeEndpoint(deviceId)) return;

                int deviceIndex = GetDeviceIndex(deviceId);
                int wasapiIndex = GetWasapiDeviceIndex(deviceId, true); // Loopback capture for EQ
                if (deviceIndex == -1 || wasapiIndex == -1) return;

                if (!InitializeDevice(deviceIndex)) return;
                Bass.CurrentDevice = deviceIndex;

                // If this device is the source of a bridge, we don't do "Self-EQ" here.
                // The bridge logic handles EQ on its master mixer.
                if (deviceId == _bridgeSourceId) return;

                // Ensure we have a Mixer stream for this device
                if (!_deviceStreams.TryGetValue(deviceId, out int mixerStream))
                {
                    // Create a Mixer stream (the master output for this engine instance)
                    mixerStream = BassMix.CreateMixerStream(48000, 2, BassFlags.Default | BassFlags.MixerNonStop);
                    _deviceStreams[deviceId] = mixerStream;

                    // 10-band ISO-octave graphic EQ. Bandwidth stays at 2.5 octaves to
                    // keep the wide, "musical" feel from the previous 5-band layout -
                    // narrower Q on 10 bands made each slider feel weak in testing.
                    int[] handles = new int[MasterEngine.IsoCenterFrequencies.Length];

                    for (int i = 0; i < MasterEngine.IsoCenterFrequencies.Length; i++)
                    {
                        handles[i] = Bass.ChannelSetFX(mixerStream, EffectType.PeakEQ, 1);
                        if (handles[i] == 0)
                        {
                            Debug.WriteLine($"BASS EQ: PeakEQ band {i} attach failed for {deviceId}: {Bass.LastError}");
                            continue;
                        }

                        MasterEngine.SetIsoPeakEqBand(handles[i], i, i < gains.Length ? gains[i] : 0f);
                    }
                    _deviceEqHandles[deviceId] = handles;

                    // Start the mixer
                    Bass.ChannelPlay(mixerStream);

                    // IMPORTANT: To affect "single device" Windows sound, we must capture it.
                    // This creates a loopback stream (like a mirror to itself) so we can process it.
                    if (_bassWasapiAvailable && !_wasapiInitFailedDevices.Contains(deviceId))
                    {
                        try
                        {
                            BassWasapi.CurrentDevice = wasapiIndex;
                            bool wasapiOk = BassWasapi.Init(wasapiIndex, 0, 0, WasapiInitFlags.AutoFormat | WasapiInitFlags.Buffer, 0.1f, 0.05f,
                                (buffer, length, user) =>
                                {
                                    ApplySpatialIfActive(deviceId, buffer, length);
                                    Bass.StreamPutData(mixerStream, buffer, length);
                                    return length;
                                });

                            if (wasapiOk)
                            {
                                BassWasapi.CurrentDevice = wasapiIndex;
                                BassWasapi.Start();
                                _loopbackHandles[deviceId] = wasapiIndex;
                                var info = BassWasapi.Info;
                                _captureFormats[deviceId] = (info.Frequency, info.Channels);
                            }
                            else
                            {
                                // Cache the failure so we don't keep poking this device every meter tick.
                                _wasapiInitFailedDevices.Add(deviceId);
                                Debug.WriteLine($"BASS WASAPI: Loopback init failed for {deviceId} (WASAPI index {wasapiIndex}). Error: {Bass.LastError}");
                            }
                        }
                        catch (DllNotFoundException)
                        {
                            // basswasapi.dll wasn't shipped. Disable the BassWasapi loopback path
                            // entirely - the NAudio-based DuplicationManager already handles loopback
                            // capture for the mirror feature, so EQ-only-on-default-device is the only
                            // capability we lose here.
                            _bassWasapiAvailable = false;
                            Debug.WriteLine("BASS WASAPI: basswasapi.dll missing. Single-device EQ capture disabled for this session.");
                        }
                    }
                }
                else
                {
                    // Update existing EQ handles
                    if (_deviceEqHandles.TryGetValue(deviceId, out var handles))
                    {
                        for (int i = 0; i < Math.Min(handles.Length, gains.Length); i++)
                            MasterEngine.SetIsoPeakEqBand(handles[i], i, gains[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BASS EQ Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Plays a professional-grade test tone using BASS.
        /// This verifies that the native DLLs are loaded and the audio driver is responding.
        /// </summary>
        public static bool PlayTestTone(string deviceId)
        {
            try
            {
                StopTestTone();

                int deviceIndex = GetDeviceIndex(deviceId);
                if (deviceIndex == -1) deviceIndex = 1;

                if (!InitializeDevice(deviceIndex)) return false;
                Bass.CurrentDevice = deviceIndex;

                // 1. Ensure the Mixer (which has the EQ) is ready for this device
                // We'll use a dummy gain array for initial setup
                UpdateEqualizer(deviceId, new float[] { 0, 0, 0, 0, 0 });
                
                if (!_deviceStreams.TryGetValue(deviceId, out int mixerStream)) return false;

                // 2. Create the noise stream, but make it a DECODING stream so we can plug it into the mixer
                _testToneStream = Bass.CreateStream(48000, 2, BassFlags.Decode, (_, buffer, length, __) => {
                    var rand = new Random();
                    float[] floatBuffer = new float[length / 4];
                    for (int i = 0; i < floatBuffer.Length; i++)
                    {
                        floatBuffer[i] = (float)(rand.NextDouble() * 2 - 1) * 0.15f; 
                    }
                    Marshal.Copy(floatBuffer, 0, buffer, floatBuffer.Length);
                    return length;
                });

                if (_testToneStream == 0) return false;

                // 3. Plug the test tone into the EQ-processed mixer
                bool added = BassMix.MixerAddChannel(mixerStream, _testToneStream, BassFlags.Default);
                
                // 4. Play the mixer
                return Bass.ChannelPlay(mixerStream);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BASS: Test tone critical error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stops the BASS test tone.
        /// </summary>
        public static void StopTestTone()
        {
            if (_testToneStream != 0)
            {
                Bass.ChannelStop(_testToneStream);
                Bass.StreamFree(_testToneStream);
                _testToneStream = 0;
            }
        }

        /// <summary>
        /// Gets the BASS device index from a Windows Device ID (GUID string).
        /// </summary>
        public static int GetDeviceIndex(string deviceId)
        {
            for (int i = 1; ; i++)
            {
                if (!Bass.GetDeviceInfo(i, out var info)) break;
                // BASS stores the Windows Device ID in the Driver property
                if (info.Driver != null && info.Driver.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        public static int GetWasapiDeviceIndex(string deviceId, bool loopback)
        {
            for (int i = 0; BassWasapi.GetDeviceInfo(i, out var info); i++)
            {
                // ManagedBass WasapiDeviceInfo.ID is the MMDevice ID
                if (info.ID == deviceId && info.IsLoopback == loopback)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>Stereo bridge split → multichannel WASAPI pull at 48 kHz (6 or 8 channels).</summary>
        private static WasapiProcedure CreateMatrixBridgePullProc(int splitHandle, int outChannels, string channelOrderMode)
        {
            return (IntPtr buf, int len, IntPtr user) =>
            {
                try
                {
                    int bytesPerFrame = outChannels * sizeof(float);
                    if (bytesPerFrame <= 0) return 0;
                    int framesRequested = len / bytesPerFrame;
                    if (framesRequested <= 0) return 0;

                    int stereoBytes = framesRequested * 2 * sizeof(float);
                    var pool = ArrayPool<float>.Shared;
                    float[] st = pool.Rent(framesRequested * 2);
                    float[] mc = pool.Rent(framesRequested * outChannels);
                    try
                    {
                        Array.Clear(mc, 0, framesRequested * outChannels);
                        int got = Bass.ChannelGetData(splitHandle, st, stereoBytes);
                        if (got < 0) return 0;
                        int gotFrames = got / (2 * sizeof(float));
                        if (gotFrames <= 0) return 0;

                        // Apply master EQ on stereo data BEFORE matrix upmix so the
                        // upmixed channels inherit the same tonal shaping. The
                        // splitHandle is registered with MasterEqDsp at 2ch/48k.
                        var eqDsp = _bridgeEqDsp;
                        if (eqDsp is not null)
                        {
                            var stHandle = System.Runtime.InteropServices.GCHandle.Alloc(st, System.Runtime.InteropServices.GCHandleType.Pinned);
                            try
                            {
                                eqDsp.ProcessInline(splitHandle, stHandle.AddrOfPinnedObject(), got);
                            }
                            finally { stHandle.Free(); }
                        }

                        SurroundUpmixer.ExpandFrames(
                            st.AsSpan(0, gotFrames * 2),
                            mc.AsSpan(0, gotFrames * outChannels),
                            gotFrames,
                            outChannels);

                        MatrixBridgeChannelReorder.Apply(
                            channelOrderMode,
                            mc.AsSpan(0, gotFrames * outChannels),
                            gotFrames,
                            outChannels);

                        Marshal.Copy(mc, 0, buf, framesRequested * outChannels);
                        return len;
                    }
                    finally
                    {
                        pool.Return(st);
                        pool.Return(mc);
                    }
                }
                catch
                {
                    return 0;
                }
            };
        }
    }
}
