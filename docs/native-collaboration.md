# Native collaboration implementation

## Architecture

`MainViewModel` opens a `WorkspaceViewModel` after sign-in. The workspace keeps
the existing chat view and adds meetings, calendar, announcements and media
settings. HTTP requests go through `CollaborationApi` and the single-unwrapping
`ApiPayload` reader. Tokens are issued by existing backend endpoints; no LiveKit
API secret is present in the desktop client.

Media path:

```
native miniaudio microphone / packaged FFmpeg camera+screen -> bounded frames -> C ABI -> LiveKit C++ SDK
LiveKit C++ subscriptions -> bounded native readers -> Avalonia WriteableBitmap
LiveKit C++ audio subscriptions -> bounded PCM queues -> miniaudio speaker output
```

The official C++ SDK itself uses LiveKit's native FFI/WebRTC implementation.
The client links C++ directly through `Native/fireuser_livekit.cpp`; it does not
use the .NET RTC SDK. Capture targets 1920x1080, camera 30 fps and screen 60
fps, matching the Angular capture ceilings. Camera encoding uses the frontend's
3 Mbps ceiling and simulcast; screen encoding uses VP8, no simulcast and the
frontend's 6.4 Mbps maximum, prioritizing frame rate. Camera prioritizes resolution.
Speech is mono, 48 kHz, with a 32 kbps encoding ceiling. Angular Ringtone.mp3, Ringback.mp3 and NotificationSound.mp3 are packaged and decoded with miniaudio; no external audio player is launched. These are ceilings;
device support, CPU and WebRTC congestion control determine delivered quality.
The browser's separate two-second application bandwidth policy (1.2 Mbps start,
720p/30 initial tier and stepped recovery/fallback) is not yet ported: the C++
SDK's public track interface does not expose equivalent live sender-parameter
writes. Native WebRTC still adapts actual transmission below its ceiling.
Local preview delivery is capped at 10 fps without capping transmitted frames.
Video subscriptions use a one-frame queue, UI delivery retains the latest
frame per tile, and bitmap allocations are reused. Up to 16 video frame producers, 64 participant placeholders and 32 native media readers are retained. Bitmap copying occurs only during rendering, so a hidden gallery avoids copying during share-only full screen. These are rendering/resource limits, not
attachment upload limits. No production CPU/latency benchmark has been measured.

Direct native calls enable the microphone after the explicit Answer/Start/Join action. Meetings honor the prejoin mute checkbox; calendar links open those choices before joining. Camera and sharing stay off until enabled. If microphone activation fails after joining, the call stays connected with a visible muted-device error. Stopping a source stops its process before
unpublishing. Leave/sign-out/application exit stop capture, cancel audio players,
join native readers and destroy the room. Local cleanup runs even when the API
cannot acknowledge leaving. Capture failure updates device state. Call teardown
also revokes remote input and releases injected keys/buttons.

## Platform setup

| Platform | Capture and output | Remote input | Validation status |
| --- | --- | --- | --- |
| Windows x64 | DirectShow device names; DXGI Desktop Duplication for matching monitor outputs, GDI fallback/window capture; native miniaudio audio | SendInput, current interactive desktop | C++ build/smoke test, managed tests and self-contained publish verified |
| Linux x64/ARM64 | PulseAudio/PipeWire, V4L2 camera, X11 desktop capture; native miniaudio audio | XTest on the default X11 display | Fedora Linux build, native ABI smoke, startup window and managed/headless tests verified; actual devices and two-party network media unverified |
| macOS Intel/Apple Silicon | AVFoundation device names/indices; default `Capture screen 0`; native miniaudio audio | CoreGraphics events, Accessibility permission, main display | Source/build scripts, `.app` manifest and CI targets added; not executed on macOS in this session |

Standard builds automatically prepare checksum-pinned native and media dependencies. No FFmpeg/FFplay installation or PATH editing is needed for packaged clients. Microphone and speaker choices use miniaudio on Windows, Linux and macOS; cameras use DirectShow, V4L2 or AVFoundation discovery. Device checks are explicit, local and stop after completion. Speaker changes apply immediately; microphone/camera changes apply on the next enable. Use headphones: acoustic echo cancellation remains pending.

macOS requires camera, microphone and screen-recording permission. Remote input
requires Accessibility permission; re-share after granting it. FFmpeg's own
permission identity can depend on how it is installed/launched, so validate the
signed packaged app and its capture executable together. No macOS permission or
notarization success is claimed from a Windows build.

