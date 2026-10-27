using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MixLane.Core;
using MixLane.Models;

namespace MixLane.UI;

/// <summary>
/// The swim-lane editor. Draws the time ruler, every lane, every clip's waveform, and each lane's effects sub-lane,
/// and handles all mouse editing: move, trim, select, scrub, zoom. Everything is positioned in samples, and at high zoom
/// individual samples are visible, so placement is exact.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    public const double RulerHeight = 28;
    const double EdgeZone = 7;
    const double SnapPixels = 8;

    Project? _project;
    AppSettings _settings = new();
    double _spp = 200;        // samples per pixel (zoom)
    long _viewStart;          // first visible sample
    double _vOffset;          // vertical scroll in px
    long _playhead;

    public readonly HashSet<Clip> SelectedClips = new();
    public Effect? SelectedEffect { get; private set; }
    public Clip? SelectedEffectClip { get; private set; }
    public Track? SelectedTrack { get; private set; }

    public event Action<long>? PlayheadChanged;
    public event Action? SelectionChanged;
    public event Action? ViewChanged;
    public event Action? EditStarting;
    public event Action<string>? EditCommitted;
    public event Action<string>? Hint;
    public event Action<Point, Clip?, Effect?, Track?>? ContextMenuRequested;
    public event Action<string[], Track?, long>? FilesDropped;
    public event Action<Clip>? ClipActivated;

    static readonly Typeface Font = new("Segoe UI");
    static readonly Color[] Palette =
    {
        Color.FromRgb(0x4F, 0xA3, 0xFF), Color.FromRgb(0x6C, 0xD4, 0x9A), Color.FromRgb(0xF2, 0xB1, 0x5B), Color.FromRgb(0xE0, 0x7A, 0xA5),
        Color.FromRgb(0xA9, 0x8B, 0xFF), Color.FromRgb(0x5C, 0xD6, 0xD6), Color.FromRgb(0xF0, 0xE0, 0x6A), Color.FromRgb(0xFF, 0x8C, 0x6B)
    };

    public TimelineControl()
    {
        Focusable = true;
        AllowDrop = true;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
    }

    // ----- public state -----
    public Project? Project
    {
        get => _project;
        set
        {
            if (_project != null) _project.Changed -= OnProjectChanged;
            _project = value;
            if (_project != null) _project.Changed += OnProjectChanged;
            SelectedClips.Clear(); SelectedEffect = null; SelectedTrack = _project?.Tracks.FirstOrDefault();
            InvalidateVisual(); ViewChanged?.Invoke();
        }
    }
    public AppSettings Settings { get => _settings; set { _settings = value; InvalidateVisual(); } }
    void OnProjectChanged() { InvalidateVisual(); ViewChanged?.Invoke(); }

    public int Rate => _project?.SampleRate ?? 48000;
    public double SamplesPerPixel => _spp;
    public long ViewStart { get => _viewStart; set { _viewStart = Math.Max(0, value); InvalidateVisual(); ViewChanged?.Invoke(); } }
    public double VerticalOffset { get => _vOffset; set { _vOffset = Math.Max(0, Math.Min(value, Math.Max(0, ContentHeight - (ActualHeight - RulerHeight)))); InvalidateVisual(); ViewChanged?.Invoke(); } }
    public long Playhead { get => _playhead; set { _playhead = Math.Max(0, value); PlayheadChanged?.Invoke(_playhead); } }
    /// <summary>Update the playhead from playback without triggering a seek.</summary>
    public void SetPlayheadSilent(long s) => _playhead = Math.Max(0, s);
    public long VisibleSamples => (long)(Math.Max(1, ActualWidth) * _spp);
    public double ContentHeight { get { double h = 0; if (_project != null) foreach (var t in _project.Tracks) h += t.TotalHeight; return h + 40; } }
    /// <summary>Scrollable extent in samples: the project length plus some working room.</summary>
    public long TotalSamples => Math.Max((_project?.LengthSamples ?? 0) + VisibleSamples, VisibleSamples * 2);

    public double SampleToX(long s) => (s - _viewStart) / _spp;
    public long XToSample(double x) => (long)Math.Round(_viewStart + x * _spp);

    public void SetZoom(double newSpp, double anchorX)
    {
        newSpp = Math.Clamp(newSpp, 0.02, 200_000);
        long anchorSample = XToSample(anchorX);
        _spp = newSpp;
        _viewStart = Math.Max(0, (long)Math.Round(anchorSample - anchorX * _spp));
        InvalidateVisual(); ViewChanged?.Invoke();
    }
    public void ZoomBy(double factor) => SetZoom(_spp * factor, ActualWidth / 2);
    public void ZoomToFit()
    {
        long len = _project?.LengthSamples ?? 0;
        if (len <= 0 || ActualWidth < 10) return;
        _viewStart = 0;
        _spp = len / (ActualWidth * 0.95);
        InvalidateVisual(); ViewChanged?.Invoke();
    }
    public void EnsurePlayheadVisible()
    {
        double x = SampleToX(_playhead);
        if (x < 0 || x > ActualWidth - 20) ViewStart = Math.Max(0, _playhead - (long)(ActualWidth * 0.1 * _spp));
    }

    public void Select(Clip? clip, bool add = false)
    {
        if (!add) SelectedClips.Clear();
        if (clip != null) { if (!SelectedClips.Add(clip) && add) SelectedClips.Remove(clip); }
        SelectedEffect = null; SelectedEffectClip = null;
        SelectionChanged?.Invoke(); InvalidateVisual();
    }
    public void SelectEffect(Clip clip, Effect? fx)
    {
        SelectedClips.Clear(); SelectedClips.Add(clip);
        SelectedEffect = fx; SelectedEffectClip = fx == null ? null : clip;
        SelectionChanged?.Invoke(); InvalidateVisual();
    }
    public void SelectTrack(Track? t) { SelectedTrack = t; SelectionChanged?.Invoke(); InvalidateVisual(); }
    public void Redraw() => InvalidateVisual();

    // ----- layout -----
    readonly record struct LaneRect(Track Track, double Top, double AudioHeight, double FxTop, double FxHeight);
    List<LaneRect> Lanes()
    {
        var list = new List<LaneRect>();
        if (_project == null) return list;
        double y = RulerHeight - _vOffset;
        foreach (var t in _project.Tracks)
        {
            list.Add(new LaneRect(t, y, t.Height, y + t.Height, t.EffectsLaneHeight));
            y += t.TotalHeight;
        }
        return list;
    }
    public Track? TrackAtY(double y) { foreach (var l in Lanes()) if (y >= l.Top && y < l.Top + l.AudioHeight + l.FxHeight) return l.Track; return null; }
    public double TrackTop(Track t) { foreach (var l in Lanes()) if (l.Track == t) return l.Top; return RulerHeight; }

    // ----- rendering -----
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1B, 0x1C, 0x20)), null, new Rect(0, 0, w, h));
        if (_project == null) return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var lanes = Lanes();
        var laneBg = new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x29));
        var laneBgAlt = new SolidColorBrush(Color.FromRgb(0x26, 0x28, 0x2E));
        var fxBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x24));
        var lanePen = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x36, 0x3E)), 1);
        var selLaneBrush = new SolidColorBrush(Color.FromArgb(0x18, 0x4F, 0xA3, 0xFF));

        // Grid lines from the ruler (drawn first so clips sit on top)
        var (step, _) = RulerStep();
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);

        int idx = 0;
        foreach (var l in lanes)
        {
            if (l.Top + l.AudioHeight + l.FxHeight < RulerHeight || l.Top > h) { idx++; continue; }
            dc.DrawRectangle(idx % 2 == 0 ? laneBg : laneBgAlt, null, new Rect(0, l.Top, w, l.AudioHeight));
            dc.DrawRectangle(fxBg, null, new Rect(0, l.FxTop, w, l.FxHeight));
            if (l.Track == SelectedTrack) dc.DrawRectangle(selLaneBrush, null, new Rect(0, l.Top, w, l.AudioHeight + l.FxHeight));
            dc.DrawLine(lanePen, new Point(0, l.FxTop + l.FxHeight - 0.5), new Point(w, l.FxTop + l.FxHeight - 0.5));
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x33)), 1), new Point(0, l.FxTop + 0.5), new Point(w, l.FxTop + 0.5));
            idx++;
        }

        // vertical grid
        long stepSamples = Math.Max(1, (long)(step * Rate));
        long first = (_viewStart / stepSamples) * stepSamples;
        for (long s = first; s < _viewStart + VisibleSamples + stepSamples; s += stepSamples)
        {
            double x = Math.Round(SampleToX(s)) + 0.5;
            if (x < 0 || x > w) continue;
            dc.DrawLine(gridPen, new Point(x, RulerHeight), new Point(x, h));
        }

        // clips
        foreach (var l in lanes)
        {
            if (l.Top + l.AudioHeight + l.FxHeight < RulerHeight || l.Top > h) continue;
            var color = Palette[l.Track.ColorIndex % Palette.Length];
            foreach (var c in l.Track.Clips)
            {
                double x0 = SampleToX(c.Start), x1 = SampleToX(c.End);
                if (x1 < 0 || x0 > w) continue;
                DrawClip(dc, c, color, x0, x1, l.Top + 2, l.AudioHeight - 4, dip);
                DrawEffects(dc, c, color, x0, x1, l, dip);
            }
        }

        DrawRuler(dc, w, dip);

        if (_project.Tracks.Count == 0)
        {
            var ft = new FormattedText("Drop audio or video files here, or click  + Add sounds  above.", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 14, new SolidColorBrush(Color.FromRgb(0x7A, 0x7D, 0x86)), dip);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, RulerHeight + 60));
        }
    }

    void DrawClip(DrawingContext dc, Clip c, Color color, double x0, double x1, double top, double height, double dip)
    {
        bool selected = SelectedClips.Contains(c);
        double cx0 = Math.Max(x0, -2), cx1 = Math.Min(x1, ActualWidth + 2);
        var rect = new Rect(cx0, top, Math.Max(1, cx1 - cx0), height);
        var fill = new SolidColorBrush(Color.FromArgb(selected ? (byte)0x58 : (byte)0x38, color.R, color.G, color.B));
        var border = new Pen(new SolidColorBrush(selected ? Colors.White : Color.FromArgb(0xB0, color.R, color.G, color.B)), selected ? 1.5 : 1);
        dc.DrawRoundedRectangle(fill, border, rect, 3, 3);

        if (c.Source == null)
        {
            var missing = new FormattedText("file not loaded", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 11, Brushes.OrangeRed, dip);
            dc.DrawText(missing, new Point(Math.Max(x0, 0) + 6, top + height / 2 - 7));
        }
        else
        {
            // waveform
            double mid = top + 14 + (height - 14) / 2, amp = (height - 16) / 2;
            var wavePen = new Pen(new SolidColorBrush(Color.FromArgb(0xD0, color.R, color.G, color.B)), 1);
            int px0 = (int)Math.Max(0, Math.Floor(x0)), px1 = (int)Math.Min(ActualWidth, Math.Ceiling(x1));
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                if (_spp >= 1)
                {
                    for (int px = px0; px < px1; px++)
                    {
                        long f0 = c.Offset + (long)((px - x0) * _spp), f1 = c.Offset + (long)((px + 1 - x0) * _spp);
                        f0 = Math.Max(f0, c.Offset); f1 = Math.Min(f1, c.Offset + c.Length);
                        if (f1 <= f0) continue;
                        c.Source.GetPeak(f0, f1, out var mn, out var mx);
                        double yTop = mid - mx * amp, yBot = mid - mn * amp;
                        if (yBot - yTop < 1) { yTop = mid - 0.5; yBot = mid + 0.5; }
                        g.BeginFigure(new Point(px + 0.5, yTop), false, false);
                        g.LineTo(new Point(px + 0.5, yBot), true, false);
                    }
                }
                else
                {
                    // Zoomed past one sample per pixel: draw the actual sample values as a connected line with dots.
                    long fStart = Math.Max(c.Offset, c.Offset + (long)Math.Floor((px0 - x0) * _spp));
                    long fEnd = Math.Min(c.Offset + c.Length, c.Offset + (long)Math.Ceiling((px1 - x0) * _spp) + 1);
                    bool started = false;
                    for (long f = fStart; f < fEnd && f < c.Source.Length; f++)
                    {
                        float v = (c.Source.Samples[f * 2] + c.Source.Samples[f * 2 + 1]) * 0.5f;
                        double x = x0 + (f - c.Offset) / _spp, y = mid - v * amp;
                        if (!started) { g.BeginFigure(new Point(x, y), false, false); started = true; } else g.LineTo(new Point(x, y), true, false);
                    }
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, wavePen, geo);
            if (_spp < 0.25)
            {
                var dot = new SolidColorBrush(Color.FromArgb(0xFF, color.R, color.G, color.B));
                long fStart = Math.Max(c.Offset, c.Offset + (long)Math.Floor((px0 - x0) * _spp));
                long fEnd = Math.Min(c.Offset + c.Length, c.Offset + (long)Math.Ceiling((px1 - x0) * _spp) + 1);
                for (long f = fStart; f < fEnd && f < c.Source.Length; f++)
                {
                    float v = (c.Source.Samples[f * 2] + c.Source.Samples[f * 2 + 1]) * 0.5f;
                    dc.DrawEllipse(dot, null, new Point(x0 + (f - c.Offset) / _spp, mid - v * amp), 2, 2);
                }
            }
        }

        // name strip
        var nameBg = new SolidColorBrush(Color.FromArgb(0x70, color.R, color.G, color.B));
        dc.DrawRectangle(nameBg, null, new Rect(rect.X, top, rect.Width, 14));
        string label = c.Name + (c.GainDb != 0 ? $"  ({TimeUtil.FormatDb(c.GainDb)})" : "");
        var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 11, Brushes.White, dip) { MaxTextWidth = Math.Max(1, rect.Width - 8), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(ft, new Point(Math.Max(x0, 0) + 4, top));
    }

    void DrawEffects(DrawingContext dc, Clip c, Color color, double x0, double x1, LaneRect l, double dip)
    {
        if (c.Effects.Count == 0) return;
        if (!l.Track.EffectsExpanded)
        {
            // Collapsed: one thin strip per clip plus a count badge, so the user still knows something is attached.
            var strip = new Rect(Math.Max(x0, 0), l.FxTop + 3, Math.Max(2, Math.Min(x1, ActualWidth) - Math.Max(x0, 0)), l.FxHeight - 6);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x60, 0xF2, 0xB1, 0x5B)), null, strip, 2, 2);
            var ft = new FormattedText($"{c.Effects.Count} effect{(c.Effects.Count == 1 ? "" : "s")} ▸ click ▾ on the lane to expand", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 9.5, Brushes.White, dip) { MaxTextWidth = Math.Max(1, strip.Width - 6), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(ft, new Point(strip.X + 4, strip.Y - 1));
            return;
        }
        int row = 0;
        foreach (var e in c.Effects)
        {
            if (row >= 6) break;
            double ex0 = SampleToX(c.Start + e.RegionStart), ex1 = SampleToX(c.Start + e.RegionStart + e.RegionLength);
            ex0 = Math.Max(ex0, x0); ex1 = Math.Min(ex1, x1);
            double y = l.FxTop + 2 + row * Track.EffectsRowHeight;
            var r = new Rect(Math.Max(ex0, -2), y, Math.Max(3, Math.Min(ex1, ActualWidth + 2) - Math.Max(ex0, -2)), Track.EffectsRowHeight - 3);
            bool sel = SelectedEffect == e;
            var isTransition = e.Def.IsTransition;
            var fc = isTransition ? Color.FromRgb(0xF2, 0xB1, 0x5B) : Color.FromRgb(0xA9, 0x8B, 0xFF);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(e.Enabled ? (byte)0x70 : (byte)0x30, fc.R, fc.G, fc.B)),
                new Pen(sel ? Brushes.White : new SolidColorBrush(Color.FromArgb(0xC0, fc.R, fc.G, fc.B)), sel ? 1.5 : 1), r, 3, 3);
            // Fade shape hint
            if (e.Kind == EffectKind.FadeIn || e.Kind == EffectKind.FadeOut)
            {
                var p0 = e.Kind == EffectKind.FadeIn ? new Point(r.Left, r.Bottom) : new Point(r.Left, r.Top);
                var p1 = e.Kind == EffectKind.FadeIn ? new Point(r.Right, r.Top) : new Point(r.Right, r.Bottom);
                dc.DrawLine(new Pen(Brushes.White, 1), p0, p1);
            }
            var ft = new FormattedText(e.Def.Label + (e.Enabled ? "" : " (off)"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10, Brushes.White, dip) { MaxTextWidth = Math.Max(1, r.Width - 6), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(ft, new Point(Math.Max(r.X, 0) + 3, y + 1));
            row++;
        }
    }

    (double step, bool micro) RulerStep()
    {
        double[] steps = { 0.00001, 0.00002, 0.00005, 0.0001, 0.0002, 0.0005, 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 };
        double secPerPx = _spp / Rate;
        foreach (var s in steps) if (s / secPerPx >= 90) return (s, s < 0.001);
        return (3600, false);
    }

    void DrawRuler(DrawingContext dc, double w, double dip)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x2D, 0x33)), null, new Rect(0, 0, w, RulerHeight));
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x45)), 1), new Point(0, RulerHeight - 0.5), new Point(w, RulerHeight - 0.5));
        var (step, micro) = RulerStep();
        long stepSamples = Math.Max(1, (long)Math.Round(step * Rate));
        long first = (_viewStart / stepSamples) * stepSamples;
        var tick = new Pen(new SolidColorBrush(Color.FromRgb(0x8A, 0x8D, 0x96)), 1);
        var minor = new Pen(new SolidColorBrush(Color.FromRgb(0x4A, 0x4D, 0x56)), 1);
        var txt = new SolidColorBrush(Color.FromRgb(0xC8, 0xCA, 0xD0));
        bool showSamples = _spp < 2;
        for (long s = first; s < _viewStart + VisibleSamples + stepSamples; s += stepSamples)
        {
            double x = Math.Round(SampleToX(s)) + 0.5;
            if (x < -100 || x > w) continue;
            dc.DrawLine(tick, new Point(x, RulerHeight - 10), new Point(x, RulerHeight));
            for (int i = 1; i < 5; i++)
            {
                double mx = Math.Round(SampleToX(s + stepSamples * i / 5)) + 0.5;
                dc.DrawLine(minor, new Point(mx, RulerHeight - 4), new Point(mx, RulerHeight));
            }
            string label = showSamples ? $"{TimeUtil.FormatSeconds(s / (double)Rate, micro)}  ·  smp {s}" : TimeUtil.FormatSeconds(s / (double)Rate, micro);
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10.5, txt, dip);
            dc.DrawText(ft, new Point(x + 3, 2));
        }
    }

    // ----- hit testing -----
    enum Zone { None, Ruler, Empty, ClipBody, ClipLeft, ClipRight, FxBody, FxLeft, FxRight, FxLane }
    readonly record struct Hit(Zone Zone, Track? Track, Clip? Clip, Effect? Effect);

    Hit HitTest(Point p)
    {
        if (_project == null) return new(Zone.None, null, null, null);
        if (p.Y < RulerHeight) return new(Zone.Ruler, null, null, null);
        foreach (var l in Lanes())
        {
            if (p.Y < l.Top || p.Y >= l.Top + l.AudioHeight + l.FxHeight) continue;
            bool inFx = p.Y >= l.FxTop;
            // iterate clips topmost-last so later clips win when overlapping
            for (int i = l.Track.Clips.Count - 1; i >= 0; i--)
            {
                var c = l.Track.Clips[i];
                double x0 = SampleToX(c.Start), x1 = SampleToX(c.End);
                if (inFx)
                {
                    if (!l.Track.EffectsExpanded) { if (p.X >= x0 && p.X <= x1 && c.Effects.Count > 0) return new(Zone.FxLane, l.Track, c, null); continue; }
                    int row = (int)((p.Y - l.FxTop - 2) / Track.EffectsRowHeight);
                    if (row >= 0 && row < c.Effects.Count)
                    {
                        var e = c.Effects[row];
                        double ex0 = SampleToX(c.Start + e.RegionStart), ex1 = SampleToX(c.Start + e.RegionStart + e.RegionLength);
                        if (p.X >= ex0 - 2 && p.X <= ex1 + 2)
                        {
                            if (p.X - ex0 < EdgeZone) return new(Zone.FxLeft, l.Track, c, e);
                            if (ex1 - p.X < EdgeZone) return new(Zone.FxRight, l.Track, c, e);
                            return new(Zone.FxBody, l.Track, c, e);
                        }
                    }
                    continue;
                }
                if (p.X >= x0 - 2 && p.X <= x1 + 2)
                {
                    if (p.X - x0 < EdgeZone) return new(Zone.ClipLeft, l.Track, c, null);
                    if (x1 - p.X < EdgeZone) return new(Zone.ClipRight, l.Track, c, null);
                    return new(Zone.ClipBody, l.Track, c, null);
                }
            }
            return new(inFx ? Zone.FxLane : Zone.Empty, l.Track, null, null);
        }
        return new(Zone.Empty, null, null, null);
    }

    // ----- mouse -----
    enum DragMode { None, Scrub, MoveClip, TrimLeft, TrimRight, MoveFx, TrimFxLeft, TrimFxRight, Pending }
    DragMode _drag;
    Point _dragStart;
    Hit _dragHit;
    bool _dragMoved;
    Dictionary<Clip, (long start, long offset, long length, Track track)> _orig = new();
    (long start, long length) _origFx;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        var hit = HitTest(p);
        _dragStart = p; _dragHit = hit; _dragMoved = false;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        switch (hit.Zone)
        {
            case Zone.Ruler:
                Playhead = XToSample(p.X); _drag = DragMode.Scrub; CaptureMouse(); break;
            case Zone.ClipBody:
            case Zone.ClipLeft:
            case Zone.ClipRight:
                if (!SelectedClips.Contains(hit.Clip!) || ctrl) Select(hit.Clip, ctrl);
                else { SelectedEffect = null; SelectedEffectClip = null; SelectionChanged?.Invoke(); }
                SelectedTrack = hit.Track;
                if (e.ClickCount == 2) { ClipActivated?.Invoke(hit.Clip!); break; }
                _drag = DragMode.Pending; CaptureMouse();
                break;
            case Zone.FxBody:
            case Zone.FxLeft:
            case Zone.FxRight:
                SelectEffect(hit.Clip!, hit.Effect);
                SelectedTrack = hit.Track;
                _origFx = (hit.Effect!.RegionStart, hit.Effect.RegionLength);
                _drag = DragMode.Pending; CaptureMouse();
                break;
            case Zone.FxLane:
                if (hit.Clip != null) { hit.Track!.EffectsExpanded = true; hit.Track.NotifyLayoutChanged(); InvalidateVisual(); ViewChanged?.Invoke(); }
                SelectTrack(hit.Track); break;
            case Zone.Empty:
                Select(null); SelectTrack(hit.Track);
                Playhead = XToSample(p.X); _drag = DragMode.Scrub; CaptureMouse();
                break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag == DragMode.None)
        {
            var hit = HitTest(p);
            Cursor = hit.Zone switch
            {
                Zone.ClipLeft or Zone.ClipRight or Zone.FxLeft or Zone.FxRight => Cursors.SizeWE,
                Zone.ClipBody or Zone.FxBody => Cursors.Hand,
                _ => Cursors.Arrow
            };
            Hint?.Invoke(hit.Zone switch
            {
                Zone.ClipLeft or Zone.ClipRight => "Drag to trim the edge of this piece. Hold Alt to ignore snapping.",
                Zone.ClipBody => "Drag to move. Double-click to see exact numbers. Right-click for more. Hold Alt while dragging to ignore snapping.",
                Zone.FxBody => "Drag to move this effect along the piece. Drag its edges to change how long it lasts.",
                Zone.FxLeft or Zone.FxRight => "Drag to change where this effect starts or ends.",
                Zone.Ruler => "Click or drag to move the playhead.",
                Zone.Empty => "Click to move the playhead here. Ctrl + wheel zooms, Shift + wheel scrolls sideways.",
                _ => ""
            });
            return;
        }

        double dx = p.X - _dragStart.X;
        if (_drag == DragMode.Pending)
        {
            if (Math.Abs(dx) < 3 && Math.Abs(p.Y - _dragStart.Y) < 3) return;
            EditStarting?.Invoke();
            _drag = _dragHit.Zone switch
            {
                Zone.ClipLeft => DragMode.TrimLeft, Zone.ClipRight => DragMode.TrimRight, Zone.ClipBody => DragMode.MoveClip,
                Zone.FxLeft => DragMode.TrimFxLeft, Zone.FxRight => DragMode.TrimFxRight, Zone.FxBody => DragMode.MoveFx, _ => DragMode.None
            };
            _orig.Clear();
            foreach (var c in SelectedClips) _orig[c] = (c.Start, c.Offset, c.Length, TrackOf(c)!);
        }
        _dragMoved = true;
        long delta = (long)Math.Round(dx * _spp);
        bool noSnap = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        switch (_drag)
        {
            case DragMode.Scrub:
                Playhead = XToSample(Math.Max(0, p.X)); break;

            case DragMode.MoveClip:
            {
                var lead = _dragHit.Clip!;
                var o = _orig[lead];
                long newStart = Math.Max(0, o.start + delta);
                if (!noSnap) newStart = SnapStart(newStart, o.length, SelectedClips);
                long applied = newStart - o.start;
                foreach (var (c, oc) in _orig) c.Start = Math.Max(0, oc.start + applied);
                // vertical: move the dragged clip (only when a single clip is selected) to the lane under the mouse
                if (SelectedClips.Count == 1)
                {
                    var target = TrackAtY(p.Y);
                    var cur = TrackOf(lead);
                    if (target != null && cur != null && target != cur) { cur.Clips.Remove(lead); target.Clips.Add(lead); SelectedTrack = target; _project!.Touch(); }
                }
                Hint?.Invoke($"{Terminology.L("Start")}: {TimeUtil.FormatSamples(lead.Start, Rate)}  (sample {lead.Start})");
                break;
            }
            case DragMode.TrimLeft:
            {
                var c = _dragHit.Clip!; var o = _orig[c];
                long newStart = o.start + delta;
                if (!noSnap) newStart = SnapPoint(newStart, SelectedClips);
                long minStart = o.start - o.offset, maxStart = o.start + o.length - 1;
                newStart = Math.Clamp(newStart, Math.Max(0, minStart), maxStart);
                long d = newStart - o.start;
                c.Start = newStart; c.Offset = o.offset + d; c.Length = o.length - d;
                foreach (var fx in c.Effects) fx.RegionStart = Math.Max(0, fx.RegionStart); // regions are clip-relative; keep as-is
                Hint?.Invoke($"{Terminology.L("Start")}: {TimeUtil.FormatSamples(c.Start, Rate)}   {Terminology.L("Length")}: {TimeUtil.FormatSamples(c.Length, Rate)}");
                break;
            }
            case DragMode.TrimRight:
            {
                var c = _dragHit.Clip!; var o = _orig[c];
                long newEnd = o.start + o.length + delta;
                if (!noSnap) newEnd = SnapPoint(newEnd, SelectedClips);
                long maxLen = c.Source != null ? c.Source.Length - o.offset : long.MaxValue;
                c.Length = Math.Clamp(newEnd - o.start, 1, Math.Max(1, maxLen));
                Hint?.Invoke($"{Terminology.L("Length")}: {TimeUtil.FormatSamples(c.Length, Rate)}  ends {TimeUtil.FormatSamples(c.End, Rate)}");
                break;
            }
            case DragMode.MoveFx:
            {
                var fx = _dragHit.Effect!; var c = _dragHit.Clip!;
                fx.RegionStart = Math.Clamp(_origFx.start + delta, 0, Math.Max(0, c.Length - fx.RegionLength));
                Hint?.Invoke($"{fx.Def.Label} starts {TimeUtil.FormatSamples(fx.RegionStart, Rate)} into the piece");
                break;
            }
            case DragMode.TrimFxLeft:
            {
                var fx = _dragHit.Effect!;
                long ns = Math.Clamp(_origFx.start + delta, 0, _origFx.start + _origFx.length - 1);
                fx.RegionLength = _origFx.length - (ns - _origFx.start); fx.RegionStart = ns;
                break;
            }
            case DragMode.TrimFxRight:
            {
                var fx = _dragHit.Effect!; var c = _dragHit.Clip!;
                fx.RegionLength = Math.Clamp(_origFx.length + delta, 1, Math.Max(1, c.Length - fx.RegionStart));
                Hint?.Invoke($"{fx.Def.Label} lasts {TimeUtil.FormatSamples(fx.RegionLength, Rate)}");
                break;
            }
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag != DragMode.None && _drag != DragMode.Pending && _drag != DragMode.Scrub && _dragMoved)
        {
            _project?.Touch();
            EditCommitted?.Invoke(_drag switch { DragMode.MoveClip => "Move", DragMode.TrimLeft or DragMode.TrimRight => "Trim", _ => "Edit effect" });
        }
        _drag = DragMode.None; ReleaseMouseCapture(); InvalidateVisual();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        var hit = HitTest(p);
        if (hit.Clip != null && hit.Effect == null && !SelectedClips.Contains(hit.Clip)) Select(hit.Clip);
        if (hit.Effect != null) SelectEffect(hit.Clip!, hit.Effect);
        if (hit.Track != null) SelectedTrack = hit.Track;
        InvalidateVisual();
        ContextMenuRequested?.Invoke(p, hit.Clip, hit.Effect, hit.Track);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) SetZoom(_spp * (e.Delta > 0 ? 1 / 1.25 : 1.25), p.X);
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ViewStart = _viewStart - (long)(e.Delta * _spp * 0.5);
        else if (ContentHeight > ActualHeight - RulerHeight) VerticalOffset = _vOffset - e.Delta * 0.5;
        else ViewStart = _viewStart - (long)(e.Delta * _spp * 0.5);
        e.Handled = true;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    protected override void OnDrop(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var p = e.GetPosition(this);
        FilesDropped?.Invoke(files, TrackAtY(p.Y), Math.Max(0, XToSample(p.X)));
        e.Handled = true;
    }

    // ----- snapping -----
    IEnumerable<long> SnapCandidates(HashSet<Clip> exclude)
    {
        yield return 0;
        yield return _playhead;
        if (_project == null) yield break;
        foreach (var t in _project.Tracks) foreach (var c in t.Clips)
        {
            if (exclude.Contains(c)) continue;
            yield return c.Start; yield return c.End;
        }
        if (_settings.SnapToGrid && _settings.GridSeconds > 0)
        {
            long g = (long)(_settings.GridSeconds * Rate);
            long from = (_viewStart / g) * g;
            for (long s = from; s < _viewStart + VisibleSamples; s += g) yield return s;
        }
    }
    long SnapPoint(long sample, HashSet<Clip> exclude)
    {
        if (!_settings.SnapToClipEdges && !_settings.SnapToGrid) return sample;
        long best = sample; double bestDist = SnapPixels;
        foreach (var cand in SnapCandidates(exclude))
        {
            double d = Math.Abs(cand - sample) / _spp;
            if (d < bestDist) { bestDist = d; best = cand; }
        }
        return best;
    }
    long SnapStart(long start, long length, HashSet<Clip> exclude)
    {
        long s1 = SnapPoint(start, exclude);
        if (s1 != start) return s1;
        long e1 = SnapPoint(start + length, exclude);
        return e1 != start + length ? e1 - length : start;
    }

    public Track? TrackOf(Clip c) => _project?.Tracks.FirstOrDefault(t => t.Clips.Contains(c));
}

/// <summary>Only the playhead line, drawn on its own layer so playback doesn't redraw every waveform 60 times a second.</summary>
public sealed class PlayheadOverlay : FrameworkElement
{
    public TimelineControl? Timeline { get; set; }
    long _playhead;
    public long Playhead { get => _playhead; set { _playhead = value; InvalidateVisual(); } }
    public PlayheadOverlay() { IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        if (Timeline == null) return;
        double x = Math.Round(Timeline.SampleToX(_playhead)) + 0.5;
        if (x < -1 || x > ActualWidth + 1) return;
        dc.DrawLine(new Pen(Brushes.White, 1.2), new Point(x, 0), new Point(x, ActualHeight));
        var tri = new StreamGeometry();
        using (var g = tri.Open()) { g.BeginFigure(new Point(x - 6, 0), true, true); g.LineTo(new Point(x + 6, 0), true, false); g.LineTo(new Point(x, 9), true, false); }
        dc.DrawGeometry(Brushes.White, null, tri);
    }
}
