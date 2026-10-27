using System.Windows;
using System.Windows.Controls;
using MixLane.Audio;
using MixLane.Core;
using static MixLane.UI.UiHelpers;

namespace MixLane.UI;

/// <summary>
/// Asks which audio format/settings to use. Used both when a video is imported (convert) and when the mix is exported.
/// In convert mode it can remember the answer so the user is never asked again.
/// </summary>
public sealed class FormatDialog : Window
{
    public ConversionDefaults Result { get; } = new();
    public bool RememberChoice { get; private set; }

    readonly ComboBox _format = new();
    readonly ComboBox _bitrate = new();
    readonly ComboBox _rate = new();
    readonly ComboBox _channels = new();
    readonly RadioButton _nextTo = new() { Content = "Save the converted audio next to the video" };
    readonly RadioButton _folder = new() { Content = "Save it in this folder:" };
    readonly TextBox _folderBox = new() { Width = 300, Margin = new Thickness(20, 0, 6, 0) };
    readonly CheckBox _remember = new() { Content = "Use these settings automatically from now on (you can change them later in Settings)" };
    readonly StackPanel _bitrateRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

    public FormatDialog(ConversionDefaults defaults, bool exportMode, string? sourceName = null)
    {
        Result.CopyFrom(defaults);
        Title = exportMode ? "Export finished mix" : "Convert video to audio";
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Res("Bg"); Foreground = Res("Fg"); FontSize = 13;

        var root = new StackPanel { Margin = new Thickness(18) };
        Content = root;
        root.Children.Add(Text(exportMode
            ? "Choose the file type for your finished mix. MP3 is the safe choice for sharing; WAV keeps full quality."
            : $"\"{sourceName}\" is a video. MixLane will pull the sound out of it and save it as an audio file you can use in the mix.", 13));

        foreach (AudioFormat f in Enum.GetValues<AudioFormat>()) _format.Items.Add(new ComboBoxItem { Content = MediaConverter.DescribeFormat(f), Tag = f });
        foreach (var b in new[] { 96, 128, 160, 192, 256, 320 }) _bitrate.Items.Add(new ComboBoxItem { Content = $"{b} kbps" + (b == 192 ? "  (good default)" : b == 320 ? "  (best)" : ""), Tag = b });
        foreach (var r in new[] { 44100, 48000 }) _rate.Items.Add(new ComboBoxItem { Content = r == 44100 ? "44,100 Hz  (CD quality, most common)" : "48,000 Hz  (video standard)", Tag = r });
        _channels.Items.Add(new ComboBoxItem { Content = "Stereo (left + right)", Tag = 2 });
        _channels.Items.Add(new ComboBoxItem { Content = "Mono (single channel, smaller file)", Tag = 1 });

        root.Children.Add(Labeled("File type", _format, "The kind of audio file to create."));
        _bitrateRow.Children.Add(Labeled("Quality (bitrate)", _bitrate, "Higher = better sound and bigger file. 192 kbps is hard to tell apart from the original for most people."));
        root.Children.Add(_bitrateRow);
        root.Children.Add(Labeled("Sample rate", _rate, "How many times per second the sound is measured. Either choice is fine; 44,100 is the most compatible."));
        root.Children.Add(Labeled("Channels", _channels, "Stereo keeps left and right separate. Mono merges them."));

        if (!exportMode)
        {
            var where = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            where.Children.Add(Text("Where to save the converted file", 12, bold: true));
            where.Children.Add(_nextTo);
            where.Children.Add(_folder);
            var fr = new StackPanel { Orientation = Orientation.Horizontal };
            fr.Children.Add(_folderBox);
            fr.Children.Add(Btn("Browse...", () =>
            {
                var d = new Microsoft.Win32.OpenFolderDialog();
                if (d.ShowDialog(this) == true) { _folderBox.Text = d.FolderName; _folder.IsChecked = true; }
            }));
            where.Children.Add(fr);
            root.Children.Add(where);
            _remember.Margin = new Thickness(0, 12, 0, 0);
            root.Children.Add(_remember);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(Btn(exportMode ? "Export..." : "Convert", () => { Apply(); DialogResult = true; }, width: 110));
        buttons.Children.Add(Btn("Cancel", () => DialogResult = false, width: 90));
        root.Children.Add(buttons);

        SelectTag(_format, Result.Format); SelectTag(_bitrate, Result.BitrateKbps); SelectTag(_rate, Result.SampleRate); SelectTag(_channels, Result.Channels);
        _nextTo.IsChecked = Result.SaveNextToSource; _folder.IsChecked = !Result.SaveNextToSource; _folderBox.Text = Result.OutputFolder;
        _format.SelectionChanged += (_, _) => _bitrateRow.Visibility = SelectedTag<AudioFormat>(_format) == AudioFormat.Wav ? Visibility.Collapsed : Visibility.Visible;
        _bitrateRow.Visibility = Result.Format == AudioFormat.Wav ? Visibility.Collapsed : Visibility.Visible;
    }

    static StackPanel Labeled(string label, FrameworkElement el, string help)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var t = Text(label, 12); t.Width = 130; t.VerticalAlignment = VerticalAlignment.Center;
        el.Width = 330;
        Tip(p, help);
        p.Children.Add(t); p.Children.Add(el);
        return p;
    }
    static void SelectTag<T>(ComboBox cb, T value) { foreach (ComboBoxItem i in cb.Items) if (Equals(i.Tag, value)) { cb.SelectedItem = i; return; } cb.SelectedIndex = 0; }
    static T SelectedTag<T>(ComboBox cb) => (T)((ComboBoxItem)cb.SelectedItem).Tag;

    void Apply()
    {
        Result.Format = SelectedTag<AudioFormat>(_format);
        Result.BitrateKbps = SelectedTag<int>(_bitrate);
        Result.SampleRate = SelectedTag<int>(_rate);
        Result.Channels = SelectedTag<int>(_channels);
        Result.SaveNextToSource = _nextTo.IsChecked == true || string.IsNullOrWhiteSpace(_folderBox.Text);
        Result.OutputFolder = _folderBox.Text;
        RememberChoice = _remember.IsChecked == true;
        Result.AskEveryTime = !RememberChoice;
    }
}
