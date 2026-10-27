using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MixLane.Core;
using MixLane.Models;
using static MixLane.UI.UiHelpers;

namespace MixLane.UI;

/// <summary>
/// Exact numbers for the selected piece (start, length, offset, volume) that can be typed to the sample,
/// plus the list of effects attached to it and the knobs of the selected effect.
/// </summary>
public sealed class ClipInspector : UserControl
{
    Clip? _clip;
    Effect? _effect;
    int _rate = 48000;

    public Func<string, IDisposable>? BeginEdit;   // returns a scope that records undo + touches the project on dispose
    public Action<Clip, Effect?>? EffectSelected;
    public Action? Redraw;
    public Action<Clip>? AddEffectRequested;

    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    public ClipInspector()
    {
        Content = _scroll;
        Background = Res("Bg2");
        Build();
    }

    public void Show(Clip? clip, Effect? effect, int rate)
    {
        _clip = clip; _effect = effect; _rate = rate;
        Build();
    }
    public void Refresh() => Build();

    TextBox TimeBox(string initial, string help, Action<long> commit, Func<long> current)
    {
        var tb = new TextBox { Text = initial, Width = 130, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(0, 0, 6, 0) };
        Tip(tb, help + "  You can type m:ss.mmm, seconds (12.5s), milliseconds (350ms) or samples (96000smp).");
        void Commit()
        {
            var v = TimeUtil.ParseToSamples(tb.Text, _rate);
            if (v == null) { tb.Text = TimeUtil.FormatSamples(current(), _rate); return; }
            if (v.Value == current()) return;
            commit(v.Value);
        }
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        tb.LostFocus += (_, _) => Commit();
        return tb;
    }

    void Build()
    {
        var root = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        _scroll.Content = root;
        if (_clip == null)
        {
            root.Children.Add(Text("Click a sound piece on a lane to see its exact position, length and effects here.", 13, dim: true));
            root.Children.Add(Text("Tip: double-click a piece to jump straight to this panel.", 12, dim: true));
            return;
        }
        var c = _clip;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(grid);

        // ---- left: exact numbers ----
        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        grid.Children.Add(left);
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var nameBox = new TextBox { Text = c.Name, Width = 240, FontWeight = FontWeights.SemiBold };
        Tip(nameBox, "Name shown on the piece. Press Enter to apply.");
        void CommitName() { if (nameBox.Text != c.Name) { using (BeginEdit?.Invoke("Rename")) c.Name = nameBox.Text; Redraw?.Invoke(); } }
        nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitName(); };
        nameBox.LostFocus += (_, _) => CommitName();
        title.Children.Add(nameBox);
        title.Children.Add(Text("  " + System.IO.Path.GetFileName(c.SourcePath), 11, dim: true));
        left.Children.Add(title);

        var fields = new Grid();
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        void AddField(string label, string help, string value, Action<long> commit, Func<long> current, string samplesText)
        {
            fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Text(label, 12); l.VerticalAlignment = VerticalAlignment.Center; Tip(l, help);
            Grid.SetRow(l, row); fields.Children.Add(l);
            var tb = TimeBox(value, help, commit, current); tb.Margin = new Thickness(0, 2, 6, 2);
            Grid.SetRow(tb, row); Grid.SetColumn(tb, 1); fields.Children.Add(tb);
            var s = Text(samplesText, 11, dim: true); s.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(s, row); Grid.SetColumn(s, 2); fields.Children.Add(s);
            row++;
        }
        AddField(Terminology.L("Start"), Terminology.Help("Start"), TimeUtil.FormatSamples(c.Start, _rate),
            v => { using (BeginEdit?.Invoke("Move")) c.Start = v; Build(); Redraw?.Invoke(); }, () => c.Start, $"= sample {c.Start:N0}");
        AddField(Terminology.L("Length"), Terminology.Help("Length"), TimeUtil.FormatSamples(c.Length, _rate),
            v => { using (BeginEdit?.Invoke("Resize")) c.Length = Math.Min(v, c.MaxLength); Build(); Redraw?.Invoke(); }, () => c.Length, $"= {c.Length:N0} samples  (max {c.MaxLength:N0})");
        AddField("Ends at", "Where this piece stops in the mix. Changing it changes the length.", TimeUtil.FormatSamples(c.End, _rate),
            v => { using (BeginEdit?.Invoke("Resize")) c.Length = Math.Clamp(v - c.Start, 1, c.MaxLength); Build(); Redraw?.Invoke(); }, () => c.End, $"= sample {c.End:N0}");
        AddField(Terminology.L("Offset"), Terminology.Help("Offset"), TimeUtil.FormatSamples(c.Offset, _rate),
            v => { using (BeginEdit?.Invoke("Trim")) { long max = Math.Max(0, c.SourceLength - 1); c.Offset = Math.Min(v, max); c.Length = Math.Min(c.Length, c.MaxLength); } Build(); Redraw?.Invoke(); }, () => c.Offset,
            $"file is {TimeUtil.FormatSamples(c.SourceLength, _rate)} long");
        left.Children.Add(fields);

        var vol = MakeSlider(Terminology.L("Volume") + " of this piece", Terminology.Help("Volume"), -60, 12, c.GainDb, "dB", v => { c.GainDb = v; Redraw?.Invoke(); }, width: 330);
        IDisposable? volScope = null;
        vol.DragStarted = () => volScope ??= BeginEdit?.Invoke("Volume");
        vol.DragDone = () => { volScope?.Dispose(); volScope = null; };
        vol.Panel.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(vol.Panel);

