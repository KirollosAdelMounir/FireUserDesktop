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

Windows enumerates visible, non-minimized windows and Avalonia monitor bounds. FFmpeg gdigrab receives the chosen HWND or the exact monitor rectangle, including negative monitor offsets. There is no additional Linux portal dialog. Protected windows/secure desktops remain subject to OS restrictions.

macOS 12.3+ enumerates `SCShareableContent` and uses ScreenCaptureKit with the selected display ID or a desktop-independent window filter. The native adapter keeps one latest frame, handles BGRA row stride and converts to RGBA. OS Screen Recording permission is still required; application code cannot bypass the initial permission prompt. ScreenCaptureKit is packaged as `libfireuser_screencapture.dylib`. Source IDs are resolved again before capture so a closed source produces a failure.

X11 sessions enumerate window IDs and monitor rectangles. FFmpeg x11grab receives `window_id` for a window or the selected monitor region. Closing/minimizing a source may end its stream; choosing it again requires a fresh source selection.

Portal/window/individual-monitor capture does not advertise default-desktop remote input, because the existing input protocol does not carry a selected-source coordinate mapping. This does not grant remote-control consent or start any input injection.

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

The C++ bridge samples publisher WebRTC statistics every two seconds while a screen track is active. Upload estimates below 100 decimal KB/s use 480p/15 FPS; 100–200 KB/s use 720p/30 FPS; 200–400 KB/s use 1080p/30 FPS; 400 KB/s and above permit 1080p/60 FPS. High RTT or packet loss triggers the low tier. Increases require a healthy interval; without an estimate the ceiling is 720p/30 FPS. VP8 uses maintain-resolution degradation to prioritize screen textures. These are adaptive ceilings, not guaranteed delivery rates or exact parity with Angular’s separate camera budget.

Tier changes replace only the screen publication and can briefly interrupt that tile. Lower tiers are resampled in native C++; 1080p uses the direct buffer-copy path. Producer pacing preserves the final update from a static portal source and releases the publication lock while waiting so microphone frames can continue. Camera and screen previews retain their independent 10 FPS ceiling.

Windows native compilation and disconnected ABI/quality-boundary tests ran locally for this revision. Linux/macOS compilation, compositor/device permissions and two-client network quality still require the target platform or configured synthetic LiveKit tests; Windows build results do not prove those paths.
