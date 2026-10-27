using System.Windows;
using System.Windows.Controls;
using MixLane.Audio;
using MixLane.Core;
using static MixLane.UI.UiHelpers;

namespace MixLane.UI;

public sealed class SettingsWindow : Window
{
    readonly AppSettings _s;
    readonly RadioButton _plain = new() { Content = "Plain language  —  \"Bass\", \"Reduce hiss\", \"Only this lane\"" };
    readonly RadioButton _pro = new() { Content = "Industry-standard terms  —  \"Low shelf\", \"High-cut\", \"Solo\"" };
    readonly ComboBox _rate = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
    readonly CheckBox _snapEdges = new() { Content = "Pieces stick to the edges of other pieces while dragging (hold Alt to move freely)" };
    readonly CheckBox _snapGrid = new() { Content = "Pieces also stick to a time grid of" };
    readonly TextBox _grid = new() { Width = 60, Margin = new Thickness(6, 0, 4, 0) };
    readonly CheckBox _tips = new() { Content = "Show explanations when hovering over controls" };
    readonly Slider _laneH = new() { Minimum = 48, Maximum = 220, Width = 200, TickFrequency = 8, IsSnapToTickEnabled = true };
    readonly CheckBox _ask = new() { Content = "Ask me every time a video is added" };
    readonly ComboBox _fmt = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
    readonly ComboBox _br = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
    readonly ComboBox _crate = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
    readonly ComboBox _ch = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
    readonly RadioButton _nextTo = new() { Content = "Next to the video file" };
    readonly RadioButton _folder = new() { Content = "In this folder:" };
    readonly TextBox _folderBox = new() { Width = 300, Margin = new Thickness(20, 0, 6, 0) };

    public bool ModeChanged { get; private set; }

    public SettingsWindow(AppSettings s)
    {
        _s = s;
        Title = "Settings"; Width = 640; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Res("Bg"); Foreground = Res("Fg"); FontSize = 13;

        var root = new StackPanel { Margin = new Thickness(18) };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 820 };

        var g1 = new GroupBox { Header = "How things are described" };
        var p1 = new StackPanel();
        p1.Children.Add(Text("Both modes have exactly the same controls. Only the words change.", 11, dim: true));
        p1.Children.Add(_plain); p1.Children.Add(_pro);
        g1.Content = p1; root.Children.Add(g1);

        var g2 = new GroupBox { Header = "Working with pieces" };
        var p2 = new StackPanel();
        p2.Children.Add(_snapEdges);
        var gridRow = new StackPanel { Orientation = Orientation.Horizontal };
        gridRow.Children.Add(_snapGrid); gridRow.Children.Add(_grid); gridRow.Children.Add(Text("seconds", 12));
        p2.Children.Add(gridRow);
        p2.Children.Add(_tips);
        var lh = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        lh.Children.Add(Text("Lane height for new lanes", 12)); _laneH.Margin = new Thickness(10, 0, 0, 0); lh.Children.Add(_laneH);
        p2.Children.Add(lh);
        g2.Content = p2; root.Children.Add(g2);

        var g3 = new GroupBox { Header = "New mixes" };
        var p3 = new StackPanel();
        p3.Children.Add(Text("Sample rate for new mixes. Every file you add is converted to this rate automatically so everything plays together. 48,000 Hz is a good default.", 11, dim: true));
        _rate.Items.Add(new ComboBoxItem { Content = "44,100 Hz (CD)", Tag = 44100 });
        _rate.Items.Add(new ComboBoxItem { Content = "48,000 Hz (recommended)", Tag = 48000 });
        _rate.Items.Add(new ComboBoxItem { Content = "96,000 Hz (high resolution, uses more memory)", Tag = 96000 });
        p3.Children.Add(_rate);
        g3.Content = p3; root.Children.Add(g3);

