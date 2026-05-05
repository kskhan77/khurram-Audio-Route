using System;
using NAudio.Wave;

namespace KhurramAudioRoute.Core.Voice;

/// <summary>
/// Single-channel spectral-subtraction noise suppressor for the voice chain.
/// Continuously estimates a per-bin noise floor with a leaky minimum tracker,
/// then subtracts the noise floor magnitude from each frame's spectrum
/// (overestimated and floored to avoid musical-noise artifacts). Designed for
/// stationary background noise — fan, AC, room tone — see
/// <c>docs/MIC_CHAIN_PLAN.md</c>. Not as smart as RNNoise on transient noise.
/// </summary>
/// <remarks>
/// Topology: 512-point FFT with 256-sample hop and Hann analysis/synthesis
/// window. Latency ≈ 5 ms at 48 kHz. The first ~256 samples after Start are
/// silent while the input ring fills.
/// </remarks>
public sealed class SpectralDenoise : ISampleProvider
{
    private const int FftSize = 512;
    private const int HopSize = 256;          // 50% overlap.
    private const int SpectrumSize = FftSize / 2 + 1;

    private readonly ISampleProvider _source;
    private readonly int _channels;

    private readonly float[] _window = new float[FftSize];
    private readonly float[] _inputFrame = new float[FftSize];
    private readonly float[] _outputFrame = new float[FftSize];
    private readonly float[] _re = new float[FftSize];
    private readonly float[] _im = new float[FftSize];
    private readonly float[] _magnitude = new float[SpectrumSize];
    private readonly float[] _phase = new float[SpectrumSize];
    private readonly float[] _noiseFloor = new float[SpectrumSize];

    // Circular ring of incoming samples we still need to process.
    private readonly float[] _inputRing = new float[FftSize];
    private int _inputRingFill;

    // Output overlap-add buffer + read pointer into already-mixed samples.
    private readonly float[] _outputRing = new float[FftSize];
    private int _outputReadPos;
    private int _outputAvailable;

    private float[] _readScratch = Array.Empty<float>();

    public WaveFormat WaveFormat => _source.WaveFormat;
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum reduction in dB applied to noise-only bins. Higher = quieter background but more risk of artifacts.</summary>
    public float ReductionDb { get; set; } = 12f;

    /// <summary>How aggressively the subtraction overestimates the noise floor (1.0 = exact, 1.5 = aggressive).</summary>
    public float Overestimate { get; set; } = 1.2f;

    /// <summary>Per-frame leak rate that lets the noise floor estimate adapt upward when the room gets noisier.</summary>
    public float NoiseLeakRate { get; set; } = 1.001f;

    public SpectralDenoise(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("SpectralDenoise requires IEEE float input.", nameof(source));
        _channels = source.WaveFormat.Channels;

        for (int i = 0; i < FftSize; i++)
            _window[i] = 0.5f * (1f - (float)Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));

        // Initialise the noise floor to a tiny non-zero so the leak doesn't divide by zero.
        for (int i = 0; i < SpectrumSize; i++) _noiseFloor[i] = 1e-6f;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (!Enabled)
            return _source.Read(buffer, offset, count);

