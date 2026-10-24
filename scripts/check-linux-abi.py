#!/usr/bin/env python3
"""Reject Linux release payloads that require a newer ABI than the supported baseline."""
import argparse
from pathlib import Path
import re
import subprocess

LIMITS = {'GLIBC': (2, 35), 'GLIBCXX': (3, 4, 30), 'OPENSSL': (3, 0, 0)}

def required_versions(text):
    needs = False
    versions = []
    for line in text.splitlines():
        if line.startswith('Version '):
            needs = line.startswith('Version needs section')
        if needs:
            versions.extend(re.findall(r'Name: ((?:GLIBC|GLIBCXX|OPENSSL)_[A-Za-z0-9_.]+)', line))
    return versions

def check(source, limits=None):
    limits = limits or LIMITS
    checked = 0
    for path in sorted(Path(source).rglob('*')):
        if not path.is_file() or path.is_symlink():
            continue
        with path.open('rb') as stream:
            if stream.read(4) != b'\x7fELF':
                continue
        checked += 1
        text = subprocess.check_output(['readelf', '--version-info', str(path)], text=True)
        for name in required_versions(text):
            family, version = name.split('_', 1)
            if not re.fullmatch(r'\d+(?:\.\d+)+', version):
                raise ValueError(f'{path.name} requires unsupported ABI {name}; rebuild on the supported Linux baseline.')
            numbers = tuple(map(int, version.split('.')))
            if numbers > limits[family]:
                raise ValueError(f'{path.name} requires {name}, newer than supported {family}_{".".join(map(str,limits[family]))}; rebuild on the supported Linux baseline.')
    if checked == 0:
        raise ValueError('No Linux ELF payloads found to validate.')
    return checked

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--ubuntu24', action='store_true', help='Explicit Ubuntu 24.04-only package, rather than the normal 22.04 baseline')
    args = parser.parse_args()
    limits = {'GLIBC': (2,39), 'GLIBCXX': (3,4,33), 'OPENSSL': (3,0,0)} if args.ubuntu24 else LIMITS
    print(f'Validated {check(args.source,limits)} Linux ELF payloads.')
