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

        public DuplicationTarget(string deviceId, MMDevice device, IWavePlayer player, BufferedWaveProvider buffer, EqualizerSampleProvider equalizer)
        {
            DeviceId = deviceId;
            Device = device;
            Player = player;
            Buffer = buffer;
            Equalizer = equalizer;
        }

        public void Dispose()
        {
            try { Player.Stop(); } catch { }
            try { Player.Dispose(); } catch { }
            try { Device.Dispose(); } catch { }
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
        private const int TargetPlaybackLatencyMs = 45;
        private static readonly TimeSpan TargetBufferDuration = TimeSpan.FromMilliseconds(180);

        private WasapiLoopbackCapture? _capture;
        private MMDevice? _sourceDevice;
        private readonly Dictionary<string, DuplicationTarget> _targets = new();
        private readonly object _sync = new();

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
                        return false;
                    Thread.Sleep(RetryDelayMs);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Duplication: source init failed for {sourceDeviceId}: {ex.Message}");
                    ReleaseCapture();
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

            for (int attempt = 1; attempt <= MaxInitAttempts; attempt++)
            {
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    var device = enumerator.GetDevice(deviceId);

                    if (device == null || device.State != DeviceState.Active)
                    {
                        device?.Dispose();
                        return null;
                    }

                    var buffer = new BufferedWaveProvider(_capture.WaveFormat)
                    {
                        DiscardOnBufferOverflow = true,
                        BufferDuration = TargetBufferDuration
                    };

                    var player = new WasapiOut(device, AudioClientShareMode.Shared, true, TargetPlaybackLatencyMs);
                    ISampleProvider provider = buffer.ToSampleProvider();
                    if (_capture.WaveFormat.SampleRate != player.OutputWaveFormat.SampleRate)
                        provider = new WdlResamplingSampleProvider(provider, player.OutputWaveFormat.SampleRate);

                    var equalizer = new EqualizerSampleProvider(provider, gains);
                    provider = equalizer;

                    player.Init(provider);
                    player.Play();

                    Debug.WriteLine($"Duplication: player ready for device {deviceId} (attempt {attempt})");
                    return new DuplicationTarget(deviceId, device, player, buffer, equalizer);
                }
                catch (COMException ex) when (IsTransientWasapi(ex))
                {
                    Debug.WriteLine($"Duplication: target not ready 0x{(uint)ex.HResult:X} (attempt {attempt}/{MaxInitAttempts}), retrying in {RetryDelayMs}ms...");
                    Thread.Sleep(RetryDelayMs);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Duplication: fatal error for {deviceId}: {ex.Message}");
                    return null;
                }
            }

            return null;
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

        public static bool StartDuplication(string sourceDeviceId, IEnumerable<string> targetDeviceIds, float[]? equalizerGains = null)
        {
            if (!_sessions.TryGetValue(sourceDeviceId, out var session))
            {
                session = new DuplicationSession(sourceDeviceId);
                _sessions[sourceDeviceId] = session;
            }

            bool ok = session.StartOrUpdate(sourceDeviceId, targetDeviceIds, equalizerGains ?? new float[10]);
            if (!ok)
            {
                session.Stop();
                _sessions.Remove(sourceDeviceId);
            }

            return ok;
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
    }
}
