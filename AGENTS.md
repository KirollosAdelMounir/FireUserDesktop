# Fire User desktop: implementation guide

## Product requirements

- Maintain the orange Fire User theme in light and dark modes. Prioritize chat,
  meetings, calendar scheduling and announcements. Keep motion short and honor
  the reduced-motion setting and `FIREUSER_REDUCED_MOTION`.
- This is a native Avalonia 12 / .NET 10 application for Windows, Linux and
  macOS. Do not introduce WebView, browser-based media, Electron or JavaScript
  media runtimes. Route LiveKit transport through the official C++ SDK and the
  versioned C ABI bridge.
- Match the Angular workspace's camera/screen quality ceilings when changing
  media. Inspect `src/app/call/call.component.ts` and
  `src/app/services/Call/Service/share-bandwidth.ts` in the sibling frontend.
  Current native targets are camera 1080p/30 FPS/3 Mbps with simulcast; screen
  1080p/60 FPS/6.4 Mbps with VP8 and no simulcast; mono speech 32 kbps.
  Delivered quality is adaptive. Browser application-level bandwidth budgeting
  is still a parity gap; do not claim exact quality-policy parity.
- Remote control is a separate explicit-consent operation, available only for
  hosts advertising the protocol while sharing the supported primary desktop.
  Native and web clients may request control with explicit sharer consent.
  Browser screen shares remain view-only; browsers never host OS input.

## Solution map

| Location | Responsibility |
| --- | --- |
| `Core` | Models, transport DTOs and service contracts |
| `Data` | Authentication, configuration, response envelope reader and existing infrastructure |
| `Service` | Chat encryption, chat/presence, typed collaboration APIs, native media lifecycle |
| `Application` | Dependency registration |
| `ViewModels` | UI state, commands, conflict review and orchestration |
| `Views` | Native Avalonia views and reusable bitmap video rendering |
| `Native` | C++ LiveKit transport bridge and platform input providers |
| `Tests` | HTTP contracts, cryptography, consent policy, headless views and opt-in real transport tests |
| `scripts`, `packaging`, `.github/workflows` | Native build, distribution and platform validation |

Read `docs/native-collaboration.md` for detailed implementation, platform limits,
endpoint coverage and validation. Read the backend/frontend `AGENTS.md` files
before coordinated changes in those repositories. Preserve ongoing user edits,
especially project versions, server configuration and developer tooling files.

## API and real-time contracts

- Match existing backend routes and JSON contracts. `ApiPayload` unwraps the
  standard `{status,data,errorText,traceId}` envelope once, while supporting
  bare payloads. Do not log raw authentication/error response bodies.
- Use backend-issued participant tokens in the application. Never embed a
  LiveKit API key/secret in source, desktop configuration, binaries or docs.
- Refresh rotates and persists both credentials under the existing serialized
  refresh gate. Never reuse a consumed refresh value or remove bearer logout.
- Load an initial snapshot and use SignalR invalidations/reconnect recovery.
  Do not add change-detection polling. Heartbeats and local security expiry
  timers are liveness mechanisms, not snapshot polling.
- Calendar save requires review of the exact draft and explicit conflict
  acknowledgement. Invitation response has its own review. Preserve separate
  persistence/media-configuration outcomes and visible server policy errors.
- Do not present the current continuation as full Angular parity. Group and
  meeting encrypted chat, richer meeting controls, recurring calendars and
  management surfaces remain documented gaps.

## Encryption and attachments

- Preserve FUCHAT1 compatibility: ES256 JWS inside RSA-OAEP-256/A256GCM general
  JWE. Keep private identities and PIN grants transient. Retain public key pins
  and fail closed when identity/authentication validation fails.
- FUATT1 files use authenticated one-megabyte frames. Stream/spool ciphertext;
  dispose streams before deleting temporary files. Authenticate a download to
  a temporary destination before replacing the requested output.
- Do not introduce file/folder/request/batch attachment size caps. Resumable
  upload requests use the backend chunk protocol; process-restart resume persistence
  and encrypted thumbnails still need explicit adapters.
- `Tests/chat-vector.json` contains disposable test keys only. Never replace
  these with account/deployment credentials.

## Native lifecycle and performance

