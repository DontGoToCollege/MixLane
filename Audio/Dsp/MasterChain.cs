using MixLane.Core;
using MixLane.Models;
using NAudio.Dsp;

namespace MixLane.Audio.Dsp;

/// <summary>The "whole mix" processing: 3-band EQ, hiss/rumble clean-up, master gain and a simple peak limiter.</summary>
public sealed class MasterChain
{
    readonly MasterSettings _m;
    readonly int _rate;
    BiQuadFilter[] _bass = null!, _mid = null!, _treble = null!, _hiss = null!, _rumble = null!;
    bool _useBass, _useMid, _useTreble, _useHiss, _useRumble;
    float _gain = 1;
    volatile bool _dirty = true;
    float _envL;

    public MasterChain(MasterSettings m, int rate)
    {
        _m = m; _rate = rate;
        m.PropertyChanged += (_, _) => _dirty = true;
    }

    void Configure()
    {
        _useBass = Math.Abs(_m.BassDb) > 0.05; _useMid = Math.Abs(_m.MidDb) > 0.05; _useTreble = Math.Abs(_m.TrebleDb) > 0.05;
        _useHiss = _m.Hiss > 0.5; _useRumble = _m.Rumble > 0.5;
        _bass = Pair(() => BiQuadFilter.LowShelf(_rate, (float)_m.BassHz, 0.9f, (float)_m.BassDb));
        _mid = Pair(() => BiQuadFilter.PeakingEQ(_rate, (float)_m.MidHz, (float)_m.MidQ, (float)_m.MidDb));
        _treble = Pair(() => BiQuadFilter.HighShelf(_rate, (float)_m.TrebleHz, 0.9f, (float)_m.TrebleDb));
        _hiss = Pair(() => BiQuadFilter.LowPassFilter(_rate, BiQuadProcessor.HissCutoff(_m.Hiss), 0.707f));
        _rumble = Pair(() => BiQuadFilter.HighPassFilter(_rate, BiQuadProcessor.RumbleCutoff(_m.Rumble), 0.707f));
        _gain = (float)TimeUtil.DbToLinear(_m.GainDb);
    }
    static BiQuadFilter[] Pair(Func<BiQuadFilter> f) => new[] { f(), f() };

    public void Reset() { _dirty = true; _envL = 0; }

    public void Process(float[] buf, int offset, int frames)
    {
        if (_dirty) { _dirty = false; Configure(); }
        bool limiter = _m.Limiter;
        const float ceiling = 0.966f; // -0.3 dBFS
        const float release = 0.9995f;
        for (int i = 0; i < frames; i++)
        {
            int il = offset + i * 2, ir = il + 1;
            float l = buf[il] * _gain, r = buf[ir] * _gain;
            if (_useRumble) { l = _rumble[0].Transform(l); r = _rumble[1].Transform(r); }
            if (_useBass) { l = _bass[0].Transform(l); r = _bass[1].Transform(r); }
            if (_useMid) { l = _mid[0].Transform(l); r = _mid[1].Transform(r); }
            if (_useTreble) { l = _treble[0].Transform(l); r = _treble[1].Transform(r); }
            if (_useHiss) { l = _hiss[0].Transform(l); r = _hiss[1].Transform(r); }
            if (limiter)
            {
                // Fast-attack, slow-release peak limiter: track the envelope and scale down when it exceeds the ceiling.
                float pk = Math.Max(Math.Abs(l), Math.Abs(r));
                float env = Math.Max(_envL * release, pk);
                _envL = env;
                if (env > ceiling) { float g = ceiling / env; l *= g; r *= g; }
                if (l > 1) l = 1; else if (l < -1) l = -1;
                if (r > 1) r = 1; else if (r < -1) r = -1;
            }
            buf[il] = l; buf[ir] = r;
        }
    }
}
