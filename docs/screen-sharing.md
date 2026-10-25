# Native screen and window selection

On Windows, macOS and X11, Share screen opens an Avalonia picker with **Entire screen** and **Window** tabs. Discovery enumerates metadata only; it does not take screenshots, publish media, or start camera/microphone capture. Cancel/Escape starts nothing. Stop sharing does not open the picker again. Source availability is refreshed for each sharing action.

## Wayland (including Hyprland)

Wayland bypasses the application picker and opens the compositor’s screen selection directly. If the compositor exposes only window sharing, that advertised type is used. Discovery checks `org.freedesktop.portal.ScreenCast.AvailableSourceTypes`. The system selection runs `CreateSession → SelectSources → Start → OpenPipeWireRemote`. `Start` opens the compositor's sharing dialog; Fire User does not implement or replace Hyprland/KDE/GNOME's permission UI. The portal receives one requested type and `multiple=false`, with no persistence or restore token. Cancellation closes the pending request and session. Session revocation stops capture.

Native GStreamer consumes the portal-issued PipeWire Unix FD and selected stream. A returned `pipewire-serial` is preferred as `target-object`; older portals use their selected node ID. The application never opens an unrestricted PipeWire connection as a fallback, and never uses XWayland capture under Wayland. It publishes only after the first complete RGBA frame. GStreamer converts/scales to the existing 1920×1080 screen transport; one latest appsink buffer is retained, with local preview capped at 10 FPS. Static sources do not require a repeated frame or an inactivity timeout.

The desktop session must have PipeWire, `xdg-desktop-portal`, and the correct compositor backend. Hyprland uses `xdg-desktop-portal-hyprland`; KDE/GNOME/wlroots use their corresponding backends. Portal configuration belongs to the desktop session, not to this application. Required GStreamer core/app, base and PipeWire plugins remain distribution dependencies:

- Fedora: `gstreamer1`, `gstreamer1-plugins-base`, `gstreamer1-plugin-pipewire`.
- Debian/Ubuntu: `libgstreamer1.0-0`, `libgstreamer-plugins-base1.0-0`, `gstreamer1.0-plugins-base`, `gstreamer1.0-pipewire`.
- Arch/Hyprland: `gstreamer`, `gst-plugins-base`, `gst-plugin-pipewire`, plus the running Hyprland portal backend.

Missing services/plugins produce an actionable error; they do not start a black X11 stream. The application does not install packages, change portal configuration, or restart desktop services.

## Windows, macOS and X11

Windows enumerates visible, non-minimized windows and Avalonia monitor bounds. Monitor capture resolves the selected physical bounds to the matching DXGI output on FFmpeg's default adapter and uses Desktop Duplication (`ddagrab`) at up to 60 FPS, including the mouse cursor and composed desktop surfaces. D3D11 scales to the aspect-correct 1080p ceiling and converts to NV12 before download. If this driver path fails at startup or during a share, capture retries the exact selected monitor bounds through GDI once, retaining the published track and control scope. It never falls back from a selected window to the entire desktop. Window capture retains `gdigrab` with the chosen HWND; monitors outside that adapter retain exact GDI bounds, including negative offsets. A window-only share may omit separate popup windows; share the entire monitor when these are required. There is no additional Linux portal dialog. Protected windows/secure desktops remain subject to OS restrictions.

macOS 12.3+ enumerates `SCShareableContent` and uses ScreenCaptureKit with the selected display ID or a desktop-independent window filter. The native adapter keeps one latest frame, handles BGRA row stride and converts to RGBA. OS Screen Recording permission is still required; application code cannot bypass the initial permission prompt. ScreenCaptureKit is packaged as `libfireuser_screencapture.dylib`. Source IDs are resolved again before capture so a closed source produces a failure.

X11 sessions enumerate window IDs and monitor rectangles. FFmpeg x11grab receives `window_id` for a window or the selected monitor region. Closing/minimizing a source may end its stream; choosing it again requires a fresh source selection.

