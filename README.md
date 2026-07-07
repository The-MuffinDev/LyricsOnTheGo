<div align="center">

<img src="lyricsonthego.png" alt="LyricsOnTheGo logo" width="120" />

# LyricsOnTheGo

**A lightweight glass overlay that shows real-time synced lyrics for whatever music is playing on Windows.**

Native Windows app built with WPF (.NET 8) · No account or login required

<sub>A native Windows rewrite of the original <a href="https://github.com/The-MuffinDev/LyricsOnTheGo-Rust">LyricsOnTheGo</a>.</sub>

<br />

<a href="https://www.buymeacoffee.com/TheMuffinDev" target='_blank'><img src="https://img.buymeacoffee.com/button-api/?text=Buy me a coffee&emoji=&slug=TheMuffinDev&button_colour=FFDD00&font_colour=000000&font_family=Cookie&outline_colour=000000&coffee_colour=ffffff" alt="Buy me a coffee" /></a>

</div>

---

## What it is

LyricsOnTheGo is an always-on-top desktop overlay that displays the lyrics of the current
song, **synchronized line-by-line** with playback, inside a frosted-glass card you can drag
anywhere and fully customize.

It reads what is playing directly from **Windows System Media Transport Controls (SMTC)** — the
same system behind the little media popup that appears when you change the volume — so it works
with **any player that reports to Windows media controls**: the Spotify desktop app, browsers,
and most media players. Synced lyrics come from the free, open [LRCLIB](https://lrclib.net)
database.

## Features

- **Real-time synchronized lyrics** — the active line is highlighted and auto-centered, with
  local interpolation between polls so it tracks the playback head smoothly (no heavy repaint loop).
- **Persistent glass** — an acrylic frosted-glass blur with rounded corners that **stays visible
  even while the overlay is inactive** (unfocused), on both Windows 10 and 11.
- **Karaoke mode** — one click switches to borderless fullscreen with large, centered lyrics; the
  mouse cursor auto-hides after a couple of seconds, like a video player. Double-clicking the
  header also toggles borderless fullscreen.
- **Hands-on lyrics** — scroll freely with the mouse wheel (smooth, momentum-style), a click-drag,
  or the thin scrollbar; a **Resync** button snaps back to the active line. **Ctrl + wheel** resizes
  the text on the fly.
- **Bilingual UI (English / Spanish)** — English by default, with an in-app EN/ES switch.
- **Deeply customizable** (persisted): text color (with a screen **eyedropper**), background color
  and opacity, font size, inactive-line dimming, and alignment.
- **Per-song sync offset** — nudge a song that's slightly out of sync (±6 s; **+** delays the line,
  **−** advances it). The offset is remembered **per song** and resets for the next track, so a
  single tuned song never shifts the rest.
- **On-disk lyrics cache** — each song is fetched at most once and loads instantly on replay
  (survives restarts).
- **Smart visibility** — an auto-hiding header (fade-up), plus a toggleable progress bar and time
  labels; the header briefly reappears when the song changes and shows the current title and artist.
- **System tray** — closing the window hides it to the tray; quitting is only from the tray menu.
  Click-through mode (ignore the mouse) is toggleable and always recoverable from the tray. A pin
  button toggles always-on-top.
- **Start with Windows** — optional autostart at login, from settings.
- **Tolerant lyrics matching** — works even when a player reports messy metadata. When the source is
  a **browser** (YouTube, etc.), where the channel/uploader is reported as the artist, the app
  searches **by title only** — both the exact video title and a cleaned version (decorations like
  "(Official Video)" stripped) — and ranks every candidate by whether its track name appears in the
  video title, then by closest duration, so it lands the right synced lyrics instead of a wrong-song
  near-match.
- **Plain-lyrics fallback** — if only unsynced lyrics exist, they're shown and can be scrolled.

## The glass

The headline feature — a blur that persists while the window is inactive — is only possible with a
manually composed **`Windows.UI.Composition`** acrylic brush (the same technique Windows Terminal
uses for its unfocused acrylic). That brush can't be shown through a transparent region of a normal
WPF window, so the overlay is built from **two coordinated windows**:

```
┌────────────────────────────────────────────────────────────┐
│  UI window  (WPF, AllowsTransparency)                       │
│  • lyrics, header, settings, tray, input, drag/resize       │
│  • visually transparent so the glass shows through          │
└───────────────▲────────────────────────────────────────────┘
                │  glued on top (SetWindowPos), size + position synced
┌───────────────┴────────────────────────────────────────────┐
│  Glass window  (borderless Win32, own STA thread)           │
│  • hosts the persistent Windows.UI.Composition acrylic       │
│    (CreateHostBackdropBrush + tint + rounded clip)           │
└────────────────────────────────────────────────────────────┘
```