        var g4 = new GroupBox { Header = "When a video file is added" };
        var p4 = new StackPanel();
        p4.Children.Add(Text("Videos are converted to an audio file first. These are the defaults used.", 11, dim: true));
        p4.Children.Add(_ask);
        foreach (AudioFormat f in Enum.GetValues<AudioFormat>()) _fmt.Items.Add(new ComboBoxItem { Content = MediaConverter.DescribeFormat(f), Tag = f });
        foreach (var b in new[] { 96, 128, 160, 192, 256, 320 }) _br.Items.Add(new ComboBoxItem { Content = $"{b} kbps", Tag = b });
        foreach (var r in new[] { 44100, 48000 }) _crate.Items.Add(new ComboBoxItem { Content = $"{r:N0} Hz", Tag = r });
        _ch.Items.Add(new ComboBoxItem { Content = "Stereo", Tag = 2 }); _ch.Items.Add(new ComboBoxItem { Content = "Mono", Tag = 1 });
        p4.Children.Add(Row("File type", _fmt)); p4.Children.Add(Row("Quality (bitrate)", _br)); p4.Children.Add(Row("Sample rate", _crate)); p4.Children.Add(Row("Channels", _ch));
        p4.Children.Add(Text("Where to save the converted audio", 12, bold: true));
        p4.Children.Add(_nextTo); p4.Children.Add(_folder);
        var fr = new StackPanel { Orientation = Orientation.Horizontal };
        fr.Children.Add(_folderBox);
        fr.Children.Add(Btn("Browse...", () => { var d = new Microsoft.Win32.OpenFolderDialog(); if (d.ShowDialog(this) == true) { _folderBox.Text = d.FolderName; _folder.IsChecked = true; } }));
        p4.Children.Add(fr);
        g4.Content = p4; root.Children.Add(g4);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Btn("Save", () => { Apply(); DialogResult = true; }, width: 100));
        buttons.Children.Add(Btn("Cancel", () => DialogResult = false, width: 90));
        root.Children.Add(buttons);

        // load
        _plain.IsChecked = s.Mode == UserMode.Beginner; _pro.IsChecked = s.Mode == UserMode.Advanced;
        _snapEdges.IsChecked = s.SnapToClipEdges; _snapGrid.IsChecked = s.SnapToGrid; _grid.Text = s.GridSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _tips.IsChecked = s.ShowTooltips; _laneH.Value = s.LaneHeight;
        Sel(_rate, s.ProjectSampleRate);
        _ask.IsChecked = s.Conversion.AskEveryTime;
        Sel(_fmt, s.Conversion.Format); Sel(_br, s.Conversion.BitrateKbps); Sel(_crate, s.Conversion.SampleRate); Sel(_ch, s.Conversion.Channels);
        _nextTo.IsChecked = s.Conversion.SaveNextToSource; _folder.IsChecked = !s.Conversion.SaveNextToSource; _folderBox.Text = s.Conversion.OutputFolder;
    }

    static StackPanel Row(string label, FrameworkElement el)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        var t = Text(label, 12); t.Width = 130; t.VerticalAlignment = VerticalAlignment.Center;
        p.Children.Add(t); p.Children.Add(el);
        return p;
    }
    static void Sel<T>(ComboBox cb, T v) { foreach (ComboBoxItem i in cb.Items) if (Equals(i.Tag, v)) { cb.SelectedItem = i; return; } cb.SelectedIndex = 0; }
    static T TagOf<T>(ComboBox cb) => (T)((ComboBoxItem)cb.SelectedItem).Tag;

    void Apply()
    {
        var newMode = _pro.IsChecked == true ? UserMode.Advanced : UserMode.Beginner;
        ModeChanged = newMode != _s.Mode;
        _s.Mode = newMode;
        _s.SnapToClipEdges = _snapEdges.IsChecked == true;
        _s.SnapToGrid = _snapGrid.IsChecked == true;
        if (double.TryParse(_grid.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g) && g > 0) _s.GridSeconds = g;
        _s.ShowTooltips = _tips.IsChecked == true;
        _s.LaneHeight = (int)_laneH.Value;
        _s.ProjectSampleRate = TagOf<int>(_rate);
        _s.Conversion.AskEveryTime = _ask.IsChecked == true;
        _s.Conversion.Format = TagOf<AudioFormat>(_fmt);
        _s.Conversion.BitrateKbps = TagOf<int>(_br);
        _s.Conversion.SampleRate = TagOf<int>(_crate);
        _s.Conversion.Channels = TagOf<int>(_ch);
        _s.Conversion.SaveNextToSource = _nextTo.IsChecked == true || string.IsNullOrWhiteSpace(_folderBox.Text);
        _s.Conversion.OutputFolder = _folderBox.Text;
        _s.Save();
    }
}