- Keep C declarations, P/Invoke calling conventions and ABI version synchronized.
  Root managed callbacks until C++ destruction has joined every reader.
- Keep bounded video/audio/data queues and latest-frame delivery. Reuse bitmap
  storage and marshal view changes through Avalonia's dispatcher. Avoid
  unbounded per-frame UI posts. Local preview is capped independently of send
  quality.
- Never unwind managed exceptions across the C ABI. Do not destroy/join a room
  from its callback thread. Device stop, leave, sign-out and application exit
  must stop processes/readers even when the backend leave request fails.
- Keep automatic native/media packaging in the standard build. Invoke packaged
  FFmpeg camera/screen capture using argument lists without a shell. FFplay is
  no longer used. Native audio uses miniaudio. Never require end users to type
  device identifiers or edit PATH. Acoustic echo cancellation remains work.
- Windows input uses SendInput; macOS uses CoreGraphics/Accessibility; Linux
  uses X11/XTest. Wayland capture uses ScreenCast/PipeWire; Wayland
  monitor sharing always requests control permission through the consent-based RemoteDesktop portal and approved monitor coordinates. See `docs/screen-sharing.md`. Do not
  silently fall back to input on another desktop or bypass OS permissions.
- Use the publication's track source to identify screen shares; the SDK track
  source can be unset. Compile MSVC bridge sources as UTF-8.

## Remote-control security

- Trust sender identity from LiveKit's authenticated packet event, never JSON.
  Capability packets are negotiation, not platform attestation.
- Require the host's explicit approval, a random per-share scope and grant,
  exact participant binding, increasing sequence numbers, validated coordinates
  and input codes, rate limits and five-minute expiry.
- Revoke on unshare, stop, disconnect, participant departure and expiry. Release
  held keys/buttons on focus loss, emergency Ctrl+Alt+Escape and idle timeout.
- Only a remote screen tile may send control input. Map letterboxing correctly.
  Do not introduce clipboard/file transfer, unattended control or privileged
  desktop access incidentally.
- Never test by injecting input into the user's current desktop without their
  explicit instruction. Synthetic media tests must not capture real devices.

## Build and verification

Use PowerShell 7, .NET 10, CMake 3.24+ and a C++20 compiler. On Linux install
X11/XTest development libraries. Run from this repository:

```powershell
pwsh ./scripts/build-native.ps1
dotnet build UserIdentityDesktop.csproj -c Release
dotnet test Tests/UserIdentityDesktop.Tests.csproj -c Release
pwsh ./scripts/publish-desktop.ps1
```

The native builder downloads pinned SDK 1.12.2 archives and verifies SHA-256.
Keep SDK/build/output directories ignored. Build on the target OS/architecture;
do not treat Windows cross-publishing as proof of Linux/macOS native support.
Run relevant native tests after C++ changes and rebuild before managed tests.

The opt-in two-client network test publishes only synthetic audio, camera and
screen frames. Use scoped expiring tokens in `FIREUSER_LIVEKIT_TEST_SENDER_TOKEN`
and `FIREUSER_LIVEKIT_TEST_RECEIVER_TOKEN`, plus `FIREUSER_LIVEKIT_TEST_URL`;
tokens need the same room and distinct identities. Test-only key/secret env vars
are also supported for isolated authorized testing. Clear credential environment
variables afterwards; never persist or print credentials. External URLs require
WSS; WS is limited to loopback. Normal test runs skip network tests.

Screenshots are opt-in with `FIREUSER_TEST_ARTIFACTS`; use reduced motion for
deterministic captures. The CI matrix is prepared for Windows, Linux and both
macOS architectures. Report which platforms and device paths actually ran.
Do not publish/deploy, alter production data, change firewall rules or elevate
as an incidental verification step. Document remaining failures honestly.

## Desktop parity and device UX

Preserve the Angular palette and Outfit font, the 70px navigation rail expanding to 210px on hover/focus, vector icons, 240ms cubic-bezier(.2,.8,.2,1) content transitions and reduced motion. Calls belong in an owned child window; closing it must leave and clean up media. Remote control must appear only for a native peer actively sharing its supported desktop, or an active host/grant requiring revocation. Refresh device discovery automatically when device menus open. Discovery must never start a microphone, camera or speaker test. See docs/desktop-design-and-devices.md.
