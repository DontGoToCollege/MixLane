# MixLane

A lightweight, lane-based music mixer for Windows. Drop in any audio or video file, place pieces on
lanes with sample-exact precision, attach fades and effects to them, shape the whole mix, and export.

## Getting the executable

Already built: **`dist\MixLane.exe`** — one self-contained file. No .NET runtime needed on the
target machine, no installer. Copy it anywhere and run it. You can also drag media files onto the
`.exe` (or use "Open with") to start a mix from them.

To rebuild:

```bash
dotnet publish MixLane.csproj -c Release -o dist
```

or run `publish.ps1`, which also does this: Every build is kept: the current one at the top level, and a copy per version in `Versions\Archive` (not in git; each version's program is also on the GitHub Releases page). A version that's already archived is refused, so raise `<Version>` in the csproj first. Targets `net10.0-windows` / `win-x64` (WPF). Retarget to `net8.0-windows`
in the `.csproj` if you prefer; nothing depends on anything newer.

## What's in the shell

| Goal | How it's covered |
|---|---|
| Lightweight & snappy | Custom-drawn timeline (no per-clip WPF controls), waveform peak cache, files fully decoded once into memory, playhead on its own layer, 40 ms WASAPI output. Only two small dependencies (NAudio, NAudio.Vorbis). |
| Beginner friendly, rich for advanced users | Every label exists in two vocabularies. **Plain language** (default) vs **Industry terms** — toggle on the toolbar. Same controls either way; advanced mode also reveals extra knobs (filter frequencies, Q). Every control has a hover explanation. |
| No vagueness | Buttons say what they do ("Cut here", "Blend into next piece", "Reduce hiss & fuzz"). The status bar explains what the thing under the mouse does. |
| All audio formats together | Any file is decoded and resampled to the project rate on import, so 22 kHz mono and 96 kHz stereo sit on the same timeline. Reads mp3, wav, aiff, flac, alac, m4a/aac, wma, ogg (+ whatever Windows Media Foundation can decode). |
| Video → audio | Drop an mp4/mov/mkv/avi/wmv/… and MixLane pulls the audio out and saves it as MP3 / WAV / M4A / WMA using the encoders built into Windows. A dialog asks the first time; tick "use these settings automatically" and it never asks again (change later in Settings). |
| Precise control | All positions are whole samples. Drag with optional edge-snapping (Alt bypasses), nudge with arrow keys (1 ms, Shift 100 ms, Ctrl 1 sample), or type exact values (`1:23.456`, `350ms`, `96000smp`) in the details panel. Zoom down to individual samples (Ctrl+9). |
| Swim lanes | Lanes with mute / solo / volume / pan; clips move between lanes by dragging or ↑/↓. |
| Minimal visual noise | Toolbar holds only transport + zoom + two toggles. Everything else is in menus, right-click and shortcuts. The details panel can be hidden. |
| Effects & transitions in their own lane | Each lane has an effects sub-lane. Effects attach to a piece and cover a region of it (drag to move / resize). Stack as many as you like; collapse the sub-lane to a thin strip with a count badge. "Blend into next piece" (X) creates a crossfade. |
| EQ / mixing / mastering tools | Per-piece effects: fade in/out, volume, mute, bass, treble, muffle (low-pass), thin (high-pass), reduce hiss, reduce rumble, echo, pan. Whole-mix panel: loudness, bass / middle / treble, hiss and rumble clean-up, distortion guard (limiter). |

## Layout

```
 Menu
 [+ Add sounds] [⏮] [▶ Play] [■ Stop]  0:00.000 sample 0  [Loop]        [Stick to edges] [− + Fit] [Plain language]
 ┌──────────┬────────────────────────────────────────────────────────────────┐
 │ Lanes    │ ruler                                                          │
 │ name     │ ┌ piece ────────────────────────┐ ┌ piece ──────┐              │
 │ Silence  │ │ waveform                      │ │             │              │
 │ Only this│ └───────────────────────────────┘ └─────────────┘              │
 │ ▾ Effects│   [Fade in]      [Bass]                [Fade out]              │
 ├──────────┴────────────────────────────────────────────────────────────────┤
 │ Selected piece  |  Whole mix sound                                        │
 │ Starts at [0:00.000] = sample 0     Effects on this piece   [+ Add]       │
 │ Lasts for [0:03.000] ...            (list)      (knobs of selected fx)    │
 └───────────────────────────────────────────────────────────────────────────┘
 status: hint for whatever is under the mouse · zoom · level meter · sample rate
```

## Shortcuts

Space play/pause · Esc stop · Home/End · S cut at playhead · X crossfade · Del · Ctrl+D duplicate ·
Ctrl+Z/Y · ←/→ nudge (Shift 100 ms, Ctrl 1 sample) · ↑/↓ move to other lane · Ctrl+wheel zoom ·
Shift+wheel scroll · Ctrl+0 fit · Ctrl+9 sample zoom · Ctrl+I add files · Ctrl+E export · Ctrl+T new lane.

## Files

* `*.mixlane` — the project (JSON; references the audio files by path, does not copy them).
* Settings: `%AppData%\MixLane\settings.json`.

## Code map

```
Core/      TimeUtil (samples <-> time, parsing), AppSettings, Terminology (plain/pro labels), UndoManager
Models/    Project, Track (lane), Clip (piece), Effect + EffectCatalog
Audio/     AudioDecoder (any file -> stereo float at project rate), MediaConverter (video -> audio file),
           AudioSource (+ peak cache), MixEngine (TimelineRenderer, Player, Exporter), Dsp/ (effects, master chain)
UI/        TimelineControl (custom-drawn lanes/clips/effects + all mouse editing), MainWindow,
           ClipInspector, MasterPanel, FormatDialog, SettingsWindow, UiHelpers
```

## Known limits of this first shell

* Files are decoded entirely into RAM as 32-bit float stereo (about 11 MB per minute at 48 kHz).
  Fine for songs; very long files add up.
* Opus and WebM depend on codecs installed in Windows; Ogg Vorbis is handled internally.
* No time-stretch / pitch-shift, no recording, no automation curves yet.

## Download

Download `MixLane.exe` from the [latest release](https://github.com/DontGoToCollege/MixLane/releases/latest). It's a single program: no installer, just run it.

## License

MIT: free for anyone to use, change and share. See [LICENSE](LICENSE).
