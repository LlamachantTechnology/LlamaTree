# Building LlamaTree

How to build, run, test, package and release LlamaTree from source, and how the app is put together. This page is for people working on LlamaTree itself; to install and use it, start with [Getting started](getting-started.md).

## Building

```powershell
cd web; npm ci; cd ..                                   # once: xterm.js + Monaco
dotnet run --project src\LlamaTree -f net10.0-windows   # the Windows app
dotnet test                                             # tests
```

On macOS and Linux:

```sh
(cd web && npm ci)                  # once: xterm.js + Monaco
dotnet run --project src/LlamaTree
dotnet test
```

The .NET 10 SDK builds it for .NET 10. A .NET 11 SDK builds it for .NET 11 as well (`net11.0`), so it runs on a machine with only .NET 11: run it with `dotnet run --project src/LlamaTree -f net11.0`, and `dotnet test` tests both. Releases are self-contained and built for .NET 10, so they need neither.

- **Host:** one app with two windows (`src/LlamaTree`). `net10.0-windows` is the Windows app: WinForms + WebView2 (`Hosting/WinForms`). `net10.0` is the macOS and Linux app: a [Photino](https://www.tryphotino.io/) window, which hosts WKWebView on macOS and WebKitGTK on Linux (`Hosting/Photino`); it also runs on Windows (`-f net10.0`, on WebView2) for trying it out. Everything else is shared: `AppHost` holds the sessions, git, panels and plugins, and talks to the same page through either window.
- **Session host:** `AppHost` runs in a process of its own, the same program started with `--host` (`Hosting/SessionHost.cs`), so sessions can outlive the window. The window starts it if it isn't running and connects through a named pipe (Windows) or a Unix socket (macOS, Linux; in `$XDG_RUNTIME_DIR` or the per-user temp folder) that only the same user can open, one per profile (`Hosting/Link`). The page's messages pass through unchanged; the folder picker, clipboard, title bar colours and plugins' pages stay with the window (`Hosting/HostLink.cs`). The host keeps the end of each session's output for a window that attaches later, and ends once no window is attached and no session is running. An update waits for both processes to end; the installer checks for either (`AppMutex`).
- **Sessions on servers among your own:** this computer's session host keeps an ssh connection to each server with sessions here (`Hosting/ServerConnection.cs`) and is a window to that server's host: what it tells its page about a session is passed to this page under the session's id here, and requests about the session go there under its id there; a request naming a server instead (adding sessions, browsing its folders, its saved conversations) goes to that server (`AppHost.Servers.cs`). `sessions.json` keeps each server session's place, project, and what it was last seen as (`Server`, `RemoteId`). While connected, the server's host passes its sessions' tool calls (panels, messages) up to this host, which answers them as for its own sessions; without it the server answers them itself.
- **Sessions on a server:** a window started with `--remote <target>` runs `ssh -T -o BatchMode=yes <target> llamatree --attach` and speaks the same protocol over ssh's input and output. On the server, `--attach` (`Hosting/HostBridge.cs`) connects to that user's session host there, starting it detached if needed, and copies between the two; when SSH drops only the bridge ends. The host's lock is per user across login sessions on macOS and Linux (`Hosting/InstanceLock.cs`), and it listens again if its socket file is removed (Linux empties `$XDG_RUNTIME_DIR` when the last login ends). Told the window is remote, the host skips update checks and opens nothing on its own desktop: a folder opens in the page's folder browser (`fs.list`), and a file is sent to the window in pieces (`Hosting/ServerFiles.cs`, `ServerCopies.cs`), which opens its copy. Pasted files go the other way (`upload.*`, the window waiting every few pieces for the host to catch up), and a plugin's folder is sent ahead of the page hearing of it, so the window serves the copy as it would a local folder. `LLAMATREE_SSH` names another ssh program; `LLAMATREE_REMOTE_COMMAND` replaces `llamatree --attach`.
- **Terminals:** real pseudo terminals rendered with xterm.js: ConPTY on Windows, where child processes live in a kill-on-close job object; a Unix pty on macOS and Linux (`UnixPtyProcess`, started with posix_spawn in a session of its own), where closing a session hangs up its terminal.
- **Diffs:** Monaco diff editor, with dark and light themes after Visual Studio's.
- **Built-in themes:** `src/LlamaTree/wwwroot/themes` (Matrix, Nerds, Tron) are copies of what [LlamaTree-Themes](https://github.com/LlamachantTechnology/LlamaTree-Themes) publishes, which is where they're changed. `node tools/sync-themes.mjs` brings the copies up to date from its catalog; the release pipeline runs it with `--check` and stops if they differ.

## macOS and Linux

Run from source as above; [GitHub Actions](../.github/workflows/build.yml) also builds and tests every push on Windows, Linux and macOS and keeps a self-contained build of each (`LlamaTree-osx-arm64`, `-osx-x64`, `-linux-x64`) on the run for two weeks, with a Mac disk image of each Mac build, signed ad hoc, whose program it starts to see it runs under the app's entitlements. Releases carry Linux packages (see [Linux](getting-started.md#linux)), made by [`tools/package-linux.cs`](../tools/package-linux.cs) from `publish.ps1` (on Windows, with no Linux tools), and a Mac disk image for each processor (see [macOS app](#macos-app)). A macOS copy doesn't update itself yet: *Check for updates* says when a new version is out and links its release.

- **Linux** needs WebKitGTK 4.1 and libnotify (Debian/Ubuntu: `sudo apt install libwebkit2gtk-4.1-0 libnotify4`). Pasting an image or copied files into a terminal uses `wl-paste` (Wayland) or `xclip` (X11) if they're installed.
- **Sessions get your login shell's environment** (`$SHELL -l -i`, read again after a minute), so `claude`, `codex`, `node` and `git` are found as in a terminal even when the app is started from the Dock or a desktop menu. Commands run through `/bin/sh`, so a session's command is written the way you'd type it in a terminal.
- **Keys:** on a Mac, ⌘ does what Ctrl does on Windows (⌘1…⌘9, ⌘⇧G, ⌘⇧N, ⌘W, ⌘C/⌘V in the terminal); Ctrl+C and the other Ctrl keys go to the terminal as themselves.
- **Data** lives in `~/Library/Application Support/LlamaTree` (macOS) or `~/.config/LlamaTree` (Linux); `LLAMATREE_DATA` picks another folder, as on Windows.
- **The page and plugins** are served from `http://127.0.0.1:<port>/` for this run only (WebView2's virtual hosts don't exist elsewhere). The window's message bridge is reachable from every frame, so the host only acts on messages carrying a token it gives the top-level page; each plugin's files are under a random path made for the run, so one plugin can't load another's page.
- **Undo Changes** moves files to the Trash (Linux: `gio trash`, else the freedesktop.org trash folder).

## Installer

```powershell
winget install JRSoftware.InnoSetup   # once
.\publish.ps1                         # -> artifacts\LlamaTree-Setup-<version>.exe
.\publish.ps1 -SkipInstaller          # -> artifacts\publish\ (portable folder only)
```

- Self-contained x64 build (no .NET install needed). The version comes from `<Version>` in `LlamaTree.csproj`; bump it for each release.
- Installs per-user to `%LOCALAPPDATA%\Programs\LlamaTree` (no admin), with a Start Menu shortcut, an optional desktop shortcut and an uninstaller. Admins can choose an all-users install.
- Upgrading: run the newer setup. It asks to close a running copy first. Saved sessions are kept; uninstalling also keeps them in `%APPDATA%\LlamaTree`.
- Setup warns and links to the download if the WebView2 Runtime is missing (it's built into Windows 11).
- Local builds aren't code-signed, so on other PCs SmartScreen shows "unknown publisher" (More info › Run anyway).
- The icon is generated by `tools\make-icon.ps1`, which also writes the Mac app's (`installer/macos/LlamaTree.icns`).

## macOS app

[`tools/package-macos.sh`](../tools/package-macos.sh) makes `LlamaTree-<version>-macos-<arm64|x64>.dmg` from a self-contained `osx-arm64` or `osx-x64` publish. It runs on a Mac, since codesign, hdiutil and notarytool are Apple's:

```sh
dotnet publish src/LlamaTree -f net10.0 -c Release -r osx-arm64 --self-contained -p:DebugType=None -o artifacts/osx-arm64
tools/package-macos.sh artifacts/osx-arm64 1.2.3 arm64 artifacts/macos    # signed ad hoc: for this Mac only
```

- **The bundle:** `Contents/MacOS` holds the program and the .NET runtime; the page (`wwwroot`) and `app.png` go in `Contents/Resources`, where the app looks for them when it's in a bundle (`Platform/AppFiles.cs`). `Info.plist`, the entitlements and the icon are in [`installer/macos`](../installer/macos). The bundle id is `dev.llamatree.LlamaTree`; it needs macOS 14 or later.
- **Signing:** with `MACOS_SIGN_IDENTITY` set, every file in `Contents/MacOS` is signed on its own (macOS counts everything there as code), then the program with `LlamaTree.entitlements`, then the bundle; all with the hardened runtime and Apple's timestamp. The entitlements let .NET's JIT run, and let a session's commands drive other apps when you allow it (`NSAppleEventsUsageDescription`); `Info.plist` also says why sessions may ask for Documents, Desktop and the like. Without an identity it's signed ad hoc, which runs on the Mac that made it (and under the same hardened runtime) but nowhere else.
- **Notarizing:** with `MACOS_NOTARY_KEY`, `MACOS_NOTARY_KEY_ID` and `MACOS_NOTARY_ISSUER` (an App Store Connect API key) the app is sent to Apple's notary service and its ticket stapled to it, then the disk image is signed, notarized and stapled the same way. A rejection prints Apple's log of what it found.
- **The Developer ID** is *Developer ID Application: Llamachant Technology Ltd. (N83JJRC3Y5)*. Its certificate expires on 26 December 2027: before then, make a new one (Apple Developer › Certificates › Developer ID Application, from a certificate signing request, which `openssl req` makes on Windows as well as a Mac does), export it with its key as a `.p12`, and replace the pipeline's secure file. Apps signed before it expires keep opening, as their signatures are timestamped. `install.sh` checks a Mac download against the team (N83JJRC3Y5), not the certificate, so a renewed one needs no change there.

## Releasing

Signed releases are built by `azure-pipelines.yml` on the Azure DevOps agent with the company's code signing token, after a job on a Microsoft-hosted Mac has made the Mac disk images:

1. Bump `<Version>` in `LlamaTree.csproj` and merge to `main`.
2. In Azure DevOps, run the pipeline on `main` (or on a tag `v<version>`) with *Create the GitHub release* ticked. Untick it for a trial run that only builds and signs, keeping the installer, packages and disk images on the run's Artifacts. Windows, Linux and macOS are each a tickbox, all ticked for a new release (see [Adding a platform to a release](#adding-a-platform-to-a-release)).
3. The macOS job builds `osx-arm64` and `osx-x64`, and `tools/package-macos.sh` signs each with the Developer ID, has Apple notarize it, and makes its disk image (see [macOS app](#macos-app)). The certificate (`developer-id-application.p12`) and the App Store Connect API key (`AuthKey_AW2Y3CBGMT.p8`) are the pipeline's secure files, and the certificate's password its secret variable `AppleCertificatePassword`; the certificate goes into a keychain made for the run and deleted after it.
4. Then on the Windows agent, the pipeline runs `publish.ps1 -Sign`, which signs `LlamaTree.exe`/`.dll`, setup and its uninstaller as *Llamachant Technology Ltd* (picked from the certificate store by subject, as the NuGet signing does), timestamps them with Sectigo's server, and fails unless each carries a valid, timestamped signature from that publisher. It then writes `SHA256SUMS`, the SHA-256 of every file the release carries, signed with the same certificate (`tools/sign-checksums.cs`, writing `SHA256SUMS.p7s`), and creates a **draft** GitHub release with the installer, the Linux packages, the Mac disk images, the two install scripts from `tools/install` (which the one-line installs fetch from the latest release, so keep their names) and the two checksum files.
5. Write the release notes on the draft and publish it: that's what makes the in-app updater offer the new version. The updater shows everything above the notes' *Install* heading, and needs the installer named `LlamaTree-Setup-<version>.exe` on a `v<version>` tag (Linux copies update from the `.deb`, `.rpm` and `.tar.gz` under the names `publish.ps1` gives them).
6. Don't replace or add files on a release by hand once it's built. The app installs an update only when the download matches its line in the signed `SHA256SUMS` (and, on Windows, carries our Authenticode signature), so a file changed afterwards, or a release without `SHA256SUMS` and `SHA256SUMS.p7s`, is refused. Rebuild instead.

### Adding a platform to a release

A new release carries all three platforms: the one-line installs and *Install LlamaTree on a server* take whatever release is latest, so a release without a platform would leave it nothing to install. The pipeline stops a new release with a platform unticked.

To add a platform to a release that's out already (the Mac disk images, when the macOS job couldn't run, say), run the pipeline with only that platform ticked, for the same `<Version>`. The release must be published, not a draft. The pipeline:

- reads the release's `SHA256SUMS` and checks its signature is ours, as the app would (`tools/sign-checksums.cs --add-to`), and stops if the release has any of this run's files already: nothing on a release is replaced but the install scripts and the checksums;
- signs the list again with this run's files and the current install scripts in;
- uploads them to the release, keeping its title, notes and the files already there.

The new files are built from the commit the run is on, so run it on the release's tag when that tag has what the build needs (for the first Mac release, it's built from `main`, as the tag predates the Mac packaging).

The one-line installs check the same way: `install.ps1` runs the installer only when it matches its line in the signed `SHA256SUMS` and carries our Authenticode signature, and `install.sh` checks `SHA256SUMS.p7s` with `openssl` against Sectigo's root, which is in the script, before installing anything. On a Mac, whose own `openssl` (LibreSSL) can't check it, `install.sh` checks the signed checksums only when OpenSSL itself is installed, and always checks that the disk image and the app in it are signed by our Developer ID team and notarized (Gatekeeper doesn't look at what `curl` downloads). Neither installs a release without the two checksum files (1.3.0 and before). What they can't check is themselves: today every install command (this repository's README and docs, llamatree.dev's home page and docs, and the app's *Install LlamaTree on a server*, `Hosting/ServerInstall.cs`) fetches the scripts from `github.com/.../releases/latest/download/`, where anyone who can publish a release can replace a script and its checksums together. To close that, serve the scripts from somewhere a GitHub token can't write, such as llamatree.dev (its own repository and site), and point the install commands there.

The app pins the certificate by what survives renewing it: the organisation (*Llamachant Technology Ltd*), its registration number and country as the EV certificate names them, and the authority's root (*Sectigo Public Code Signing Root R46*, by its key; see `Updates/Publisher.cs`). The notices are checked the same way. Renewing the certificate with Sectigo needs nothing more. Moving to another authority, or to a certificate without the registration details, needs a release that accepts the new certificate first, signed with the old one, since copies only install what the copy they have accepts. The install scripts in `tools/install` pin the same things, so change them with it.
