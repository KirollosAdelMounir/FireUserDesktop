# Native installation packages

`python3 scripts/build-release.py --target all` requests both 64-bit processor families on Windows, macOS and Linux. GitHub Actions generates the complete archives and the following installers, then the local command downloads both artifact types. The workflow and current source must first be pushed to GitHub and the local GitHub CLI must be authenticated. See [build instructions](building-release-packages.md).

| Platform | Files | Installation behavior |
| --- | --- | --- |
| Windows x64 | `FireUserConnect-<version>-win-x64-Setup.exe` | Per-user installation, Start menu shortcut, optional desktop shortcut, meeting-link handler, uninstaller; Windows 10 22H2 minimum |
| Windows ARM64 compatibility | `FireUserConnect-<version>-win-arm64-emulated-Setup.exe` | Windows 11 ARM64 only, x64 emulation; clearly separate from native ARM builds |
| macOS Intel / Apple Silicon | `FireUserConnect-<version>-osx-<arch>.pkg` | Installs the matching `.app` into `/Applications`; Sonoma 14 minimum and matching CPU architecture enforced |
| Ubuntu/Debian family | `.deb` for amd64 / arm64 | Application/menu/icon/meeting-link registration; distro dependencies declared for apt |
| Fedora / compatible RPM distributions | `.rpm` for x86_64 / aarch64 | Application/menu/icon/meeting-link registration; distro dependencies declared for dnf |
| Arch / Arch Linux ARM | `.pkg.tar.zst` for x86_64 / aarch64 | Native pacman package with dependencies and desktop integration; Arch Linux ARM is a separate community distribution |
| Other compatible Linux desktops | `.AppImage` for x86_64 / aarch64 | Portable executable containing the app, .NET and packaged media; host graphics/audio/system-library requirements still apply |

The actual executable/process image is **Fire User Connect** (`Fire User Connect.exe` on Windows). Existing configuration, encryption identity pins and notification/application identifiers retain their existing storage locations. Renaming the application does not create a new account/session store. Some Linux tools truncate the kernel's short process-name field; the executable and full command line retain the complete name.

## Dependencies and permissions

The packaged .NET runtime, native bridge/SDK, FFmpeg and Windows app-local C++ redistributables come with the application. Mac FFmpeg 9.0.2 is compiled from checksum-pinned official source with a Sonoma 14 deployment target, avoiding the previous supplier's HTTP 403; its license files accompany the binary. End users do not need a compiler, Python, CMake or the .NET SDK. Linux package metadata declares ICU, libc/C++ runtime, X11/XTest, OpenSSL, font, graphics, audio, notifications and GStreamer dependencies using each distribution's package names. GStreamer PipeWire plugins are optional dependencies for Wayland sharing. Wayland remote input remains unavailable.

Use a dependency-resolving installer, for example `apt install ./file.deb`, `dnf install ./file.rpm` or `pacman -U ./file.pkg.tar.zst`. The Linux builds originate on Ubuntu 22.04 to keep the libc baseline older than current Fedora/Arch. Portable AppImages do not install host dependencies automatically; if FUSE is unavailable their runtime supports `--appimage-extract-and-run`. AppImage registration uses the persistent AppImage path rather than its temporary mount.

OS microphone, camera, Screen Recording and Accessibility permissions remain explicit user choices. Installers do not grant those privileges, alter firewall rules or replace system glibc. Arch packages and installers are not added to official distribution repositories by this workflow.

## Signing and validation limits

The workflow does not contain signing identities or credentials. Windows installers and macOS PKGs are unsigned until an owner-provided signing/notarization workflow is configured; generating an installer does not bypass SmartScreen or Gatekeeper.

Windows ARM packaging is gated on the actual x64 bridge ABI/lifecycle, FFmpeg startup and application startup running on a Windows 11 ARM runner. Other target builds run native CTest, managed tests and packaged FFmpeg startup. Real microphone/camera/remote-control sessions, privileged installation and uninstall testing on all target systems remain release acceptance work.

## Regression evidence

| Behavior | Tests / checks |
| --- | --- |
| Host caches do not reuse Fedora curl on Ubuntu | `NativeBuildTests.test_environment_key_is_stable_but_changes_for_distro_libc_compiler_and_checkout`, `test_curl_reuses_only_matching_host_and_ignores_legacy_fedora_prefix`; actual Ubuntu 24.04 native build and CTest |
| Correct remote run and installer downloads | `ReleaseBuildTests.test_dispatch_correlates_exact_run_and_downloads_requested_target`, `test_resume_does_not_dispatch_and_downloads_all_packages`, `test_failed_run_is_not_downloaded`, `test_timeout_reports_request_and_never_dispatches_twice`, `test_windows_arm_download_is_explicitly_labelled_emulated` |
| Install payload, dependency metadata, CPU/OS gates and tool integrity | `InstallerBuildTests` verifies payload preservation, quoted executable path, CPU mappings/dependencies for all Linux formats, Windows architecture gates, Sonoma architecture/minimum requirements and checksum rejection |
| Renamed assembly retains resources and AppImage uses persistent launcher | `AppIdentityTests.BrandedAssemblyKeepsIconsAndEmbeddedFontsAccessible`, `AppImageRegistrationUsesPersistentLauncherRatherThanTemporaryMount` |

Run Python build-tool tests with `python3 -m unittest discover -s Tests -p '*Build*Tests.py'`. These test observed generated data and command behavior; they do not substitute for running the native installers on their target operating systems.

Local checks: 315 desktop tests passed, 4 platform/network tests skipped; 16 build-tool tests passed. All four Linux formats were built in a packaging verification container, and an actual Arch Linux container accepted the `.pkg.tar.zst` metadata using `pacman -Qip`. Packaging-test payloads compiled on Fedora are not advertised as Ubuntu-compatible release binaries; the GitHub release payloads are compiled on Ubuntu 22.04.
