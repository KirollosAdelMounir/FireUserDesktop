#!/usr/bin/env python3
"""Create native installers from a complete self-contained publish directory."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import tempfile
import time
import urllib.request

ROOT = Path(__file__).resolve().parent.parent
TARGETS = ('win-x64', 'win-arm64-emulated', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64')
DEB_DEPS = ('libc6 (>= 2.35)', 'libicu70 | libicu72 | libicu74 | libicu76 | libicu78', 'libstdc++6', 'libgcc-s1', 'libx11-6', 'libxtst6', 'libxi6',
            'libxext6', 'libice6', 'libsm6', 'libfontconfig1', 'fonts-dejavu-core', 'libssl3 | libssl3t64', 'zlib1g',
            'libasound2 | libasound2t64', 'libpulse0', 'libgl1', 'libnotify-bin',
            'libgstreamer1.0-0', 'libgstreamer-plugins-base1.0-0')
RPM_DEPS = ('glibc >= 2.35', 'libicu', 'libstdc++', 'libgcc', 'libX11', 'libXtst', 'libXi', 'libXext',
            'libICE', 'libSM', 'fontconfig', 'dejavu-sans-fonts', 'openssl-libs', 'zlib', 'alsa-lib', 'pulseaudio-libs',
            'libglvnd-glx', 'libnotify', 'gstreamer1', 'gstreamer1-plugins-base')
ARCH_DEPS = ('glibc>=2.35', 'icu', 'gcc-libs', 'libx11', 'libxtst', 'libxi', 'libxext', 'libice', 'libsm',
             'fontconfig', 'ttf-dejavu', 'openssl', 'zlib', 'alsa-lib', 'libpulse', 'libglvnd', 'libnotify',
             'gstreamer', 'gst-plugins-base-libs')


def run(*args, **kwargs):
    subprocess.run(list(map(str, args)), check=True, **kwargs)


def stage_linux(source, stage):
    app = stage / 'opt/fire-user-connect'
    shutil.copytree(source, app)
    (app / 'Fire User Connect').chmod(0o755)
    media = app / 'media/ffmpeg'
    if media.exists():
        media.chmod(0o755)
    applications = stage / 'usr/share/applications'
    applications.mkdir(parents=True)
    (applications / 'fire-user-connect.desktop').write_text(
        '[Desktop Entry]\nType=Application\nName=Fire User Connect\n'
        'Comment=Chat, meetings and workplace collaboration\n'
        'Exec="/opt/fire-user-connect/Fire User Connect" --open-chat %u\n'
        'TryExec=/opt/fire-user-connect/Fire User Connect\nIcon=fire-user-connect\n'
        'Terminal=false\nCategories=Network;Chat;\nMimeType=x-scheme-handler/fireuserconnect;\n')
    icons = stage / 'usr/share/icons/hicolor/256x256/apps'
    icons.mkdir(parents=True)
    shutil.copy2(ROOT / 'Assets/PageLogo.png', icons / 'fire-user-connect.png')


def verified_download(url, checksum, destination):
    if not destination.exists():
        partial = destination.with_suffix('.partial')
        try:
            urllib.request.urlretrieve(url, partial)
            partial.replace(destination)
        finally:
            partial.unlink(missing_ok=True)
    with destination.open('rb') as stream:
        actual = hashlib.file_digest(stream, 'sha256').hexdigest()
    if actual != checksum:
        raise RuntimeError(f'Checksum mismatch for {destination.name}; review the pinned tool release.')


def appimage(source, output, version, arch, work):
    appdir = work / 'FireUserConnect.AppDir'
    (appdir / 'usr').mkdir(parents=True)
    shutil.copytree(source, appdir / 'usr/app')
    (appdir / 'usr/app/Fire User Connect').chmod(0o755)
    launcher = appdir / 'AppRun'
    launcher.write_text('#!/bin/sh\nset -eu\nHERE="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"\nexec "$HERE/usr/app/Fire User Connect" "$@"\n')
    launcher.chmod(0o755)
    (appdir / 'fire-user-connect.desktop').write_text(
        '[Desktop Entry]\nType=Application\nName=Fire User Connect\nExec=AppRun %u\n'
        'Icon=fire-user-connect\nTerminal=false\nCategories=Network;Chat;\n'
        'MimeType=x-scheme-handler/fireuserconnect;\n')
    shutil.copy2(ROOT / 'Assets/PageLogo.png', appdir / 'fire-user-connect.png')
    definitions = json.loads((ROOT / 'packaging/appimage-tools.json').read_text())[arch]
    cache = ROOT / 'Native/sdk/appimage-tools' / arch
    cache.mkdir(parents=True, exist_ok=True)
    tool, runtime = cache / 'appimagetool.AppImage', cache / 'runtime'
    verified_download(definitions['toolUrl'], definitions['toolSha256'], tool)
    verified_download(definitions['runtimeUrl'], definitions['runtimeSha256'], runtime)
    tool.chmod(0o755)
    env = dict(os.environ, ARCH=arch, APPIMAGE_EXTRACT_AND_RUN='1')
    run(tool, '--runtime-file', runtime, appdir, output / f'FireUserConnect-{version}-{arch}.AppImage', env=env)


def linux(source, output, version, rid):
    deb_arch, arch = ('amd64', 'x86_64') if rid == 'linux-x64' else ('arm64', 'aarch64')
    with tempfile.TemporaryDirectory(prefix='fireuser-install-') as directory:
        work = Path(directory)
        stage = work / 'payload'
        stage_linux(source, stage)
        control = stage / 'DEBIAN'
        control.mkdir()
        (control / 'control').write_text(
            f'Package: fire-user-connect\nVersion: {version}\nArchitecture: {deb_arch}\n'
            'Maintainer: Fire User\nSection: net\nPriority: optional\n'
            f'Depends: {", ".join(DEB_DEPS)}\n'
            'Recommends: gstreamer1.0-pipewire, gstreamer1.0-plugins-good, fonts-noto-color-emoji\n'
            'Description: Fire User native chat, meetings and workplace client\n')
        for name in ('postinst', 'postrm'):
            script = control / name
            script.write_text('#!/bin/sh\nset -e\nif command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database -q /usr/share/applications || true; fi\nif command -v gtk-update-icon-cache >/dev/null 2>&1; then gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true; fi\n')
            script.chmod(0o755)
        run('dpkg-deb', '--root-owner-group', '--build', stage, output / f'fire-user-connect_{version}_{deb_arch}.deb')
        shutil.rmtree(control)
        rpmroot = work / 'rpm'
        for name in ('BUILD', 'BUILDROOT', 'RPMS', 'SOURCES', 'SPECS', 'SRPMS'):
            (rpmroot / name).mkdir(parents=True)
        spec = rpmroot / 'SPECS/fire-user-connect.spec'
        spec.write_text(f'''Name: fire-user-connect
Version: {version}
Release: 1
Summary: Fire User native collaboration client
License: LicenseRef-Proprietary
BuildArch: {arch}
Requires: {', '.join(RPM_DEPS)}
Recommends: google-noto-emoji-color-fonts
AutoReqProv: no
%global debug_package %{{nil}}
%global __os_install_post %{{nil}}
%description
Native chat, meetings and workplace collaboration.
%install
mkdir -p "%{{buildroot}}"
cp -a "{stage}/." "%{{buildroot}}/"
%files
%defattr(-,root,root,-)
/opt/fire-user-connect
/usr/share/applications/fire-user-connect.desktop
/usr/share/icons/hicolor/256x256/apps/fire-user-connect.png
%post
update-desktop-database -q /usr/share/applications >/dev/null 2>&1 || :
%postun
update-desktop-database -q /usr/share/applications >/dev/null 2>&1 || :
''')
        run('rpmbuild', '--define', f'_topdir {rpmroot}', '--target', arch, '-bb', spec)
        for package in (rpmroot / 'RPMS').rglob('*.rpm'):
            shutil.copy2(package, output)
        size = sum(p.stat().st_size for p in stage.rglob('*') if p.is_file())
        metadata = [f'pkgname = fire-user-connect', f'pkgver = {version}-1',
                    'pkgdesc = Fire User native chat, meetings and workplace client',
                    'url = https://github.com/KirollosAdelMounir/FireUserDesktop',
                    f'builddate = {int(time.time())}', 'packager = Fire User',
                    f'size = {size}', f'arch = {arch}', 'license = LicenseRef-Proprietary']
        metadata += [f'depend = {dependency}' for dependency in ARCH_DEPS]
        metadata += ['optdepend = gst-plugin-pipewire: Wayland screen sharing',
                     'optdepend = gst-plugins-good: Additional capture backends',
                     'optdepend = noto-fonts-emoji: Color emoji in chat']
        (stage / '.PKGINFO').write_text('\n'.join(metadata) + '\n')
        # pacman expects .PKGINFO at the archive root, without a './' prefix.
        run('tar', '--zstd', '--owner=0', '--group=0', '--numeric-owner', '-cf',
            output / f'fire-user-connect-{version}-1-{arch}.pkg.tar.zst', '-C', stage,
            *sorted(p.name for p in stage.iterdir()))
        appimage(source, output, version, arch, work)


def build(rid, source, output, version):
    if rid not in TARGETS or not re.fullmatch(r'\d+\.\d+\.\d+(?:\.\d+)?', version):
        raise ValueError('Use a supported RID and a numeric release version, for example 1.0.0.')
    source, output = Path(source).resolve(), Path(output).resolve()
    if output == source or output.is_relative_to(source):
        raise ValueError('Installer output must be outside the published application directory.')
    executable = source / ('Fire User Connect.exe' if rid.startswith('win-') else 'Fire User Connect')
    if rid.startswith('osx-'):
        executable = source / 'Fire User Connect.app/Contents/MacOS/Fire User Connect'
    if not executable.is_file():
        raise ValueError(f'Complete published application missing: {executable}')
    output.mkdir(parents=True, exist_ok=True)
    if rid.startswith('linux-'):
        linux(source, output, version, rid)
    elif rid.startswith('win-'):
        compiler = shutil.which('ISCC') or shutil.which('iscc')
        if not compiler:
            compiler = str(Path(os.environ.get('ProgramFiles(x86)', r'C:\Program Files (x86)')) / 'Inno Setup 6/ISCC.exe')
        arm = rid == 'win-arm64-emulated'
        run(compiler, f'/DAppVersion={version}', f'/DPublishDirectory={source}', f'/DInstallerOutput={output}',
            f'/DPackageTarget={rid}', f'/DAppIcon={ROOT / "Assets/favicon.ico"}',
            '/DAllowedArchitectures=' + ('arm64 and x64compatible' if arm else 'x64os'),
            '/DMinimumWindows=' + ('10.0.22000' if arm else '10.0.19045'), ROOT / 'packaging/windows/setup.iss')
    else:
        with tempfile.TemporaryDirectory(prefix='fireuser-pkg-') as directory:
            requirements = Path(directory) / 'requirements.plist'
            requirements.write_bytes(plistlib.dumps({'os': ['14.0'], 'arch': ['x86_64' if rid == 'osx-x64' else 'arm64']}))
            run('productbuild', '--product', requirements, '--component', source / 'Fire User Connect.app',
                '/Applications', output / f'FireUserConnect-{version}-{rid}.pkg')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--rid', choices=TARGETS, required=True)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('installers'))
    parser.add_argument('--version', default='1.0.0')
    args = parser.parse_args()
    try:
        build(args.rid, args.source, args.output, args.version)
    except (ValueError, RuntimeError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, f'Installer build failed: {error}\n')
