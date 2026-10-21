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

## Independent call window

Connecting or accepting a call opens an independent nonmodal `CallWindow`. The main workspace and call window minimize independently; application exit still closes the call. The call window has a
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
still need .NET 10, CMake 3.24+ and a C++20 compiler. Windows uses
PowerShell; Linux/macOS use Python 3.12+ and do not require PowerShell.
Linux additionally needs X11/XTest and OpenSSL development files.

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
independent-call-window cleanup, remote-control gating and headless light/dark views
are covered. Screenshots include expanded keyboard navigation and the call
window. Debug and Release output must both include `media/ffmpeg.exe` and the
native bridge/runtime DLLs.

Linux/macOS device execution and permission flows have not been exercised on
those operating systems here. Real compositor capture, Wayland remote control, acoustic echo
cancellation, browser-equivalent application bandwidth budgeting and the wider
frontend feature-parity backlog remain documented work. Source paths and CI
targets do not establish real device compatibility or sustained 1080p/60 quality.


## Native collaboration screens and performance

Calendar now uses a Sunday-first, six-week native month grid, category-colored
chips (up to three per day), previous/next/today controls, arrow-key day selection,
and a bounded 24-hour agenda. Creating from a free slot fills the selected local
date/time in the existing review-before-save editor. The snapshot covers the
visible 42-day range. Day changes inside the same month filter locally;
SignalR invalidations and reconnects reload the snapshot. There is no polling.

The meetings screen shows a join panel, scheduled meetings from the loaded
calendar range, and a create-schedule action. Announcement cards include category,
expiry and per-card acknowledgement. The composer supports the Angular categories,
publication/expiry dates and email preference. Server permission checks and
calendar conflict review remain authoritative. Narrow windows place editors below
the browser pane instead of clipping them beside it.

Message history uses a ListBox with its virtualizing panel and its own scroll
viewport. The 1,000-message regression verifies a bounded realized item count and
that the final message can be scrolled into view. Reduced motion also stops the
chat typing animation and message transitions. This is behavior verification,
not a latency/frame-rate benchmark.

## Notifications

Foreground notifications use native Avalonia notification cards with Fire User
colors/branding, six-second expiry and at most three active cards. Clicking opens
the conversation. When the window is inactive, delivery goes to the system:

- Windows: ToastGeneric, app logo, registered application identity and protocol
  activation. XML is escaped and passed as Base64 data to the bounded PowerShell
  delivery process. It is not inserted into executable script source.
- Linux: libnotify's `notify-send` on the user desktop notification bus, with
  desktop-entry identity, icon, message sound hint and an Open action. The command
  has a bounded wait and arguments are passed without a shell. `notify-send` must
  be installed (Fedora: libnotify; Ubuntu: libnotify-bin). The compositor owns
  notification history, expiry and action support.
- macOS: a small Objective-C++ bridge uses UserNotifications, permission requests
  and native click callbacks. Run the packaged `.app` with its bundle identity;
  notification authorization and signing/notarization must be validated on macOS.
  Startup does not rewrite a signed bundle's Info.plist.

The OS owns background toast layout and appearance. Custom foreground cards do
not override system Do Not Disturb or attempt to replace the OS notification
center. External audio-player processes are no longer launched for notifications;
the OS handles background notification sounds.

## Linux startup and compiler verification

The default build uses `NETCoreSdkPortableRuntimeIdentifier`, so Fedora resolves
`linux-x64` rather than passing `fedora.44-x64` to the native archive selector.
`scripts/build-native.py` shares the pinned SDK manifest with the Windows script,
verifies archives, builds the bridge, runs CTest and prepares media automatically.
The XTest CMake check uses the imported target. Unix instance contention is handled
inside TryAcquire; the secondary process waits for activation delivery and never
removes the owning instance's socket. Cancellation closes the listener before its
ownership lock is released.

The official Ubuntu SDK references `CURL_OPENSSL_4`, which Fedora's system curl
does not export. Linux packages therefore include a checksum-pinned curl 8.23.0
built with OpenSSL and versioned symbols, along with its license. OpenSSL, zlib,
glibc and platform windowing/audio dependencies remain system libraries. Build
release packages on the oldest supported Linux distribution; a Fedora-built
binary is not evidence of compatibility with older glibc/OpenSSL versions.

