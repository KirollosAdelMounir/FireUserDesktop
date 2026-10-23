# Desktop platform compatibility

Audit date: 23 October 2026. This is a compatibility target and evidence record, not certification that every listed system has executed the packaged app.

| Operating system and processor | Package / dependency status | Verification in this audit |
| --- | --- | --- |
| Windows 10 22H2 or newer, x64 | Native `win-x64`; pinned LiveKit and FFmpeg available | Managed Release compile passes; Windows execution outstanding |
| Windows 11 ARM64 | Use the complete `win-x64` package under Windows x64 emulation | Execution on ARM hardware outstanding; no native Windows ARM64 package |
| Windows 10 ARM64 | Not supported by the current package | Windows 10 ARM emulates x86, not x64; pinned LiveKit has no Windows ARM64 release |
| macOS Sonoma 14+, Intel | Native `osx-x64`; bridge deployment target and bundle minimum explicitly 14.0 | Managed Release compile passes; authenticated SDK archive inspected; actual Sonoma execution outstanding |
| macOS Sonoma 14+, Apple Silicon | Native `osx-arm64`; bridge deployment target and bundle minimum explicitly 14.0 | Managed Release compile passes; authenticated SDK archive inspected; actual Sonoma execution outstanding |
| Ubuntu 22.04 LTS or equivalent, x64 | Native `linux-x64`; kernel 5.x alone is insufficient to describe library compatibility | Managed compile and local Linux native checks; execution on the minimum distribution outstanding |
| Ubuntu 22.04 LTS or equivalent, ARM64 | Native `linux-arm64`; pinned LiveKit and FFmpeg available | Managed Release compile passes; ARM64 execution outstanding |

ARM here means **64-bit ARM**, not ARMv7/32-bit. The local audit host is Fedora x64; it cannot execute Windows/macOS native binaries or prove ARM hardware behavior. A managed cross-target build skips native packaging and is not proof of runtime support.

All five managed Release targets passed with zero warnings/errors in this audit. The Linux x64 native build and `bridge_abi_lifecycle` CTest passed, followed by the normal Linux Release build with zero warnings/errors. The native compiler reported existing indentation warnings. Its first attempt encountered stale temporary X11 development-header paths; restoring locally extracted development headers resolved that environment failure without installing machine packages. No live-device/media session was exercised.

## macOS dependencies

Both pinned LiveKit 1.12.2 SDK archives passed their manifest SHA-256 checks. Their Mach-O load commands declare macOS 12.3 for `liblivekit.dylib`; the underlying FFI library declares 10.12 on Intel and 11.0 on Apple Silicon. Those deployment versions are below Sonoma. This check does not establish that every runtime symbol, UI library or media executable is compatible.

The pinned Intel macOS FFmpeg download returned HTTP 403 during this audit, so its binary deployment version and packaged media startup could not be verified. The ARM64 media executable also remains unverified. Release packaging must resolve and validate these dependencies before claiming complete macOS compatibility. No checksum verification was bypassed and no substitute binary was introduced.

Sonoma is Avalonia 12 Tier 2. It is outside the current .NET 10 supported OS list. An application may be tested for compatibility on an OS outside a dependency vendor's support policy, but that does not restore vendor support.

## Required release acceptance

Run the complete package on Sonoma Intel, Sonoma Apple Silicon, Windows 10 x64, Windows 11 ARM64 using x64 emulation, Ubuntu 22.04 x64 and Ubuntu 22.04 ARM64. Record OS/build, CPU, package hash and results. At minimum verify:

- Cold launch, sign-in, profile picture, theme and time preferences.
- Chat send/receive, offline queue/reconnect, notifications and encrypted attachment upload/download/preview.
- Microphone/speaker, camera, incoming calls, meeting join, screen sharing, share fullscreen and media cleanup on exit.
- Explicit-consent remote control and permission denial/revocation on supported desktops.
- Calendar, vacation review and notification actions.

Linux Wayland remote control remains unavailable; X11/XTest is the supported Linux host-control path. macOS requires user-granted Screen Recording and Accessibility permissions. Neither compilation nor headless tests confirm these permission/device paths.

Sources: [LiveKit SDK platforms](https://github.com/livekit/client-sdk-cpp), [Windows ARM emulation](https://learn.microsoft.com/en-us/windows/arm/apps-on-arm-x86-emulation), [.NET 10 OS support](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md), [Avalonia platforms](https://docs.avaloniaui.net/docs/supported-platforms).
