namespace MixLane.Core;

/// <summary>
/// One place that holds every user-facing label in two flavours:
/// plain language for beginners, and industry-standard wording for advanced users.
/// Nothing is dumbed down in beginner mode; the same controls just get described in everyday words.
/// </summary>
public sealed record Term(string Plain, string Pro, string PlainHelp, string ProHelp);

public static class Terminology
{
    public static readonly Dictionary<string, Term> Terms = new()
    {
        ["Lane"] = new("Lane", "Track", "A horizontal row that holds sound pieces. Sounds in different lanes play at the same time.", "A track: a channel of clips mixed together with the others."),
        ["Clip"] = new("Sound piece", "Clip", "A piece of an audio file placed on a lane.", "A region of a source file placed on the timeline."),
        ["Volume"] = new("Volume", "Gain", "How loud this is. 0 means unchanged; negative is quieter, positive is louder.", "Gain in decibels relative to unity."),
        ["Pan"] = new("Left / right", "Pan", "Where the sound sits between your left and right speakers.", "Stereo pan position, -100 (L) to +100 (R)."),
        ["Mute"] = new("Silence", "Mute", "Turn this lane off without deleting anything.", "Mute the track."),
        ["Solo"] = new("Only this", "Solo", "Hear only this lane (and other 'only this' lanes) while it is on.", "Solo the track."),
        ["MasterGain"] = new("Overall loudness", "Master gain", "Makes the whole finished mix louder or quieter.", "Gain applied on the master bus, in dB."),
        ["Bass"] = new("Bass (low sounds)", "Low shelf", "Boost or cut the deep, booming part of the sound.", "Low shelf EQ gain."),
        ["Mid"] = new("Middle (voices, guitars)", "Mid peak", "Boost or cut the middle part of the sound where voices sit.", "Peaking EQ gain at the centre frequency."),
        ["Treble"] = new("Treble (bright, crisp sounds)", "High shelf", "Boost or cut the bright, sparkly part of the sound.", "High shelf EQ gain."),
        ["Hiss"] = new("Reduce hiss & fuzz", "High-cut (de-noise)", "Turn this up if the mix sounds fuzzy or has a static-like hiss. Too much makes it dull.", "Low-pass filter that rolls off the top end where broadband noise lives."),
        ["Rumble"] = new("Reduce hum & rumble", "Low-cut (high-pass)", "Turn this up if there is a low humming or rumbling in the background.", "High-pass filter to remove sub-bass rumble and mains hum."),
        ["Limiter"] = new("Stop it from distorting", "Brickwall limiter", "Keeps the loudest moments from crackling or distorting when the mix gets loud.", "Peak limiter on the master bus at -0.3 dBFS."),
        ["Frequency"] = new("Where it acts (pitch)", "Frequency", "Lower numbers are deeper sounds, higher numbers are brighter sounds.", "Centre / corner frequency in Hz."),
        ["Q"] = new("How narrow", "Q", "Higher = affects a narrower range of pitches.", "Filter Q / bandwidth."),
        ["Effects"] = new("Effects & transitions", "FX", "Things attached to a sound piece that change how it sounds at a certain moment, like fades.", "Clip-attached effect regions."),
        ["Split"] = new("Cut here", "Split at cursor", "Cut the selected sound piece into two at the playhead line.", "Split clip at playhead."),
        ["Playhead"] = new("Playhead line", "Playhead", "The vertical line that shows where you are in the mix.", "Transport position."),
        ["Snap"] = new("Stick to edges", "Snap", "When moving a piece, it gently sticks to the edges of other pieces so they line up exactly. Hold Alt to move freely.", "Snap to clip boundaries. Hold Alt to bypass."),
        ["Offset"] = new("Start inside the file", "Source offset", "How far into the original file this piece begins.", "Offset into the source in samples."),
        ["Start"] = new("Starts at", "Position", "Exactly when this piece starts in the mix.", "Timeline position."),
        ["Length"] = new("Lasts for", "Length", "How long this piece plays.", "Clip length."),
        ["MasterPanel"] = new("Whole mix sound", "Master bus", "Adjust the finished mix as one: loudness, bass, treble, noise clean-up.", "Master bus processing chain."),
        ["ClipPanel"] = new("Selected piece", "Clip inspector", "Exact numbers for where the selected piece starts, how long it lasts and its effects.", "Clip inspector."),
    };

    public static AppSettings? Settings { get; set; }
    static bool Adv => Settings?.IsAdvanced == true;

    public static string L(string key) => Terms.TryGetValue(key, out var t) ? (Adv ? t.Pro : t.Plain) : key;
    public static string Help(string key) => Terms.TryGetValue(key, out var t) ? (Adv ? t.ProHelp : t.PlainHelp) : "";
}
