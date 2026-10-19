# Fire User Connect

Native Avalonia 12 / .NET 10 collaboration client for Windows, Linux and macOS.
The UI preserves the orange Fire User theme in light and dark modes. LiveKit
voice, video, screen sharing and remote-control data travel through the official
C++ SDK and a small C ABI bridge. There is no WebView or JavaScript media runtime.

## Build and run

For development install .NET 10, PowerShell 7, CMake 3.24+ and a C++20 compiler.
On Windows install Visual Studio's Desktop development with C++ workload.
On Linux install the X11/XTest development packages (`libx11-dev libxtst-dev`).
Build on the intended operating system and architecture:

```powershell
pwsh ./scripts/build-native.ps1
dotnet build UserIdentityDesktop.csproj -c Release
dotnet test Tests/UserIdentityDesktop.Tests.csproj -c Release
dotnet run --project UserIdentityDesktop.csproj -c Release
```

The native build downloads the pinned LiveKit C++ SDK 1.12.2 and verifies its
SHA-256 digest. SDK files and generated binaries are ignored by Git. To use a
preinstalled SDK, pass `-SdkPath /absolute/path/to/livekit-sdk`.

Create a self-contained local build with `pwsh ./scripts/publish-desktop.ps1`.
macOS publishing creates an unsigned `.app` with microphone/camera permission
descriptions. Signing, notarization and native permission validation must be
performed on macOS. Standard builds automatically prepare and package FFmpeg for camera/screen capture. Native audio does not require FFplay or PATH setup. OS privacy consent remains under user control.

## Collaboration

- Direct encrypted text/file chat, account PIN unlock, rotated authentication
  credentials and SignalR messages/presence.
- Native LiveKit calls and meeting-code joins. Microphone and camera start off.
- Camera up to 1080p/30 FPS; screen sharing up to 1080p/60 FPS, with the
  frontend's encoding ceilings. Delivered quality depends on the connection.
- Calendar agenda, people selection, conflict review, scheduling, invitation
  responses, cancellation and online meeting configuration.
- Announcement reading, acknowledgement and immediate publishing to selected
  people, subject to server permissions.
- Remote desktop control between native participants, with explicit consent,
  per-share grants, revoke controls and automatic expiry. Web participants do
  not advertise the native capability and cannot be selected for control.
- Short view/call-panel fades and hover feedback, with a reduced-motion switch.

This is a collaboration continuation, **not full Angular feature parity**.
See [native collaboration](docs/native-collaboration.md) for platform constraints,
security boundaries, validation evidence and the remaining endpoint inventory.
Repository implementation rules are in [AGENTS.md](AGENTS.md).
See [latest desktop design and device updates](docs/desktop-design-and-devices.md)
for the sidebar, owned call window, automatic runtime setup, device checks and
current verification results.

The workspace uses the Angular client’s charcoal/orange palette, bundled Outfit typography and an expanding icon sidebar with 240 ms motion. Calls open in an owned child window. Device menus automatically discover microphone, speaker and camera choices; local checks are available without joining a call. See docs/desktop-design-and-devices.md.
