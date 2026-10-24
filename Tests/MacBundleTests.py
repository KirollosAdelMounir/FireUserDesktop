import importlib.util
from pathlib import Path
import plistlib
import struct
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('mac_bundle', Path(__file__).resolve().parents[1] / 'scripts/package-macos.py')
mac = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mac)

class MacBundleTests(unittest.TestCase):
    def test_bundle_includes_declared_retina_icon_native_payload_and_executable_permissions(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory)
            (source / 'Fire User Connect').write_bytes(b'native app')
            (source / 'libfireuser_livekit.dylib').write_bytes(b'native bridge')
            (source / 'media').mkdir()
            (source / 'media/ffmpeg').write_bytes(b'ffmpeg')
            app = mac.bundle(source)
            info = plistlib.loads((app / 'Contents/Info.plist').read_bytes())
            icon = (app / 'Contents/Resources' / info['CFBundleIconFile']).read_bytes()
            self.assertEqual(icon[:4], b'icns')
            self.assertEqual(struct.unpack('>I', icon[4:8])[0], len(icon))
            entries = []
            offset = 8
            while offset < len(icon):
                kind, length = struct.unpack('>4sI', icon[offset:offset+8])
                self.assertGreaterEqual(length, 8)
                entries.append(kind)
                if kind == b'ic10':
                    png = icon[offset+8:offset+length]
                    self.assertEqual(png[:8], b'\x89PNG\r\n\x1a\n')
                    self.assertEqual(struct.unpack('>II', png[16:24]), (1024, 1024))
                offset += length
            self.assertEqual(offset, len(icon))
            self.assertIn(b'ic10', entries)
            self.assertEqual((app / 'Contents/MacOS/libfireuser_livekit.dylib').read_bytes(), b'native bridge')
            self.assertEqual((app / 'Contents/MacOS/media/ffmpeg').read_bytes(), b'ffmpeg')
            self.assertEqual((app / 'Contents/MacOS/Fire User Connect').stat().st_mode & 0o777, 0o755)
            self.assertEqual(mac.bundle(source), app)
            self.assertFalse((app / 'Contents/MacOS/Fire User Connect.app').exists())

    def test_missing_executable_does_not_create_an_empty_bundle(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, 'executable'):
                mac.bundle(directory)
            self.assertFalse((Path(directory) / 'Fire User Connect.app').exists())

if __name__ == '__main__':
    unittest.main()
