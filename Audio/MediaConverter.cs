using System.IO;
using MixLane.Core;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MixLane.Audio;

/// <summary>
/// Converts a video (or any audio file) to the user's chosen audio format using the encoders that ship with Windows.
/// The user never has to run a separate tool to get the audio out of a video.
/// </summary>
public static class MediaConverter
{
    public static string ExtensionFor(AudioFormat f) => f switch
    {
        AudioFormat.Mp3 => ".mp3",
        AudioFormat.Wav => ".wav",
        AudioFormat.M4a => ".m4a",
        AudioFormat.Wma => ".wma",
        _ => ".mp3"
    };

    public static string DescribeFormat(AudioFormat f) => f switch
    {
        AudioFormat.Mp3 => "MP3 — small files, plays everywhere",
        AudioFormat.Wav => "WAV — full quality, large files",
        AudioFormat.M4a => "M4A (AAC) — small files, good quality",
        AudioFormat.Wma => "WMA — Windows Media Audio",
        _ => f.ToString()
    };

    public static string OutputPathFor(string inputPath, ConversionDefaults d)
    {
        string dir = d.SaveNextToSource || string.IsNullOrWhiteSpace(d.OutputFolder)
            ? Path.GetDirectoryName(inputPath) ?? "."
            : d.OutputFolder;
        string name = Path.GetFileNameWithoutExtension(inputPath) + ExtensionFor(d.Format);
        string candidate = Path.Combine(dir, name);
        int i = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(inputPath)} ({i++}){ExtensionFor(d.Format)}");
        return candidate;
    }

    /// <summary>Convert and return the written output path.</summary>
    public static string Convert(string inputPath, string outputPath, ConversionDefaults d, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var reader = AudioDecoder.OpenReader(inputPath);
        ISampleProvider sp = reader.ToSampleProvider();
        if (d.Channels == 1 && sp.WaveFormat.Channels > 1) sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        else if (d.Channels == 2 && sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        if (sp.WaveFormat.SampleRate != d.SampleRate) sp = new WdlResamplingSampleProvider(sp, d.SampleRate);

        var counted = new ProgressSampleProvider(sp, (long)(reader.TotalTime.TotalSeconds * d.SampleRate), progress, ct);
        IWaveProvider pcm16 = new SampleToWaveProvider16(counted);

        try
        {
            switch (d.Format)
            {
                case AudioFormat.Wav:
                    WaveFileWriter.CreateWaveFile(outputPath, pcm16);
                    break;
                case AudioFormat.Mp3:
                    MediaFoundationEncoder.EncodeToMp3(pcm16, outputPath, d.BitrateKbps * 1000);
                    break;
                case AudioFormat.M4a:
                    MediaFoundationEncoder.EncodeToAac(pcm16, outputPath, d.BitrateKbps * 1000);
                    break;
                case AudioFormat.Wma:
                    MediaFoundationEncoder.EncodeToWma(pcm16, outputPath, d.BitrateKbps * 1000);
                    break;
            }
        }
        catch
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            throw;
        }
        progress?.Report(1);
        return outputPath;
    }

    public static Task<string> ConvertAsync(string inputPath, string outputPath, ConversionDefaults d, IProgress<double>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Convert(inputPath, outputPath, d, progress, ct), ct);

    sealed class ProgressSampleProvider : ISampleProvider
    {
        readonly ISampleProvider _src; readonly long _totalFrames; readonly IProgress<double>? _p; readonly CancellationToken _ct;
        long _done;
        public ProgressSampleProvider(ISampleProvider src, long totalFrames, IProgress<double>? p, CancellationToken ct) { _src = src; _totalFrames = totalFrames; _p = p; _ct = ct; }
        public WaveFormat WaveFormat => _src.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            _ct.ThrowIfCancellationRequested();
            int n = _src.Read(buffer, offset, count);
            _done += n / WaveFormat.Channels;
            if (_totalFrames > 0) _p?.Report(Math.Min(0.99, _done / (double)_totalFrames));
            return n;
        }
    }
}
