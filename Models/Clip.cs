using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using MixLane.Audio;
using MixLane.Core;

namespace MixLane.Models;

/// <summary>
/// A piece of an audio file placed on a lane. All positions are whole samples at the project rate,
/// so placement, trimming and splitting are exact down to a single sample.
/// </summary>
public sealed class Clip : ObservableObject
{
    string _name = "";
    long _start;
    long _offset;
    long _length;
    double _gainDb;
    string _sourcePath = "";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>Path of the source audio file (after any video conversion).</summary>
    public string SourcePath { get => _sourcePath; set => Set(ref _sourcePath, value); }
    /// <summary>Where the clip starts on the timeline, in samples.</summary>
    public long Start { get => _start; set => Set(ref _start, Math.Max(0, value)); }
    /// <summary>How far into the source file the clip begins, in samples.</summary>
    public long Offset { get => _offset; set => Set(ref _offset, Math.Max(0, value)); }
    /// <summary>How many samples of the source are played.</summary>
    public long Length { get => _length; set => Set(ref _length, Math.Max(1, value)); }
    /// <summary>Per-clip volume in dB (0 = unchanged).</summary>
    public double GainDb { get => _gainDb; set => Set(ref _gainDb, Math.Clamp(value, -60, 24)); }
    public ObservableCollection<Effect> Effects { get; set; } = new();

    [JsonIgnore] public long End => Start + Length;
    /// <summary>Decoded audio, set after loading. Not serialized.</summary>
    [JsonIgnore] public AudioSource? Source { get; set; }
    [JsonIgnore] public long SourceLength => Source?.Length ?? Offset + Length;
    [JsonIgnore] public long MaxLength => Math.Max(1, SourceLength - Offset);

    public Clip Clone()
    {
        var c = new Clip { Id = Guid.NewGuid(), Name = Name, SourcePath = SourcePath, Start = Start, Offset = Offset, Length = Length, GainDb = GainDb, Source = Source };
        foreach (var e in Effects) c.Effects.Add(e.Clone());
        return c;
    }

    /// <summary>Cut this clip in two at an absolute timeline sample. Returns the right-hand half, or null if the point isn't inside.</summary>
    public Clip? SplitAt(long timelineSample)
    {
        long rel = timelineSample - Start;
        if (rel <= 0 || rel >= Length) return null;
        var right = Clone();
        right.Start = Start + rel;
        right.Offset = Offset + rel;
        right.Length = Length - rel;
        Length = rel;

        // Effects: keep those overlapping each half, re-based to the new clip start.
        var mine = Effects.ToList();
        Effects.Clear();
        right.Effects.Clear();
        foreach (var e in mine)
        {
            long es = e.RegionStart, ee = e.RegionStart + e.RegionLength;
            if (es < rel)
            {
                var left = e.Clone(); left.RegionLength = Math.Min(ee, rel) - es; Effects.Add(left);
            }
            if (ee > rel)
            {
                var r = e.Clone(); r.RegionStart = Math.Max(0, es - rel); r.RegionLength = ee - Math.Max(es, rel); right.Effects.Add(r);
            }
        }
        return right;
    }
}
