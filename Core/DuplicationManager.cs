using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace KhurramAudioRoute.Core
{
    public class DuplicationSession : IDisposable
    {
        private WasapiLoopbackCapture? _capture;
        private MMDevice? _sourceDevice;           // kept alive until Stop()
        private readonly List<IWavePlayer> _players = new();
        private readonly List<BufferedWaveProvider> _buffers = new();
        private readonly List<MMDevice> _targetDevices = new();

        public int ProcessId { get; }
        public DuplicationSession(int pid) => ProcessId = pid;

        // Right after a routing change, WASAPI clients on the affected endpoint can
        // briefly fail with these HRESULTs. Both are transient and clear after a short wait.
        // 0x88890004 = AUDCLNT_E_DEVICE_INVALIDATED
        // 0x8889000F = AUDCLNT_E_ENDPOINT_CREATE_FAILED
        private static bool IsTransientWasapi(COMException ex)
            => (uint)ex.HResult == 0x88890004 || (uint)ex.HResult == 0x8889000F;

        // Blocks while initializing (call from a background thread).
        // Waits for devices to settle after routing, then retries transient WASAPI failures.
        public bool Start(string sourceDeviceId, IEnumerable<string> targetDeviceIds)
        {
            Stop();

            // Give Windows audio engine time to settle after routing changes before
            // opening any WASAPI clients — without this, devices are often invalidated.
            Thread.Sleep(600);

            try
            {
                // Source-side init can race with routing changes the same way as targets,
                // so wrap it in the same retry pattern.
                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    try
                    {
                        var enumerator = new MMDeviceEnumerator();
                        _sourceDevice = enumerator.GetDevice(sourceDeviceId);
                        enumerator.Dispose();

                        _capture = new WasapiLoopbackCapture(_sourceDevice);
                        break;
                    }
                    catch (COMException ex) when (IsTransientWasapi(ex))
                    {
                        Debug.WriteLine($"Duplication: source not ready 0x{(uint)ex.HResult:X} (attempt {attempt}/4), retrying in 600ms…");
                        try { _capture?.Dispose(); } catch { }
                        _capture = null;
                        try { _sourceDevice?.Dispose(); } catch { }
                        _sourceDevice = null;
                        if (attempt == 4) throw;
                        Thread.Sleep(600);
                    }
                }

                foreach (var id in targetDeviceIds)
                {
                    bool initialized = false;
                    for (int attempt = 1; attempt <= 4 && !initialized; attempt++)
                    {
                        try
                        {
                            var devEnum = new MMDeviceEnumerator();
                            var device = devEnum.GetDevice(id);
                            devEnum.Dispose();

                            if (device == null || device.State != DeviceState.Active)
                            {
                                device?.Dispose();
                                break;
                            }

                            var buffer = new BufferedWaveProvider(_capture!.WaveFormat)
                            {
                                DiscardOnBufferOverflow = true
                            };

                            var outDevice = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);

                            ISampleProvider provider = buffer.ToSampleProvider();
                            if (_capture.WaveFormat.SampleRate != outDevice.OutputWaveFormat.SampleRate)
                                provider = new WdlResamplingSampleProvider(provider, outDevice.OutputWaveFormat.SampleRate);

                            outDevice.Init(provider);
                            outDevice.Play();

                            _buffers.Add(buffer);
                            _players.Add(outDevice);
                            _targetDevices.Add(device);
                            initialized = true;
                            Debug.WriteLine($"Duplication: player ready for device {id} (attempt {attempt})");
                        }
                        catch (COMException ex) when (IsTransientWasapi(ex))
                        {
                            Debug.WriteLine($"Duplication: target not ready 0x{(uint)ex.HResult:X} (attempt {attempt}/4), retrying in 600ms…");
                            Thread.Sleep(600);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Duplication: fatal error for {id}: {ex.Message}");
                            break;
                        }
                    }
                }

                if (_players.Count == 0)
                {
                    Debug.WriteLine("Duplication: no players initialized.");
                    Stop();
                    return false;
                }

                _capture!.DataAvailable += (_, e) =>
                {
                    foreach (var buf in _buffers)
                        buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
                };

                // StartRecording is where audioClient.Initialize() actually runs — also
                // subject to the post-routing race, so retry it too.
                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    try
                    {
                        _capture.StartRecording();
                        break;
                    }
                    catch (COMException ex) when (IsTransientWasapi(ex))
                    {
                        Debug.WriteLine($"Duplication: StartRecording not ready 0x{(uint)ex.HResult:X} (attempt {attempt}/4), retrying in 600ms…");
                        if (attempt == 4) throw;
                        Thread.Sleep(600);
                    }
                }

                Debug.WriteLine($"Duplication running: PID {ProcessId} → {_players.Count} device(s).");
                return true;
            }
            catch (Exception ex)
            {
                uint hr = ex is COMException c ? (uint)c.HResult : 0;
                Debug.WriteLine($"Duplication start failed PID {ProcessId}: 0x{hr:X} {ex.GetType().Name} - {ex.Message}");
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            try { _capture?.StopRecording(); } catch { }
            try { _capture?.Dispose(); } catch { }
            _capture = null;

            foreach (var p in _players)
            {
                try { p.Stop(); } catch { }
                try { p.Dispose(); } catch { }
            }
            _players.Clear();
            _buffers.Clear();

            foreach (var d in _targetDevices)
                try { d.Dispose(); } catch { }
            _targetDevices.Clear();

            try { _sourceDevice?.Dispose(); } catch { }
            _sourceDevice = null;
        }

        public void Dispose() => Stop();
    }

    public static class DuplicationManager
    {
        private static readonly Dictionary<int, DuplicationSession> _sessions = new();

        // Returns false if no players could be initialized.
        public static bool StartDuplication(int pid, string sourceDeviceId, IEnumerable<string> targetDeviceIds)
        {
            if (_sessions.TryGetValue(pid, out var existing))
                existing.Stop();

            var session = new DuplicationSession(pid);
            _sessions[pid] = session;
            return session.Start(sourceDeviceId, targetDeviceIds);
        }

        public static void StopDuplication(int pid)
        {
            if (_sessions.TryGetValue(pid, out var session))
            {
                session.Stop();
                _sessions.Remove(pid);
            }
        }

        public static void StopAll()
        {
            foreach (var s in _sessions.Values)
                s.Stop();
            _sessions.Clear();
        }
    }
}