The transparent WPF window sits exactly on top of the glass window and stays fully hit-testable, so
all interaction (dragging, resizing, clicks, scrolling) happens there while the blur renders behind
it. The now-playing state comes from SMTC on a 1-second poll; the position is interpolated locally
at ~4 Hz so the active line and progress move smoothly without a 60 fps loop. Lyrics are fetched
through a bounded retry state machine and cached to disk keyed by a hash of the track metadata.

## Tech stack

| Layer       | Tech                                                                        |
| ----------- | --------------------------------------------------------------------------- |
| UI          | WPF (.NET 8, `net8.0-windows10.0.19041`), XAML                              |
| Glass       | `Windows.UI.Composition` acrylic via WinRT interop (own dispatcher thread)  |
| Now-playing | Windows SMTC (`GlobalSystemMediaTransportControlsSessionManager`)           |
| Lyrics      | [LRCLIB](https://lrclib.net) public API · `HttpClient` · `System.Text.Json` |
| Tray        | `System.Windows.Forms.NotifyIcon`                                           |

## Requirements

- **Windows 10 or 11.** The installer ships a self-contained build, so **no .NET runtime is
  required** to run it.
- A media player (Spotify desktop app, a browser, etc.) open and playing.

To build from source you also need the [.NET 8 SDK](https://dotnet.microsoft.com/download).

## Install

1. **Download the latest installer** — grab the `LyricsOnTheGo-<version>.msi` from the
   [**latest release**](https://github.com/The-MuffinDev/LyricsOnTheGo/releases/latest) and run it.
2. Open your music player and start a song. That's all.

Every version and its release notes are on the
[**Releases**](https://github.com/The-MuffinDev/LyricsOnTheGo/releases) page.

## Offline mode — local LRCLIB database (optional)

By default, lyrics are fetched from the public [LRCLIB](https://lrclib.net) API, which can slow down
or time out at peak hours. For **instant, fully offline** lookups you can link the complete LRCLIB
database and read lyrics straight from disk — no network requests, and the app is never left waiting
on the public API.

1. Download the database dump from [**lrclib.net/db-dumps**](https://lrclib.net/db-dumps) and
   decompress it (**~117 GB** uncompressed).
2. In the app: **Settings → Local database → Select database file…** and pick the `db.sqlite3`.

Once linked, the local database becomes the primary source and results are effectively
instantaneous; the public API is kept only as an automatic fallback for songs newer than your dump.
The file is opened read-only (nothing is written to it) and can be unlinked at any time.

## Build from source

```powershell
# Build
dotnet build src\LyricsOnTheGo\LyricsOnTheGo.csproj -c Debug

# Run
.\src\LyricsOnTheGo\bin\Debug\net8.0-windows10.0.19041.0\LyricsOnTheGo.exe
```

To produce the MSI installer (WiX Toolset v5, see [`installer/`](installer/README.md)):

```powershell
dotnet tool install --global wix
wix extension add -g WixToolset.UI.wixext
powershell -File installer\build-msi.ps1   # -> dist\LyricsOnTheGo-<version>.msi
```

## Usage

- **Move it:** drag the header (the top bar).
- **Settings (gear):** colors, font size, opacity, alignment, sync offset, visibility, cache.
- **Karaoke (mic):** borderless-fullscreen, large centered lyrics.
- **Pin:** toggle always-on-top.
- **Close (×):** hides the overlay to the system tray (it keeps running).
- **Tray icon:** left-click to show/hide; right-click for _Show/Hide_, _Click-through_, _Quit_.
- **Scroll the lyrics:** mouse wheel, click-drag, or the scrollbar; **Ctrl + wheel** resizes text.
  A **Resync** button appears when you scroll away — click it to return to the active line.

## Project structure

```
src/LyricsOnTheGo/
  Glass/            persistent-acrylic backdrop window (own STA thread)
  Interop/          Win32 + WinRT composition interop
  Services/         SMTC reader, LRCLIB client, disk cache, per-song offsets, i18n, tray, autostart
  Models/           Settings, NowPlaying, lyric types
  Controls/         SettingsPanel, ColorPicker (HSV + eyedropper)
  MainWindow.xaml   overlay UI (lyrics, header, footer)
installer/          WiX v5 MSI (branding, wizard images, build script)
```

## Lyrics & privacy

- Lyrics are fetched from **LRCLIB** (free, no auth). Please consider supporting or contributing to
  LRCLIB.
- The app sends no personal data; it only reads the current track metadata exposed by Windows and
  queries LRCLIB by song title / artist.

## Author

Created by **Luis Anchondo(TheMuffinDev)** — feedback and contributions are welcome.

## Support

If LyricsOnTheGo is useful to you, you can support its development at:

<a href="https://www.buymeacoffee.com/TheMuffinDev">buymeacoffee.com/TheMuffinDev</a>

## License

Released under the [MIT License](LICENSE).
