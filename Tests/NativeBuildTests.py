import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('native_build', Path(__file__).resolve().parents[1] / 'scripts/build-native.py')
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)


class NativeBuildTests(unittest.TestCase):
    def test_environment_key_is_stable_but_changes_for_distro_libc_compiler_and_checkout(self):
        with patch.object(native.platform, 'system', return_value='Linux'), \
             patch.object(native.platform, 'machine', return_value='x86_64'), \
             patch.object(native.platform, 'mac_ver', return_value=('', (), '')), \
             patch.object(native.platform, 'freedesktop_os_release', return_value={'ID': 'fedora', 'VERSION_ID': '44'}) as distro, \
             patch.object(native.platform, 'libc_ver', return_value=('glibc', '2.43')) as libc, \
             patch.object(native.subprocess, 'check_output', return_value='g++ 16') as compiler:
            original = native.build_environment_key()
            self.assertEqual(original, native.build_environment_key())
            distro.return_value = {'ID': 'ubuntu', 'VERSION_ID': '24.04'}
            self.assertNotEqual(original, native.build_environment_key())
            distro.return_value = {'ID': 'fedora', 'VERSION_ID': '44'}
            libc.return_value = ('glibc', '2.44')
            self.assertNotEqual(original, native.build_environment_key())
            libc.return_value = ('glibc', '2.43')
            compiler.return_value = 'g++ 17'
            self.assertNotEqual(original, native.build_environment_key())
            compiler.return_value = 'g++ 16'
            with patch.object(native, 'ROOT', Path('/other/checkout')):
                self.assertNotEqual(original, native.build_environment_key())

    def test_curl_reuses_only_matching_host_and_ignores_legacy_fedora_prefix(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(native, 'ROOT', Path(directory)), \
             patch.object(native, 'download'), patch.object(native, 'extract'):
            cache = Path(directory) / 'Native/sdk'
            legacy = cache / 'curl-linux-x64/lib/libcurl.so.4'
            legacy.parent.mkdir(parents=True)
            legacy.write_bytes(b'old Fedora binary')
            calls = []

            def cmake(*args, **kwargs):
                calls.append(args)
                if '--install' in args:
                    # Read the install prefix from the preceding configure call.
                    prefix = next(str(x).split('=', 1)[1] for x in calls[-3] if str(x).startswith('-DCMAKE_INSTALL_PREFIX='))
                    library = Path(prefix) / 'lib/libcurl.so.4'
                    library.parent.mkdir(parents=True)
                    library.write_bytes(b'built on current host')

            with patch.object(native, 'run', side_effect=cmake):
                ubuntu = native.linux_curl('linux-x64', 'cmake', 'ubuntu24')
                self.assertEqual((ubuntu / 'lib/libcurl.so.4').read_bytes(), b'built on current host')
                self.assertEqual(legacy.read_bytes(), b'old Fedora binary')
                count = len(calls)
                self.assertEqual(ubuntu, native.linux_curl('linux-x64', 'cmake', 'ubuntu24'))
                self.assertEqual(count, len(calls))
                fedora = native.linux_curl('linux-x64', 'cmake', 'fedora44')
                self.assertNotEqual(ubuntu, fedora)
                self.assertEqual(count + 3, len(calls))


if __name__ == '__main__':
    unittest.main()
