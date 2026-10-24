#!/usr/bin/env python3
"""Verified Unix native/media build. No PowerShell or machine installation required."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shlex
import shutil
import subprocess
import sys
import tarfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent


def download(url, path, checksum):
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists():
        partial = path.with_suffix(path.suffix + '.partial')
        try:
            print(f'Downloading {path.name}', flush=True)
            urllib.request.urlretrieve(url, partial)
            partial.replace(path)
        finally:
            partial.unlink(missing_ok=True)
    with path.open('rb') as source:
        actual = hashlib.file_digest(source, 'sha256').hexdigest()
    if actual != checksum.lower():
        raise RuntimeError(f'Checksum mismatch: {path.name}; remove this cache file and retry.')


def extract(archive, destination):
    if zipfile.is_zipfile(archive):
        with zipfile.ZipFile(archive) as source:
            for item in source.infolist():
                if not (destination / item.filename).resolve().is_relative_to(destination.resolve()):
                    raise RuntimeError('Unsafe archive path')
            source.extractall(destination)
    else:
        with tarfile.open(archive) as source:
            source.extractall(destination, filter='data')


def run(*args, env=None, cwd=None):
    subprocess.run([str(arg) for arg in args], check=True, env=env, cwd=cwd)


def build_environment_key():
    # A RID describes CPU/OS, not the distro's libc or C++ ABI. Never reuse
    # Fedora object files or curl when the checkout moves to Ubuntu (or back).
    compiler = shlex.split(os.environ.get('CXX', 'c++'))
    identity = subprocess.check_output([*compiler, '--version'], text=True).strip()
    release = platform.freedesktop_os_release() if platform.system() == 'Linux' else {}
    environment = {
        'root': str(ROOT.resolve()), 'system': platform.system(),
        'machine': platform.machine(), 'release': platform.mac_ver()[0],
        'distro': {key: release.get(key, '') for key in ('ID', 'VERSION_ID')},
        'libc': platform.libc_ver(), 'compiler': identity,
        'flags': {key: os.environ.get(key, '') for key in ('CC', 'CXX', 'CFLAGS', 'CXXFLAGS', 'LDFLAGS')},
    }
    return hashlib.sha256(json.dumps(environment, sort_keys=True).encode()).hexdigest()[:20]


def media(rid):
    definition = json.loads((ROOT / 'scripts/media-runtimes.json').read_text())[rid]
    output = ROOT / 'Native/media' / rid
    stamp = output / 'runtime-source.json'
    if (output / definition['executable']).exists() and stamp.exists():
        if json.loads(stamp.read_text()) == definition:
            return
    if definition.get('build') == 'macos-source-v1':
        macos_media(rid, definition, output)
        stamp.write_text(json.dumps(definition, indent=2))
        return
    cache = ROOT / 'Native/sdk' / f'media-{rid}' / definition['sha256']
    archive = cache / ('runtime.' + definition['archive'])
    download(definition['url'], archive, definition['sha256'])
    extract(archive, cache)
    binary = next(cache.rglob(definition['executable']))
    output.mkdir(parents=True, exist_ok=True)
    shutil.copy2(binary, output)
    (output / binary.name).chmod(0o755)
    for item in cache.rglob('*'):
        if item.is_file() and item.name.upper().startswith(('LICENSE', 'COPYING', 'README', 'NOTICE')):
            shutil.copy2(item, output / item.name)
    (output / 'runtime-source.json').write_text(json.dumps(definition, indent=2))


def macos_media(rid, definition, output):
    # The previous binary supplier rejects automated downloads with HTTP 403.
    # Build the same pinned release with only OS-provided frameworks/libraries.
    if platform.system() != 'Darwin':
        raise RuntimeError('macOS media must be built on the matching Mac host.')
    version = definition['version']
    cache = ROOT / 'Native/sdk/ffmpeg-source'
    archive = cache / f'ffmpeg-{version}.tar.xz'
    download(definition['sourceUrl'], archive, definition['sourceSha256'])
    source = cache / f'ffmpeg-{version}'
    if not (source / 'configure').exists():
        extract(archive, cache)
    build = ROOT / 'Native/build' / f'ffmpeg-{rid}-{version}-{build_environment_key()}'
    build.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, MACOSX_DEPLOYMENT_TARGET='14.0')
    if not (build / 'ffmpeg').exists():
        run(source / 'configure', '--disable-shared', '--enable-static', '--disable-doc',
            '--disable-debug', '--disable-autodetect', '--disable-ffplay', '--disable-ffprobe',
            '--enable-avfoundation', '--enable-videotoolbox', '--enable-audiotoolbox', '--enable-zlib',
            '--extra-cflags=-mmacosx-version-min=14.0', '--extra-ldflags=-mmacosx-version-min=14.0',
            cwd=build, env=env)
        run('make', '-C', build, '-j', min(os.cpu_count() or 2, 8), env=env)
    output.mkdir(parents=True, exist_ok=True)
    shutil.copy2(build / 'ffmpeg', output / 'ffmpeg')
    (output / 'ffmpeg').chmod(0o755)
    for item in source.glob('COPYING*'):
        shutil.copy2(item, output / item.name)
    shutil.copy2(source / 'LICENSE.md', output / 'LICENSE.md')


def linux_curl(rid, cmake, build_key):
    # The official Ubuntu SDK requires CURL_OPENSSL_4; Fedora uses a different
    # system symbol namespace. Bundle our own HTTPS-enabled ABI-compatible curl.
    version = '8.23.0'
    cache = ROOT / 'Native/sdk'
    archive = cache / f'curl-{version}.tar.xz'
    prefix = cache / f'curl-{version}-{rid}-{build_key}'
    library = prefix / 'lib/libcurl.so.4'
    if not library.exists():
        download(f'https://github.com/curl/curl/releases/download/curl-8_23_0/{archive.name}', archive,
                 '8dadd7d2536a314f982ad05a651b713f426bf7d02cc2c76cffb9796f2218e6c5')
        source = cache / f'curl-{version}'
        if not source.exists():
            extract(archive, cache)
        build = cache / f'curl-build-{version}-{rid}-{build_key}'
        run(cmake, '-S', source, '-B', build, '-DCMAKE_BUILD_TYPE=Release',
            f'-DCMAKE_INSTALL_PREFIX={prefix}', '-DCMAKE_INSTALL_LIBDIR=lib',
            '-DBUILD_SHARED_LIBS=ON', '-DBUILD_STATIC_LIBS=OFF', '-DBUILD_CURL_EXE=OFF',
            '-DBUILD_TESTING=OFF', '-DCURL_USE_OPENSSL=ON', '-DCURL_USE_LIBPSL=OFF',
            '-DCURL_ZSTD=OFF', '-DCURL_BROTLI=OFF', '-DUSE_LIBIDN2=OFF',
            '-DCURL_USE_LIBSSH2=OFF', '-DCURL_USE_GSSAPI=OFF', '-DUSE_NGHTTP2=OFF',
            '-DCURL_DISABLE_LDAP=ON', '-DCURL_LIBCURL_VERSIONED_SYMBOLS=ON')
        run(cmake, '--build', build, '--parallel', min(os.cpu_count() or 2, 8))
        run(cmake, '--install', build)
    return prefix


def prepare_macos_install(output):
    # CMake modifies the installed dylib rpath in place. Recopy the original before repeating that fixup.
    for name in ('libfireuser_livekit.dylib', 'libfireuser_notifications.dylib', 'libfireuser_screencapture.dylib'):
        (output / name).unlink(missing_ok=True)


def native(rid, sdk, cmake):
    definition = json.loads((ROOT / 'scripts/native-runtimes.json').read_text())[rid]
    if not sdk:
        name = f"livekit-sdk-{definition['name']}-1.12.2"
        cache = ROOT / 'Native/sdk' / rid
        archive = cache / (name + '.' + definition['archive'])
        download(f'https://github.com/livekit/client-sdk-cpp/releases/download/v1.12.2/{archive.name}',
                 archive, definition['sha256'])
        sdk = cache / name
        if not (sdk / 'include/livekit/room.h').exists():
            extract(archive, cache)
    sdk = Path(sdk).resolve()
    header = ROOT / 'Native/sdk/miniaudio/miniaudio.h'
    download('https://raw.githubusercontent.com/mackron/miniaudio/0.11.23/miniaudio.h', header,
             '7e4f3f13c8fe66df2080ac3dd12a89193e3c2463cb7f067c798abd7331cd8ee6')
    build_key = build_environment_key()
    build = ROOT / 'Native/build' / f'{rid}-{build_key}'
    output = ROOT / 'Native/artifacts' / rid
    curl = linux_curl(rid, cmake, build_key) if rid.startswith('linux-') else None
    options = [f'-DFIREUSER_CURL_LIBRARY={curl / "lib/libcurl.so.4"}'] if curl else []
    if rid.startswith('osx-'):
        options += ['-DCMAKE_OSX_DEPLOYMENT_TARGET=14.0']
    run(cmake, '-S', ROOT / 'Native', '-B', build, f'-DCMAKE_PREFIX_PATH={sdk}', '-DCMAKE_BUILD_TYPE=Release', *options)
    run(cmake, '--build', build, '--parallel', min(os.cpu_count() or 2, 8))
    if rid.startswith('osx-'):
        prepare_macos_install(output)
    run(cmake, '--install', build, '--prefix', output)
    for item in (sdk / 'lib').iterdir():
        if '.so' in item.name or item.suffix == '.dylib':
            shutil.copy2(item, output / item.name)
    if curl:
        for item in (curl / 'lib').glob('libcurl.so*'):
            shutil.copy2(item, output / item.name)
        shutil.copy2(ROOT / f'Native/sdk/curl-8.23.0/COPYING', output / 'CURL-COPYING')
    for item in (ROOT / 'Native/THIRD-PARTY-NOTICES.md' , sdk / 'share/livekit/build-info.json'):
        shutil.copy2(item, output / item.name)
    env = os.environ.copy()
    key = 'DYLD_LIBRARY_PATH' if platform.system() == 'Darwin' else 'LD_LIBRARY_PATH'
    env[key] = str(output) + os.pathsep + env.get(key, '')
    ctest = Path(shutil.which(cmake) or cmake).resolve().parent / 'ctest'
    run(ctest, '--test-dir', build, '--output-on-failure', env=env)
    (output / 'build-host.json').write_text(json.dumps({'rid': rid, 'buildKey': build_key}, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--rid', required=True)
    parser.add_argument('--sdk', type=Path)
    parser.add_argument('--cmake', default='cmake')
    parser.add_argument('--media-only', action='store_true')
    args = parser.parse_args()
    if sys.version_info < (3, 12):
        parser.error("Python 3.12+ is required for safe native archive extraction.")
    machine = {'x86_64': 'x64', 'aarch64': 'arm64', 'arm64': 'arm64'}.get(platform.machine())
    host = ('osx' if platform.system() == 'Darwin' else 'linux') + '-' + str(machine)
    if platform.system() not in ('Linux', 'Darwin') or args.rid != host:
        parser.error(f'Build on the matching Unix host ({host}); Windows uses build-native.ps1.')
    try:
        media(args.rid) if args.media_only else native(args.rid, args.sdk, args.cmake)
    except (RuntimeError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, f'Native build failed: {error}\n')