        // ---- right: effects ----
        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        var fxHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var addBtn = Btn("+ Add effect or transition", () => AddEffectRequested?.Invoke(c), "Attach a fade, a filter, echo, etc. to this piece. It shows up in the effects lane under the piece.");
        DockPanel.SetDock(addBtn, Dock.Right);
        fxHeader.Children.Add(addBtn);
        fxHeader.Children.Add(Text(Terminology.L("Effects") + " on this piece", 13, bold: true));
        right.Children.Add(fxHeader);

        if (c.Effects.Count == 0)
        {
            right.Children.Add(Text("None yet. Effects only act on the part of the piece they cover, and you can drag them around in the lane below the piece.", 12, dim: true));
            return;
        }

        var fxGrid = new Grid();
        fxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        fxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        right.Children.Add(fxGrid);

        var list = new ListBox { MaxHeight = 170, Margin = new Thickness(0, 0, 10, 0) };
        foreach (var e in c.Effects)
        {
            var item = new ListBoxItem { Content = $"{e.Def.Label}{(e.Enabled ? "" : "  (off)")}   {TimeUtil.FormatSamples(e.RegionStart, _rate)} → {TimeUtil.FormatSamples(e.RegionStart + e.RegionLength, _rate)}", Tag = e, Foreground = Res("Fg") };
            list.Items.Add(item);
            if (e == _effect) list.SelectedItem = item;
        }
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ListBoxItem li && li.Tag is Effect e && e != _effect) { _effect = e; EffectSelected?.Invoke(c, e); Build(); }
        };
        fxGrid.Children.Add(list);

        var detail = new StackPanel();
        Grid.SetColumn(detail, 1);
        fxGrid.Children.Add(detail);
        if (_effect == null || !c.Effects.Contains(_effect))
        {
            detail.Children.Add(Text("Pick an effect on the left to adjust it.", 12, dim: true));
            return;
        }
        var fx = _effect;
        detail.Children.Add(Text(fx.Def.Label, 13, bold: true));
        detail.Children.Add(Text(fx.Def.Help, 11, dim: true));

        var regionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        var rl = Text("Covers from", 12); rl.VerticalAlignment = VerticalAlignment.Center; rl.Margin = new Thickness(0, 0, 6, 0);
        regionRow.Children.Add(rl);
        regionRow.Children.Add(TimeBox(TimeUtil.FormatSamples(fx.RegionStart, _rate), "How far into the piece the effect starts.",
            v => { using (BeginEdit?.Invoke("Effect region")) fx.RegionStart = Math.Clamp(v, 0, Math.Max(0, c.Length - 1)); Build(); Redraw?.Invoke(); }, () => fx.RegionStart));
        var rl2 = Text("for", 12); rl2.VerticalAlignment = VerticalAlignment.Center; rl2.Margin = new Thickness(0, 0, 6, 0);
        regionRow.Children.Add(rl2);
        regionRow.Children.Add(TimeBox(TimeUtil.FormatSamples(fx.RegionLength, _rate), "How long the effect lasts.",
            v => { using (BeginEdit?.Invoke("Effect region")) fx.RegionLength = Math.Clamp(v, 1, Math.Max(1, c.Length - fx.RegionStart)); Build(); Redraw?.Invoke(); }, () => fx.RegionLength));
        var rl3 = Text("(into the piece)", 11, dim: true); rl3.VerticalAlignment = VerticalAlignment.Center;
        regionRow.Children.Add(rl3);
        detail.Children.Add(regionRow);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var onBox = new CheckBox { Content = "Turned on", IsChecked = fx.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        onBox.Click += (_, _) => { using (BeginEdit?.Invoke("Toggle effect")) fx.Enabled = onBox.IsChecked == true; Redraw?.Invoke(); };
        btnRow.Children.Add(onBox);
        btnRow.Children.Add(Btn("Cover whole piece", () => { using (BeginEdit?.Invoke("Effect region")) { fx.RegionStart = 0; fx.RegionLength = c.Length; } Build(); Redraw?.Invoke(); }, "Stretch this effect over the entire piece."));
        btnRow.Children.Add(Btn("Remove", () => { using (BeginEdit?.Invoke("Remove effect")) c.Effects.Remove(fx); _effect = null; EffectSelected?.Invoke(c, null); Build(); Redraw?.Invoke(); }, "Take this effect off the piece."));
        detail.Children.Add(btnRow);

        var knobs = new WrapPanel();
        bool adv = App.Settings.IsAdvanced;
        foreach (var p in fx.Def.Params)
        {
            if (p.AdvancedOnly && !adv) continue;
            IDisposable? scope = null;
            var s = MakeSlider(adv ? p.Pro : p.Plain, p.PlainHelp, p.Min, p.Max, fx.P(p.Key), p.Unit, v => { fx.SetP(p.Key, v); Redraw?.Invoke(); },
                () => scope ??= BeginEdit?.Invoke("Adjust effect"), () => { scope?.Dispose(); scope = null; }, width: 220, decimals: p.Max - p.Min > 50 ? 0 : 2);
            knobs.Children.Add(s.Panel);
        }
        if (fx.Def.Params.Length == 0) knobs.Children.Add(Text("This effect has nothing to adjust. Just move or resize it.", 12, dim: true));
        detail.Children.Add(knobs);
    }
}
