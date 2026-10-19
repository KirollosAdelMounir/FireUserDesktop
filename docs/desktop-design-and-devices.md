# Desktop design, calls and device setup

## Angular design alignment

The native workspace now follows the Angular client's design resources rather
than the previous oversized default tab strip. Light/dark brushes match
`src/styles.scss`: charcoal backgrounds, neutral borders and the softer orange
brand colors. Outfit regular/medium/semibold/bold fonts are bundled under OFL.

The main navigation is a 70px icon rail that expands over the content to 210px
on hover or keyboard focus. Chat, Meetings, Calendar and Announcements are at
the top; Media settings is at the bottom. Labels have accessible names even
when visually collapsed. The rail uses Angular's 240ms
`cubic-bezier(.2,.8,.2,1)` curve. Page/conversation entry uses the same 240ms fade
and 10px vertical movement as the web fallback. Fast selections cancel stale
animations, without delaying requests. Reduced motion disables these effects.
The native transition uses the live view; it does not duplicate interactive
content or implement browser document snapshots.

Conversation lists have their own heading and filter; the account menu stays
in the footer. Voice/video actions are inside the conversation header. Global
member search, existing message/file behavior and profile actions are retained.

## Owned call window

Connecting or accepting a call opens a nonmodal `CallWindow` owned by the main
application window. The main workspace remains usable. The call window has a
header/device menu, video/presentation stage and fixed bottom media controls.
Closing it leaves the call; leaving/signing out/application exit closes it and
cleans up local media even if the backend leave request fails. Call errors are
visible in both the call window and workspace.

Remote desktop is an optional panel shown only when an eligible native peer is
sharing its supported desktop, the local user is sharing, or a pending/active
grant needs consent/revocation. The request list excludes peers without native
hosting capability and active screen sharing. Web call participants never
advertise the ordinary desktop protocol and cannot be selected. Capability
negotiation is not platform attestation; explicit consent and authenticated,
scoped grants remain the security boundary. See `native-collaboration.md`.

## Automatic dependencies

Normal `dotnet build` prepares the matching LiveKit C++ bridge and a portable
camera/screen runtime, verifies downloaded checksums and copies the required
files into Debug/Release/publish output. The native SDK/miniaudio source and
media archives are cached in ignored directories. End users do not install
FFmpeg/FFplay, edit PATH, type device IDs or install machine-wide VC runtimes.
Windows builds package app-local compiler redistributables. Developer machines
still need the .NET/C++/CMake/PowerShell toolchain.

`scripts/media-runtimes.json` pins archive URLs and SHA-256 for Windows x64,
Linux x64/ARM64 and macOS Intel/Apple Silicon. Windows uses Gyan essentials;
Linux uses BtbN LGPL builds; macOS uses Martin Riedl release builds. Updating
these requires reviewing provenance, device support and redistribution terms.
Retain upstream notices/source information and satisfy the selected build's
license before public distribution. Do not fall back to unverified downloads.

FFmpeg is resolved from the application `media` directory first, followed by
the app directory and PATH. Microphone/speaker operation no longer uses
FFmpeg/FFplay: miniaudio 0.11.23 is compiled into the C++ bridge, ABI version 3.

## Discovery, selection and checks

Opening Media settings or Call devices automatically refreshes connected device
choices. A manual refresh is also available after hardware changes. Discovery
does not start a camera, microphone or speaker test. OS audio defaults are
selected automatically; the first discovered camera is used if none is chosen.

| Platform | Microphone/speaker discovery and I/O | Camera discovery |
| --- | --- | --- |
| Windows | miniaudio native audio backend, normally WASAPI | DirectShow friendly names from FFmpeg's device listing |
| Linux | miniaudio system audio backend, normally PulseAudio/ALSA | V4L2 `/dev/video*` with sysfs device labels |
| macOS | miniaudio CoreAudio backend | AVFoundation video indices/names; display capture entries are excluded |

The same device options are available in the call window. Speaker changes apply
immediately. Microphone/camera choices apply the next time the source is enabled.
If a device disappears, its failure is visible and discovery can select a new
choice; no arbitrary other camera is opened while an existing device is active.

Explicit local checks provide a one-second microphone sound-level result, a
short low-volume speaker tone, or one camera preview frame. They never publish
to a LiveKit room and stop after completion. Checks are serialized, cancellable
and bounded by timeouts. A microphone/camera already active in a call must be
turned off before its local check. OS permissions remain mandatory: no software
can silently grant microphone/camera, Screen Recording or Accessibility consent.

## Verification and remaining work

Latest Windows validation (2026-10-19): **38 passed, 2 skipped** in the managed
suite. The two network transport cases are opt-in and were not rerun in this
design/device update. Native CMake/CTest smoke checks passed, and Debug and
Release compiled successfully. The bundled FFmpeg 9.0.2 executable ran its
version/device-capability queries without capturing hardware. Tests also verify
font weights and rapid animation cancellation. No physical microphone, camera,
speaker or remote-input test was run against the user's desktop.

Windows native compilation/ABI smoke tests, packaged runtime resolution,
Windows/macOS camera-list parsing, device-selection behavior without capture,
owned-call-window cleanup, remote-control gating and headless light/dark views
are covered. Screenshots include expanded keyboard navigation and the call
window. Debug and Release output must both include `media/ffmpeg.exe` and the
native bridge/runtime DLLs.

Linux/macOS device execution and permission flows have not been exercised on
those operating systems here. Wayland portal capture/control, acoustic echo
cancellation, browser-equivalent application bandwidth budgeting and the wider
frontend feature-parity backlog remain documented work. Source paths and CI
targets do not establish real device compatibility or sustained 1080p/60 quality.