        int produced = 0;
        int frames = count / _channels;
        for (int frame = 0; frame < frames; frame++)
        {
            // Make sure we have at least one sample of output ready.
            while (_outputAvailable == 0)
                ProcessOneHop();

            float v = _outputRing[_outputReadPos];
            _outputReadPos = (_outputReadPos + 1) % FftSize;
            _outputAvailable--;

            int o = offset + frame * _channels;
            for (int c = 0; c < _channels; c++) buffer[o + c] = v;
            produced += _channels;
        }
        return produced;
    }

    private void ProcessOneHop()
    {
        // Pull HopSize new mono samples from the source into the input ring.
        // Source is mono (capture upstream is mono float) — we still average
        // channels defensively in case this stage is reused elsewhere.
        int scratchCount = HopSize * _channels;
        if (_readScratch.Length < scratchCount) _readScratch = new float[scratchCount];
        int read = _source.Read(_readScratch, 0, scratchCount);
        if (read <= 0)
        {
            // No upstream data — emit silence for this hop so the chain doesn't stall.
            for (int i = 0; i < HopSize; i++) _outputRing[(_outputReadPos + _outputAvailable + i) % FftSize] = 0f;
            _outputAvailable += HopSize;
            return;
        }

        int hopFrames = read / _channels;
        // Slide the input ring left by one hop and append new mono samples.
        Array.Copy(_inputRing, HopSize, _inputRing, 0, FftSize - HopSize);
        for (int i = 0; i < HopSize; i++)
        {
            float mono = 0f;
            if (i < hopFrames)
            {
                int s = i * _channels;
                for (int c = 0; c < _channels; c++) mono += _readScratch[s + c];
                mono /= _channels;
            }
            _inputRing[FftSize - HopSize + i] = mono;
        }
        if (_inputRingFill < FftSize) _inputRingFill = Math.Min(FftSize, _inputRingFill + HopSize);

        // Window + FFT.
        for (int i = 0; i < FftSize; i++)
        {
            _re[i] = _inputRing[i] * _window[i];
            _im[i] = 0f;
        }
        Fft(_re, _im, forward: true);

        // Magnitude / phase + per-bin noise floor leaky-min update.
        for (int k = 0; k < SpectrumSize; k++)
        {
            float r = _re[k], im = _im[k];
            float mag = (float)Math.Sqrt(r * r + im * im);
            _magnitude[k] = mag;
            _phase[k] = (float)Math.Atan2(im, r);

            float nf = _noiseFloor[k];
            if (mag < nf) nf = mag;
            else nf *= NoiseLeakRate;
            _noiseFloor[k] = nf;
        }

        // Spectral subtraction with overestimate + soft floor.
        float reductionLin = (float)Math.Pow(10.0, -ReductionDb / 20.0); // amount of noise we keep
        for (int k = 0; k < SpectrumSize; k++)
        {
            float mag = _magnitude[k];
            float estimate = _noiseFloor[k] * Overestimate;
            float cleanMag = mag - estimate;
            float floorMag = mag * reductionLin;
            if (cleanMag < floorMag) cleanMag = floorMag;

            float ph = _phase[k];
            _re[k] = cleanMag * (float)Math.Cos(ph);
            _im[k] = cleanMag * (float)Math.Sin(ph);
        }
        // Mirror conjugate for real IFFT.
        for (int k = SpectrumSize; k < FftSize; k++)
        {
            int mirror = FftSize - k;
            _re[k] =  _re[mirror];
            _im[k] = -_im[mirror];
        }

        Fft(_re, _im, forward: false);
        // Synthesis-window + overlap-add into the output frame buffer.
        for (int i = 0; i < FftSize; i++)
            _outputFrame[i] += _re[i] * _window[i];

        // Emit the leftmost HopSize samples (now fully overlapped) and shift.
        for (int i = 0; i < HopSize; i++)
        {
            int dst = (_outputReadPos + _outputAvailable + i) % FftSize;
            // Compensate for the 1.5× total window energy of Hann@50% overlap.
            _outputRing[dst] = _outputFrame[i] / 1.5f;
        }
        _outputAvailable += HopSize;

        Array.Copy(_outputFrame, HopSize, _outputFrame, 0, FftSize - HopSize);
        Array.Clear(_outputFrame, FftSize - HopSize, HopSize);
    }

    private static void Fft(float[] re, float[] im, bool forward)
    {
        int n = re.Length;
        // Bit-reversal permutation.
        int j = 0;
        for (int i = 1; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        // Cooley–Tukey radix-2.
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2.0 * Math.PI / len * (forward ? -1 : 1);
            float wRe = (float)Math.Cos(ang);
            float wIm = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float curRe = 1f, curIm = 0f;
                int half = len >> 1;
                for (int k = 0; k < half; k++)
                {
                    float a = re[i + k], b = im[i + k];
                    float c = re[i + k + half], d = im[i + k + half];
                    float tRe = c * curRe - d * curIm;
                    float tIm = c * curIm + d * curRe;
                    re[i + k] = a + tRe;
                    im[i + k] = b + tIm;
                    re[i + k + half] = a - tRe;
                    im[i + k + half] = b - tIm;
                    float nRe = curRe * wRe - curIm * wIm;
                    float nIm = curRe * wIm + curIm * wRe;
                    curRe = nRe;
                    curIm = nIm;
                }
            }
        }
        if (!forward)
        {
            float invN = 1f / n;
            for (int i = 0; i < n; i++)
            {
                re[i] *= invN;
                im[i] *= invN;
            }
        }
    }
}

