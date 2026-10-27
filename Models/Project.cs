using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MixLane.Core;

namespace MixLane.Models;

/// <summary>Master-bus settings: the "whole mix" sliders.</summary>
public sealed class MasterSettings : ObservableObject
{
    double _gainDb, _bassDb, _midDb, _trebleDb, _hiss, _rumble;
    double _bassHz = 120, _midHz = 1000, _trebleHz = 6000, _midQ = 0.9;
    bool _limiter = true;

    public double GainDb { get => _gainDb; set => Set(ref _gainDb, Math.Clamp(value, -30, 12)); }
    public double BassDb { get => _bassDb; set => Set(ref _bassDb, Math.Clamp(value, -18, 18)); }
    public double MidDb { get => _midDb; set => Set(ref _midDb, Math.Clamp(value, -18, 18)); }
    public double TrebleDb { get => _trebleDb; set => Set(ref _trebleDb, Math.Clamp(value, -18, 18)); }
    public double BassHz { get => _bassHz; set => Set(ref _bassHz, Math.Clamp(value, 40, 500)); }
    public double MidHz { get => _midHz; set => Set(ref _midHz, Math.Clamp(value, 200, 6000)); }
    public double TrebleHz { get => _trebleHz; set => Set(ref _trebleHz, Math.Clamp(value, 1500, 14000)); }
    public double MidQ { get => _midQ; set => Set(ref _midQ, Math.Clamp(value, 0.2, 6)); }
    /// <summary>0..100 hiss reduction strength.</summary>
    public double Hiss { get => _hiss; set => Set(ref _hiss, Math.Clamp(value, 0, 100)); }
    /// <summary>0..100 rumble reduction strength.</summary>
    public double Rumble { get => _rumble; set => Set(ref _rumble, Math.Clamp(value, 0, 100)); }
    public bool Limiter { get => _limiter; set => Set(ref _limiter, value); }
}

public sealed class Project : ObservableObject
{
    string _name = "Untitled mix";
    int _sampleRate = 48000;
    string? _filePath;

    public string Name { get => _name; set => Set(ref _name, value); }
    public int SampleRate { get => _sampleRate; set => Set(ref _sampleRate, value); }
    [JsonIgnore] public string? FilePath { get => _filePath; set => Set(ref _filePath, value); }
    public ObservableCollection<Track> Tracks { get; set; } = new();
    public MasterSettings Master { get; set; } = new();

    /// <summary>Bumped every time anything audible changes; the audio engine rebuilds its render snapshot when it sees a new version.</summary>
    [JsonIgnore] public int Version { get; private set; }
    [JsonIgnore] public object SyncRoot { get; } = new();
    public event Action? Changed;

    public void Touch()
    {
        lock (SyncRoot) Version++;
        Changed?.Invoke();
    }

    [JsonIgnore] public long LengthSamples
    {
        get { long max = 0; foreach (var t in Tracks) foreach (var c in t.Clips) max = Math.Max(max, c.End); return max; }
    }

    public Track AddTrack(string? name = null)
    {
        var t = new Track { Name = name ?? $"Lane {Tracks.Count + 1}", ColorIndex = Tracks.Count % 8, Height = Terminology.Settings?.LaneHeight ?? 96 };
        Tracks.Add(t);
        Touch();
        return t;
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
    public static Project FromJson(string json) => JsonSerializer.Deserialize<Project>(json, JsonOpts) ?? new Project();

    public void Save(string path)
    {
        File.WriteAllText(path, ToJson());
        FilePath = path;
    }
}