Window capture remains view-only. A Linux Wayland monitor share always requests system permission for remote control; this uses RemoteDesktop.CreateSession → SelectDevices(keyboard|pointer) → ScreenCast.SelectSources(monitor) → RemoteDesktop.Start → ScreenCast.OpenPipeWireRemote. Hosting is advertised only if the desktop grants both input devices and returns valid monitor dimensions. Input uses that portal stream, never XTest or a privileged input device. A selected primary screen may advertise it only when its origin and dimensions exactly match the native input provider’s desktop bounds. Control still requires explicit sharer approval for each share. Browser participants can control a supported native share; browser shares remain view-only. See [call-stage-and-browser-control.md](call-stage-and-browser-control.md).

## Verification and limits

The Linux native bridge ABI lifecycle test and Release build passed. The full Release suite passed **112 tests, failed 0, skipped 2**. The skipped tests require an explicitly configured LiveKit test server. The Linux startup smoke opened a native window.

| Behavior | Regression evidence |
| --- | --- |
| Choose window before capture; stop without prompting again | `ScreenSharingTests.SharePicker_ChoosesWindowBeforeCapture_AndStopDoesNotOpenPickerAgain` |
| Cancel picker without capture | `ScreenSharingTests.SharePicker_EscapeCancelsWithoutStartingCapture` |
| Portal cancellation/error never falls back | `ScreenSharingTests.PortalCancelOrFailure_DoesNotEnableSharingOrFallback` |
| Leave cancels pending portal approval | `ScreenSharingTests.LeaveDuringPortalApproval_CancelsPendingStartAndLeavesCall` |
| A source ending during startup stays off; no stale preview tile | `ScreenSharingTests.SourceStopsDuringStartup_SharingRemainsOff_AndPendingPreviewDoesNotReappear` |
| Selected monitor/window arguments | `ScreenSharingTests.SelectedScreen_UsesOnlyItsBounds_AndSelectedWindowUsesItsStableId` |
| Refuse XWayland fallback | `ApiContractTests.Capture_RefusesXWaylandFallbackEvenWhenDisplayExists` |
| Portal request ordering, single source, serial, Unix FD ownership and idempotent close | `ScreenCastPortalTests.ApprovedSource_UsesPortalSessionAndRemoteFd_AndClosesOnDispose` |
| Portal deny/cancel cleanup | `ScreenCastPortalTests.CancelOrDenial_ClosesSessionWithoutOpeningPipeWireRemote` |
| Cancel an open system request | `ScreenCastPortalTests.CancelWhileChooserIsOpen_ClosesRequestAndSession` |
| Compositor revocation | `ScreenCastPortalTests.CompositorRevocation_MarksSessionClosed` |
| Real native sample copying and invalid-size rejection | `PipeWireFrameTests.NativeGStreamerRgbaFrames_CopyCorrectPixels_AndRejectWrongDimensions` |

Portal protocol tests use a private dbus-daemon and a fake portal, with `/dev/null` as a disposable remote FD. Native frame tests use GStreamer `videotestsrc`, never a real screen, window or device. No real compositor permission prompt/capture or multi-party transmission was exercised. Windows capture and macOS compilation/device capture must be validated on those platforms; Linux compilation does not establish their success.

