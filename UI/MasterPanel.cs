using System.Windows;
using System.Windows.Controls;
using MixLane.Core;
using MixLane.Models;
using static MixLane.UI.UiHelpers;

namespace MixLane.UI;

/// <summary>The "whole mix" controls: loudness, tone (bass / middle / treble), noise clean-up and the distortion guard.</summary>
public sealed class MasterPanel : UserControl
{
    MasterSettings? _m;
    public Func<string, IDisposable>? BeginEdit;

    public MasterPanel() { Background = Res("Bg2"); }

    public void Show(MasterSettings m) { _m = m; Build(); }
    public void Refresh() => Build();

    void Build()
    {
        var root = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (_m == null) return;
        var m = _m;
        bool adv = App.Settings.IsAdvanced;

        root.Children.Add(Text(Terminology.L("MasterPanel"), 13, bold: true));
        root.Children.Add(Text(Terminology.Help("MasterPanel") + "  These act on everything together, after all the lanes are combined. Type an exact number in any box.", 11, dim: true));

        var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        root.Children.Add(wrap);

        SliderRow Row(string key, double min, double max, double val, string unit, Action<double> set, int decimals = 1)
        {
            IDisposable? scope = null;
            var r = MakeSlider(Terminology.L(key), Terminology.Help(key), min, max, val, unit, set, () => scope ??= BeginEdit?.Invoke(Terminology.L(key)), () => { scope?.Dispose(); scope = null; }, width: 200, decimals: decimals);
            wrap.Children.Add(r.Panel);
            return r;
        }

        Row("MasterGain", -30, 12, m.GainDb, "dB", v => m.GainDb = v);
        Row("Bass", -18, 18, m.BassDb, "dB", v => m.BassDb = v);
        Row("Mid", -18, 18, m.MidDb, "dB", v => m.MidDb = v);
        Row("Treble", -18, 18, m.TrebleDb, "dB", v => m.TrebleDb = v);
        Row("Hiss", 0, 100, m.Hiss, "%", v => m.Hiss = v, 0);
        Row("Rumble", 0, 100, m.Rumble, "%", v => m.Rumble = v, 0);

        var limiterPanel = new StackPanel { Width = 200, Margin = new Thickness(0, 0, 14, 6) };
        var lim = new CheckBox { Content = Terminology.L("Limiter"), IsChecked = m.Limiter, Margin = new Thickness(0, 18, 0, 0) };
        Tip(lim, Terminology.Help("Limiter"));
        lim.Click += (_, _) => { using (BeginEdit?.Invoke("Limiter")) m.Limiter = lim.IsChecked == true; };
        limiterPanel.Children.Add(lim);
        wrap.Children.Add(limiterPanel);

        if (adv)
        {
            var advWrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            root.Children.Add(Text("Filter frequencies", 12, bold: true));
            root.Children.Add(advWrap);
            IDisposable? s1 = null, s2 = null, s3 = null, s4 = null;
            advWrap.Children.Add(MakeSlider("Low shelf corner", "Corner frequency of the low shelf.", 40, 500, m.BassHz, "Hz", v => m.BassHz = v, () => s1 ??= BeginEdit?.Invoke("EQ"), () => { s1?.Dispose(); s1 = null; }, 200, 0).Panel);
            advWrap.Children.Add(MakeSlider("Mid centre", "Centre frequency of the mid peak.", 200, 6000, m.MidHz, "Hz", v => m.MidHz = v, () => s2 ??= BeginEdit?.Invoke("EQ"), () => { s2?.Dispose(); s2 = null; }, 200, 0).Panel);
            advWrap.Children.Add(MakeSlider("Mid Q", "Bandwidth of the mid peak (higher = narrower).", 0.2, 6, m.MidQ, "", v => m.MidQ = v, () => s3 ??= BeginEdit?.Invoke("EQ"), () => { s3?.Dispose(); s3 = null; }, 200, 2).Panel);
            advWrap.Children.Add(MakeSlider("High shelf corner", "Corner frequency of the high shelf.", 1500, 14000, m.TrebleHz, "Hz", v => m.TrebleHz = v, () => s4 ??= BeginEdit?.Invoke("EQ"), () => { s4?.Dispose(); s4 = null; }, 200, 0).Panel);
            root.Children.Add(Text($"De-noise low-pass currently at {Audio.Dsp.BiQuadProcessor.HissCutoff(m.Hiss):0} Hz · low-cut high-pass at {Audio.Dsp.BiQuadProcessor.RumbleCutoff(m.Rumble):0} Hz · limiter ceiling -0.3 dBFS", 11, dim: true));
        }
        else
        {
            root.Children.Add(Text("Not sure where to start? Small moves (a few dB) usually sound best. If the mix crackles when loud, keep \"" + Terminology.L("Limiter") + "\" on.", 11, dim: true));
        }
    }
}
