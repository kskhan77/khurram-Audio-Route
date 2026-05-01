using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace KhurramAudioRoute.Core
{
    public sealed class DuplicationTarget : IDisposable
    {
        public string DeviceId { get; }
        public MMDevice Device { get; }
        public IWavePlayer Player { get; }
        public BufferedWaveProvider Buffer { get; }
        public EqualizerSampleProvider Equalizer { get; }
        public DelaySampleProvider Delay { get; }

        public DuplicationTarget(
            string deviceId,
            MMDevice device,
            IWavePlayer player,
            BufferedWaveProvider buffer,
            EqualizerSampleProvider equalizer,
            DelaySampleProvider delay)
        {
            DeviceId = deviceId;
            Device = device;
            Player = player;
            Buffer = buffer;
            Equalizer = equalizer;
            Delay = delay;
        }

        public void Dispose()
        {
            try { Player.Stop(); } catch { }
            try { Player.Dispose(); } catch { }
            try { Device.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Inserts up to N milliseconds of delay between source and output. Use it to
    /// align mirror targets that run on different transports - a Bluetooth speaker
    /// arrives ~150-300 ms later than a wired DAC, so delaying the wired side by
    /// the same amount keeps the room in phase.
    /// </summary>
    public sealed class DelaySampleProvider : ISampleProvider
    {
        private readonly object _sync = new();
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly float[] _ringBuffer;
        private int _writePos;
        private int _delayInSamples;

        public DelaySampleProvider(ISampleProvider source, int maxDelayMs)
        {
            _source = source;
            WaveFormat = source.WaveFormat;
            _channels = Math.Max(1, WaveFormat.Channels);
            _sampleRate = WaveFormat.SampleRate;

            int maxFrames = (int)((long)_sampleRate * Math.Max(1, maxDelayMs) / 1000);
            _ringBuffer = new float[(maxFrames + 1) * _channels];
        }

        public WaveFormat WaveFormat { get; }

        public int CurrentDelayMs
        {
            get
            {
                lock (_sync)
                {
                    return (int)((long)_delayInSamples * 1000 / Math.Max(1, _sampleRate * _channels));
                }
            }
        }

        public void SetDelayMs(int delayMs)
        {
            lock (_sync)
            {
                int requested = (int)((long)_sampleRate * Math.Max(0, delayMs) / 1000) * _channels;
                int max = _ringBuffer.Length - _channels;
                _delayInSamples = Math.Clamp(requested, 0, max);
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (read <= 0)
                return 0;

            lock (_sync)
            {
                if (_delayInSamples == 0)
                    return read;

                int len = _ringBuffer.Length;
                for (int i = 0; i < read; i++)
                {
                    float incoming = buffer[offset + i];
                    _ringBuffer[_writePos] = incoming;

                    int readIdx = _writePos - _delayInSamples;
                    if (readIdx < 0) readIdx += len;
                    buffer[offset + i] = _ringBuffer[readIdx];

                    _writePos++;
                    if (_writePos >= len) _writePos = 0;
                }
            }

            return read;
        }
    }

    public sealed class EqualizerSampleProvider : ISampleProvider
    {
        // 10-band ISO-octave layout aligned with BassEngine.UpdateEqualizer so the
        // mirror EQ matches the global loopback layer.
        private static readonly float[] BandFrequencies =
            { 31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };
        private readonly object _sync = new();
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly float[] _gains = new float[BandFrequencies.Length];
        private BiQuadFilter[][] _filters;

        public EqualizerSampleProvider(ISampleProvider source, float[]? gains = null)
        {
            _source = source;
            WaveFormat = source.WaveFormat;
            _channels = Math.Max(1, WaveFormat.Channels);
            _sampleRate = WaveFormat.SampleRate;
            _filters = CreateFilters();
            UpdateGains(gains ?? new float[BandFrequencies.Length]);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);

            lock (_sync)
            {
                for (int sampleIndex = 0; sampleIndex < samplesRead; sampleIndex++)
                {
                    int channel = sampleIndex % _channels;
                    float sample = buffer[offset + sampleIndex];

                    for (int band = 0; band < _filters[channel].Length; band++)
                        sample = _filters[channel][band].Transform(sample);

                    buffer[offset + sampleIndex] = sample;
                }
            }

            return samplesRead;
        }

        public void UpdateGains(float[] gains)
        {
            lock (_sync)
            {
                for (int i = 0; i < _gains.Length; i++)
                    _gains[i] = i < gains.Length ? gains[i] : 0f;

                _filters = CreateFilters();
            }
        }

        private BiQuadFilter[][] CreateFilters()
        {
            var filters = new BiQuadFilter[_channels][];
            for (int channel = 0; channel < _channels; channel++)
            {
                filters[channel] = new BiQuadFilter[BandFrequencies.Length];
                for (int band = 0; band < BandFrequencies.Length; band++)
                {
                    filters[channel][band] = BiQuadFilter.PeakingEQ(_sampleRate, BandFrequencies[band], 0.9f, _gains[band]);
                }
            }

            return filters;
        }
    }

    public class DuplicationSession : IDisposable
    {
        private const int DeviceSettleDelayMs = 350;
        private const int RetryDelayMs = 350;
        private const int MaxInitAttempts = 4;
        // Bluetooth A2DP endpoints (e.g. Sony HT-CT290) frequently fail with
        // AUDCLNT_E_ENDPOINT_CREATE_FAILED on the first second of attempts because the
        // codec handshake hasn't finished. Give targets a longer budget with backoff
        // so the negotiation has time to complete before we surface a failure.
        private const int MaxTargetInitAttempts = 10;
        private const int TargetRetryStartMs = 250;
        private const int TargetRetryMaxMs = 1500;
        private const int TargetPlaybackLatencyMs = 45;
        // Per-delay ring-buffer size. Combined baseline + source + per-target must fit
        // here, so this is larger than the sum of the per-control caps.
        private const int MaxDelayMs = 1500;
        // Baseline pre-delay added to every target so negative per-target offsets can
        // "advance" a mirror toward the source. The user-visible "0 ms" point sits at
        // this baseline; a -250 ms target offset cancels it (effective 0).
        public const int BaselinePreDelayMs = 250;
        private static readonly TimeSpan TargetBufferDuration = TimeSpan.FromMilliseconds(180);

        private WasapiLoopbackCapture? _capture;
        private MMDevice? _sourceDevice;
        private readonly Dictionary<string, DuplicationTarget> _targets = new();
        // Persists per-target latency across target add/remove so toggling a checkbox
        // doesn't reset the user's sync setting for that device.
        private readonly Dictionary<string, int> _targetLatencies = new();
        // Global pre-fan-out delay applied uniformly to every mirror target. Used so the
        // user can shift "all mirrored copies" relative to the source's natural OS playback.
        private int _sourceLatencyMs;
        private readonly object _sync = new();

        public string? LastError { get; private set; }

        public string SessionKey { get; }

        public DuplicationSession(string sessionKey) => SessionKey = sessionKey;

        private static bool IsTransientWasapi(COMException ex)
            => (uint)ex.HResult == 0x88890004 || (uint)ex.HResult == 0x8889000F;

        private bool EnsureCapture(string sourceDeviceId)
        {
            if (_capture != null)
                return true;

            Thread.Sleep(DeviceSettleDelayMs);

            for (int attempt = 1; attempt <= MaxInitAttempts; attempt++)
            {
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    _sourceDevice = enumerator.GetDevice(sourceDeviceId);
                    _capture = new WasapiLoopbackCapture(_sourceDevice);
                    _capture.DataAvailable += OnDataAvailable;

                    for (int startAttempt = 1; startAttempt <= MaxInitAttempts; startAttempt++)
                    {
                        try
                        {
                            _capture.StartRecording();
                            break;
                        }
                        catch (COMException ex) when (IsTransientWasapi(ex))
                        {
                            Debug.WriteLine($"Duplication: StartRecording not ready 0x{(uint)ex.HResult:X} (attempt {startAttempt}/{MaxInitAttempts}), retrying in {RetryDelayMs}ms...");
                            if (startAttempt == MaxInitAttempts) throw;
                            Thread.Sleep(RetryDelayMs);
                        }
                    }

                    return true;
                }
                catch (COMException ex) when (IsTransientWasapi(ex))
                {
                    Debug.WriteLine($"Duplication: source not ready 0x{(uint)ex.HResult:X} (attempt {attempt}/{MaxInitAttempts}), retrying in {RetryDelayMs}ms...");
                    ReleaseCapture();
                    if (attempt == MaxInitAttempts)
                    {
                        LastError = $"Source device not ready (HRESULT 0x{(uint)ex.HResult:X}). Try again after audio is playing.";
                        return false;
                    }
                    Thread.Sleep(RetryDelayMs);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Duplication: source init failed for {sourceDeviceId}: {ex.Message}");
                    ReleaseCapture();
                    LastError = $"Source capture failed: {ex.Message}";
                    return false;
                }
            }

            return false;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            DuplicationTarget[] targets;
            lock (_sync)
                targets = _targets.Values.ToArray();

            foreach (var target in targets)
                target.Buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }

        private DuplicationTarget? CreateTarget(string deviceId, float[] gains)
        {
            if (_capture == null)
                return null;

            string? deviceName = null;
            for (int attempt = 1; attempt <= MaxTargetInitAttempts; attempt++)
            {
                MMDevice? device = null;
                WasapiOut? player = null;
                bool success = false;
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    device = enumerator.GetDevice(deviceId);

                    if (device == null || device.State != DeviceState.Active)
                    {
                        deviceName ??= TryGetFriendlyName(device);
                        LastError = $"{deviceName ?? "Target device"} is not active. Reconnect or unmute it, then try again.";
                        return null;
                    }

                    deviceName = TryGetFriendlyName(device);

                    var buffer = new BufferedWaveProvider(_capture.WaveFormat)
                    {
                        DiscardOnBufferOverflow = true,
                        BufferDuration = TargetBufferDuration
                    };

                    player = new WasapiOut(device, AudioClientShareMode.Shared, true, TargetPlaybackLatencyMs);
                    ISampleProvider provider = buffer.ToSampleProvider();
                    if (_capture.WaveFormat.SampleRate != player.OutputWaveFormat.SampleRate)
                        provider = new WdlResamplingSampleProvider(provider, player.OutputWaveFormat.SampleRate);

                    var equalizer = new EqualizerSampleProvider(provider, gains);
                    var delay = new DelaySampleProvider(equalizer, MaxDelayMs);
                    int targetMs = _targetLatencies.TryGetValue(deviceId, out var ms) ? ms : 0;
                    delay.SetDelayMs(EffectiveDelayMs(targetMs));
                    provider = delay;

                    player.Init(provider);
                    player.Play();

                    Debug.WriteLine($"Duplication: player ready for device {deviceId} (attempt {attempt}, delay={delay.CurrentDelayMs}ms)");
                    success = true;
                    return new DuplicationTarget(deviceId, device, player, buffer, equalizer, delay);
                }
                catch (COMException ex) when (IsTransientWasapi(ex))
                {
                    int backoff = Math.Min(TargetRetryMaxMs, TargetRetryStartMs * (1 << Math.Min(8, attempt - 1)));
                    Debug.WriteLine($"Duplication: target not ready 0x{(uint)ex.HResult:X} (attempt {attempt}/{MaxTargetInitAttempts}), retrying in {backoff}ms...");
                    if (attempt == MaxTargetInitAttempts)
                        LastError = DescribeTransientWasapi(deviceName ?? "Target device", (uint)ex.HResult);
                    Thread.Sleep(backoff);
                }
                catch (COMException ex)
                {
                    Debug.WriteLine($"Duplication: COM error for {deviceId} 0x{(uint)ex.HResult:X}: {ex.Message}");
                    LastError = DescribeWasapiError(deviceName ?? deviceId, ex);
                    return null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Duplication: fatal error for {deviceId}: {ex.Message}");
                    LastError = $"{deviceName ?? "Target device"}: {ex.Message}";
                    return null;
                }
                finally
                {
                    // Critical: a leaked WasapiOut keeps the audio endpoint open in shared
                    // mode, which can wedge the device for Windows itself (BT soundbars
                    // are particularly sensitive). Always dispose if we didn't hand the
                    // player to a successful DuplicationTarget.
                    if (!success)
                    {
                        try { player?.Stop(); } catch { }
                        try { player?.Dispose(); } catch { }
                        try { device?.Dispose(); } catch { }
                    }
                }
            }

            return null;
        }

        private static string? TryGetFriendlyName(MMDevice? device)
        {
            try { return device?.FriendlyName; }
            catch { return null; }
        }

        private static string DescribeWasapiError(string deviceLabel, COMException ex)
        {
            uint code = (uint)ex.HResult;
            string hint = code switch
            {
                0x88890008 => "format not supported in shared mode (try changing the device's default format in Windows Sound settings).",
                0x8889000A => "endpoint already in exclusive use by another app.",
                0x88890001 => "device not initialized.",
                0x88890017 => "audio service not running.",
                0x80070490 => "endpoint not found - the device may have disconnected.",
                _ => ex.Message
            };
            return $"{deviceLabel}: {hint} (0x{code:X})";
        }

        // Used when transient retries are exhausted. 0x8889000F (ENDPOINT_CREATE_FAILED)
        // is the common Bluetooth A2DP failure mode — the codec handshake hasn't bound
        // the audio endpoint yet, so the user needs to wake the link first.
        private static string DescribeTransientWasapi(string deviceLabel, uint code)
        {
            string hint = code switch
            {
                0x8889000F =>
                    "audio endpoint failed to open (0x8889000F). Bluetooth devices often need waking up first - " +
                    "play any sound on it (e.g. right-click in Windows Sound Settings → Test) and then re-enable mirroring.",
                0x88890004 =>
                    "device was invalidated (0x88890004). It may have been disconnected, set as default again, or " +
                    "had its format changed mid-stream.",
                _ => $"not ready after retries (HRESULT 0x{code:X}). It may be in use by another app or asleep."
            };
            return $"{deviceLabel}: {hint}";
        }

        private void RemoveTargetInternal(string deviceId)
        {
            if (_targets.Remove(deviceId, out var target))
                target.Dispose();
        }

        private void ReleaseCapture()
        {
            if (_capture != null)
            {
                try { _capture.DataAvailable -= OnDataAvailable; } catch { }
                try { _capture.StopRecording(); } catch { }
                try { _capture.Dispose(); } catch { }
                _capture = null;
            }

            try { _sourceDevice?.Dispose(); } catch { }
            _sourceDevice = null;
        }

        public bool StartOrUpdate(string sourceDeviceId, IEnumerable<string> targetDeviceIds, float[] equalizerGains)
        {
            LastError = null;

            var desiredTargets = targetDeviceIds
                .Where(id => !string.IsNullOrWhiteSpace(id) && id != sourceDeviceId)
                .Distinct()
                .ToList();

            if (desiredTargets.Count == 0)
            {
                Stop();
                return false;
            }

            if (!EnsureCapture(sourceDeviceId))
                return false;

            lock (_sync)
            {
                foreach (var removedId in _targets.Keys.Except(desiredTargets).ToList())
                    RemoveTargetInternal(removedId);
            }

            foreach (var targetId in desiredTargets)
            {
                lock (_sync)
                {
                    if (_targets.ContainsKey(targetId))
                        continue;
                }

                var target = CreateTarget(targetId, equalizerGains);
                if (target == null)
                    continue;

                lock (_sync)
                    _targets[targetId] = target;
            }

            lock (_sync)
            {
                if (_targets.Count == 0)
                {
                    Stop();
                    return false;
                }

                Debug.WriteLine($"Duplication running: {SessionKey} -> {_targets.Count} device(s).");
                return true;
            }
        }

        public void UpdateEqualizer(float[] equalizerGains)
        {
            lock (_sync)
            {
                foreach (var target in _targets.Values)
                    target.Equalizer.UpdateGains(equalizerGains);
            }
        }

        public void SetTargetLatency(string targetDeviceId, int latencyMs)
        {
            int clamped = Math.Clamp(latencyMs, -MaxDelayMs, MaxDelayMs);
            lock (_sync)
            {
                _targetLatencies[targetDeviceId] = clamped;
                if (_targets.TryGetValue(targetDeviceId, out var target))
                    target.Delay.SetDelayMs(EffectiveDelayMs(clamped));
            }
        }

        public int GetTargetLatency(string targetDeviceId)
        {
            lock (_sync)
            {
                return _targetLatencies.TryGetValue(targetDeviceId, out var ms) ? ms : 0;
            }
        }

        public void SetSourceLatency(int latencyMs)
        {
            int clamped = Math.Clamp(latencyMs, -MaxDelayMs, MaxDelayMs);
            lock (_sync)
            {
                _sourceLatencyMs = clamped;
                foreach (var (targetId, target) in _targets)
                {
                    int targetMs = _targetLatencies.TryGetValue(targetId, out var t) ? t : 0;
                    target.Delay.SetDelayMs(EffectiveDelayMs(targetMs));
                }
            }
        }

        public int GetSourceLatency()
        {
            lock (_sync) return _sourceLatencyMs;
        }

        // Translates the user-visible signed offset into the actual ring-buffer delay
        // applied to a target. Baseline gives headroom for negative (advance) values.
        private int EffectiveDelayMs(int targetOffsetMs)
            => Math.Clamp(BaselinePreDelayMs + _sourceLatencyMs + targetOffsetMs, 0, MaxDelayMs);

        public void Stop()
        {
            lock (_sync)
            {
                foreach (var target in _targets.Values)
                    target.Dispose();
                _targets.Clear();
            }

            ReleaseCapture();
        }

        public void Dispose() => Stop();
    }

    public static class DuplicationManager
    {
        private static readonly Dictionary<string, DuplicationSession> _sessions = new();
        // Survives session disposal so the UI can still show why StartDuplication failed.
        private static readonly Dictionary<string, string?> _lastErrors = new();

        public static bool StartDuplication(string sourceDeviceId, IEnumerable<string> targetDeviceIds, float[]? equalizerGains = null)
        {
            if (!_sessions.TryGetValue(sourceDeviceId, out var session))
            {
                session = new DuplicationSession(sourceDeviceId);
                _sessions[sourceDeviceId] = session;
            }

            bool ok = session.StartOrUpdate(sourceDeviceId, targetDeviceIds, equalizerGains ?? new float[10]);
            _lastErrors[sourceDeviceId] = session.LastError;
            if (!ok)
            {
                session.Stop();
                _sessions.Remove(sourceDeviceId);
            }

            return ok;
        }

        public static string? GetLastError(string? sourceDeviceId)
        {
            if (string.IsNullOrWhiteSpace(sourceDeviceId)) return null;
            return _lastErrors.TryGetValue(sourceDeviceId, out var msg) ? msg : null;
        }

        public static void StopDuplication(string sourceDeviceId)
        {
            if (_sessions.TryGetValue(sourceDeviceId, out var session))
            {
                session.Stop();
                _sessions.Remove(sourceDeviceId);
            }
        }

        public static void StopAll()
        {
            foreach (var session in _sessions.Values)
                session.Stop();

            _sessions.Clear();
        }

        public static bool IsDuplicating(string? sourceDeviceId)
            => !string.IsNullOrWhiteSpace(sourceDeviceId) && _sessions.ContainsKey(sourceDeviceId);

        public static void UpdateEqualizer(string? sourceDeviceId, float[] equalizerGains)
        {
            if (string.IsNullOrWhiteSpace(sourceDeviceId))
                return;

            if (_sessions.TryGetValue(sourceDeviceId, out var session))
                session.UpdateEqualizer(equalizerGains);
        }

        public static void SetTargetLatency(string? sourceDeviceId, string? targetDeviceId, int latencyMs)
        {
            if (string.IsNullOrWhiteSpace(sourceDeviceId) || string.IsNullOrWhiteSpace(targetDeviceId))
                return;

            if (_sessions.TryGetValue(sourceDeviceId, out var session))
                session.SetTargetLatency(targetDeviceId, latencyMs);
        }

        public static int GetTargetLatency(string? sourceDeviceId, string? targetDeviceId)
        {
            if (string.IsNullOrWhiteSpace(sourceDeviceId) || string.IsNullOrWhiteSpace(targetDeviceId))
                return 0;

            return _sessions.TryGetValue(sourceDeviceId, out var session)
                ? session.GetTargetLatency(targetDeviceId)
                : 0;
        }

        public static void SetSourceLatency(string? sourceDeviceId, int latencyMs)
        {
            if (string.IsNullOrWhiteSpace(sourceDeviceId)) return;
            if (_sessions.TryGetValue(sourceDeviceId, out var session))
                session.SetSourceLatency(latencyMs);
        }

        public static int GetSourceLatency(string? sourceDeviceId)
        {
            if (string.IsNullOrWhiteSpace(sourceDeviceId)) return 0;
            return _sessions.TryGetValue(sourceDeviceId, out var session)
                ? session.GetSourceLatency()
                : 0;
        }
    }
}
