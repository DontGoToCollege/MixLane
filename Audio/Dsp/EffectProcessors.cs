using MixLane.Core;
using MixLane.Models;
using NAudio.Dsp;

namespace MixLane.Audio.Dsp;

/// <summary>
/// Processes a stereo interleaved buffer in place. <c>clipFrame</c> is the position of buffer[0] relative to the clip start,
/// so region-based effects (fades etc.) know where they are.
/// </summary>
public abstract class EffectProcessor
{
    protected Effect Effect = null!;
    protected int Rate;
    volatile bool _dirty = true;

    public Effect Source => Effect;

    public void Attach(Effect effect, int rate)
    {
        if (Effect != null) Effect.PropertyChanged -= OnEffectChanged;
        Effect = effect; Rate = rate;
        Effect.PropertyChanged += OnEffectChanged;
        _dirty = true;
    }
    public void Detach() { if (Effect != null) Effect.PropertyChanged -= OnEffectChanged; }
    void OnEffectChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) => _dirty = true;

    protected abstract void Configure();
    protected abstract void ProcessCore(float[] buf, int offset, int frames, long clipFrame);
    public virtual void Reset() { }

    /// <summary>Applies the effect to the part of the buffer that falls inside the effect's region.</summary>
    public void Process(float[] buf, int offset, int frames, long clipFrame)
    {
        if (!Effect.Enabled) return;
        if (_dirty) { _dirty = false; Configure(); }
        long rs = Effect.RegionStart, re = rs + Effect.RegionLength;
        long bs = clipFrame, be = clipFrame + frames;
        long s = Math.Max(rs, bs), e = Math.Min(re, be);
        if (e <= s) return;
        int localOff = (int)(s - bs);
        ProcessCore(buf, offset + localOff * 2, (int)(e - s), s);
    }

    public static EffectProcessor Create(Effect e, int rate)
    {
        EffectProcessor p = e.Kind switch
        {
            EffectKind.FadeIn => new FadeProcessor(true),
            EffectKind.FadeOut => new FadeProcessor(false),
            EffectKind.Volume => new GainProcessor(),
            EffectKind.MuteSection => new MuteProcessor(),
            EffectKind.Bass => new BiQuadProcessor(BiQuadProcessor.Mode.LowShelf),
            EffectKind.Treble => new BiQuadProcessor(BiQuadProcessor.Mode.HighShelf),
            EffectKind.Muffle => new BiQuadProcessor(BiQuadProcessor.Mode.LowPass),
            EffectKind.Thin => new BiQuadProcessor(BiQuadProcessor.Mode.HighPass),
            EffectKind.ReduceHiss => new BiQuadProcessor(BiQuadProcessor.Mode.Hiss),
            EffectKind.ReduceRumble => new BiQuadProcessor(BiQuadProcessor.Mode.Rumble),
            EffectKind.Echo => new EchoProcessor(),
            EffectKind.Pan => new PanProcessor(),
            _ => new GainProcessor()
        };
        p.Attach(e, rate);
        return p;
    }
}

sealed class FadeProcessor : EffectProcessor
{
    readonly bool _in; double _shape;
    public FadeProcessor(bool fadeIn) { _in = fadeIn; }
    protected override void Configure() => _shape = Effect.P("shape");
    protected override void ProcessCore(float[] buf, int offset, int frames, long clipFrame)
    {
        double rs = Effect.RegionStart, len = Math.Max(1, Effect.RegionLength);
        for (int i = 0; i < frames; i++)
        {
            double t = (clipFrame + i - rs) / len; // 0..1 across the region
            t = Math.Clamp(t, 0, 1);
            if (!_in) t = 1 - t;
            double g = _shape >= 0.5 ? Math.Sin(t * Math.PI / 2) : t; // equal-power or linear
            buf[offset + i * 2] *= (float)g;
            buf[offset + i * 2 + 1] *= (float)g;
        }
    }
}

sealed class GainProcessor : EffectProcessor
{
    float _g = 1;
    protected override void Configure() => _g = (float)TimeUtil.DbToLinear(Effect.P("db"));
    protected override void ProcessCore(float[] buf, int offset, int frames, long clipFrame)
    {
        for (int i = 0; i < frames * 2; i++) buf[offset + i] *= _g;
    }
}

sealed class MuteProcessor : EffectProcessor
{
    protected override void Configure() { }
    protected override void ProcessCore(float[] buf, int offset, int frames, long clipFrame) => Array.Clear(buf, offset, frames * 2);
}

