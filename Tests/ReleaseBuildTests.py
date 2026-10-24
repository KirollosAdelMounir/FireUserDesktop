import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('release_build', Path(__file__).resolve().parents[1] / 'scripts/build-release.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseBuildTests(unittest.TestCase):
    def test_dispatch_correlates_exact_run_and_downloads_requested_target(self):
        calls = []

        def gh(*args, **kwargs):
            calls.append(args)
            if args[:2] == ('repo', 'view'):
                return 'release'
            if args[:2] == ('run', 'list'):
                return json.dumps([
                    {'databaseId': 10, 'displayTitle': 'Fire User desktop - somebody-else', 'url': 'other'},
                    {'databaseId': 11, 'displayTitle': 'Fire User desktop - request', 'url': 'correct'},
                ])

        with tempfile.TemporaryDirectory() as directory, patch.object(release.shutil, 'which', return_value='/usr/bin/gh'), \
             patch.object(release.uuid, 'uuid4', return_value='request'), patch.object(release, 'gh', side_effect=gh):
            destination = release.build('owner/project', 'osx-arm64', None, directory)
            self.assertEqual(destination, Path(directory) / '11')
            self.assertTrue(destination.is_dir())
        dispatch = next(call for call in calls if call[:2] == ('workflow', 'run'))
        self.assertIn('target=osx-arm64', dispatch)
        self.assertIn('request_id=request', dispatch)
        self.assertIn('release', dispatch)
        self.assertIn(('run', 'watch', 11, '--repo', 'owner/project', '--exit-status'), calls)
        download = calls[-1]
        self.assertEqual(download[:3], ('run', 'download', 11))
        self.assertIn('fire-user-osx-arm64', download)
        self.assertEqual(download[-1], 'fire-user-installers-osx-arm64')

    def test_resume_does_not_dispatch_and_downloads_all_packages(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(release.shutil, 'which', return_value='gh'), \
             patch.object(release, 'gh') as gh:
            release.build('owner/project', 'all', None, directory, 42)
            self.assertEqual(gh.call_count, 2)
            self.assertEqual(gh.call_args_list[0].args[:3], ('run', 'watch', 42))
            self.assertEqual(gh.call_args_list[1].args[-1], 'fire-user-*')

    def test_failed_run_is_not_downloaded(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(release.shutil, 'which', return_value='gh'), \
             patch.object(release, 'gh', side_effect=subprocess.CalledProcessError(1, 'gh')) as gh:
            with self.assertRaises(subprocess.CalledProcessError):
                release.build('owner/project', 'all', None, directory, 42)
            self.assertEqual(gh.call_count, 1)
            self.assertFalse((Path(directory) / '42').exists())

    def test_windows_arm_download_is_explicitly_labelled_emulated(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(release.shutil, 'which', return_value='gh'), \
             patch.object(release, 'gh') as gh:
            release.build('owner/project', 'win-arm64', None, directory, 42)
            self.assertIn('fire-user-win-arm64-emulated', gh.call_args.args)
            self.assertEqual(gh.call_args.args[-1], 'fire-user-installers-win-arm64-emulated')

    def test_timeout_reports_request_and_never_dispatches_twice(self):
        def gh(*args, **kwargs):
            return '[]' if args[:2] == ('run', 'list') else None
        with tempfile.TemporaryDirectory() as directory, patch.object(release.shutil, 'which', return_value='gh'), \
             patch.object(release.uuid, 'uuid4', return_value='unique'), patch.object(release.time, 'sleep'), \
             patch.object(release, 'gh', side_effect=gh) as command:
            with self.assertRaisesRegex(RuntimeError, 'unique'):
                release.build('owner/project', 'all', 'main', directory)
            self.assertEqual(sum(c.args[:2] == ('workflow', 'run') for c in command.call_args_list), 1)

    def test_invalid_repo_or_target_and_missing_cli_do_not_dispatch(self):
        with patch.object(release, 'gh') as gh:
            for repo, target in [('https://github.com/owner/repo', 'all'), ('owner/repo', 'win-arm32')]:
                with self.assertRaises(ValueError):
                    release.build(repo, target, None, '/unused')
            with patch.object(release.shutil, 'which', return_value=None):
                with self.assertRaisesRegex(RuntimeError, 'gh auth login'):
                    release.build('owner/repo', 'all', None, '/unused')
            gh.assert_not_called()


if __name__ == '__main__':
    unittest.main()
