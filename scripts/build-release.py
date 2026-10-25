#!/usr/bin/env python3
"""Start target-native GitHub builds from any OS and download the packages."""
import argparse
import json
from pathlib import Path
import re
import shutil
import subprocess
import time
import uuid

TARGETS = ('all', 'win-x64', 'win-arm64', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64')


def gh(*args, capture=True):
    result = subprocess.run(['gh', *map(str, args)], check=True, text=True,
                            stdout=subprocess.PIPE if capture else None)
    return result.stdout.strip() if capture else None


def build(repo, target, ref, output, run_id=None):
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repo):
        raise ValueError('Use a GitHub owner/repository name.')
    if target not in TARGETS:
        raise ValueError('Unsupported package target.')
    if not shutil.which('gh'):
        raise RuntimeError('Install GitHub CLI (gh), then run gh auth login. Never paste a token into this command.')
    if run_id is None:
        ref = ref or gh('repo', 'view', repo, '--json', 'defaultBranchRef', '--jq', '.defaultBranchRef.name')
        request = str(uuid.uuid4())
        print(f'Building {repo}@{ref} on GitHub. Local/uncommitted files are not uploaded.', flush=True)
        gh('workflow', 'run', 'desktop.yml', '--repo', repo, '--ref', ref,
           '-f', f'target={target}', '-f', f'request_id={request}', capture=False)
        for _ in range(60):
            runs = json.loads(gh('run', 'list', '--repo', repo, '--workflow', 'desktop.yml',
                                '--event', 'workflow_dispatch', '--branch', ref, '--limit', '100',
                                '--json', 'databaseId,displayTitle,url'))
            match = next((run for run in runs if run['displayTitle'] == f'Fire User desktop - {request}'), None)
            if match:
                run_id = match['databaseId']
                print(match['url'], flush=True)
                break
            time.sleep(5)
        else:
            raise RuntimeError(f'Build was dispatched but not found yet. Check GitHub Actions for request {request}; do not dispatch it again blindly.')
    print(f'Watching run {run_id}. Resume with --repo {repo} --run-id {run_id} if interrupted.', flush=True)
    gh('run', 'watch', run_id, '--repo', repo, '--exit-status', capture=False)
    destination = Path(output) / str(run_id)
    destination.mkdir(parents=True, exist_ok=True)
    package = 'win-arm64-emulated' if target == 'win-arm64' else target
    pattern = 'fire-user-*' if target == 'all' else f'fire-user-{package}'
    patterns = ['--pattern', pattern]
    if target != 'all':
        patterns += ['--pattern', f'fire-user-installers-{package}']
    gh('run', 'download', run_id, '--repo', repo, '--dir', destination, *patterns, capture=False)
    # GitHub artifact ZIPs discard Unix executable bits for loose AppImages.
    for image in destination.rglob('*.AppImage'):
        image.chmod(image.stat().st_mode | 0o111)
    print(f'Packages downloaded to {destination.resolve()}', flush=True)
    return destination


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', default='Tetracheia/FireUserDesktop', help='GitHub owner/repository containing this workflow and source')
    parser.add_argument('--target', choices=TARGETS, default='all')
    parser.add_argument('--ref', help='Already-pushed branch or tag; defaults to the GitHub default branch')
    parser.add_argument('--output', type=Path, default=Path(__file__).resolve().parent.parent / 'artifacts/downloads')
    parser.add_argument('--run-id', type=int, help='Resume an existing run without dispatching another build')
    args = parser.parse_args()
    try:
        build(args.repo, args.target, args.ref, args.output, args.run_id)
    except (ValueError, RuntimeError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, f'Release build failed: {error}\n')
