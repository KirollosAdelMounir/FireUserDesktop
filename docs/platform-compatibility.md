# Desktop platform compatibility

Audit date: 23 October 2026. This is a compatibility target and evidence record, not certification that every listed system has executed the packaged app.

| Operating system and processor | Package / dependency status | Verification in this audit |
| --- | --- | --- |
| Windows 10 22H2 or newer, x64 | Native `win-x64`; pinned LiveKit and FFmpeg available | Native ABI/lifecycle, full suite (309 passed, 12 skipped), FFmpeg and packaged startup pass on Windows Server 2022; Windows 10 device acceptance outstanding |
| Windows 11 ARM64 | Use the complete `win-x64` package under Windows x64 emulation | Actual Windows 11 ARM runner passes x64 native ABI/lifecycle, FFmpeg and packaged startup; no native Windows ARM64 package |
| Windows 10 ARM64 | Not supported by the current package | Windows 10 ARM emulates x86, not x64; pinned LiveKit has no Windows ARM64 release |
| macOS Sonoma 14+, Intel | Native `osx-x64`; bridge deployment target and bundle minimum explicitly 14.0 | Native ABI/lifecycle, full suite (309 passed, 12 skipped), FFmpeg and packaged startup pass on macOS 15 Intel; Intel Sonoma execution outstanding |
| macOS Sonoma 14+, Apple Silicon | Native `osx-arm64`; bridge deployment target and bundle minimum explicitly 14.0 | Native ABI/lifecycle, full suite (309 passed, 12 skipped), FFmpeg and packaged startup pass on Sonoma ARM64 |
| Ubuntu 22.04 LTS or equivalent, x64 | Native `linux-x64`; kernel 5.x alone is insufficient to describe library compatibility | Native ABI/lifecycle, full suite (317 passed, 4 skipped), FFmpeg and native window startup pass on Ubuntu 22.04 x64 |
| Ubuntu 22.04 LTS or equivalent, ARM64 | Native `linux-arm64`; pinned LiveKit and FFmpeg available | Native ABI/lifecycle, full suite (317 passed, 4 skipped), FFmpeg and native window startup pass on Ubuntu 22.04 ARM64 |

ARM here means **64-bit ARM**, not ARMv7/32-bit. The local host is Fedora x64. Platform execution evidence comes from matching GitHub runners in [successful run 39991431377](https://github.com/KirollosAdelMounir/FireUserDesktop/actions/runs/39991431377), source commit `0a6358fd61f98e5929aefdec08575f62f8d7c3b6`. A managed cross-target build alone is not proof of runtime support. All five native targets also passed 16 build-tool tests each.

All five managed Release targets passed with zero warnings/errors in this audit. The Linux x64 native build and `bridge_abi_lifecycle` CTest passed, followed by the normal Linux Release build with zero warnings/errors. The native compiler reported existing indentation warnings. Its first attempt encountered stale temporary X11 development-header paths; restoring locally extracted development headers resolved that environment failure without installing machine packages. No live-device/media session was exercised.

An additional Ubuntu 24.04 container rebuilt the native bridge and passed its ABI/lifecycle test and shared-library resolution. An Arch Linux container accepted the Arch package metadata; native LiveKit and a real application window also started in a minimal Arch container without system fonts after configuring the bundled Outfit default. These checks do not exercise live media or every installer interaction.

## macOS dependencies

Both pinned LiveKit 1.12.2 SDK archives passed their manifest SHA-256 checks. Their Mach-O load commands declare macOS 12.3 for `liblivekit.dylib`; the underlying FFI library declares 10.12 on Intel and 11.0 on Apple Silicon. Those deployment versions are below Sonoma. This check does not establish that every runtime symbol, UI library or media executable is compatible.

The previous pinned macOS binary supplier returned HTTP 403 both locally and on the Sonoma GitHub runner. The build now compiles the same FFmpeg 9.0.2 release from checksum-pinned official source, with a macOS 14.0 deployment target and OS-provided frameworks. No checksum verification is bypassed. The native GitHub validation run is recorded in the release-build documentation; device/session acceptance remains distinct from compilation and startup checks.

Sonoma is Avalonia 12 Tier 2. It is outside the current .NET 10 supported OS list. An application may be tested for compatibility on an OS outside a dependency vendor's support policy, but that does not restore vendor support.

## Remaining device acceptance

Startup and automated checks above are complete. Interactive end-to-end device acceptance remains on Sonoma Intel, Sonoma Apple Silicon, Windows 10 x64, Windows 11 ARM64 using x64 emulation, Ubuntu 22.04 x64 and Ubuntu 22.04 ARM64. Record OS/build, CPU, package hash and results. At minimum verify:

- Cold launch, sign-in, profile picture, theme and time preferences.
- Chat send/receive, offline queue/reconnect, notifications and encrypted attachment upload/download/preview.
- Microphone/speaker, camera, incoming calls, meeting join, screen sharing, share fullscreen and media cleanup on exit.
- Explicit-consent remote control and permission denial/revocation on supported desktops.
- Calendar, vacation review and notification actions.

Linux Wayland remote control is opt-in and requires a RemoteDesktop portal supporting keyboard, pointer and one approved monitor stream. The current compositor passed live keyboard delivery and pointer acceptance testing. X11/XTest remains the separate X11 host-control path. macOS requires user-granted Screen Recording and Accessibility permissions. Neither compilation nor headless tests confirm these permission/device paths.

Sources: [LiveKit SDK platforms](https://github.com/livekit/client-sdk-cpp), [Windows ARM emulation](https://learn.microsoft.com/en-us/windows/arm/apps-on-arm-x86-emulation), [.NET 10 OS support](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md), [Avalonia platforms](https://docs.avaloniaui.net/docs/supported-platforms).

## Overall test release 1.0.1 (25 October 2026)

Completed the requested remote-control updates before building the installers: Wayland monitor sharing always requests system input permission, participant names replace screen-choice identifiers, screen context menus request control, and a grant opens/focuses the corresponding screen fullscreen. Explicit sharer consent, authenticated participant identity, lease expiry and revocation remain required. Local full suite: 356 passed, 4 platform/environment skips (360 total); final focused checks: 22 passed. Packaging/build tooling: 20 passed plus 2 macOS bundle checks.

The prior Fedora-built package was not portable to Ubuntu 24.04: loader checks found GLIBC_ABI_GNU2_TLS, GLIBC_2.43 and OPENSSL_3.2.0 requirements. Rebuilt/verified native dependencies on supported Ubuntu baselines and added a release ABI gate. The actual new CI Linux x64 package passed media library load/create/destroy and native-window startup in an Ubuntu 24.04 container. Real device calls remain part of overall testing.

macOS now has a declared branded ICNS resource, including a 1024-pixel representation. CoreGraphics linking and repeat install-time rpath fixups were repaired. Intel and Apple Silicon native builds, tests, packaged startup and installer production all passed. Windows x64, Linux x64/ARM64 and Windows 11 ARM64 x64-emulation validation also passed. Windows/macOS platform input providers were retained.

Published unsigned test installers and SHA-256 checksums at https://github.com/KirollosAdelMounir/FireUserDesktop/releases/tag/v1.0.1-test.20261025 . Build run 40136236803 and publication run 40137338202 succeeded. Product source commit: 47882ab8a06bfe60e984192043b590441b1c0575. A separate publication-only workflow branch transfers artifacts from that exact successful build; it cannot publish an incomplete/failed build or a release pinned to another commit. The release includes 12 installers, a checksum manifest and an overall testing checklist. Existing backend/frontend services were not changed or restarted for this release.
