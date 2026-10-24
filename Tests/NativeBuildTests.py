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
    def test_media_rebuilds_legacy_supplier_output_then_reuses_matching_source_stamp(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(native, 'ROOT', Path(directory)):
            root = Path(directory)
            definition = {'build': 'macos-source-v1', 'executable': 'ffmpeg', 'version': '9.0.2'}
            (root / 'scripts').mkdir()
            (root / 'scripts/media-runtimes.json').write_text(json.dumps({'osx-arm64': definition}))
            output = root / 'Native/media/osx-arm64'
            output.mkdir(parents=True)
            (output / 'ffmpeg').write_bytes(b'legacy supplier binary')
            (output / 'runtime-source.json').write_text(json.dumps({'url': 'old vendor'}))
            with patch.object(native, 'macos_media') as build:
                native.media('osx-arm64')
                build.assert_called_once_with('osx-arm64', definition, output)
                self.assertEqual(json.loads((output / 'runtime-source.json').read_text()), definition)
                native.media('osx-arm64')
                self.assertEqual(build.call_count, 1)

    def test_mac_source_build_pins_sonoma_target_and_packages_binary_and_license(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(native, 'ROOT', Path(directory)), \
             patch.object(native.platform, 'system', return_value='Darwin'), \
             patch.object(native, 'build_environment_key', return_value='host'), \
             patch.object(native, 'download') as download:
            root = Path(directory)
            source = root / 'Native/sdk/ffmpeg-source/ffmpeg-9.0.2'
            source.mkdir(parents=True)
            for name in ('configure', 'LICENSE.md', 'COPYING.LGPLv2.1'):
                (source / name).write_text(name)
            calls = []

            def command(*args, **kwargs):
                calls.append((args, kwargs))
                if args[0] == 'make':
                    (Path(args[2]) / 'ffmpeg').write_bytes(b'compiled native media')

            definition = {'version': '9.0.2', 'sourceUrl': 'https://ffmpeg.org/release', 'sourceSha256': 'verified-pin'}
            output = root / 'Native/media/osx-arm64'
            with patch.object(native, 'run', side_effect=command):
                native.macos_media('osx-arm64', definition, output)
            self.assertEqual((output / 'ffmpeg').read_bytes(), b'compiled native media')
            self.assertEqual((output / 'LICENSE.md').read_text(), 'LICENSE.md')
            download.assert_called_once()
            self.assertEqual(download.call_args.args[2], 'verified-pin')
            configure, options = calls[0]
            self.assertIn('--extra-cflags=-mmacosx-version-min=14.0', configure)
            self.assertIn('--extra-ldflags=-mmacosx-version-min=14.0', configure)
            self.assertIn('--enable-avfoundation', configure)
            self.assertEqual(options['env']['MACOSX_DEPLOYMENT_TARGET'], '14.0')
            with patch.object(native, 'run') as command:
                native.macos_media('osx-arm64', definition, output)
                command.assert_not_called()

    def test_mac_incremental_install_replaces_only_owned_dylibs_before_rpath_fixup(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            owned = ['libfireuser_livekit.dylib', 'libfireuser_notifications.dylib', 'libfireuser_screencapture.dylib']
            for name in owned + ['liblivekit.dylib', 'build-info.json']:
                (output / name).write_bytes(b'keep sdk data')
            native.prepare_macos_install(output)
            self.assertTrue(all(not (output / name).exists() for name in owned))
            self.assertEqual((output / 'liblivekit.dylib').read_bytes(), b'keep sdk data')
            self.assertEqual((output / 'build-info.json').read_bytes(), b'keep sdk data')
            native.prepare_macos_install(output)

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
