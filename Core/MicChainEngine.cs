using System;
using KhurramAudioRoute.Core.Voice;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KhurramAudioRoute.Core;

/// <summary>
/// Phase 2 of the CABLE-B voice studio. Captures from a real microphone via
/// WASAPI, runs the audio through a configurable stage chain (currently just
/// <see cref="NoiseGate"/>), resamples to the render endpoint's mix-format
/// rate and renders to CABLE-B Input. See <c>docs/MIC_CHAIN_PLAN.md</c>.
/// </summary>
/// <remarks>
/// The chain is independent of the playback bus and runs on its own WASAPI
/// capture/render pair. Stages live behind a mono <see cref="ISampleProvider"/>
/// pipeline; future phases will append voice EQ, pitch shift, and limiter.
/// </remarks>
public sealed class MicChainEngine : IDisposable
{
    public const int CaptureBufferMs = 10;
    public const int RenderLatencyMs = 30;
    private const int RenderBufferLengthMs = 200;

    private readonly object _stateLock = new();
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _captureBuffer;
    private NoiseGate? _noiseGate;
    private VoiceEq? _voiceEq;
    private PitchShifter? _pitchShifter;
    private SoftLimiter? _softLimiter;
    private PeakMonitor? _outputMonitor;
    private bool _disposed;

    public bool IsRunning { get; private set; }
    public string? CurrentMicId { get; private set; }
    public string? CurrentRenderId { get; private set; }
    public int CaptureSampleRate { get; private set; }
    public int CaptureChannels { get; private set; }
    public int RenderSampleRate { get; private set; }

    /// <summary>Gate stage exposed for UI binding (threshold/hold). Null while stopped.</summary>
    public NoiseGate? NoiseGate => _noiseGate;

    /// <summary>Voice EQ stage exposed for UI binding (preset). Null while stopped.</summary>
    public VoiceEq? VoiceEq => _voiceEq;

    /// <summary>Pitch shifter stage exposed for UI binding (semitones). Null while stopped.</summary>
    public PitchShifter? PitchShifter => _pitchShifter;

    /// <summary>Soft limiter stage exposed for diagnostics. Null while stopped.</summary>
    public SoftLimiter? SoftLimiter => _softLimiter;

    private float _inputPeak;

    /// <summary>
    /// Latest peak amplitude (0..1) observed in the most recent capture frame, with
    /// frame-to-frame decay so the meter falls off when the user stops talking.
    /// Reset to 0 on stop.
    /// </summary>
    public float InputPeak => _inputPeak;

    /// <summary>Post-chain peak amplitude (after limiter) for the output level meter.</summary>
    public float OutputPeak => _outputMonitor?.Peak ?? 0f;

    public void Start(string micDeviceId, string renderDeviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(micDeviceId))
            throw new ArgumentException("micDeviceId required", nameof(micDeviceId));
        if (string.IsNullOrWhiteSpace(renderDeviceId))
            throw new ArgumentException("renderDeviceId required", nameof(renderDeviceId));