sealed class PanProcessor : EffectProcessor
{
    float _l = 1, _r = 1;
    protected override void Configure()
    {
        double p = Effect.P("pan") / 100.0; // -1..1
        double a = (p + 1) * Math.PI / 4;   // 0..pi/2
        _l = (float)Math.Cos(a) * 1.4142f; _r = (float)Math.Sin(a) * 1.4142f;
    }
    protected override void ProcessCore(float[] buf, int offset, int frames, long clipFrame)
    {
        for (int i = 0; i < frames; i++) { buf[offset + i * 2] *= _l; buf[offset + i * 2 + 1] *= _r; }
    }
}

/// <summary>All the tone-shaping effects share one biquad-per-channel implementation.</summary>
sealed class BiQuadProcessor : EffectProcessor
{
    public enum Mode { LowShelf, HighShelf, LowPass, HighPass, Hiss, Rumble }
    readonly Mode _mode;
    BiQuadFilter _l = null!, _r = null!;
    public BiQuadProcessor(Mode m) { _mode = m; }

    protected override void Configure()
    {
        float hz, q = (float)Effect.P("q"), db = (float)Effect.P("db");
        if (q <= 0) q = 0.707f;
        switch (_mode)
        {
            case Mode.LowShelf:
                hz = (float)Effect.P("hz"); _l = BiQuadFilter.LowShelf(Rate, hz, 0.9f, db); _r = BiQuadFilter.LowShelf(Rate, hz, 0.9f, db); break;
            case Mode.HighShelf:
                hz = (float)Effect.P("hz"); _l = BiQuadFilter.HighShelf(Rate, hz, 0.9f, db); _r = BiQuadFilter.HighShelf(Rate, hz, 0.9f, db); break;
            case Mode.LowPass:
                hz = (float)Effect.P("hz"); _l = BiQuadFilter.LowPassFilter(Rate, hz, q); _r = BiQuadFilter.LowPassFilter(Rate, hz, q); break;
            case Mode.HighPass:
                hz = (float)Effect.P("hz"); _l = BiQuadFilter.HighPassFilter(Rate, hz, q); _r = BiQuadFilter.HighPassFilter(Rate, hz, q); break;
            case Mode.Hiss:
                hz = HissCutoff(Effect.P("amount")); _l = BiQuadFilter.LowPassFilter(Rate, hz, 0.707f); _r = BiQuadFilter.LowPassFilter(Rate, hz, 0.707f); break;
            default:
                hz = RumbleCutoff(Effect.P("amount")); _l = BiQuadFilter.HighPassFilter(Rate, hz, 0.707f); _r = BiQuadFilter.HighPassFilter(Rate, hz, 0.707f); break;
        }
    }

    /// <summary>0% = 20 kHz (no effect) ... 100% = 3 kHz, on a log curve so the slider feels even.</summary>
    public static float HissCutoff(double amount) => (float)(20000 * Math.Pow(3000.0 / 20000.0, Math.Clamp(amount, 0, 100) / 100.0));
    /// <summary>0% = 10 Hz (no effect) ... 100% = 250 Hz.</summary>
    public static float RumbleCutoff(double amount) => (float)(10 * Math.Pow(25.0, Math.Clamp(amount, 0, 100) / 100.0));

    protected override void ProcessCore(float[] buf, int offset, int frames, long clipFrame)
    {
        for (int i = 0; i < frames; i++)
        {
            buf[offset + i * 2] = _l.Transform(buf[offset + i * 2]);
            buf[offset + i * 2 + 1] = _r.Transform(buf[offset + i * 2 + 1]);
        }
    }
}

sealed class EchoProcessor : EffectProcessor
{
    float[] _line = Array.Empty<float>(); int _pos; int _len; float _fb, _mix;
    protected override void Configure()
    {
        int len = (int)(Effect.P("ms") / 1000.0 * Rate);
        len = Math.Max(1, len);
        if (len != _len) { _len = len; _line = new float[len * 2]; _pos = 0; }
        _fb = (float)(Effect.P("feedback") / 100.0);
        _mix = (float)(Effect.P("mix") / 100.0);
    }
    public override void Reset() { Array.Clear(_line); _pos = 0; }
    protected override void ProcessCore(float[] buf, int offset, int frames, long clipFrame)
    {
        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < 2; c++)
            {
                int idx = offset + i * 2 + c;
                float dry = buf[idx];
                float delayed = _line[_pos * 2 + c];
                _line[_pos * 2 + c] = dry + delayed * _fb;
                buf[idx] = dry + delayed * _mix;
            }
            _pos++; if (_pos >= _len) _pos = 0;
        }
    }
}
