import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
spec = importlib.util.spec_from_file_location('linux_abi',Path(__file__).resolve().parents[1]/'scripts/check-linux-abi.py')
abi=importlib.util.module_from_spec(spec)
spec.loader.exec_module(abi)

class LinuxAbiBuildTests(unittest.TestCase):
    def check(self, needed):
        with tempfile.TemporaryDirectory() as directory:
            (Path(directory)/'libfireuser_livekit.so').write_bytes(b'\x7fELFtest')
            text="Version definition section '.gnu.version_d':\n Name: GLIBC_2.99\nVersion needs section '.gnu.version_r':\n"+''.join(' Name: '+x+'\n' for x in needed)
            with patch.object(abi.subprocess,'check_output',return_value=text):
                return abi.check(directory)
    def test_accepts_supported_requirements_and_ignores_definitions(self):
        self.assertEqual(self.check(['GLIBC_2.35','GLIBCXX_3.4.30','OPENSSL_3.0.0','GLIBC_PRIVATE']),1)
    def test_rejects_new_glibc_openssl_cpp_and_tls_abi(self):
        for version in ['GLIBC_2.43','OPENSSL_3.2.0','GLIBCXX_3.4.33','GLIBC_ABI_GNU2_TLS']:
            with self.subTest(version=version),self.assertRaisesRegex(ValueError,version):
                self.check([version])
    def test_missing_elf_payload_cannot_pass_release_validation(self):
        with tempfile.TemporaryDirectory() as directory,self.assertRaisesRegex(ValueError,'No Linux ELF'):
            abi.check(directory)
if __name__=='__main__':unittest.main()
