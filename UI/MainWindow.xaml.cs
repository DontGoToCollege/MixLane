using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MixLane.Audio;
using MixLane.Core;
using MixLane.Models;
using static MixLane.UI.UiHelpers;
using Track = MixLane.Models.Track;

namespace MixLane.UI;

public partial class MainWindow : Window
{
    Project _project = new();
    Player? _player;
    readonly UndoManager _undo = new();
    readonly Dictionary<string, AudioSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    readonly Dictionary<Track, Border> _headerByTrack = new();
    CancellationTokenSource? _busyCts;
    long _playStartPos;
    bool _dirty;
    AppSettings S => App.Settings;

    static readonly Color[] Palette =
    {
        Color.FromRgb(0x4F, 0xA3, 0xFF), Color.FromRgb(0x6C, 0xD4, 0x9A), Color.FromRgb(0xF2, 0xB1, 0x5B), Color.FromRgb(0xE0, 0x7A, 0xA5),
        Color.FromRgb(0xA9, 0x8B, 0xFF), Color.FromRgb(0x5C, 0xD6, 0xD6), Color.FromRgb(0xF0, 0xE0, 0x6A), Color.FromRgb(0xFF, 0x8C, 0x6B)
    };

    public MainWindow()
    {
        InitializeComponent();
        Timeline.Settings = S;
        Playhead.Timeline = Timeline;

        Timeline.PlayheadChanged += OnUserMovedPlayhead;
        Timeline.SelectionChanged += OnSelectionChanged;
        Timeline.ViewChanged += SyncView;
        Timeline.EditStarting += () => PushUndo("Edit");
        Timeline.EditCommitted += _ => { MarkDirty(); Inspector.Refresh(); };
        Timeline.Hint += h => HintText.Text = h;
        Timeline.ContextMenuRequested += ShowTimelineMenu;
        Timeline.FilesDropped += (files, track, sample) => _ = ImportFiles(files, track, sample);
        Timeline.ClipActivated += c => { BottomTabs.SelectedItem = ClipTab; ShowBottom(true); };
        Timeline.SizeChanged += (_, _) => { SyncView(); Playhead.InvalidateVisual(); };

        Inspector.BeginEdit = label => new EditScope(this, label);
        Inspector.Redraw = () => { Timeline.Redraw(); MarkDirty(); };
        Inspector.EffectSelected = (c, e) => Timeline.SelectEffect(c, e);
        Inspector.AddEffectRequested = c => ShowAddEffectMenu(c);
        Master.BeginEdit = label => new EditScope(this, label);

        _undo.Changed += UpdateUndoMenus;
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        Drop += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) _ = ImportFiles((string[])e.Data.GetData(DataFormats.FileDrop)!, null, null); };
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        PreviewKeyDown += OnKey;
        Closing += OnClosing;

        NewProject();
        ApplyLabels();
        Loaded += (_, _) =>
        {
            SyncView(); Timeline.Focus();
            var args = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToArray();
            if (args.Length > 0) _ = ImportFiles(args, null, 0);
        };
    }

    // ---------- project lifecycle ----------
    void SetProject(Project p)
    {
        _player?.Dispose();
        _project = p;
        _player = new Player(p);
        p.Changed += () => { RefreshLaneHeaders(); Timeline.Redraw(); UpdateLoopRange(); };
        Timeline.Project = p;
        Master.Show(p.Master);
        Inspector.Show(null, null, p.SampleRate);
        _undo.Clear();
        _dirty = false;
        RebuildLaneHeaders();
        UpdateTitle();
        InfoText.Text = $"{p.SampleRate:N0} Hz";
        Timeline.Playhead = 0;
        Playhead.Playhead = 0;
        UpdatePositionText(0);
    }

    void NewProject()
    {
        var p = new Project { SampleRate = S.ProjectSampleRate };
        SetProject(p);
        Timeline.SetZoom(p.SampleRate / 100.0, 0); // 10 ms per pixel to start
        HintText.Text = "Add a sound to get started: click + Add sounds, or drop audio/video files anywhere.";
    }

    bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show(this, "Save the changes to this mix first?", "MixLane", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return SaveProject(false);
        return true;
    }

    void OnClosing(object? s, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        _timer.Stop();
        _player?.Dispose();
        S.Save();
    }

    void UpdateTitle() => Title = $"MixLane — {(_project.FilePath != null ? Path.GetFileNameWithoutExtension(_project.FilePath) : _project.Name)}{(_dirty ? " *" : "")}";
    void MarkDirty() { _dirty = true; UpdateTitle(); }

    // ---------- undo ----------
    sealed class EditScope : IDisposable
    {
        readonly MainWindow _w;
        public EditScope(MainWindow w, string label) { _w = w; _w.PushUndo(label); }
        public void Dispose() { _w._project.Touch(); _w.MarkDirty(); _w.Timeline.Redraw(); }
    }
    void PushUndo(string label) => _undo.Push(label, _project.ToJson());
    IDisposable Edit(string label) => new EditScope(this, label);

    void UpdateUndoMenus()
    {
        UndoMenu.Header = _undo.CanUndo ? $"_Undo {_undo.UndoLabel}" : "_Undo";
        RedoMenu.Header = _undo.CanRedo ? $"_Redo {_undo.RedoLabel}" : "_Redo";
        UndoMenu.IsEnabled = _undo.CanUndo; RedoMenu.IsEnabled = _undo.CanRedo;
    }

    void ApplyJson(string json)
    {
        var p = Project.FromJson(json);
        _project.Name = p.Name;
        _project.Tracks.Clear();
        foreach (var t in p.Tracks)
        {
            foreach (var c in t.Clips) c.Source = _sources.GetValueOrDefault(c.SourcePath);
            _project.Tracks.Add(t);
        }
        CopyMaster(p.Master, _project.Master);
        Timeline.Select(null);
        Timeline.SelectTrack(_project.Tracks.FirstOrDefault());
        _project.Touch();
        RebuildLaneHeaders();
        Master.Refresh();
        Inspector.Show(null, null, _project.SampleRate);
        MarkDirty();
    }
    static void CopyMaster(MasterSettings from, MasterSettings to)
    {
        to.GainDb = from.GainDb; to.BassDb = from.BassDb; to.MidDb = from.MidDb; to.TrebleDb = from.TrebleDb; to.BassHz = from.BassHz; to.MidHz = from.MidHz;
        to.TrebleHz = from.TrebleHz; to.MidQ = from.MidQ; to.Hiss = from.Hiss; to.Rumble = from.Rumble; to.Limiter = from.Limiter;
    }

    void Undo_Click(object s, RoutedEventArgs e) { var j = _undo.Undo(_project.ToJson()); if (j != null) ApplyJson(j); }
    void Redo_Click(object s, RoutedEventArgs e) { var j = _undo.Redo(_project.ToJson()); if (j != null) ApplyJson(j); }

    // ---------- busy overlay ----------
    async Task<T> RunBusy<T>(string message, Func<IProgress<double>, CancellationToken, Task<T>> work)
    {
        _busyCts = new CancellationTokenSource();
        BusyText.Text = message; BusyBar.Value = 0; Busy.Visibility = Visibility.Visible;
        var progress = new Progress<double>(v => BusyBar.Value = v);
        try { return await work(progress, _busyCts.Token); }
        finally { Busy.Visibility = Visibility.Collapsed; _busyCts.Dispose(); _busyCts = null; }
    }
    void BusyCancel_Click(object s, RoutedEventArgs e) => _busyCts?.Cancel();

    // ---------- import ----------
    async Task<AudioSource?> LoadSource(string path)
    {
        if (_sources.TryGetValue(path, out var cached)) return cached;
        var src = await RunBusy($"Reading {Path.GetFileName(path)}…", (p, ct) => AudioDecoder.DecodeAsync(path, _project.SampleRate, p, ct));
        _sources[path] = src;
        return src;
    }

    async Task ImportFiles(string[] paths, Track? targetTrack, long? atSample)
    {
        bool firstEver = _project.LengthSamples == 0;
        long nextStart = atSample ?? Timeline.Playhead;
        Clip? last = null;
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) continue;
            if (!AudioDecoder.IsSupported(path))
            {
                MessageBox.Show(this, $"\"{Path.GetFileName(path)}\" isn't a file type MixLane can read.\n\nSupported audio: {string.Join(", ", AudioDecoder.AudioExtensions)}\nSupported video: {string.Join(", ", AudioDecoder.VideoExtensions)}", "Can't add this file", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }
            string audioPath = path;
            try
            {
                if (AudioDecoder.IsVideo(path))
                {
                    var conv = S.Conversion;
                    var use = conv;
                    if (conv.AskEveryTime)
                    {
                        var dlg = new FormatDialog(conv, false, Path.GetFileName(path)) { Owner = this };
                        if (dlg.ShowDialog() != true) continue;
                        use = dlg.Result;
                        if (dlg.RememberChoice) { conv.CopyFrom(dlg.Result); conv.AskEveryTime = false; S.Save(); }
                    }
                    string outPath = MediaConverter.OutputPathFor(path, use);
                    audioPath = await RunBusy($"Converting video to {use.Format.ToString().ToUpperInvariant()}: {Path.GetFileName(path)}…",
                        (p, ct) => MediaConverter.ConvertAsync(path, outPath, use, p, ct));
                    HintText.Text = $"Saved converted audio to {outPath}";
                }
                var src = await LoadSource(audioPath);
                if (src == null) continue;

                using (Edit("Add sound"))
                {
                    var track = targetTrack ?? _project.AddTrack(Path.GetFileNameWithoutExtension(audioPath));
                    var clip = new Clip { Name = Path.GetFileNameWithoutExtension(audioPath), SourcePath = audioPath, Start = nextStart, Offset = 0, Length = src.Length, Source = src };
                    track.Clips.Add(clip);
                    last = clip;
                    if (targetTrack != null) nextStart = clip.End; // several files dropped on one lane line up one after another
                    Timeline.SelectTrack(track);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't add \"{Path.GetFileName(path)}\".\n\n{ex.Message}", "MixLane", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        if (last != null)
        {
            Timeline.Select(last);
            if (firstEver) Timeline.ZoomToFit();
        }
        RebuildLaneHeaders();
    }

    void Import_Click(object s, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Multiselect = true, Filter = AudioDecoder.OpenFileFilter, Title = "Add audio or video files" };
        if (d.ShowDialog(this) == true) _ = ImportFiles(d.FileNames, null, null);
    }

    // ---------- save / open / export ----------
    void NewProject_Click(object s, RoutedEventArgs e) { if (ConfirmDiscard()) NewProject(); }
    void SaveProject_Click(object s, RoutedEventArgs e) => SaveProject(false);
    void SaveProjectAs_Click(object s, RoutedEventArgs e) => SaveProject(true);
    bool SaveProject(bool saveAs)
    {
        string? path = _project.FilePath;
        if (saveAs || path == null)
        {
            var d = new SaveFileDialog { Filter = "MixLane mix (*.mixlane)|*.mixlane", FileName = _project.Name, DefaultExt = ".mixlane" };
            if (d.ShowDialog(this) != true) return false;
            path = d.FileName;
            _project.Name = Path.GetFileNameWithoutExtension(path);
        }
        try { _project.Save(path); _dirty = false; UpdateTitle(); HintText.Text = $"Saved {path}"; return true; }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't save: " + ex.Message, "MixLane", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }

    async void OpenProject_Click(object s, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var d = new OpenFileDialog { Filter = "MixLane mix (*.mixlane)|*.mixlane" };
        if (d.ShowDialog(this) != true) return;
        try
        {
            var p = Project.FromJson(File.ReadAllText(d.FileName));
            p.FilePath = d.FileName;
            SetProject(p);
            var missing = new List<string>();
            foreach (var t in p.Tracks) foreach (var c in t.Clips)
            {
                if (!File.Exists(c.SourcePath)) { missing.Add(c.SourcePath); continue; }
                try { c.Source = await LoadSource(c.SourcePath); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { missing.Add($"{c.SourcePath} ({ex.Message})"); }
            }
            p.Touch();
            RebuildLaneHeaders();
            Timeline.ZoomToFit();
            if (missing.Count > 0) MessageBox.Show(this, "Some sound files couldn't be found or read. Their pieces are shown but won't play:\n\n" + string.Join("\n", missing.Distinct()), "MixLane", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't open that mix: " + ex.Message, "MixLane", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    async void Export_Click(object s, RoutedEventArgs e)
    {
        if (_project.LengthSamples == 0) { MessageBox.Show(this, "There's nothing in the mix yet.", "MixLane"); return; }
        var dlg = new FormatDialog(S.Conversion, true) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var fmt = dlg.Result.Format;
        var ext = MediaConverter.ExtensionFor(fmt);
        var sd = new SaveFileDialog { Filter = $"{fmt.ToString().ToUpperInvariant()} file (*{ext})|*{ext}", FileName = _project.Name + ext, DefaultExt = ext };
        if (sd.ShowDialog(this) != true) return;
        _player?.Pause(); UpdatePlayButton();
        try
        {
            await RunBusy($"Exporting mix to {Path.GetFileName(sd.FileName)}…", (p, ct) => Task.Run(() => { Exporter.Export(_project, sd.FileName, fmt, dlg.Result.BitrateKbps, p, ct); return true; }, ct));
            HintText.Text = $"Exported {sd.FileName}";
            MessageBox.Show(this, $"Done! Your mix was saved to:\n{sd.FileName}", "MixLane", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { try { File.Delete(sd.FileName); } catch { } }
        catch (Exception ex) { MessageBox.Show(this, "Export failed: " + ex.Message, "MixLane", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void Settings_Click(object s, RoutedEventArgs e)
    {
        var w = new SettingsWindow(S) { Owner = this };
        if (w.ShowDialog() == true) { Timeline.Settings = S; ApplyLabels(); }
    }
    void Exit_Click(object s, RoutedEventArgs e) => Close();

    // ---------- transport ----------
    void OnUserMovedPlayhead(long sample)
    {
        Playhead.Playhead = sample;
        UpdatePositionText(sample);
        if (_player != null) _player.Position = sample;
    }

    void OnTick()
    {
        if (_player == null) return;
        if (_player.IsPlaying)
        {
            long pos = _player.Position;
            Timeline.SetPlayheadSilent(pos);
            Playhead.Playhead = pos;
            UpdatePositionText(pos);
            Timeline.EnsurePlayheadVisible();
            var r = _player.Renderer;
            MeterText.Text = $"L {TimeUtil.LinearToDb(r.PeakL),6:0.0}  R {TimeUtil.LinearToDb(r.PeakR),6:0.0} dB";
            if (!r.Loop && pos >= _project.LengthSamples + _project.SampleRate / 4) { _player.Pause(); UpdatePlayButton(); }
        }
    }

    void UpdatePositionText(long sample)
    {
        if (!PositionBox.IsKeyboardFocused) PositionBox.Text = TimeUtil.FormatSamples(sample, _project.SampleRate);
        PositionSamples.Text = $"sample {sample:N0}";
    }
    void UpdatePlayButton() { PlayBtn.Content = _player?.IsPlaying == true ? "❚❚  Pause" : "▶  Play"; if (_player?.IsPlaying != true) MeterText.Text = ""; }
    void UpdateLoopRange() { if (_player == null) return; _player.Renderer.LoopStart = 0; _player.Renderer.LoopEnd = _project.LengthSamples; }

    void Play_Click(object s, RoutedEventArgs e) => TogglePlay();
    void TogglePlay()
    {
        if (_player == null) return;
        if (_player.IsPlaying) _player.Pause();
        else
        {
            _playStartPos = Timeline.Playhead;
            _player.Position = Timeline.Playhead;
            UpdateLoopRange();
            try { _player.Play(); InfoText.Text = $"{_project.SampleRate:N0} Hz · {_player.DeviceInfo}"; }
            catch (Exception ex) { MessageBox.Show(this, "Couldn't open the sound device: " + ex.Message, "MixLane", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        UpdatePlayButton();
    }
    void Stop_Click(object s, RoutedEventArgs e) => StopPlayback();
    void StopPlayback()
    {
        if (_player == null) return;
        bool wasPlaying = _player.IsPlaying;
        _player.Stop();
        if (wasPlaying) Timeline.Playhead = _playStartPos;
        UpdatePlayButton();
    }
    void GoStart_Click(object s, RoutedEventArgs e) { Timeline.Playhead = 0; Timeline.ViewStart = 0; }
    void Loop_Click(object s, RoutedEventArgs e) { if (_player != null) { _player.Renderer.Loop = LoopBtn.IsChecked == true; UpdateLoopRange(); } }
    void PositionBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var v = TimeUtil.ParseToSamples(PositionBox.Text, _project.SampleRate);
        if (v != null) { Timeline.Playhead = v.Value; Timeline.EnsurePlayheadVisible(); }
        Timeline.Focus(); e.Handled = true;
    }

    // ---------- view sync ----------
    void SyncView()
    {
        long visible = Timeline.VisibleSamples;
        long total = Timeline.TotalSamples;
        HScroll.Minimum = 0; HScroll.Maximum = Math.Max(0, total - visible); HScroll.ViewportSize = visible; HScroll.Value = Timeline.ViewStart;
        HScroll.SmallChange = visible / 10.0; HScroll.LargeChange = visible * 0.9;
        double viewport = Math.Max(1, Timeline.ActualHeight - TimelineControl.RulerHeight);
        VScroll.Minimum = 0; VScroll.Maximum = Math.Max(0, Timeline.ContentHeight - viewport); VScroll.ViewportSize = viewport; VScroll.Value = Timeline.VerticalOffset;
        LaneHeadersOffset.Y = -Timeline.VerticalOffset;
        Playhead.InvalidateVisual();
        double spp = Timeline.SamplesPerPixel;
        ZoomText.Text = spp >= 1 ? $"1 px = {spp:0.#} samples ({spp / _project.SampleRate * 1000:0.###} ms)" : $"1 sample = {1 / spp:0.#} px";
    }
    void HScroll_Scroll(object s, ScrollEventArgs e) => Timeline.ViewStart = (long)e.NewValue;
    void VScroll_Scroll(object s, ScrollEventArgs e) => Timeline.VerticalOffset = e.NewValue;

    void ZoomIn_Click(object s, RoutedEventArgs e) => Timeline.ZoomBy(1 / 1.5);
    void ZoomOut_Click(object s, RoutedEventArgs e) => Timeline.ZoomBy(1.5);
    void ZoomFit_Click(object s, RoutedEventArgs e) => Timeline.ZoomToFit();
    void ZoomSample_Click(object s, RoutedEventArgs e)
    {
        Timeline.SetZoom(0.1, Timeline.ActualWidth / 2);
        Timeline.ViewStart = Math.Max(0, Timeline.Playhead - (long)(Timeline.ActualWidth / 2 * 0.1));
    }
    void CollapseFx_Click(object s, RoutedEventArgs e) { foreach (var t in _project.Tracks) { t.EffectsExpanded = false; t.NotifyLayoutChanged(); } _project.Touch(); }
    void ExpandFx_Click(object s, RoutedEventArgs e) { foreach (var t in _project.Tracks) { t.EffectsExpanded = true; t.NotifyLayoutChanged(); } _project.Touch(); }
    void ToggleBottom_Click(object s, RoutedEventArgs e) => ShowBottom(BottomPanelMenu.IsChecked);
    void ShowBottom(bool show) { BottomPanelMenu.IsChecked = show; BottomRow.Height = show ? new GridLength(270) : new GridLength(0); }

    // ---------- selection / editing ----------
    void OnSelectionChanged()
    {
        var clip = Timeline.SelectedClips.Count == 1 ? Timeline.SelectedClips.First() : null;
        Inspector.Show(clip, Timeline.SelectedEffect, _project.SampleRate);
        if (clip != null && Timeline.SelectedEffect != null) BottomTabs.SelectedItem = ClipTab;
        foreach (var (t, b) in _headerByTrack) b.Background = t == Timeline.SelectedTrack ? new SolidColorBrush(Color.FromArgb(0x30, 0x4F, 0xA3, 0xFF)) : Brushes.Transparent;
        SplitMenu.IsEnabled = Timeline.SelectedClips.Count > 0;
        CrossfadeMenu.IsEnabled = clip != null;
    }

    void Split_Click(object s, RoutedEventArgs e) => SplitAtPlayhead();
    void SplitAtPlayhead()
    {
        var targets = Timeline.SelectedClips.Where(c => Timeline.Playhead > c.Start && Timeline.Playhead < c.End).ToList();
        if (targets.Count == 0)
        {
            // Nothing selected under the playhead: cut whichever clips sit under it on the selected lane.
            if (Timeline.SelectedTrack != null) targets = Timeline.SelectedTrack.Clips.Where(c => Timeline.Playhead > c.Start && Timeline.Playhead < c.End).ToList();
            if (targets.Count == 0) { HintText.Text = "Put the playhead inside a piece (and select it) to cut it there."; return; }
        }
        Clip? newest = null;
        using (Edit("Cut"))
        {
            foreach (var c in targets)
            {
                var t = Timeline.TrackOf(c)!;
                var right = c.SplitAt(Timeline.Playhead);
                if (right != null) { t.Clips.Insert(t.Clips.IndexOf(c) + 1, right); newest = right; }
            }
        }
        Timeline.Select(newest);
        HintText.Text = $"Cut at {TimeUtil.FormatSamples(Timeline.Playhead, _project.SampleRate)} (sample {Timeline.Playhead:N0}).";
    }

    void Delete_Click(object s, RoutedEventArgs e) => DeleteSelected();
    void DeleteSelected()
    {
        if (Timeline.SelectedEffect != null && Timeline.SelectedEffectClip != null)
        {
            using (Edit("Remove effect")) Timeline.SelectedEffectClip.Effects.Remove(Timeline.SelectedEffect);
            var c = Timeline.SelectedEffectClip;
            Timeline.SelectEffect(c, null);
            return;
        }
        if (Timeline.SelectedClips.Count == 0) return;
        using (Edit("Delete"))
            foreach (var c in Timeline.SelectedClips.ToList()) Timeline.TrackOf(c)?.Clips.Remove(c);
        Timeline.Select(null);
    }

    void Duplicate_Click(object s, RoutedEventArgs e)
    {
        if (Timeline.SelectedClips.Count == 0) return;
        Clip? last = null;
        using (Edit("Duplicate"))
            foreach (var c in Timeline.SelectedClips.ToList())
            {
                var d = c.Clone(); d.Start = c.End; d.Name = c.Name;
                Timeline.TrackOf(c)?.Clips.Add(d); last = d;
            }
        Timeline.Select(last);
    }

    void SelectAll_Click(object s, RoutedEventArgs e)
    {
        Timeline.Select(null);
        foreach (var t in _project.Tracks) foreach (var c in t.Clips) Timeline.Select(c, true);
    }

    void Crossfade_Click(object s, RoutedEventArgs e) => CrossfadeSelected();
    void CrossfadeSelected()
    {
        if (Timeline.SelectedClips.Count != 1) { HintText.Text = "Select one piece first; it will blend into the next piece on the same lane."; return; }
        var a = Timeline.SelectedClips.First();
        var t = Timeline.TrackOf(a)!;
        var b = t.Clips.Where(c => c != a && c.Start >= a.Start && c.Start > a.Start).OrderBy(c => c.Start).FirstOrDefault();
        if (b == null) { HintText.Text = "There's no piece after this one on the same lane to blend into."; return; }
        long defaultOverlap = Math.Min(_project.SampleRate, Math.Min(a.Length, b.Length) / 2);
        using (Edit("Crossfade"))
        {
            long overlap = a.End - b.Start;
            if (overlap <= 0) { b.Start = Math.Max(0, a.End - defaultOverlap); overlap = a.End - b.Start; }
            a.Effects.Add(Models.Effect.Create(EffectKind.FadeOut, a.Length - overlap, overlap));
            b.Effects.Add(Models.Effect.Create(EffectKind.FadeIn, 0, overlap));
            t.EffectsExpanded = true; t.NotifyLayoutChanged();
        }
        Timeline.Select(a);
        HintText.Text = $"Blended over {TimeUtil.FormatSamples(a.End - b.Start, _project.SampleRate)}. Drag the fade edges in the effects lane to change it.";
    }

    void AddEffect(Clip c, EffectKind kind)
    {
        long twoSec = Math.Min(c.Length, _project.SampleRate * 2L);
        var fx = kind switch
        {
            EffectKind.FadeIn => Models.Effect.Create(kind, 0, twoSec),
            EffectKind.FadeOut => Models.Effect.Create(kind, c.Length - twoSec, twoSec),
            _ => Models.Effect.Create(kind, 0, c.Length)
        };
        using (Edit("Add " + fx.Def.Label))
        {
            c.Effects.Add(fx);
            var t = Timeline.TrackOf(c); if (t != null) { t.EffectsExpanded = true; t.NotifyLayoutChanged(); }
        }
        Timeline.SelectEffect(c, fx);
        BottomTabs.SelectedItem = ClipTab; ShowBottom(true);
    }

    void AddTrack_Click(object s, RoutedEventArgs e) { using (Edit("Add lane")) _project.AddTrack(); Timeline.SelectTrack(_project.Tracks.Last()); RebuildLaneHeaders(); }
    void RemoveTrack_Click(object s, RoutedEventArgs e) => RemoveTrack(Timeline.SelectedTrack);
    void RemoveTrack(Track? t)
    {
        if (t == null) return;
        if (t.Clips.Count > 0 && MessageBox.Show(this, $"Remove lane \"{t.Name}\" and the {t.Clips.Count} piece(s) on it?", "MixLane", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        using (Edit("Remove lane")) _project.Tracks.Remove(t);
        Timeline.Select(null); Timeline.SelectTrack(_project.Tracks.FirstOrDefault()); RebuildLaneHeaders();
    }

    // ---------- context menus ----------
    MenuItem Item(string header, Action a, string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
        mi.Click += (_, _) => a();
        return mi;
    }
    MenuItem AddEffectSubmenu(Clip c)
    {
        var root = new MenuItem { Header = "Add effect or transition" };
        var trans = new MenuItem { Header = "Transitions (fades, sweeps)" };
        var tone = new MenuItem { Header = "Sound shaping & clean-up" };
        foreach (var d in EffectCatalog.All)
        {
            var mi = Item(d.Label, () => AddEffect(c, d.Kind));
            mi.ToolTip = d.Help;
            (d.IsTransition ? trans : tone).Items.Add(mi);
        }
        root.Items.Add(trans); root.Items.Add(tone);
        return root;
    }
    void ShowAddEffectMenu(Clip c)
    {
        var m = new ContextMenu();
        foreach (var it in AddEffectSubmenu(c).Items.Cast<MenuItem>().ToList()) { (it.Parent as MenuItem)?.Items.Remove(it); m.Items.Add(it); }
        m.IsOpen = true;
    }

    void ShowTimelineMenu(Point p, Clip? clip, Effect? fx, Track? track)
    {
        var m = new ContextMenu();
        if (fx != null && clip != null)
        {
            m.Items.Add(Item(fx.Enabled ? "Turn this effect off" : "Turn this effect on", () => { using (Edit("Toggle effect")) fx.Enabled = !fx.Enabled; Inspector.Refresh(); }));
            m.Items.Add(Item("Cover the whole piece", () => { using (Edit("Effect region")) { fx.RegionStart = 0; fx.RegionLength = clip.Length; } Inspector.Refresh(); }));
            m.Items.Add(Item("Remove this effect", () => { using (Edit("Remove effect")) clip.Effects.Remove(fx); Timeline.SelectEffect(clip, null); }, "Del"));
            m.Items.Add(new Separator());
        }
        if (clip != null)
        {
            bool inside = Timeline.Playhead > clip.Start && Timeline.Playhead < clip.End;
            m.Items.Add(Item(Terminology.L("Split") + (inside ? "" : "  (move the playhead inside the piece first)"), SplitAtPlayhead, "S", inside));
            m.Items.Add(Item("Blend into next piece (crossfade)", CrossfadeSelected, "X"));
            m.Items.Add(AddEffectSubmenu(clip));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Move playhead to start of piece", () => Timeline.Playhead = clip.Start));
            m.Items.Add(Item("Move playhead to end of piece", () => Timeline.Playhead = clip.End));
            m.Items.Add(Item("Snap piece to playhead", () => { using (Edit("Move")) clip.Start = Timeline.Playhead; Inspector.Refresh(); }));
            m.Items.Add(Item("Move piece to its own new lane", () => { using (Edit("Move to lane")) { Timeline.TrackOf(clip)?.Clips.Remove(clip); var t = _project.AddTrack(clip.Name); t.Clips.Add(clip); } RebuildLaneHeaders(); }));
            m.Items.Add(Item("Duplicate", () => Duplicate_Click(this, new RoutedEventArgs()), "Ctrl+D"));
            m.Items.Add(Item("Delete", DeleteSelected, "Del"));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Exact numbers & effects…", () => { BottomTabs.SelectedItem = ClipTab; ShowBottom(true); }));
        }
        if (track != null)
        {
            if (m.Items.Count > 0) m.Items.Add(new Separator());
            m.Items.Add(Item(track.EffectsExpanded ? "Collapse this lane's effects" : "Expand this lane's effects", () => { track.EffectsExpanded = !track.EffectsExpanded; track.NotifyLayoutChanged(); _project.Touch(); }));
            m.Items.Add(Item("Add empty lane", () => AddTrack_Click(this, new RoutedEventArgs()), "Ctrl+T"));
            m.Items.Add(Item("Remove this lane", () => RemoveTrack(track)));
        }
        if (m.Items.Count == 0) m.Items.Add(Item("Add audio or video files…", () => Import_Click(this, new RoutedEventArgs()), "Ctrl+I"));
        m.PlacementTarget = Timeline; m.IsOpen = true;
    }

    // ---------- lane headers ----------
    void RebuildLaneHeaders()
    {
        LaneHeaders.Children.Clear();
        _headerByTrack.Clear();
        foreach (var t in _project.Tracks)
        {
            var b = BuildLaneHeader(t);
            _headerByTrack[t] = b;
            LaneHeaders.Children.Add(b);
        }
        OnSelectionChanged();
        SyncView();
    }
    void RefreshLaneHeaders()
    {
        if (_headerByTrack.Count != _project.Tracks.Count || _project.Tracks.Any(t => !_headerByTrack.ContainsKey(t))) { RebuildLaneHeaders(); return; }
        foreach (var (t, b) in _headerByTrack)
        {
            if (Math.Abs(b.Height - t.TotalHeight) > 0.5) { RebuildLaneHeaders(); return; }
            if (b.Tag is TextBlock fxLabel) fxLabel.Text = FxLabel(t);
        }
    }
    static string FxLabel(Track t)
    {
        int n = t.Clips.Sum(c => c.Effects.Count);
        return (t.EffectsExpanded ? "▾ " : "▸ ") + Terminology.L("Effects") + (n > 0 ? $" ({n})" : "");
    }

    Border BuildLaneHeader(Track t)
    {
        var color = Palette[t.ColorIndex % Palette.Length];
        var border = new Border { Height = t.TotalHeight, BorderBrush = Res("Border"), BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent, ClipToBounds = true };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(t.Height) });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(t.EffectsLaneHeight) });
        var strip = new Border { Background = new SolidColorBrush(color) };
        Grid.SetRowSpan(strip, 2);
        g.Children.Add(strip);

        var body = new StackPanel { Margin = new Thickness(6, 2, 6, 0) };
        Grid.SetColumn(body, 1);
        g.Children.Add(body);

        var top = new DockPanel();
        var name = new TextBox { Text = t.Name, Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        Tip(name, "Lane name. Click to rename.");
        void CommitName() { if (name.Text != t.Name) using (Edit("Rename lane")) t.Name = name.Text; }
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitName(); Timeline.Focus(); } };
        name.LostFocus += (_, _) => CommitName();
        name.GotFocus += (_, _) => Timeline.SelectTrack(t);
        top.Children.Add(name);
        body.Children.Add(top);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var mute = new ToggleButton { Content = Terminology.L("Mute"), IsChecked = t.Mute, FontSize = 11, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 0) };
        Tip(mute, Terminology.Help("Mute"));
        mute.Click += (_, _) => { t.Mute = mute.IsChecked == true; MarkDirty(); };
        var solo = new ToggleButton { Content = Terminology.L("Solo"), IsChecked = t.Solo, FontSize = 11, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 0) };
        Tip(solo, Terminology.Help("Solo"));
        solo.Click += (_, _) => { t.Solo = solo.IsChecked == true; MarkDirty(); };
        btns.Children.Add(mute); btns.Children.Add(solo);
        body.Children.Add(btns);

        if (t.Height >= 80)
        {
            body.Children.Add(MiniSlider(Terminology.L("Volume"), Terminology.Help("Volume"), -60, 12, t.GainDb, v => t.GainDb = v, v => TimeUtil.FormatDb(v)));
            if (t.Height >= 92)
                body.Children.Add(MiniSlider(Terminology.L("Pan"), Terminology.Help("Pan"), -100, 100, t.Pan, v => t.Pan = v, v => v == 0 ? "centre" : v < 0 ? $"L {-v:0}" : $"R {v:0}"));
        }

        var fxLabel = new TextBlock { Text = FxLabel(t), FontSize = 10.5, Foreground = Res("FgDim"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
        Tip(fxLabel, "Click to collapse or expand the effects lane. Collapsed lanes still show a strip where effects are attached.");
        fxLabel.MouseLeftButtonDown += (_, _) => { t.EffectsExpanded = !t.EffectsExpanded; t.NotifyLayoutChanged(); _project.Touch(); };
        Grid.SetRow(fxLabel, 1); Grid.SetColumn(fxLabel, 1);
        g.Children.Add(fxLabel);
        border.Tag = fxLabel;

        border.MouseLeftButtonDown += (_, _) => { Timeline.SelectTrack(t); Timeline.Focus(); };
        border.MouseRightButtonDown += (_, e) =>
        {
            Timeline.SelectTrack(t);
            var m = new ContextMenu();
            m.Items.Add(Item("Rename", () => { name.Focus(); name.SelectAll(); }));
            m.Items.Add(Item("Move lane up", () => MoveTrack(t, -1), enabled: _project.Tracks.IndexOf(t) > 0));
            m.Items.Add(Item("Move lane down", () => MoveTrack(t, 1), enabled: _project.Tracks.IndexOf(t) < _project.Tracks.Count - 1));
            var h = new MenuItem { Header = "Lane height" };
            foreach (var (label, hv) in new[] { ("Compact", 64), ("Normal", 96), ("Tall", 140), ("Very tall", 200) })
                h.Items.Add(Item(label, () => { t.Height = hv; t.NotifyLayoutChanged(); _project.Touch(); }));
            m.Items.Add(h);
            m.Items.Add(Item(t.EffectsExpanded ? "Collapse effects lane" : "Expand effects lane", () => { t.EffectsExpanded = !t.EffectsExpanded; t.NotifyLayoutChanged(); _project.Touch(); }));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Remove lane", () => RemoveTrack(t)));
            m.IsOpen = true; e.Handled = true;
        };
        border.Child = g;
        return border;
    }

    FrameworkElement MiniSlider(string label, string help, double min, double max, double value, Action<double> set, Func<double, string> fmt)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        var l = new TextBlock { Text = label, FontSize = 10.5, Foreground = Res("FgDim"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var s = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0), Height = 18 };
        var v = new TextBlock { Text = fmt(value), FontSize = 10.5, Foreground = Res("FgDim"), VerticalAlignment = VerticalAlignment.Center };
        Tip(g, help + " Double-click the slider to reset.");
        IDisposable? scope = null;
        s.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => scope ??= Edit(label)));
        s.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { scope?.Dispose(); scope = null; }));
        s.ValueChanged += (_, e) => { set(e.NewValue); v.Text = fmt(e.NewValue); MarkDirty(); };
        s.MouseDoubleClick += (_, _) => { using (Edit(label)) s.Value = 0; };
        Grid.SetColumn(s, 1); Grid.SetColumn(v, 2);
        g.Children.Add(l); g.Children.Add(s); g.Children.Add(v);
        return g;
    }

    void MoveTrack(Track t, int dir)
    {
        int i = _project.Tracks.IndexOf(t), j = i + dir;
        if (j < 0 || j >= _project.Tracks.Count) return;
        using (Edit("Reorder lanes")) _project.Tracks.Move(i, j);
        RebuildLaneHeaders();
    }

    // ---------- labels / mode ----------
    void ToggleMode_Click(object s, RoutedEventArgs e)
    {
        S.Mode = S.Mode == UserMode.Beginner ? UserMode.Advanced : UserMode.Beginner;
        S.Save();
        ApplyLabels();
    }
    void Snap_Click(object s, RoutedEventArgs e) { S.SnapToClipEdges = SnapBtn.IsChecked == true; S.Save(); }

    void ApplyLabels()
    {
        bool adv = S.IsAdvanced;
        ModeBtn.IsChecked = adv; ModeBtn.Content = adv ? "Industry terms" : "Plain language";
        ModeMenu.IsChecked = adv;
        SnapBtn.IsChecked = S.SnapToClipEdges; SnapBtn.Content = Terminology.L("Snap"); Tip(SnapBtn, Terminology.Help("Snap"));
        ClipTab.Header = Terminology.L("ClipPanel"); MasterTab.Header = Terminology.L("MasterPanel");
        SplitMenu.Header = Terminology.L("Split");
        LanesHeaderText.Text = Terminology.L("Lane") + "s";
        AddFxMenu.Items.Clear();
        foreach (var d in EffectCatalog.All)
        {
            var mi = Item(d.Label, () => { var c = Timeline.SelectedClips.FirstOrDefault(); if (c != null) AddEffect(c, d.Kind); else HintText.Text = "Select a piece first."; });
            mi.ToolTip = d.Help;
            AddFxMenu.Items.Add(mi);
        }
        Inspector.Refresh(); Master.Refresh(); RebuildLaneHeaders(); Timeline.Redraw();
    }

    // ---------- keyboard ----------
    void OnKey(object s, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox && e.Key != Key.Escape) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        int rate = _project.SampleRate;
        long nudge = ctrl ? 1 : shift ? rate / 10 : rate / 1000; // 1 sample / 100 ms / 1 ms
        bool handled = true;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Escape: StopPlayback(); Timeline.Focus(); break;
            case Key.Home: Timeline.Playhead = 0; Timeline.ViewStart = 0; break;
            case Key.End: Timeline.Playhead = _project.LengthSamples; Timeline.EnsurePlayheadVisible(); break;
            case Key.S when !ctrl: SplitAtPlayhead(); break;
            case Key.S when ctrl && shift: SaveProject(true); break;
            case Key.S when ctrl: SaveProject(false); break;
            case Key.X when !ctrl: CrossfadeSelected(); break;
            case Key.Delete or Key.Back: DeleteSelected(); break;
            case Key.Z when ctrl: Undo_Click(this, new RoutedEventArgs()); break;
            case Key.Y when ctrl: Redo_Click(this, new RoutedEventArgs()); break;
            case Key.D when ctrl: Duplicate_Click(this, new RoutedEventArgs()); break;
            case Key.A when ctrl: SelectAll_Click(this, new RoutedEventArgs()); break;
            case Key.I when ctrl: Import_Click(this, new RoutedEventArgs()); break;
            case Key.E when ctrl: Export_Click(this, new RoutedEventArgs()); break;
            case Key.O when ctrl: OpenProject_Click(this, new RoutedEventArgs()); break;
            case Key.N when ctrl: NewProject_Click(this, new RoutedEventArgs()); break;
            case Key.T when ctrl: AddTrack_Click(this, new RoutedEventArgs()); break;
            case Key.OemPlus or Key.Add when ctrl: Timeline.ZoomBy(1 / 1.5); break;
            case Key.OemMinus or Key.Subtract when ctrl: Timeline.ZoomBy(1.5); break;
            case Key.D0 or Key.NumPad0 when ctrl: Timeline.ZoomToFit(); break;
            case Key.D9 or Key.NumPad9 when ctrl: ZoomSample_Click(this, new RoutedEventArgs()); break;
            case Key.Left or Key.Right:
            {
                long d = e.Key == Key.Left ? -nudge : nudge;
                if (Timeline.SelectedEffect != null)
                {
                    using (Edit("Nudge effect")) Timeline.SelectedEffect.RegionStart += d;
                }
                else if (Timeline.SelectedClips.Count > 0)
                {
                    using (Edit("Nudge")) foreach (var c in Timeline.SelectedClips) c.Start += d;
                    var f = Timeline.SelectedClips.First();
                    HintText.Text = $"Moved by {(d > 0 ? "+" : "")}{d:N0} sample(s). {Terminology.L("Start")}: {TimeUtil.FormatSamples(f.Start, rate)} (sample {f.Start:N0})";
                }
                else { Timeline.Playhead = Math.Max(0, Timeline.Playhead + d); Timeline.EnsurePlayheadVisible(); }
                Inspector.Refresh();
                break;
            }
            case Key.Up or Key.Down when Timeline.SelectedClips.Count == 1:
            {
                var c = Timeline.SelectedClips.First(); var t = Timeline.TrackOf(c)!;
                int i = _project.Tracks.IndexOf(t) + (e.Key == Key.Up ? -1 : 1);
                if (i >= 0 && i < _project.Tracks.Count) { using (Edit("Move to lane")) { t.Clips.Remove(c); _project.Tracks[i].Clips.Add(c); } Timeline.SelectTrack(_project.Tracks[i]); }
                break;
            }
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    // ---------- help ----------
    void Shortcuts_Click(object s, RoutedEventArgs e) => MessageBox.Show(this,
        "Space  Play / pause\nEsc  Stop (playhead returns to where play started)\nHome / End  Jump to start / end\n" +
        "Click on a lane  Move playhead there\nDrag a piece  Move it (Alt = ignore snapping)\nDrag a piece's edge  Trim it\nDrag between lanes  Move to another lane\n" +
        "← →  Nudge selected piece by 1 ms  (Shift: 100 ms, Ctrl: 1 sample)\n↑ ↓  Move selected piece to the lane above / below\n" +
        "S  Cut selected piece at the playhead\nX  Blend into next piece (crossfade)\nDel  Delete selected piece or effect\nCtrl+D  Duplicate\nCtrl+A  Select all\n" +
        "Ctrl+Z / Ctrl+Y  Undo / redo\nCtrl+I  Add files\nCtrl+E  Export\nCtrl+T  Add lane\n" +
        "Ctrl + mouse wheel  Zoom\nShift + wheel  Scroll sideways\nCtrl + = / -  Zoom in / out\nCtrl+0  Fit whole mix\nCtrl+9  Zoom to individual samples at the playhead",
        "Keyboard shortcuts");
    void About_Click(object s, RoutedEventArgs e) => MessageBox.Show(this,
        "MixLane 0.1 — a lightweight lane-based music mixer.\n\nAny audio or video file in, exact sample-level placement, effects and transitions attached to pieces, and a plain-language or industry-term interface.",
        "About MixLane");
}