References: [XDG ScreenCast lifecycle and stream permissions](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.ScreenCast.html), [Hyprland portal integration](https://wiki.hypr.land/Hypr-Ecosystem/xdg-desktop-portal-hyprland/), [FFmpeg source options](https://ffmpeg.org/ffmpeg-devices.html), [Apple ScreenCaptureKit](https://developer.apple.com/documentation/screencapturekit/capturing-screen-content-in-macos).


## Adaptive screen quality (October 2026)

The C++ bridge samples publisher WebRTC statistics every two seconds while a screen track is active. Upload estimates below 100 decimal KB/s use 480p/15 FPS; 100–200 KB/s use 720p/30 FPS; 200–400 KB/s use 1080p/30 FPS; 400 KB/s and above permit 1080p/60 FPS. High RTT or packet loss triggers the low tier. Increases require a healthy interval; without an estimate the ceiling is 720p/30 FPS. Screen VP8 uses maintain-framerate degradation so WebRTC can lower resolution under encoder/network pressure instead of building a stale screen queue. These are adaptive ceilings, not guaranteed delivery rates or exact parity with Angular’s separate camera budget.

Tier changes retain the same screen source/publication and grant; frame dimensions and pacing adapt without unpublishing the tile. The encoder advertises the 6.4 Mbps / 60 FPS ceiling once and WebRTC controls actual transmission. Lower tiers are resampled in native C++; 1080p uses the direct buffer-copy path. RGBA portal/macOS input retains producer pacing to preserve a static source's final update. Continuous FFmpeg screen input skips early frames without sleeping on an old image, uses fixed-point I420 plane scaling, and does not hold the publication lock during resampling, SDK submission or local preview conversion. FFmpeg capture drains continuously into one pooled latest-frame slot, dropping superseded frames while the sender is paced; it does not accumulate stale input-responsive screen updates. Camera and screen previews retain their independent 10 FPS ceiling.

Windows native compilation and disconnected ABI/quality-boundary tests ran locally for this revision. Linux/macOS compilation, compositor/device permissions and two-client network quality still require the target platform or configured synthetic LiveKit tests; Windows build results do not prove those paths.


### Linux Wayland input verification (24 October 2026)

Wayland monitor sharing always requests the combined system permission for capture, keyboard and pointer input. There is no next-share opt-in checkbox. The desktop system prompt and Fire User participant approval are both required. This system portal path is Linux/Wayland-only. Windows SendInput and macOS Accessibility paths are unchanged by this feature; no Windows/macOS packages were published for it.

Portal input uses the documented RemoteDesktop D-Bus Notify methods, with physical HID keys mapped to evdev codes, per-monitor absolute coordinates, and held-input tracking. Revocation, disconnect, grant expiry and stop-sharing reject stale input and release pressed keys/buttons. Sessions request no persistent permission or restore token. Desktop implementations must expose the RemoteDesktop portal for a monitor share to be ready for remote input. Window shares remain view-only.

On the current Wayland session the user approved the system prompt and ran an isolated input probe: its own field received `fireuser`, and the portal accepted absolute pointer movement. No real mouse-button clicks were injected. Private D-Bus protocol tests verify button codes, permission masks, geometry, cancellation, stale-grant rejection and held-input release. This is evidence for this compositor; other Wayland implementations require their own RemoteDesktop portal support.

API reference: https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.RemoteDesktop.html


## Named screen control workflow

Remote screen choices and screen tiles use the participant's display name (for example, `Kirollos Adel's screen`); protocol identities remain internal. Late participant metadata refreshes those labels without replacing the security identity. Right-clicking a remote screen offers **Request remote control** when the host advertises a supported share. Requesting alone cannot enable input. A matching grant focuses that screen, enters fullscreen and focuses its native input surface. A grant received before the first screen frame waits for that frame. Revocation disables input and restores the pre-control window/panels. Ctrl+Alt+Escape releases control and leaves fullscreen without a later redundant status update forcing it open again. Ordinary Escape is forwarded during active control. Explicit sharer consent and Ctrl+Alt+Escape revocation remain in place.

Regression evidence: `NamedShareContextRequestRequiresGrantThenFocusesFullscreenAndReleasesOnRevoke`, `GrantBeforeScreenFrameWaitsForThatShareAndRenamingNeverShowsIdentifiers`, and `PortalScreenShareIsAlwaysReadyToRequestSystemControlPermission`.

## Remote control request and input UX (25 October 2026)

Incoming requests open a non-activating, topmost toast at the right of the call monitor, with the participant name, Approve/Reject buttons and a 30-second countdown. The host rejects unanswered requests on its own timer; approval checks the same deadline, and repeated pending requests cannot extend it. Approval still grants only five minutes. Closing the call closes the toast. Both themes and reduced motion are supported. While controlling a screen, right-click is reserved for remote input and cannot open the local tile menu. Pointer moves are bounded to roughly 60 per second. Control uses the same adaptive screen track and quality ceilings as viewing; granting control never restarts capture or publication.

The native package and managed client require matching ABI 7. Desktop Duplication behavior follows the [FFmpeg ddagrab implementation](https://ffmpeg.org/doxygen/trunk/vsrc__ddagrab_8c.html). Protected content and secure desktops remain excluded by the operating system. Real desktop capture, injected input and two-client latency were not exercised during this change; automated checks use synthetic frames and substituted input services.

### Windows LAN delay and capture recovery (25 October 2026)

The FFmpeg screen pipe now carries planar I420 (3,110,400 bytes at 1920×1080) instead of RGBA (8,294,400 bytes), a 62.5% reduction in raw inter-process bytes. FFmpeg uses one input queue slot, minimal probing, no probe buffering and immediate output flushing. GDI fallback uses the same I420 transport. The SDK receives I420 directly; only a 10 FPS local preview converts to RGBA. Camera capture and native portal/macOS formats stay on their existing RGBA contract. Sender/receiver video queues remain bounded. These changes reduce local copying, conversion and queuing; they do not establish a measured end-to-end LAN latency guarantee.

Recovery tests simulate preferred-provider startup failure, midstream loss, both-provider failure and cancellation, including disposal of both attempts and refusal to widen a window share. The packaged FFmpeg is exercised with generated color only. Native tests verify planar scaling pixels, quality tiers and SDK source/preview conversion using synthetic I420 frames. The affected second Windows machine and real two-client latency still require verification.

Follow-up validation: Release build passed; native CTest passed 3/3 (`video_scaling_pixels`, `video_source_i420`, `bridge_abi_lifecycle`). Focused managed checks passed 31/31. The latest full desktop run passed 358, failed 1 and skipped 18; the failure was the existing `ShutdownTests.ExplicitExit_IsBounded_AndStartsCleanupOnlyOnce` timeout, which passed in the focused run. The viewer also uses a contiguous bitmap copy when strides match and render-priority frame dispatch to avoid background-priority starvation.

## Interactive control, clipboard and fullscreen footer (25 October 2026)

The native client targets a healthy-network input budget of 50 ms. This is a target, not a verified maximum input-to-photon delay. Native peers negotiate fast input; pointer moves use a one-item latest queue and lossy delivery, with replay counters independent from reliable keys/buttons. Clicks and releases include their positions and fence earlier pointer packets, so late motion cannot move a click back to an old location. Compatible older peers retain reliable input. Approved control enables 60 FPS pacing at the 720p and 1080p tiers; the low-bandwidth 480p fallback remains 15 FPS. Input-driven acknowledgements show input round-trip time in call controls; that metric excludes capture, encoding, decoding, monitor refresh and application rendering. No business-state polling is added.

System keys are captured while the foreground call owns an approved remote screen. Windows uses its low-level hook, Linux uses an asynchronous call-window XCB/XWayland grab, and macOS uses an Accessibility-authorized session event tap. Keyboard capture expires if the UI cannot renew its lease. Scrolling and side buttons use negotiated extended input. Ctrl+Alt+Escape is the local escape hatch; ordinary Escape/F11 go to the remote screen. OS secure sequences and compositor-reserved shortcuts remain governed by the OS. See [focused remote input](native-collaboration.md#focused-remote-input-104) for lifecycle and validation limits.

Ctrl+C on an approved remote screen requests a copy of the remote selection. Windows waits for a private message-only clipboard notification, then reads the changed text or file/folder list. Ctrl+V sends the local clipboard to the host and pastes after verified receipt. Received remote content can be pasted locally through the usual OS clipboard. File/folder data is spooled and streamed with offsets, SHA-256 verification and a four-chunk acknowledgement window. Binary chunks are 8 KiB, keeping their JSON envelopes within the 15 KiB reliable budget; pointer packets stay under 1,300 bytes, per [LiveKit packet guidance](https://docs.livekit.io/transport/data/packets/). These are transport chunk limits, with no total file/folder/batch size cap.

Clipboard traffic has a separate reliable topic and worker from pointer/key delivery. It requires the exact authenticated participant, scope, grant and live consent, including separate authorities during reciprocal control. The sharer's approval toast discloses clipboard/file access. There is no ambient clipboard mirroring or arbitrary remote filesystem lookup. Transfers support one active copy/paste per control direction; a paste during a copy waits for completion. Progress is visible in call feedback. A control grant ending cancels its transfers and cleans its private temporary staging; already pasted copies and clipboard text remain with their recipient. Symbolic links, traversal, alternate data streams and invalid portable filenames are rejected. Interrupted transfers must be copied again after reconnect or renewed approval; persistent clipboard resume is not implemented.

Screen/control actions use a TV remote vector glyph. Fullscreen call controls occupy a reserved row below the video and never overlap the shared image. Hover/focus changes their opacity and hit testing, with stable stage geometry and both light/dark themes.

Validation uses serialized synthetic peer transfers, generated folder trees, pixel/quality tests, headless layout/focus tests and OS export/private-notification-window lifecycle checks. Tests do not inject input into the user's desktop. Actual two-PC keyboard shortcuts, clipboard interoperability with target applications, and end-to-end LAN latency still need device verification. Managed/native output must be updated together (ABI 7).

Final Windows validation for this change: clean Release build succeeded with zero warnings/errors; the full managed suite passed 389, failed 0 and skipped 18 (407 total). All 55 focused checks and all three native CTest checks passed. The earlier intermittent shutdown timeout did not recur in this final full run. Light/dark fullscreen captures confirm the footer stays below the synthetic shared image.

### Transfer progress and cancellation

Explicit remote file/folder copy and paste now use the same persistent transfer toast as chat attachments. The sending device shows **Uploading remote files**, and the receiving device shows **Downloading remote files**, with byte progress, speed and percentage. Preparation starts with an unknown total; the byte total is the staged archive's transport size. Upload progress counts acknowledged chunks, download progress counts received chunks, and verification/extraction/paste must finish before success reaches 100%. Item counts represent selected top-level files/folders. Text-only clipboard gestures do not create file-transfer toasts.

Every active chat attachment or remote file transfer has its own **Cancel** button. Chat cancellation reaches encryption/preparation, HTTP upload chunks, explicit downloads and ZIP downloads; incomplete local output is deleted and existing destinations are preserved. A remote cancellation is sent to the other participant, stops that clipboard transfer worker and removes incomplete staging without revoking screen control. A new copy/paste gesture starts a fresh worker. Cancelled operations show **Transfer cancelled**; integrity failures show **Transfer failed**. Completed server messages and copies already handed to a target application cannot be undone by cancellation. Update both desktop clients for progress/cancellation on both devices.

Validation for transfer progress/cancellation: clean Windows Release build passed with zero warnings/errors. All 56 focused checks passed, including copy and paste cancelled from either device, chat cancellation during identity lookup/chunk upload, single and ZIP download cancellation, actual partial-download cleanup and light/dark toast controls. The final full suite passed 401, failed 1 and skipped 18 (420 total); the pre-existing `ShutdownTests.ExplicitExit_IsBounded_AndStartsCleanupOnlyOnce` timeout recurred and passed in the focused run. Tests use substituted peers/HTTP and synthetic files; live two-PC transfer validation remains.
