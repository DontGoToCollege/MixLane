using MixLane.Core;

namespace MixLane.Models;

public enum EffectKind
{
    FadeIn, FadeOut, Volume, MuteSection, Bass, Treble, Muffle, Thin, ReduceHiss, ReduceRumble, Echo, Pan
}

/// <summary>A single adjustable number on an effect, with labels in both vocabularies.</summary>
public sealed record ParamDef(string Key, string Plain, string Pro, double Min, double Max, double Default, string Unit, bool AdvancedOnly = false, string PlainHelp = "");

/// <summary>Describes what an effect is, how to explain it, and which knobs it has.</summary>
public sealed record EffectDef(EffectKind Kind, string Plain, string Pro, string PlainHelp, string ProHelp, bool IsTransition, ParamDef[] Params)
{
    public string Label => Terminology.Settings?.IsAdvanced == true ? Pro : Plain;
    public string Help => Terminology.Settings?.IsAdvanced == true ? ProHelp : PlainHelp;
}

public static class EffectCatalog
{
    public static readonly EffectDef[] All =
    {
        new(EffectKind.FadeIn, "Fade in", "Fade in", "Starts silent and rises to full volume over this region. Use at the start of a piece so it doesn't pop in.", "Linear or equal-power fade in over the region.", true,
            new[] { new ParamDef("shape", "Shape (0 = straight, 1 = smooth)", "Curve (0 linear, 1 equal-power)", 0, 1, 1, "") }),
        new(EffectKind.FadeOut, "Fade out", "Fade out", "Gradually goes silent over this region. Use at the end of a piece so it doesn't cut off abruptly.", "Linear or equal-power fade out over the region.", true,
            new[] { new ParamDef("shape", "Shape (0 = straight, 1 = smooth)", "Curve (0 linear, 1 equal-power)", 0, 1, 1, "") }),
        new(EffectKind.Volume, "Volume change", "Gain", "Makes just this part louder or quieter.", "Static gain over the region.", false,
            new[] { new ParamDef("db", "Quieter ← 0 → Louder", "Gain", -60, 12, -6, "dB") }),
        new(EffectKind.MuteSection, "Mute this part", "Mute", "Silences this part of the piece completely.", "Zero gain over the region.", false, Array.Empty<ParamDef>()),
        new(EffectKind.Bass, "Bass", "Low shelf", "Boost or cut the deep, booming sounds.", "Low shelf filter.", false,
            new[] { new ParamDef("db", "Less bass ← 0 → More bass", "Gain", -24, 24, 6, "dB"), new ParamDef("hz", "Where it acts (pitch)", "Corner frequency", 40, 500, 120, "Hz", true) }),
        new(EffectKind.Treble, "Treble / brightness", "High shelf", "Boost or cut the bright, crisp sounds.", "High shelf filter.", false,
            new[] { new ParamDef("db", "Duller ← 0 → Brighter", "Gain", -24, 24, 6, "dB"), new ParamDef("hz", "Where it acts (pitch)", "Corner frequency", 1000, 12000, 6000, "Hz", true) }),
        new(EffectKind.Muffle, "Muffle (like through a wall)", "Low-pass filter", "Removes the bright sounds so it feels muffled, distant or underwater. Great for build-ups and transitions.", "Resonant low-pass filter.", true,
            new[] { new ParamDef("hz", "How muffled (lower = more)", "Cutoff", 100, 20000, 1200, "Hz"), new ParamDef("q", "How narrow", "Resonance (Q)", 0.3, 6, 0.7, "", true) }),
        new(EffectKind.Thin, "Thin out (old radio / phone)", "High-pass filter", "Removes the deep sounds so it feels small and tinny, like an old radio. Great for intros.", "Resonant high-pass filter.", true,
            new[] { new ParamDef("hz", "How thin (higher = more)", "Cutoff", 20, 6000, 600, "Hz"), new ParamDef("q", "How narrow", "Resonance (Q)", 0.3, 6, 0.7, "", true) }),
        new(EffectKind.ReduceHiss, "Reduce hiss & fuzz", "High-cut (de-noise)", "Softens static-like hiss and fuzz. Turn up until the hiss fades; too much makes it dull.", "Low-pass roll-off of the top end.", false,
            new[] { new ParamDef("amount", "Strength", "Amount", 0, 100, 40, "%") }),
        new(EffectKind.ReduceRumble, "Reduce hum & rumble", "Low-cut (high-pass)", "Removes low humming and rumbling in the background.", "High-pass roll-off of the bottom end.", false,
            new[] { new ParamDef("amount", "Strength", "Amount", 0, 100, 40, "%") }),
        new(EffectKind.Echo, "Echo", "Delay", "Repeats the sound like an echo in a big room.", "Feedback delay line.", false,
            new[] { new ParamDef("ms", "Time between echoes", "Delay time", 20, 2000, 350, "ms"), new ParamDef("feedback", "How many repeats", "Feedback", 0, 90, 40, "%"), new ParamDef("mix", "How loud the echoes are", "Wet mix", 0, 100, 35, "%") }),
        new(EffectKind.Pan, "Move left / right", "Pan", "Pushes this part toward the left or right speaker.", "Constant-power pan.", false,
            new[] { new ParamDef("pan", "Left ← 0 → Right", "Pan", -100, 100, 0, "") }),
    };

    public static EffectDef Get(EffectKind kind) => All.First(d => d.Kind == kind);
}

/// <summary>An effect attached to a clip. It only acts over a region of the clip, expressed relative to the clip's start.</summary>
public sealed class Effect : ObservableObject
{
    EffectKind _kind;
    long _regionStart;
    long _regionLength;
    bool _enabled = true;

    public Guid Id { get; set; } = Guid.NewGuid();
    public EffectKind Kind { get => _kind; set => Set(ref _kind, value); }
    /// <summary>Offset from the clip start, in samples.</summary>
    public long RegionStart { get => _regionStart; set => Set(ref _regionStart, Math.Max(0, value)); }
    /// <summary>Length of the region, in samples.</summary>
    public long RegionLength { get => _regionLength; set => Set(ref _regionLength, Math.Max(1, value)); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public Dictionary<string, double> Params { get; set; } = new();

    public EffectDef Def => EffectCatalog.Get(Kind);

    public double P(string key)
    {
        if (Params.TryGetValue(key, out var v)) return v;
        var def = Def.Params.FirstOrDefault(p => p.Key == key);
        return def?.Default ?? 0;
    }
    public void SetP(string key, double v) { Params[key] = v; OnPropertyChanged("Params"); }

    public static Effect Create(EffectKind kind, long regionStart, long regionLength)
    {
        var e = new Effect { Kind = kind, RegionStart = regionStart, RegionLength = regionLength };
        foreach (var p in e.Def.Params) e.Params[p.Key] = p.Default;
        return e;
    }

    public Effect Clone() => new()
    {
        Id = Guid.NewGuid(), Kind = Kind, RegionStart = RegionStart, RegionLength = RegionLength, Enabled = Enabled,
        Params = new Dictionary<string, double>(Params)
    };
}
