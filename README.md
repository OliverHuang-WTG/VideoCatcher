# VideoCatcher

English | [简体中文](README.zh-CN.md)

A Windows video grabber with a built-in browser. Browse as usual; videos that play show up in the sidebar, and one click on Download saves them as files PotPlayer plays directly.

Built on WPF, WebView2 (the Edge engine) and ffmpeg.

## Features

- **Automatic capture**: watches page network responses for `video/*`, `audio/*`, m3u8 (HLS), mpd (DASH) and common video extensions. Small files are filtered out by size, and range requests for the same file show up once. m3u8 entries show an estimated size, resolution and duration, and the variant playlists of a master playlist aren't listed separately.
- **Parallel downloads**: HLS segments download 8 at a time (AES-128 decryption included), and direct files from servers that support byte ranges download in 8 parts at once. ffmpeg then only remuxes the local files.
- **Automatic naming**: uses the source file name, taken from the server's `Content-Disposition` header or else the last URL path segment. When there is none, or it's a generic name like `index`, `master` or `playlist`, 8 random letters are used instead. Invalid characters are replaced, and name clashes get a ` (1)` suffix.
- **Automatic remux**: everything goes through ffmpeg `-c copy`, which changes only the container, so there's no quality loss.
- **Proxy**: `http://` and `socks5://` (credentials optional). Browsing and downloads both use it; a toolbar switch turns it on or off instantly, no restart needed.
- Edge-style UI that follows the system light/dark theme: tabs in the title bar, https auto-completion in the address bar, multiple tabs, fullscreen playback, persistent logins.

## Install

Download either installer from [Releases](../../releases) and run it:

| Installer | Notes |
|---|---|
| `VideoCatcher-Setup-x.y.z.exe` | Includes the .NET runtime; nothing else to install |
| `VideoCatcher-Setup-x.y.z-NoRuntime.exe` | Smaller; requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64). If it's missing, the app offers a download link on first launch |

- Both installers bundle ffmpeg and install the same app; whichever you install last replaces the other.
- Installs per user to `%LocalAppData%\Programs\VideoCatcher`; no admin rights needed.
- Requires the WebView2 Runtime (built into Windows 11, and usually already present on Windows 10).
- The installers are unsigned. If SmartScreen warns about an unknown publisher, choose "More info → Run anyway".

## Usage

1. New tabs open blank with the cursor in the address bar. Open a video page and start playback. The address bar behaves like Edge's: input with a scheme opens as typed; host-like input (such as `example.com` or `localhost:3000`) tries `https://` first and falls back to `http://` if the connection fails; anything else is a Bing search.
2. The sidebar on the right lists the current page's videos on top and your downloads below; drag the line between them to resize the two sections. Choose Download next to a video; progress appears in the downloads section.
3. Collapse the sidebar to an icon rail with its arrow button or the toolbar's video and download buttons, and drag its left edge to change its width. Both are remembered.
4. Double-click a finished download to open it in the default player. Right-click it to show it in its folder or remove it from the list (the file stays on disk). Finished downloads are kept across restarts.
5. Settings holds the save folder, proxy address and minimum file size. The toolbar's proxy button turns the proxy on or off: it's filled with the accent color and reads "代理已开" while on.

| Shortcut | Action |
|---|---|
| Ctrl+T | New tab |
| Ctrl+W / middle-click a tab | Close tab |
| Ctrl+L | Focus the address bar |
| F5 | Reload |

### Output formats

| Captured resource | Output |
|---|---|
| mp4 / mkv / webm / flv / ts / mov / avi / wmv / m4v | Kept as is |
| m3u8 / mpd / m4s / other | `.mp4` |
| Audio | `.m4a` |
| Writing the format above fails (the container can't hold the codec) | Retried as `.mkv` |

### Proxy

Enter in Settings, for example:

```text
http://127.0.0.1:7890
socks5://user:pass@127.0.0.1:1080
```

The toolbar's proxy button is highlighted while the proxy is on; when it's off or no address is set, connections go direct. Internally the app runs a relay on `127.0.0.1` that both WebView2 and ffmpeg connect to, and the relay forwards to your upstream proxy. That's why switching needs no restart and why ffmpeg can use socks5. Switching interrupts downloads in progress.

## Known limitations

- DRM-protected (Widevine) content can't be captured.
- Sites that serve audio and video separately (such as bilibili) list two `.m4s` entries; they can only be downloaded separately for now, not merged.
- Live HLS streams, DASH (mpd) and encryption other than AES-128 are still downloaded by ffmpeg itself, one segment at a time.
- Sites using proprietary delivery protocols, such as YouTube, rarely yield usable URLs.
- Some sites don't allow Google sign-in from an embedded browser.
- Limited subtitle support: the bundled minimal ffmpeg only understands WebVTT subtitles in HLS streams; when present, the download is saved as `.mkv` because mp4 can't hold them.

## Data locations

| Data | Path |
|---|---|
| Settings | `%AppData%\VideoCatcher\settings.json` |
| Download history | `%AppData%\VideoCatcher\downloads.json` |
| Browser data (cookies, logins) | `%LocalAppData%\VideoCatcher\WebView2` |

## Build from source

Requires the .NET 10 SDK:

```bash
dotnet publish VideoCatcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Put `ffmpeg.exe` in `publish/` next to `VideoCatcher.exe` (otherwise `ffmpeg` from PATH is used). Any full ffmpeg build works. To match the minimal build in the installers, install `gcc-mingw-w64-x86-64` and `cmake` on Linux or WSL and run:

```bash
bash ffmpeg/build.sh ffmpeg-out
```

## Releasing

Pushing a `v*` tag triggers [release.yml](.github/workflows/release.yml). It cross-compiles the minimal ffmpeg on Ubuntu (cached by the contents of `build.sh`) and smoke-tests it against public test streams, publishes self-contained and framework-dependent builds, packages both with Inno Setup, and creates the GitHub release.

```bash
git tag vX.Y.Z
git push origin vX.Y.Z
```

## Third-party components

- [FFmpeg](https://ffmpeg.org/): the installers bundle a minimal LGPL build compiled from the official source by [ffmpeg/build.sh](ffmpeg/build.sh). It keeps only the protocols, container and parsing components that stream copy (`-c copy`) needs, with no encoders. `ffmpeg -version` lists the full configure flags; the license is in `ffmpeg-LICENSE.txt` in the install folder.
- [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/)
