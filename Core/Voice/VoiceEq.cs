using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace KhurramAudioRoute.Core.Voice;

/// <summary>Voice EQ preset library — see <c>docs/MIC_CHAIN_PLAN.md</c>.</summary>
public enum VoiceEqPreset
{
    Off,
    Bright,
    Warm,
    Radio,
    Telephone,
}

/// <summary>
/// Mono voice EQ as an <see cref="ISampleProvider"/>. Each preset is a small
/// chain of <see cref="BiQuadFilter"/> stages applied in series. Setting
/// <see cref="Preset"/> rebuilds the chain atomically so the read loop never
/// sees a partial set of coefficients.
/// </summary>
public sealed class VoiceEq : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sampleRate;
    private readonly int _channels;
    private BiQuadFilter[] _chain = Array.Empty<BiQuadFilter>();
    private VoiceEqPreset _preset = VoiceEqPreset.Off;

    public WaveFormat WaveFormat => _source.WaveFormat;
    public bool Enabled { get; set; } = true;

    public VoiceEqPreset Preset
    {
        get => _preset;
        set
        {
            if (_preset == value) return;
            _preset = value;
            _chain = BuildChain(value, _sampleRate);
        }
    }

    public VoiceEq(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("VoiceEq requires IEEE float input.", nameof(source));
        _sampleRate = source.WaveFormat.SampleRate;
        _channels = source.WaveFormat.Channels;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (!Enabled || read == 0) return read;

        var chain = _chain;
        if (chain.Length == 0) return read;

        if (_channels == 1)
        {
            int end = offset + read;
            for (int i = offset; i < end; i++)
            {
                float v = buffer[i];
                for (int k = 0; k < chain.Length; k++) v = chain[k].Transform(v);
                buffer[i] = v;
            }
        }
        else
        {
            // Multi-channel: same filter state cascaded across channels (the mic
            // chain is mono today — this branch is here so the class is reusable).
            int frames = read / _channels;
            for (int f = 0; f < frames; f++)
            {
                int i = offset + f * _channels;
                for (int c = 0; c < _channels; c++)
                {
                    float v = buffer[i + c];
                    for (int k = 0; k < chain.Length; k++) v = chain[k].Transform(v);
                    buffer[i + c] = v;
                }
            }
        }

        return read;
    }

    private static BiQuadFilter[] BuildChain(VoiceEqPreset preset, int sr)
    {
        switch (preset)
        {
            case VoiceEqPreset.Off:
                return Array.Empty<BiQuadFilter>();

            case VoiceEqPreset.Bright:
                // Lifts air + presence; tames a hint of low-mid mud.
                return new[]
                {
                    BiQuadFilter.LowShelf(sr, 250f, 1f, -1.5f),
                    BiQuadFilter.PeakingEQ(sr, 3000f, 1.0f, 2f),
                    BiQuadFilter.HighShelf(sr, 6000f, 0.7f, 4f),
                };

            case VoiceEqPreset.Warm:
                // Adds chest, softens sibilance.
                return new[]
                {
                    BiQuadFilter.LowShelf(sr, 200f, 0.7f, 3f),
                    BiQuadFilter.PeakingEQ(sr, 4500f, 1.2f, -1.5f),
                    BiQuadFilter.HighShelf(sr, 8000f, 0.7f, -2f),
                };

            case VoiceEqPreset.Radio:
                // Compressed AM-broadcast vibe: tight band-pass + presence push.
                return new[]
                {
                    BiQuadFilter.HighPassFilter(sr, 250f, 0.707f),
                    BiQuadFilter.PeakingEQ(sr, 1800f, 1.4f, 5f),
                    BiQuadFilter.LowPassFilter(sr, 4000f, 0.707f),
                };

            case VoiceEqPreset.Telephone:
                // Old POTS line: harsh narrow band-pass.
                return new[]
                {
                    BiQuadFilter.HighPassFilter(sr, 400f, 0.707f),
                    BiQuadFilter.PeakingEQ(sr, 1000f, 1.2f, 4f),
                    BiQuadFilter.LowPassFilter(sr, 3400f, 0.707f),
                };

            default:
                return Array.Empty<BiQuadFilter>();
        }
    }
}
