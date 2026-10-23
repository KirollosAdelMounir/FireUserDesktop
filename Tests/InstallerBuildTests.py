import importlib.util
import os
from pathlib import Path
import plistlib
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('installer_build', Path(__file__).resolve().parents[1] / 'scripts/build-installers.py')
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class InstallerBuildTests(unittest.TestCase):
    def test_linux_payload_preserves_native_files_and_quotes_branded_executable(self):
        with tempfile.TemporaryDirectory() as directory:
            source, stage = Path(directory) / 'publish', Path(directory) / 'stage'
            source.mkdir()
            (source / 'Fire User Connect').write_bytes(b'published executable')
            (source / 'libfireuser_livekit.so').write_bytes(b'native bridge')
            (source / 'media').mkdir()
            (source / 'media/ffmpeg').write_bytes(b'ffmpeg')
            installer.stage_linux(source, stage)
            app = stage / 'opt/fire-user-connect'
            self.assertEqual((app / 'libfireuser_livekit.so').read_bytes(), b'native bridge')
            if os.name != 'nt':
                self.assertEqual((app / 'Fire User Connect').stat().st_mode & 0o777, 0o755)
                self.assertEqual((app / 'media/ffmpeg').stat().st_mode & 0o777, 0o755)
            desktop = (stage / 'usr/share/applications/fire-user-connect.desktop').read_text()
            self.assertIn('Exec="/opt/fire-user-connect/Fire User Connect" --open-chat %u', desktop)
            self.assertIn('MimeType=x-scheme-handler/fireuserconnect;', desktop)
            self.assertGreater((stage / 'usr/share/icons/hicolor/256x256/apps/fire-user-connect.png').stat().st_size, 0)

    def test_macos_installer_limits_architecture_and_sonoma_minimum(self):
        for rid, arch in [('osx-x64', 'x86_64'), ('osx-arm64', 'arm64')]:
            with self.subTest(rid=rid), tempfile.TemporaryDirectory() as directory:
                source, output = Path(directory) / 'publish', Path(directory) / 'installers'
                executable = source / 'Fire User Connect.app/Contents/MacOS/Fire User Connect'
                executable.parent.mkdir(parents=True)
                executable.touch()
                captured = {}

                def productbuild(*args):
                    captured['requirements'] = plistlib.loads(Path(args[2]).read_bytes())
                    captured['command'] = args

                with patch.object(installer, 'run', side_effect=productbuild):
                    installer.build(rid, source, output, '2.1.0')
                self.assertEqual(captured['requirements'], {'os': ['14.0'], 'arch': [arch]})
                self.assertEqual(captured['command'][-2], '/Applications')
                self.assertEqual(captured['command'][-1].suffix, '.pkg')

    def test_windows_installers_gate_real_x64_and_windows11_arm_emulation(self):
        for rid, architectures, minimum in [('win-x64', 'x64os', '10.0.19045'),
                                             ('win-arm64-emulated', 'arm64 and x64compatible', '10.0.22000')]:
            with self.subTest(rid=rid), tempfile.TemporaryDirectory() as directory:
                source = Path(directory) / 'publish'
                source.mkdir()
                (source / 'Fire User Connect.exe').touch()
                with patch.object(installer.shutil, 'which', return_value='ISCC'), patch.object(installer, 'run') as run:
                    installer.build(rid, source, Path(directory) / 'installers', '1.0.0')
                self.assertIn('/DAllowedArchitectures=' + architectures, run.call_args.args)
                self.assertIn('/DMinimumWindows=' + minimum, run.call_args.args)
                self.assertIn('/DPublishDirectory=' + str(source), run.call_args.args)

    def test_linux_declares_each_package_managers_dependencies_and_architecture(self):
        for rid, deb_arch, arch in [('linux-x64', 'amd64', 'x86_64'), ('linux-arm64', 'arm64', 'aarch64')]:
            with self.subTest(rid=rid), tempfile.TemporaryDirectory() as directory:
                source, output = Path(directory) / 'publish', Path(directory) / 'out'
                source.mkdir()
                (source / 'Fire User Connect').touch()
                metadata = {}

                def command(*args):
                    if args[0] == 'dpkg-deb':
                        metadata['deb'] = (args[-2] / 'DEBIAN/control').read_text()
                    elif args[0] == 'rpmbuild':
                        metadata['rpm'] = args[-1].read_text()
                    elif args[0] == 'tar':
                        metadata['arch'] = (args[args.index('-C') + 1] / '.PKGINFO').read_text()
                        self.assertIn('.PKGINFO', args)
                        self.assertNotIn('.', args)

                with patch.object(installer, 'run', side_effect=command), patch.object(installer, 'appimage') as image:
                    installer.build(rid, source, output, '1.2.3')
                self.assertIn('Architecture: ' + deb_arch, metadata['deb'])
                self.assertIn('libssl3 | libssl3t64', metadata['deb'])
                self.assertIn('BuildArch: ' + arch, metadata['rpm'])
                self.assertIn('openssl-libs', metadata['rpm'])
                self.assertIn('arch = ' + arch, metadata['arch'])
                self.assertIn('depend = glibc>=2.35', metadata['arch'])
                self.assertIn('depend = gst-plugins-base-libs', metadata['arch'])
                self.assertEqual(image.call_args.args[3], arch)

    def test_appimage_tools_refuse_a_wrong_checksum(self):
        with tempfile.TemporaryDirectory() as directory:
            binary = Path(directory) / 'tool'
            binary.write_bytes(b'wrong download')
            with self.assertRaisesRegex(RuntimeError, 'Checksum mismatch'):
                installer.verified_download('https://example.invalid', '0' * 64, binary)

    def test_invalid_input_or_nested_output_never_runs_packagers(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(installer, 'run') as run:
            root = Path(directory)
            for rid, source, output, version in [('linux-x64', root, root / 'out', '1.0.0'),
                                                ('linux-arm32', root, root.parent / 'out', '1.0.0'),
                                                ('win-x64', root, root.parent / 'out', '1.0.0; injected'),
                                                ('osx-x64', root / 'missing', root / 'out', '1.0.0')]:
                with self.assertRaises(ValueError):
                    installer.build(rid, source, output, version)
            run.assert_not_called()


if __name__ == '__main__':
    unittest.main()