        lock (_stateLock)
        {
            if (IsRunning)
                throw new InvalidOperationException("MicChainEngine already running.");

            using var enumerator = new MMDeviceEnumerator();
            MMDevice micDev = enumerator.GetDevice(micDeviceId);
            MMDevice renderDev = enumerator.GetDevice(renderDeviceId);

            try
            {
                _capture = new WasapiCapture(micDev, useEventSync: true, audioBufferMillisecondsLength: CaptureBufferMs);
                CaptureSampleRate = _capture.WaveFormat.SampleRate;
                CaptureChannels = _capture.WaveFormat.Channels;

                var monoFmt = WaveFormat.CreateIeeeFloatWaveFormat(CaptureSampleRate, 1);
                _captureBuffer = new BufferedWaveProvider(monoFmt)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromMilliseconds(RenderBufferLengthMs),
                };

                ISampleProvider chain = _captureBuffer.ToSampleProvider();
                _noiseGate = new NoiseGate(chain);
                chain = _noiseGate;
                _voiceEq = new VoiceEq(chain);
                chain = _voiceEq;
                _pitchShifter = new PitchShifter(chain);
                chain = _pitchShifter;
                _softLimiter = new SoftLimiter(chain);
                chain = _softLimiter;
                _outputMonitor = new PeakMonitor(chain);
                chain = _outputMonitor;

                RenderSampleRate = renderDev.AudioClient.MixFormat.SampleRate;
                if (RenderSampleRate != CaptureSampleRate)
                    chain = new WdlResamplingSampleProvider(chain, RenderSampleRate);

                ISampleProvider stereo = new MonoToStereoSampleProvider(chain);

                _capture.DataAvailable += OnCaptureData;
                _output = new WasapiOut(renderDev, AudioClientShareMode.Shared, true, RenderLatencyMs);
                _output.Init(new SampleToWaveProvider(stereo));

                _capture.StartRecording();
                _output.Play();

                CurrentMicId = micDeviceId;
                CurrentRenderId = renderDeviceId;
                IsRunning = true;
            }
            catch
            {
                CleanupInternal();
                throw;
            }
            finally
            {
                micDev.Dispose();
                renderDev.Dispose();
            }
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (!IsRunning) return;
            CleanupInternal();
        }
    }

    private void CleanupInternal()
    {
        try { _capture?.StopRecording(); } catch { }
        if (_capture is not null)
        {
            try { _capture.DataAvailable -= OnCaptureData; } catch { }
            try { _capture.Dispose(); } catch { }
        }
        _capture = null;

        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        _output = null;

        _captureBuffer = null;
        _noiseGate = null;
        _voiceEq = null;
        _pitchShifter = null;
        _softLimiter = null;
        _outputMonitor = null;
        _inputPeak = 0f;
        IsRunning = false;
        CurrentMicId = null;
        CurrentRenderId = null;
    }

    private void OnCaptureData(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        var buffer = _captureBuffer;
        if (capture is null || buffer is null || e.BytesRecorded <= 0) return;

        var fmt = capture.WaveFormat;
        float framePeak = 0f;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            int totalSamples = e.BytesRecorded / 4;
            int frames = totalSamples / fmt.Channels;
            float[] mono = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                int frameOfs = f * fmt.Channels * 4;
                for (int c = 0; c < fmt.Channels; c++)
                    sum += BitConverter.ToSingle(e.Buffer, frameOfs + c * 4);
                float s = sum / fmt.Channels;
                mono[f] = s;
                float a = s < 0f ? -s : s;
                if (a > framePeak) framePeak = a;
            }
            byte[] pcm = new byte[mono.Length * 4];
            Buffer.BlockCopy(mono, 0, pcm, 0, pcm.Length);
            buffer.AddSamples(pcm, 0, pcm.Length);
        }
        else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            int frames = e.BytesRecorded / (fmt.Channels * 2);
            const float scale = 1f / 32768f;
            float[] mono = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                int frameOfs = f * fmt.Channels * 2;
                int sum = 0;
                for (int c = 0; c < fmt.Channels; c++)
                {
                    int o = frameOfs + c * 2;
                    short s16 = (short)(e.Buffer[o] | (e.Buffer[o + 1] << 8));
                    sum += s16;
                }
                float s = sum * scale / fmt.Channels;
                mono[f] = s;
                float a = s < 0f ? -s : s;
                if (a > framePeak) framePeak = a;
            }
            byte[] pcm = new byte[mono.Length * 4];
            Buffer.BlockCopy(mono, 0, pcm, 0, pcm.Length);
            buffer.AddSamples(pcm, 0, pcm.Length);
        }

        // Peak hold with simple per-callback decay so the meter falls off when
        // the speaker pauses. Capture callbacks fire every ~10 ms.
        const float decay = 0.85f;
        float held = _inputPeak * decay;
        _inputPeak = framePeak > held ? framePeak : held;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
