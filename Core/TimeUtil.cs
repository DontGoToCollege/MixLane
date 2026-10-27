using System.Globalization;

namespace MixLane.Core;

/// <summary>
/// Everything on the timeline is positioned in whole samples at the project sample rate.
/// That is the finest unit of precision that exists in digital audio, so the user can place
/// things exactly where they want with no rounding to beats or seconds.
/// These helpers convert between samples and human-readable time.
/// </summary>
public static class TimeUtil
{
    public static double SamplesToSeconds(long samples, int rate) => samples / (double)rate;
    public static long SecondsToSamples(double seconds, int rate) => (long)Math.Round(seconds * rate);

    /// <summary>h:mm:ss.mmm (hours only when non-zero).</summary>
    public static string FormatSeconds(double seconds, bool showMicro = false)
    {
        if (seconds < 0) return "-" + FormatSeconds(-seconds, showMicro);
        var whole = Math.Floor(seconds);
        var ts = TimeSpan.FromSeconds(whole);
        double fracValue = seconds - whole;
        string frac = showMicro
            ? fracValue.ToString("0.000000", CultureInfo.InvariantCulture)[1..]
            : fracValue.ToString("0.000", CultureInfo.InvariantCulture)[1..];
        return ts.Hours > 0
            ? $"{ts.Hours}:{ts.Minutes:00}:{ts.Seconds:00}{frac}"
            : $"{ts.Minutes}:{ts.Seconds:00}{frac}";
    }

    public static string FormatSamples(long samples, int rate) => FormatSeconds(SamplesToSeconds(samples, rate));

    /// <summary>
    /// Accepts "1:23.456", "83.456", "83.456s", "1234ms", "48000smp" / "48000 samples", or "0:01:23.456".
    /// Returns null if the text can't be understood.
    /// </summary>
    public static long? ParseToSamples(string text, int rate)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().ToLowerInvariant().Replace(" ", "");
        var inv = CultureInfo.InvariantCulture;

        if (text.EndsWith("samples") || text.EndsWith("smp") || text.EndsWith("sa"))
        {
            var digits = new string(text.TakeWhile(c => char.IsDigit(c) || c == '-').ToArray());
            return long.TryParse(digits, out var s) ? s : null;
        }
        if (text.EndsWith("ms"))
        {
            return double.TryParse(text[..^2], NumberStyles.Float, inv, out var ms) ? SecondsToSamples(ms / 1000.0, rate) : null;
        }
        if (text.EndsWith("s") && !text.Contains(':'))
        {
            return double.TryParse(text[..^1], NumberStyles.Float, inv, out var sec) ? SecondsToSamples(sec, rate) : null;
        }
        if (text.Contains(':'))
        {
            var parts = text.Split(':');
            double total = 0;
            foreach (var p in parts)
            {
                if (!double.TryParse(p, NumberStyles.Float, inv, out var v)) return null;
                total = total * 60 + v;
            }
            return SecondsToSamples(total, rate);
        }
        return double.TryParse(text, NumberStyles.Float, inv, out var plain) ? SecondsToSamples(plain, rate) : null;
    }

    public static string FormatDb(double db) => (db >= 0 ? "+" : "") + db.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
    public static double DbToLinear(double db) => Math.Pow(10.0, db / 20.0);
    public static double LinearToDb(double lin) => lin <= 1e-9 ? -180 : 20.0 * Math.Log10(lin);
}
