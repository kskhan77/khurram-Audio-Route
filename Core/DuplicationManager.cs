using NAudio.CoreAudioApi;
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
    public class DuplicationSession : IDisposable
    {
        private WasapiLoopbackCapture? _capture;
        private MMDevice? _sourceDevice;
        private readonly List<IWavePlayer> _players = new();
        private readonly List<BufferedWaveProvider> _buffers = new();
        private readonly List<MMDevice> _targetDevices = new();

        public string SessionKey { get; }

        public DuplicationSession(string sessionKey) => SessionKey = sessionKey;

        private static bool IsTransientWasapi(COMException ex)
            => (uint)ex.HResult == 0x88890004 || (uint)ex.HResult == 0x8889000F;

        public bool Start(string sourceDeviceId, IEnumerable<string> targetDeviceIds)
        {
            Stop();
            Thread.Sleep(600);

            try
            {
                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    try
                    {
                        using var enumerator = new MMDeviceEnumerator();
                        _sourceDevice = enumerator.GetDevice(sourceDeviceId);
                        _capture = new WasapiLoopbackCapture(_sourceDevice);
                        break;
                    }
                    catch (COMException ex) when (IsTransientWasapi(ex))
                    {
                        Debug.WriteLine($"Duplication: source not ready 0x{(uint)ex.HResult:X} (attempt {attempt}/4), retrying in 600ms...");
                        try { _capture?.Dispose(); } catch { }
                        _capture = null;
                        try { _sourceDevice?.Dispose(); } catch { }
                        _sourceDevice = null;
                        if (attempt == 4) throw;
                        Thread.Sleep(600);
                    }
                }

                foreach (var id in targetDeviceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct())
                {
                    bool initialized = false;
                    for (int attempt = 1; attempt <= 4 && !initialized; attempt++)
                    {
                        try
                        {
                            using var deviceEnumerator = new MMDeviceEnumerator();
                            var device = deviceEnumerator.GetDevice(id);

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
                            Debug.WriteLine($"Duplication: target not ready 0x{(uint)ex.HResult:X} (attempt {attempt}/4), retrying in 600ms...");
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

                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    try
                    {
                        _capture.StartRecording();
                        break;
                    }
                    catch (COMException ex) when (IsTransientWasapi(ex))
                    {
                        Debug.WriteLine($"Duplication: StartRecording not ready 0x{(uint)ex.HResult:X} (attempt {attempt}/4), retrying in 600ms...");
                        if (attempt == 4) throw;
                        Thread.Sleep(600);
                    }
                }

                Debug.WriteLine($"Duplication running: {SessionKey} -> {_players.Count} device(s).");
                return true;
            }
            catch (Exception ex)
            {
                uint hr = ex is COMException c ? (uint)c.HResult : 0;
                Debug.WriteLine($"Duplication start failed {SessionKey}: 0x{hr:X} {ex.GetType().Name} - {ex.Message}");
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            try { _capture?.StopRecording(); } catch { }
            try { _capture?.Dispose(); } catch { }
            _capture = null;

            foreach (var player in _players)
            {
                try { player.Stop(); } catch { }
                try { player.Dispose(); } catch { }
            }
            _players.Clear();
            _buffers.Clear();

            foreach (var device in _targetDevices)
                try { device.Dispose(); } catch { }
            _targetDevices.Clear();

            try { _sourceDevice?.Dispose(); } catch { }
            _sourceDevice = null;
        }

        public void Dispose() => Stop();
    }

    public static class DuplicationManager
    {
        private static readonly Dictionary<string, DuplicationSession> _sessions = new();

        public static bool StartDuplication(string sourceDeviceId, IEnumerable<string> targetDeviceIds)
        {
            if (_sessions.TryGetValue(sourceDeviceId, out var existing))
                existing.Stop();

            var session = new DuplicationSession(sourceDeviceId);
            _sessions[sourceDeviceId] = session;
            return session.Start(sourceDeviceId, targetDeviceIds);
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
    }
}