Local verification on Fedora 44 x64: GCC bridge compilation, CTest ABI lifecycle,
loading the packaged library without LD_LIBRARY_PATH, native window startup and
headless rendering in both themes. `--startup-smoke-test` skips OS registration
and closes after the native window opens. Run it with isolated XDG config/data
paths so saved accounts are not loaded. CI includes an Xvfb startup check.
No microphone/camera/screen capture, remote input or authenticated server session
was used for these checks.

### MinGW

Prefer a matching MinGW SDK when available:

```powershell
pwsh ./scripts/build-native.ps1 -Toolchain MinGW -SdkPath C:/sdk/livekit-mingw
# The same selection is available in the ordinary automatic build:
dotnet build UserIdentityDesktop.csproj -c Release -p:NativeToolchain=MinGW -p:LiveKitSdkPath=C:/sdk/livekit-mingw
```

MinGW uses its own build directory and packages its compiler runtime DLLs.
`Auto` selects MinGW when the supplied SDK contains `lib/liblivekit.dll.a`;
otherwise it retains the supported MSVC Windows archive. The SDK and all its C++
dependencies must be compiled with a compatible MinGW ABI and supplied with
working LiveKit CMake configuration. A `.dll.a` filename is an eligibility check,
not proof of ABI compatibility; CTest must pass before packaging.
The official Windows SDK is MSVC-based and cannot safely be linked into a MinGW
C++ bridge. No matching SDK/toolchain is available here, so MinGW execution and
Windows/macOS compilation remain unverified. This does not claim an all-MinGW
Windows distribution has been produced.

## Remaining Angular parity

These changes align the native collaboration layouts, typography, colors,
calendar navigation and existing motion; they do not establish complete visual
or functional Angular parity. Meeting-specific encrypted chat, guest admission and recording management, recurring/imported/shared calendar management and expanded
announcement audience/withdrawal/history workflows remain in the existing native
collaboration backlog. Preserve the API/security contracts when adding those
surfaces. Native Wayland remote control, real device paths and full
Windows/macOS notification integration still need platform validation.


## Regression evidence for this change

| Requirement | Evidence |
| --- | --- |
| Native UI and motion | `Workspace_RendersEveryTab_InBothThemes`, `NavigationMotion_RapidChangesCancelWithoutLeavingInvisibleContent`, `MonthCalendar_MapsOvernightEventsAndClearsStaleChips` |
| Message performance | `MessageHistory_VirtualizesLongConversations` verifies virtualization with 1,000 messages and scrolling to the final item |
| Unix instance ownership | `ContendingInstance_HandsOffActivationWithoutRemovingOwnersSocket` |
| Toast branding, bounds, reduced motion, activation | `ForegroundToast_BoundsVisibleCardsAndRendersBranding` (both motion settings), `ForegroundToast_RoutesSanitizedContentAndActivationOnUiThread`, `NotificationActivation_OpensStandardAndLegacyChatLinks` |
| Publication fields and validation | `AnnouncementPublication_UsesChosenDatesCategoryAndEmailPreference`, `AnnouncementPublication_RejectsExpiryBeforePublication` |
| Native Linux runtime | GCC build and `bridge_abi_lifecycle` CTest pass; isolated native window startup exits 0 |
| Full managed suite | `dotnet test Tests/UserIdentityDesktop.Tests.csproj -c Release`: Passed 66, Failed 0, Skipped 2 (opt-in network media cases) |
| MinGW and other OS execution | Toolchain selection implemented; matching MinGW SDK unavailable; Windows/macOS runs remain unverified |


## Meeting protocol activation

`fireuserconnect://meeting/{locator}` opens the Meetings join screen and fills
its code. The public locator is case-sensitive; only ASCII letters/digits,
hyphen and underscore (1–32 characters) are accepted. Additional path segments,
query parameters, fragments, credentials, ports and encoded separators are
rejected. Protocol URLs never supply a backend URL or authentication credential.

Windows/Linux reuse the existing single-instance IPC handler. macOS also handles
Avalonia's native protocol-activation event for an already-running bundle. A
signed-out client keeps the latest meeting destination and consumes it on normal
login completion without polling or a short login timeout. Opening a link never
joins a call, enables devices or leaves the existing call automatically. The
existing Join meeting command continues to obtain admission/token decisions
from the configured backend. Emails provide a separate browser fallback because
mail clients cannot reliably detect a running app and may block custom schemes.

Regression evidence: `MeetingProtocol_ParsesPublicLocatorWithoutChangingCase`,
`MeetingProtocol_RejectsNonMeetingRoutesAndCredentialParameters`, and
`MeetingLink_SurvivesSignInAndReusesOpenWorkspaceWithoutStartingMedia`.
