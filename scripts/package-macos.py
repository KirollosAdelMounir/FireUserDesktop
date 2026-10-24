#!/usr/bin/env python3
"""Assemble an unsigned native .app, including its Finder/Dock icon."""
import argparse
from pathlib import Path
import plistlib
import shutil
import struct

ROOT = Path(__file__).resolve().parents[1]

def bundle(source):
    source = Path(source).resolve()
    if not (source / 'Fire User Connect').is_file():
        raise ValueError('The published macOS executable is missing.')
    icon = ROOT / 'Assets/FireUserConnect.icns'
    data = icon.read_bytes()
    if len(data) < 16 or data[:4] != b'icns' or struct.unpack('>I', data[4:8])[0] != len(data):
        raise ValueError('The application icon is not a valid ICNS resource.')
    metadata = plistlib.loads((ROOT / 'packaging/macos/Info.plist').read_bytes())
    if metadata.get('CFBundleIconFile') != icon.name:
        raise ValueError('The bundle icon declaration does not match its resource.')
    application = source / 'Fire User Connect.app'
    contents = application / 'Contents'
    executable = contents / 'MacOS'
    resources = contents / 'Resources'
    executable.mkdir(parents=True, exist_ok=True)
    resources.mkdir(parents=True, exist_ok=True)
    for item in source.iterdir():
        if item == application:
            continue
        destination = executable / item.name
        if item.is_dir():
            shutil.copytree(item, destination, symlinks=True, dirs_exist_ok=True)
        else:
            shutil.copy2(item, destination, follow_symlinks=False)
    (executable / 'Fire User Connect').chmod(0o755)
    shutil.copy2(icon, resources / icon.name)
    (contents / 'Info.plist').write_bytes(plistlib.dumps(metadata, sort_keys=False))
    return application

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    print(bundle(parser.parse_args().source))
