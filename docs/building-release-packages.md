# Build release packages from Fedora

Run the build command locally; GitHub Actions compiles and packages each native target on its matching runner. It then downloads the completed packages to this checkout. This is remote native building, not cross-compiling the Windows/macOS C++ libraries with Fedora's compiler.

## First use

The GitHub repository is `KirollosAdelMounir/FireUserDesktop`. The workflow and source changes must be committed and pushed there before dispatch. The local command does not push files, create commits or upload an uncommitted working tree. The workflow must exist on the repository's default branch to enable manual dispatch.

Install the GitHub CLI and sign in on the machine running the command:

```bash
sudo dnf install gh
gh auth login
```

Use an account allowed to run Actions in that repository. The script uses the CLI's authentication; no token should be pasted into the command or stored in project configuration.

## Build and download

```bash
# Windows x64/ARM compatibility, both Macs, Linux x64/ARM64
python3 scripts/build-release.py --target all

# Or build one target
python3 scripts/build-release.py --target win-x64
python3 scripts/build-release.py --target win-arm64
python3 scripts/build-release.py --target osx-x64
python3 scripts/build-release.py --target osx-arm64

# Build an already-pushed branch/tag
python3 scripts/build-release.py --target all --ref main

# Resume watching/downloading an existing run without creating another build
python3 scripts/build-release.py --run-id 123456789
```

The command prints the run URL and downloads archives into `artifacts/downloads/<run-id>/`. Archives contain the complete native libraries and media runtime. Keep the whole package together; copying only the executable loses required native dependencies. `.tar.gz` archives preserve Unix executable permissions, including the unsigned macOS `.app`. Signing/notarization and real-device permission checks remain separate release work.

It also downloads the OS installers: Windows setup EXEs, macOS PKGs, Linux DEB/RPM/Arch packages and AppImages. See [installer formats and requirements](installers.md). The local command restores the executable bit on downloaded AppImages.

Failed builds are reported and are not downloaded as successful releases. The command matches its unique request ID, so another person's simultaneous run cannot be mistaken for the requested build. It never retries dispatch automatically after a timeout.

The workflow builds Linux on Ubuntu 22.04 x64/ARM64 rather than Fedora, to avoid introducing newer Fedora glibc dependencies. macOS ARM64 builds on Sonoma; Intel builds on the available Intel runner with an explicit Sonoma deployment target. Neither hosted compilation nor the automated tests prove that every camera/audio/permission path works on every supported device.

The default run produces six packages: `win-x64`, `win-arm64-emulated`, `osx-x64`, `osx-arm64`, `linux-x64` and `linux-arm64`. The Windows ARM compatibility job runs the actual x64 native ABI/lifecycle executable and application startup on a Windows 11 ARM runner before packaging. There is no native Windows ARM64 calling build; the explicitly labelled compatibility package uses x64 emulation, includes that requirement in `COMPATIBILITY.txt`, and excludes Windows 10 ARM.

## Fedora-to-Ubuntu native loader failures

An otherwise successful .NET publish can fail at call startup if it reuses Fedora-built native artifacts. The inspected Fedora curl library requires `GLIBC_2.43`; Ubuntu 24.04 provides glibc 2.39. Self-contained .NET publishing does not bundle the operating-system C library.

The Unix builder now isolates its CMake and curl build caches by distribution/version, libc, architecture, compiler identity, flags and checkout path. Standard builds always invoke the native validation step and let CMake decide which sources need compilation. Native files are copied to the publish directory regardless of timestamps, preventing an older cached native binary from surviving a host switch. Existing SDK downloads and old generated caches are preserved.

On Ubuntu, install the documented build prerequisites and publish from source using these updated scripts. On Fedora, use the GitHub command for Ubuntu-compatible release binaries. Do not copy glibc from another distribution or replace Ubuntu's system glibc.

The native loader now preserves the original exception and writes its diagnostic to standard error. If a package still fails, launch it from its extracted directory:

```bash
./"Fire User Connect" 2> native-loader.log
ldd ./libfireuser_livekit.so
ldd ./libcurl.so.4
```

The log and `ldd` identify missing libraries or incompatible symbol versions; the generic in-app message alone does not distinguish them. Loader diagnostics are emitted without logging meeting tokens.

## Verified build

[Run 39991431377](https://github.com/KirollosAdelMounir/FireUserDesktop/actions/runs/39991431377) completed successfully for source commit `0a6358fd61f98e5929aefdec08575f62f8d7c3b6`, producing all six installer sets and portable archives. Download or resume that exact output with `python3 scripts/build-release.py --run-id 39991431377`. Native startup ran on both Ubuntu architectures, Windows x64, Windows 11 ARM through x64 emulation, Sonoma ARM and macOS 15 Intel. Intel Sonoma itself and real camera/microphone/screen/permission workflows remain acceptance work.