Screen sharing first opens a native screen/window picker. Wayland uses the compositor's ScreenCast portal and an approved PipeWire FD through native GStreamer; it does not fall back to XWayland. X11/Windows capture the selected monitor rectangle or window ID. macOS uses the selected ScreenCaptureKit display/window. OS permissions still apply. Windows/macOS/X11 primary-screen input keeps its existing bounds checks. Linux Wayland monitor shares request a combined RemoteDesktop/ScreenCast session by default for one approved monitor; its input uses that stream’s compositor coordinates and requires both keyboard and pointer approval. Window shares remain view-only. See [screen-sharing.md](screen-sharing.md) for runtime dependencies, cancellation/cleanup and platform validation limits. Windows elevated/secure desktops are not targeted and privilege escalation is not attempted.

## Remote-control consent and protocol

1. Native and Angular participants exchange versioned capability packets on the
   reliable `fireuser.native.v1` LiveKit data topic. Angular advertises controller
   support only (`canHost:false`); browser screen shares remain view-only.
2. A request is permitted only while the host is sharing its supported desktop (or approved Wayland monitor) and
   the OS input provider is available. The sharing user sees the request and
   must explicitly approve that participant in a right-side toast. Unanswered requests reject after 30 seconds; pending duplicates cannot extend consent time. Merely joining never grants input.
3. The host issues a random grant bound to the authenticated LiveKit sender and
   current screen-share scope. Input must present that grant and a strictly
   increasing sequence. The host checks ranges, input kinds, finite coordinates,
   an event-rate bound and five-minute expiry before invoking the OS provider.
4. Mouse coordinates are mapped through the shared image's letterboxing. Camera
   tiles cannot produce remote input. Losing focus releases injected keys and
   buttons; a local liveness watchdog releases them after 1.5 seconds without
   accepted input to avoid stuck modifiers after packet/channel loss.
5. Stop control, Ctrl+Alt+Escape, unshare, disconnect, participant departure and
   expiry revoke access and release injected input. A fresh share needs fresh
   consent. Explicit Ctrl+C/Ctrl+V clipboard transfers are bound to that same grant; unattended clipboard synchronization and privileged input remain excluded.

The native capability announcement is **feature negotiation, not application
attestation**. An arbitrary modified client could send the same topic. The
security boundary is authenticated room membership, explicit host consent and
the scoped grant, not a self-reported platform string. Sender identity comes
from the C++ SDK's authenticated packet event, not a JSON sender field.

## Authentication and chat compatibility

Login/refresh now read the backend response envelope. Refresh preserves both
rotated credentials and serializes concurrent refreshes. Logout sends its bearer
credential. Encrypted direct messages use the Angular client's FUCHAT1 format:
ES256 compact JWS inside a multi-recipient RSA-OAEP-256/A256GCM general JWE.
Private identity material and PIN unlock grants are not written to disk.
Persistent public identity pins detect changes. Non-loopback chat identity
retrieval requires HTTPS.

Encrypted attachment content uses FUATT1's one-megabyte authenticated frames.
Upload preparation spools ciphertext to temporary files; streams are disposed
before cleanup. Downloads are authenticated into a temporary destination and
only replace the requested output after all frames verify. No request, file or
batch upload size cap was introduced. Uploads currently use the existing
streamed multipart endpoint; resumable offset persistence is not yet ported.
Encrypted image thumbnails are not yet decoded by the legacy image cache;
authenticated attachment download is available.

Failed sends retain the draft and pending files. Direct and group chat now share encrypted sending/history, edits, deletion, replies, reactions, authenticated attachment download, attachment browsing, member addition and dedicated call cards. A current server-provided roster determines group encryption recipients. Meeting-specific encrypted chat/history still needs its separate protocol adapter. Meetings expose pending invitation review and conflict acknowledgement before acceptance, then issue media credentials only for an eligible active meeting. Private identity material stays in memory for the current lock generation; validated public keys expire after five minutes and never bypass persistent fingerprint checks.

## Real-time behavior and scheduling

The workspace loads an initial snapshot and responds to `WorkplaceChanged` and
SignalR reconnects with coalesced authorized snapshots. Presence and chat use
SignalR; periodic presence reads and read-receipt polling were removed. Heartbeat
timers renew liveness only. Existing backend Hangfire jobs remain responsible
for scheduled domain transitions.

Calendar saves require a review of the exact current draft and explicit conflict
acknowledgement. Invitation acceptance has a separate review. Stale versions and
server-side policy failures remain visible. Saving the calendar entry and
configuring online media are separate operations; failure of media configuration
does not falsely report that the saved calendar entry disappeared. Announcement
creation currently publishes immediately for seven days to selected people.

## Endpoint coverage and remaining parity

