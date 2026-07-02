# Installer (LyricsOnTheGo)

Builds a Windows **`.msi`** with **WiX Toolset v5**. Per-machine x64 install with the standard
WixUI wizard (branded banner/dialog; the license page is skipped — MIT/open source), Start Menu
**and Desktop** shortcuts, and a "Launch LyricsOnTheGo" checkbox on the finish page. The MSI ships
a **self-contained** build, so the target PC does **not** need the .NET runtime.

Autostart ("Start with Windows") is handled by the app's own settings toggle, which writes the
per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value.

## One-time prerequisites (install manually)
WiX v5 installs as a .NET tool — no separate installer needed:

```powershell
dotnet tool install --global wix
wix extension add -g WixToolset.UI.wixext
```

(If `wix` isn't found afterwards, open a new terminal so `%USERPROFILE%\.dotnet\tools` is on PATH.)

## Build
From the repo root:

```powershell
powershell -File installer\build-msi.ps1
```

This publishes to `publish\`, ensures the wizard images exist, and compiles
`dist\LyricsOnTheGo-<version>.msi` (the version is read from `<Version>` in
`LyricsOnTheGo.csproj`). If WiX isn't installed it stops after publishing and prints the
install command.

## Upload to GitHub Releases
With the GitHub CLI (`gh`):

```powershell
gh release create v<version> dist\LyricsOnTheGo-<version>.msi --title "LyricsOnTheGo <version>" --notes "..."
```

Or via the web UI: Releases → Draft a new release → tag `v<version>` → attach `dist\LyricsOnTheGo-<version>.msi`.

## Files
- `LyricsOnTheGo.wxs` — WiX v5 source (package, files harvest, shortcuts, branding, UI). Built
  with the working directory set to `installer\` (build-msi.ps1 handles that) so its relative
  source paths resolve correctly.
- `license.rtf` — MIT license (kept for reference; the wizard's license page is skipped).
- `make-installer-images.ps1` — regenerates `wix-banner.bmp` (493×58) + `wix-dialog.bmp` (493×312)
  from `lyricsonthego.png` (System.Drawing; dark `#0B0B0B`, white name, gray tagline, accent underline).
- `wix-banner.bmp` / `wix-dialog.bmp` — generated wizard art.
- `build-msi.ps1` — one-shot publish + compile.

## Notes
- Output name has no language tag (`LyricsOnTheGo-<version>.msi`).
- The MSI is **per-machine** (prompts for admin). To ship a smaller MSI that **requires** the
  .NET 8 Desktop Runtime instead, change the publish line in `build-msi.ps1` to `--self-contained false`.
