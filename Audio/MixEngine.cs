using MixLane.Audio.Dsp;
using MixLane.Core;
using MixLane.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MixLane.Audio;

/// <summary>
/// Renders the project timeline sample-by-sample. Used both for live playback and for exporting.
/// Structural changes (clips added/removed, effects added/removed) go through <see cref="Project.Touch"/>, which
/// rebuilds a lightweight snapshot on the UI thread; the audio thread only ever reads the current snapshot.
/// Simple numeric properties (gain, pan, mute, positions) are read live from the model, so slider moves are heard instantly.
/// </summary>
public sealed class TimelineRenderer : ISampleProvider
{
    sealed class ClipR { public Clip Clip = null!; public EffectProcessor[] Fx = Array.Empty<EffectProcessor>(); }
    sealed class TrackR { public Track Track = null!; public ClipR[] Clips = Array.Empty<ClipR>(); }

    readonly Project _project;
    readonly MasterChain _master;
    readonly Dictionary<Guid, EffectProcessor> _fxCache = new();
    TrackR[] _snapshot = Array.Empty<TrackR>();
    int _builtVersion = -1;
    float[] _clipBuf = new float[8192];
    long _position;

    public WaveFormat WaveFormat { get; }
    public int SampleRate => _project.SampleRate;
    /// <summary>Playback position in frames. Safe to set from the UI thread.</summary>
    public long Position { get => Interlocked.Read(ref _position); set { Interlocked.Exchange(ref _position, Math.Max(0, value)); NeedsReset = true; } }
    public bool Loop { get; set; }
    public long LoopStart { get; set; }
    public long LoopEnd { get; set; }
    /// <summary>When true, filters/echo states are cleared before the next block (after a seek).</summary>
    public volatile bool NeedsReset;
    /// <summary>Peak level of the last rendered block (0..1) per channel, for the meter.</summary>
    public float PeakL, PeakR;

    public TimelineRenderer(Project project)
    {
        _project = project;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(project.SampleRate, 2);
        _master = new MasterChain(project.Master, project.SampleRate);
        project.Changed += Rebuild;
        Rebuild();
    }

    /// <summary>Stop listening to the project (used by the exporter's throwaway renderer).</summary>
    public void Detach() => _project.Changed -= Rebuild;

    /// <summary>Called on the UI thread whenever the project structure changes.</summary>
    public void Rebuild()
    {
        var tracks = new List<TrackR>();
        var seen = new HashSet<Guid>();
        foreach (var t in _project.Tracks)
        {
            var tr = new TrackR { Track = t };
            var clips = new List<ClipR>();
            foreach (var c in t.Clips)
            {
                if (c.Source == null) continue;
                var cr = new ClipR { Clip = c };
                var fx = new List<EffectProcessor>();
                foreach (var e in c.Effects)
                {
                    seen.Add(e.Id);
                    if (!_fxCache.TryGetValue(e.Id, out var p) || p.Source.Kind != e.Kind)
                    {
                        p?.Detach();
                        p = EffectProcessor.Create(e, _project.SampleRate);
                        _fxCache[e.Id] = p;
                    }
                    fx.Add(p);
                }
                cr.Fx = fx.ToArray();
                clips.Add(cr);
            }
            tr.Clips = clips.ToArray();
            tracks.Add(tr);
        }
        foreach (var k in _fxCache.Keys.Where(k => !seen.Contains(k)).ToList()) { _fxCache[k].Detach(); _fxCache.Remove(k); }
        Volatile.Write(ref _snapshot, tracks.ToArray());
        _builtVersion = _project.Version;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        Array.Clear(buffer, offset, count);
        var snap = Volatile.Read(ref _snapshot);
        long pos = Interlocked.Read(ref _position);

        if (NeedsReset)
        {
            NeedsReset = false;
            _master.Reset();
            foreach (var p in _fxCache.Values) p.Reset();
        }

        if (Loop && LoopEnd > LoopStart && pos >= LoopEnd) pos = LoopStart;
        long blockEnd = pos + frames;
        if (Loop && LoopEnd > LoopStart && blockEnd > LoopEnd) { blockEnd = LoopEnd; frames = (int)(blockEnd - pos); }

        bool anySolo = false;
        foreach (var t in snap) if (t.Track.Solo) { anySolo = true; break; }

        if (_clipBuf.Length < frames * 2) _clipBuf = new float[frames * 2];

        foreach (var t in snap)
        {
            var track = t.Track;
            if (track.Mute || (anySolo && !track.Solo)) continue;
            float tg = (float)TimeUtil.DbToLinear(track.GainDb);
            double pan = track.Pan / 100.0;
            double a = (pan + 1) * Math.PI / 4;
            float gl = tg * (float)(Math.Cos(a) * 1.4142), gr = tg * (float)(Math.Sin(a) * 1.4142);

            foreach (var cr in t.Clips)
            {
                var c = cr.Clip;
                var src = c.Source; if (src == null) continue;
                long cs = c.Start, ce = c.Start + c.Length;
                long s = Math.Max(cs, pos), e = Math.Min(ce, blockEnd);
                if (e <= s) continue;
                int n = (int)(e - s);
                long srcFrame = c.Offset + (s - cs);
                long avail = src.Length - srcFrame;
                if (avail <= 0) continue;
                int copy = (int)Math.Min(n, avail);
                Array.Copy(src.Samples, srcFrame * 2, _clipBuf, 0, copy * 2);
                if (copy < n) Array.Clear(_clipBuf, copy * 2, (n - copy) * 2);

                float cg = (float)TimeUtil.DbToLinear(c.GainDb);
                if (cg != 1f) for (int i = 0; i < n * 2; i++) _clipBuf[i] *= cg;

                foreach (var p in cr.Fx) p.Process(_clipBuf, 0, n, s - cs);

                int dst = offset + (int)(s - pos) * 2;
                for (int i = 0; i < n; i++)
                {
                    buffer[dst + i * 2] += _clipBuf[i * 2] * gl;
                    buffer[dst + i * 2 + 1] += _clipBuf[i * 2 + 1] * gr;
                }
            }
        }

        _master.Process(buffer, offset, frames);

        float pl = 0, pr = 0;
        for (int i = 0; i < frames; i++)
        {
            float l = Math.Abs(buffer[offset + i * 2]), r = Math.Abs(buffer[offset + i * 2 + 1]);
            if (l > pl) pl = l; if (r > pr) pr = r;
        }
        PeakL = pl; PeakR = pr;

        Interlocked.Exchange(ref _position, pos + frames);
        return frames * 2;
    }
}