| Area | Current desktop continuation | Remaining frontend parity |
| --- | --- | --- |
| Calls | Capability gate, create direct call, token, accept/decline/leave, native media | Add participants, browser-equivalent bandwidth budgeting, echo cancellation |
| Meetings | Configure from scheduled calendar entry; inspect, join/token, leave | Guest access, admission/invitations, recordings, encrypted meeting chat/history, host end controls |
| Calendar | Agenda, review/save, response review/accept/decline, cancel, join meeting | Editing, recurrence, import/export, shared calendars, task state and resource workflows |
| Announcements | Read, acknowledge, publish to selected users | Edit/withdraw, publishing grants, organization/unit/group audience preview, scheduled publication UI |
| Chat | Encrypted direct text and streamed encrypted attachments, PIN unlock, presence | Group chat, meeting inbox/history, PIN setup/recovery, replies/edits/reactions, encrypted thumbnails, resumable uploads |
| Identity/workplace management | Existing sign-in and profile reads | Full profile/application/group administration, organization/directory/policies, schedules/holidays/vacations, notification management and other Angular management surfaces |
| Packaging | Pinned SDK build, native ABI tests, matching-platform publish, macOS manifest, CI matrix, portal/PipeWire provider | Executed Linux/macOS device tests, signed installers, notarization, distribution GStreamer/portal dependency installation |

## Validation

Windows validation on 2026-10-19: native CMake/CTest smoke test passed; the
managed suite passed **30 tests with zero skips**, including both opt-in
remote-server transport cases (160x90 and 1920x1080 source frames). The Windows
self-contained distribution is generated under `artifacts/win-x64`.

Run the native builder before tests, since `NativeBridgeTests` loads the actual
C++ library. The tests cover native ABI/lifecycle, response envelopes, rotated
refreshes, API paths, platform capture arguments, chat/attachment cryptography,
calendar conflict rules, local cleanup on API failure, remote-control grants and
headless rendering of every workspace tab in both themes.

`Tests/chat-vector.json` contains **disposable test keys** and vectors generated
with the Angular workspace's JOSE/WebCrypto implementation. They are unrelated
to any account or deployment. `Tests/generate-chat-vector.mjs` documents how to
regenerate the fixture; Node is a development fixture tool only.

For optional screenshots set `FIREUSER_TEST_ARTIFACTS` to an output directory
before running the tests. Use `FIREUSER_REDUCED_MOTION=1` or Media settings to turn
off view fades. The GitHub Actions matrix is prepared for Windows, Ubuntu and
both macOS architectures; adding it does not mean those remote jobs have run.

Two C++ clients exchanged synthetic audio, decoded camera video, decoded
screen-share video and authenticated reliable data through an authorized remote
LiveKit server on Windows. This exposed and fixed screen track classification:
the SDK's publication source must be used when its track source is unset.
Tests also publish 1920x1080 input. In one run, camera arrived at 1080p while
the concurrent screen stream adapted to 960x540. The test verifies decoded
orange content at an adaptive resolution; it does not prove sustained 1080p/60,
latency, audio intelligibility, acoustic echo cancellation or device performance.
Do not substitute ABI coverage for end-to-end transport coverage.

A temporary loopback Windows LiveKit server (1.13.10) still failed during ICE
setup. That local networking issue remains unresolved; the remote-server test
establishes that the bridge can connect and exchange all tested track types.

To reproduce with an independently installed test-only LiveKit server:

```powershell
pwsh ./scripts/test-livekit.ps1 -ServerPath /absolute/path/to/livekit-server
```

The script creates an isolated server with random test credentials, runs the full
managed suite including synthetic audio/video/data, and stops its own server in
`finally`. `-MediaAddress <local-interface-address>` and `-ForceTcp` are optional
diagnostic settings. Ordinary `dotnet test` skips this opt-in network test unless
the explicit `FIREUSER_LIVEKIT_TEST_*` environment is provided; the skip does not
mean the call path passed. To use an authorized remote deployment, set
`FIREUSER_LIVEKIT_TEST_URL` to its WSS URL and provide two short-lived tokens in
`FIREUSER_LIVEKIT_TEST_SENDER_TOKEN` and
`FIREUSER_LIVEKIT_TEST_RECEIVER_TOKEN`. Use distinct identities in the same room
with publish/subscribe/data permissions. A server secret is unnecessary. Do not
put credentials into project files, shell history, documentation or CI output.
The test creates only synthetic tracks and never opens capture/input devices.

Real device capture/playout, remote OS input and Windows/macOS execution still
require platform integration testing. Fedora 44 x64 native build, library loading
and window startup are now verified; see desktop-design-and-devices.md for
packaging details and remaining platform limits. International keyboard/IME behavior also
needs coverage beyond the current basic key mappings. No running backend,
database, deployment configuration or frontend source was modified for these
checks.

See [call-stage-and-browser-control.md](call-stage-and-browser-control.md) for share-only full-screen controls, focus behavior and current verification.

See [Call recovery and Linux fullscreen](call-recovery-and-linux-crash.md) for reconnect behavior, disconnect diagnostics and the native window-state crash correction.
