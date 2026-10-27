using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MixLane.Core;

public enum UserMode { Beginner, Advanced }

/// <summary>Output formats we can write with the Windows Media Foundation encoders built into Windows.</summary>
public enum AudioFormat { Mp3, Wav, M4a, Wma }

public sealed class ConversionDefaults : ObservableObject
{
    AudioFormat _format = AudioFormat.Mp3;
    int _bitrateKbps = 192;
    int _sampleRate = 44100;
    int _channels = 2;
    bool _askEveryTime = true;
    bool _saveNextToSource = true;
    string _outputFolder = "";

    public AudioFormat Format { get => _format; set => Set(ref _format, value); }
    public int BitrateKbps { get => _bitrateKbps; set => Set(ref _bitrateKbps, value); }
    public int SampleRate { get => _sampleRate; set => Set(ref _sampleRate, value); }
    public int Channels { get => _channels; set => Set(ref _channels, value); }
    /// <summary>When true, importing a video shows the conversion dialog. When false, the defaults are applied silently.</summary>
    public bool AskEveryTime { get => _askEveryTime; set => Set(ref _askEveryTime, value); }
    public bool SaveNextToSource { get => _saveNextToSource; set => Set(ref _saveNextToSource, value); }
    public string OutputFolder { get => _outputFolder; set => Set(ref _outputFolder, value); }

    public ConversionDefaults Clone() => (ConversionDefaults)MemberwiseClone();
    public void CopyFrom(ConversionDefaults o)
    {
        Format = o.Format; BitrateKbps = o.BitrateKbps; SampleRate = o.SampleRate; Channels = o.Channels;
        AskEveryTime = o.AskEveryTime; SaveNextToSource = o.SaveNextToSource; OutputFolder = o.OutputFolder;
    }
}

/// <summary>User preferences, stored in %AppData%\MixLane\settings.json.</summary>
public sealed class AppSettings : ObservableObject
{
    UserMode _mode = UserMode.Beginner;
    int _projectSampleRate = 48000;
    bool _snapToClipEdges = true;
    bool _snapToGrid = false;
    double _gridSeconds = 1.0;
    bool _showTooltips = true;
    int _laneHeight = 96;

    public UserMode Mode { get => _mode; set { if (Set(ref _mode, value)) OnPropertyChanged(nameof(IsAdvanced)); } }
    [JsonIgnore] public bool IsAdvanced => Mode == UserMode.Advanced;
    /// <summary>Sample rate used for new projects. Every imported file is resampled to the project rate so any mix of formats plays together.</summary>
    public int ProjectSampleRate { get => _projectSampleRate; set => Set(ref _projectSampleRate, value); }
    public bool SnapToClipEdges { get => _snapToClipEdges; set => Set(ref _snapToClipEdges, value); }
    public bool SnapToGrid { get => _snapToGrid; set => Set(ref _snapToGrid, value); }
    public double GridSeconds { get => _gridSeconds; set => Set(ref _gridSeconds, value); }
    public bool ShowTooltips { get => _showTooltips; set => Set(ref _showTooltips, value); }
    public int LaneHeight { get => _laneHeight; set => Set(ref _laneHeight, Math.Clamp(value, 48, 300)); }
    public ConversionDefaults Conversion { get; set; } = new();
    public List<string> RecentProjects { get; set; } = new();

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MixLane");
    static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new AppSettings();
        }
        catch { /* corrupt settings: fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* non-fatal */ }
    }
}
