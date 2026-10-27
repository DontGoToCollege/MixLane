using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MixLane.UI;

/// <summary>Small factory helpers so panels can be built in code and re-labelled when the user switches vocabulary.</summary>
public static class UiHelpers
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static TextBlock Text(string text, double size = 12, bool dim = false, bool bold = false, TextWrapping wrap = TextWrapping.Wrap)
        => new() { Text = text, FontSize = size, Foreground = Res(dim ? "FgDim" : "Fg"), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = wrap, Margin = new Thickness(0, 0, 0, 2) };

    public static void Tip(FrameworkElement el, string help)
    {
        if (!string.IsNullOrEmpty(help) && (App.Settings.ShowTooltips)) { el.ToolTip = help; ToolTipService.SetInitialShowDelay(el, 400); ToolTipService.SetShowDuration(el, 20000); }
    }

    public sealed class SliderRow
    {
        public StackPanel Panel = null!;
        public Slider Slider = null!;
        public TextBox Value = null!;
        public TextBlock Label = null!;
        public Func<double, string> Format = v => v.ToString("0.0", CultureInfo.InvariantCulture);
        public Action<double>? Changed;
        public Action? DragStarted;
        public Action? DragDone;
        bool _suppress;

        public void SetValue(double v)
        {
            _suppress = true;
            Slider.Value = v;
            Value.Text = Format(v);
            _suppress = false;
        }
        internal bool Suppressed => _suppress;
    }

    /// <summary>A label, a slider and an editable exact-value box. Typing an exact number is always possible.</summary>
    public static SliderRow MakeSlider(string label, string help, double min, double max, double value, string unit, Action<double> changed, Action? dragStarted = null, Action? dragDone = null, double width = 190, int decimals = 1)
    {
        var row = new SliderRow { Changed = changed, DragStarted = dragStarted, DragDone = dragDone };
        row.Format = v => v.ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), CultureInfo.InvariantCulture) + (unit.Length > 0 ? " " + unit : "");
        row.Panel = new StackPanel { Margin = new Thickness(0, 0, 14, 6), Width = width };
        row.Label = Text(label, 12);
        row.Panel.Children.Add(row.Label);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        row.Slider = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        row.Value = new TextBox { Text = row.Format(value), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(row.Value, 1);
        g.Children.Add(row.Slider); g.Children.Add(row.Value);
        row.Panel.Children.Add(g);
        Tip(row.Panel, help);

        row.Slider.ValueChanged += (_, e) =>
        {
            if (row.Suppressed) return;
            row.Value.Text = row.Format(e.NewValue);
            row.Changed?.Invoke(e.NewValue);
        };
        row.Slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => row.DragStarted?.Invoke()));
        row.Slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => row.DragDone?.Invoke()));
        row.Slider.PreviewMouseLeftButtonDown += (_, _) => row.DragStarted?.Invoke();
        row.Slider.PreviewMouseLeftButtonUp += (_, _) => row.DragDone?.Invoke();
        void Commit()
        {
            var txt = new string(row.Value.Text.Where(ch => char.IsDigit(ch) || ch == '.' || ch == '-' || ch == ',').ToArray()).Replace(',', '.');
            if (double.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                v = Math.Clamp(v, min, max);
                row.DragStarted?.Invoke();
                row.SetValue(v);
                row.Changed?.Invoke(v);
                row.DragDone?.Invoke();
            }
            else row.Value.Text = row.Format(row.Slider.Value);
        }
        row.Value.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        row.Value.LostFocus += (_, _) => Commit();
        return row;
    }

    public static Button Btn(string text, Action onClick, string help = "", double? width = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0) };
        if (width.HasValue) b.Width = width.Value;
        b.Click += (_, _) => onClick();
        Tip(b, help);
        return b;
    }
}
