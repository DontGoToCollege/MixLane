using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using MixLane.Core;

namespace MixLane.Models;

/// <summary>A lane. Holds clips; each lane also owns a collapsible effects sub-lane drawn underneath it.</summary>
public sealed class Track : ObservableObject
{
    string _name = "Lane";
    bool _mute, _solo;
    double _gainDb;
    double _pan;
    bool _effectsExpanded = true;
    int _colorIndex;
    int _height = 96;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool Mute { get => _mute; set => Set(ref _mute, value); }
    public bool Solo { get => _solo; set => Set(ref _solo, value); }
    public double GainDb { get => _gainDb; set => Set(ref _gainDb, Math.Clamp(value, -60, 12)); }
    /// <summary>-100 (fully left) .. +100 (fully right).</summary>
    public double Pan { get => _pan; set => Set(ref _pan, Math.Clamp(value, -100, 100)); }
    public bool EffectsExpanded { get => _effectsExpanded; set { if (Set(ref _effectsExpanded, value)) OnPropertyChanged(nameof(TotalHeight)); } }
    public int ColorIndex { get => _colorIndex; set => Set(ref _colorIndex, value); }
    /// <summary>Height of the audio part of the lane in pixels.</summary>
    public int Height { get => _height; set { if (Set(ref _height, Math.Clamp(value, 40, 400))) OnPropertyChanged(nameof(TotalHeight)); } }
    public ObservableCollection<Clip> Clips { get; set; } = new();

    public const int EffectsRowHeight = 20;
    public const int EffectsCollapsedHeight = 14;

    /// <summary>Number of stacked rows needed so overlapping effect regions don't draw on top of each other.</summary>
    [JsonIgnore] public int EffectRows
    {
        get
        {
            int rows = 1;
            foreach (var c in Clips) rows = Math.Max(rows, c.Effects.Count);
            return Math.Min(rows, 6);
        }
    }

    [JsonIgnore] public int EffectsLaneHeight => EffectsExpanded ? EffectRows * EffectsRowHeight + 4 : EffectsCollapsedHeight;
    [JsonIgnore] public int TotalHeight => Height + EffectsLaneHeight;

    public void NotifyLayoutChanged() { OnPropertyChanged(nameof(EffectRows)); OnPropertyChanged(nameof(EffectsLaneHeight)); OnPropertyChanged(nameof(TotalHeight)); }
}
