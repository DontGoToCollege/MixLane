using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Vorbis;

namespace MixLane.Audio;

/// <summary>
/// Turns any supported file into an <see cref="AudioSource"/> at the project sample rate.
/// Windows Media Foundation handles the bulk of formats (mp3, wav, aiff, wma, aac, m4a, flac, alac and the
/// audio tracks of mp4/mov/mkv/avi/wmv video); NVorbis handles ogg. Everything is resampled to one rate
/// so files with different rates and channel counts play together without the user doing anything.
/// </summary>
public static class AudioDecoder
{
    public static readonly string[] AudioExtensions =
        { ".mp3", ".wav", ".wave", ".aif", ".aiff", ".aifc", ".flac", ".m4a", ".aac", ".wma", ".ogg", ".oga", ".opus", ".alac", ".mp2", ".ac3", ".amr", ".caf", ".mka" };
    public static readonly string[] VideoExtensions =
        { ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts", ".3gp", ".asf", ".vob" };

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsSupported(string path) => IsVideo(path) || IsAudio(path);

    public static string OpenFileFilter =>
        "All supported media|" + string.Join(";", AudioExtensions.Concat(VideoExtensions).Select(e => "*" + e)) +
        "|Audio files|" + string.Join(";", AudioExtensions.Select(e => "*" + e)) +
        "|Video files|" + string.Join(";", VideoExtensions.Select(e => "*" + e)) +
        "|All files|*.*";

    /// <summary>Open a reader for the file. Caller disposes.</summary>
    public static WaveStream OpenReader(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".ogg" or ".oga")
            return new VorbisWaveReader(path);
        if (ext is ".wav" or ".wave")
            return new WaveFileReader(path);
        if (ext is ".aif" or ".aiff" or ".aifc")
            return new AiffFileReader(path);
        // Media Foundation: mp3, wma, aac, m4a, flac, alac, and video containers.
        return new MediaFoundationReader(path);
    }

    /// <summary>Build a float stereo sample provider at the target rate from any reader.</summary>
    public static ISampleProvider ToStereoFloat(WaveStream reader, int targetRate)
    {
        ISampleProvider sp = reader.ToSampleProvider();
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2) sp = new DownmixToStereo(sp);
        if (sp.WaveFormat.SampleRate != targetRate) sp = new WdlResamplingSampleProvider(sp, targetRate);
        return sp;
    }

    public static AudioSource Decode(string path, int targetRate, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var reader = OpenReader(path);
        var sp = ToStereoFloat(reader, targetRate);

        long estFrames = (long)(reader.TotalTime.TotalSeconds * targetRate);
        var chunks = new List<float[]>();
        long total = 0;
        var buf = new float[targetRate * 2]; // 1 second per read
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int n = sp.Read(buf, 0, buf.Length);
            if (n <= 0) break;
            var copy = new float[n];
            Array.Copy(buf, copy, n);
            chunks.Add(copy);
            total += n;
            if (estFrames > 0) progress?.Report(Math.Min(0.99, (total / 2.0) / estFrames));
        }
        var all = new float[total];
        long pos = 0;
        foreach (var c in chunks) { Array.Copy(c, 0, all, pos, c.Length); pos += c.Length; }
        progress?.Report(1);
        return new AudioSource(path, targetRate, all);
    }

    public static Task<AudioSource> DecodeAsync(string path, int targetRate, IProgress<double>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Decode(path, targetRate, progress, ct), ct);

    /// <summary>Simple N-channel to stereo downmix (odd channels to L, even to R, scaled).</summary>
    sealed class DownmixToStereo : ISampleProvider
    {
        readonly ISampleProvider _src;
        readonly int _ch;
        float[] _tmp = Array.Empty<float>();
        public DownmixToStereo(ISampleProvider src)
        {
            _src = src; _ch = src.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
        }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int need = frames * _ch;
            if (_tmp.Length < need) _tmp = new float[need];
            int got = _src.Read(_tmp, 0, need) / _ch;
            float scale = 2f / _ch;
            for (int f = 0; f < got; f++)
            {
                float l = 0, r = 0;
                for (int c = 0; c < _ch; c++)
                {
                    float v = _tmp[f * _ch + c];
                    if (c % 2 == 0) l += v; else r += v;
                }
                buffer[offset + f * 2] = l * scale;
                buffer[offset + f * 2 + 1] = r * scale;
            }
            return got * 2;
        }
    }
}
