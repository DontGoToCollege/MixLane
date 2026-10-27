namespace MixLane.Audio;

/// <summary>
/// A fully decoded audio file: interleaved stereo 32-bit float at the project sample rate.
/// Decoding everything up front is what makes scrubbing, splitting and playback feel instant.
/// Also holds a min/max peak cache so waveforms draw quickly at any zoom level.
/// </summary>
public sealed class AudioSource
{
    public const int PeakBlock = 256;

    public string Path { get; }
    public int SampleRate { get; }
    /// <summary>Interleaved L,R,L,R...</summary>
    public float[] Samples { get; }
    /// <summary>Length in frames (one frame = one left + one right sample).</summary>
    public long Length => Samples.Length / 2;
    public double DurationSeconds => Length / (double)SampleRate;

    /// <summary>Per PeakBlock frames: [min, max] of both channels combined.</summary>
    public float[] PeakMin { get; }
    public float[] PeakMax { get; }

    public AudioSource(string path, int sampleRate, float[] samples)
    {
        Path = path;
        SampleRate = sampleRate;
        Samples = samples;
        int blocks = (int)((Length + PeakBlock - 1) / PeakBlock);
        PeakMin = new float[blocks];
        PeakMax = new float[blocks];
        for (int b = 0; b < blocks; b++)
        {
            long s = (long)b * PeakBlock * 2;
            long e = Math.Min(s + PeakBlock * 2, samples.Length);
            float mn = 0, mx = 0;
            for (long i = s; i < e; i++)
            {
                float v = samples[i];
                if (v < mn) mn = v; else if (v > mx) mx = v;
            }
            PeakMin[b] = mn; PeakMax[b] = mx;
        }
    }

    /// <summary>Min/max over an arbitrary frame range, using the cache for coarse ranges and raw samples for fine ones.</summary>
    public void GetPeak(long fromFrame, long toFrame, out float min, out float max)
    {
        min = 0; max = 0;
        if (fromFrame < 0) fromFrame = 0;
        if (toFrame > Length) toFrame = Length;
        if (toFrame <= fromFrame) return;

        long span = toFrame - fromFrame;
        if (span >= PeakBlock * 2)
        {
            int b0 = (int)(fromFrame / PeakBlock), b1 = (int)((toFrame - 1) / PeakBlock);
            for (int b = b0; b <= b1 && b < PeakMin.Length; b++)
            {
                if (PeakMin[b] < min) min = PeakMin[b];
                if (PeakMax[b] > max) max = PeakMax[b];
            }
        }
        else
        {
            long s = fromFrame * 2, e = toFrame * 2;
            for (long i = s; i < e; i++)
            {
                float v = Samples[i];
                if (v < min) min = v; else if (v > max) max = v;
            }
        }
    }
}