/// <summary>Owns the output device and the transport (play / pause / stop / seek).</summary>
public sealed class Player : IDisposable
{
    IWavePlayer? _out;
    readonly TimelineRenderer _renderer;
    readonly Project _project;

    public Player(Project project)
    {
        _project = project;
        _renderer = new TimelineRenderer(project);
    }

    public TimelineRenderer Renderer => _renderer;
    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;
    public long Position { get => _renderer.Position; set => _renderer.Position = value; }
    public string DeviceInfo { get; private set; } = "";
    public event Action? PlaybackStopped;

    void EnsureDevice()
    {
        if (_out != null) return;
        try
        {
            var w = new WasapiOut(AudioClientShareMode.Shared, true, 40);
            w.Init(new EndlessProvider(_renderer));
            _out = w;
            DeviceInfo = "WASAPI shared, 40 ms";
        }
        catch
        {
            var w = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
            w.Init(new EndlessProvider(_renderer));
            _out = w;
            DeviceInfo = "WaveOut, 120 ms";
        }
        _out.PlaybackStopped += (_, _) => PlaybackStopped?.Invoke();
    }

    public void Play()
    {
        EnsureDevice();
        if (_renderer.Position >= _project.LengthSamples && _project.LengthSamples > 0) _renderer.Position = 0;
        _renderer.NeedsReset = true;
        _out!.Play();
    }
    public void Pause() { _out?.Pause(); }
    public void Stop(long? returnTo = null)
    {
        _out?.Pause();
        if (returnTo.HasValue) _renderer.Position = returnTo.Value;
    }
    public void TogglePlay() { if (IsPlaying) Pause(); else Play(); }

    public void Dispose() { _out?.Dispose(); _out = null; }

    /// <summary>WASAPI stops the device when Read returns 0; we always hand back a full block so the transport stays open.</summary>
    sealed class EndlessProvider : ISampleProvider
    {
        readonly TimelineRenderer _r;
        public EndlessProvider(TimelineRenderer r) { _r = r; }
        public WaveFormat WaveFormat => _r.WaveFormat;
        public int Read(float[] buffer, int offset, int count) { _r.Read(buffer, offset, count); return count; }
    }
}

/// <summary>Renders the whole project offline to an audio file.</summary>
public static class Exporter
{
    public static void Export(Project project, string outputPath, AudioFormat format, int bitrateKbps, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var r = new TimelineRenderer(project) { Position = 0 };
        long total = project.LengthSamples;
        var bounded = new BoundedProvider(r, total, progress, ct);
        var d = new ConversionDefaults { Format = format, BitrateKbps = bitrateKbps, SampleRate = project.SampleRate, Channels = 2 };
        IWaveProvider pcm = format == AudioFormat.Wav ? new NAudio.Wave.SampleProviders.SampleToWaveProvider(bounded) : new NAudio.Wave.SampleProviders.SampleToWaveProvider16(bounded);
        switch (format)
        {
            case AudioFormat.Wav: WaveFileWriter.CreateWaveFile(outputPath, pcm); break;
            case AudioFormat.Mp3: MediaFoundationEncoder.EncodeToMp3(pcm, outputPath, bitrateKbps * 1000); break;
            case AudioFormat.M4a: MediaFoundationEncoder.EncodeToAac(pcm, outputPath, bitrateKbps * 1000); break;
            case AudioFormat.Wma: MediaFoundationEncoder.EncodeToWma(pcm, outputPath, bitrateKbps * 1000); break;
        }
        r.Detach();
        progress?.Report(1);
    }

    sealed class BoundedProvider : ISampleProvider
    {
        readonly TimelineRenderer _r; readonly long _total; readonly IProgress<double>? _p; readonly CancellationToken _ct;
        public BoundedProvider(TimelineRenderer r, long total, IProgress<double>? p, CancellationToken ct) { _r = r; _total = total; _p = p; _ct = ct; }
        public WaveFormat WaveFormat => _r.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            _ct.ThrowIfCancellationRequested();
            long remaining = _total - _r.Position;
            if (remaining <= 0) return 0;
            int frames = (int)Math.Min(count / 2, remaining);
            _r.Read(buffer, offset, frames * 2);
            _p?.Report(Math.Min(0.99, _r.Position / (double)Math.Max(1, _total)));
            return frames * 2;
        }
    }
}
