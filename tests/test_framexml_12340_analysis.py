"""No binary fixtures are stored. Set PORTALKEEPER_CP5_EXE for exact-source tests.
Set PORTALKEEPER_CP5_STOCK_CLIENT for optional read-only stock archive checks.
"""
import hashlib
import importlib.util
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "analyze-framexml-12340.py"
SPEC = importlib.util.spec_from_file_location("cp5_analysis", SCRIPT)
analysis = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(analysis)


class GuardTests(unittest.TestCase):
    def test_size_mismatch_rejected(self):
        with self.assertRaisesRegex(ValueError, "size mismatch"):
            analysis.analyze(b"MZ")

    def test_same_size_wrong_hash_rejected_before_pe_parsing(self):
        with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
            analysis.analyze(b"x" * analysis.SOURCE_SIZE)

    def test_wrong_source_cli_fails_closed_without_writes(self):
        with tempfile.TemporaryDirectory(prefix="pk-cp5-test-") as directory:
            source = Path(directory) / "source.exe"
            source.write_bytes(b"synthetic negative fixture")
            before = source.read_bytes()
            result = subprocess.run([sys.executable, str(SCRIPT), str(source)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 2)
            self.assertEqual(result.stdout, "")
            self.assertIn("STOP:", result.stderr)
            self.assertEqual(source.read_bytes(), before)
            self.assertEqual(list(Path(directory).iterdir()), [source])

    def test_ambiguous_or_unbacked_mapping_rejected(self):
        section = dict(name=".text", rva=0x1000, virtual_size=100, raw_size=20, raw_offset=0x400)
        with self.assertRaisesRegex(ValueError, "unique file-backed"):
            analysis.location([section, section], 0x401000)
        with self.assertRaisesRegex(ValueError, "unique file-backed"):
            analysis.location([section], 0x401020)

    def test_signature_rejects_missing_wrong_size_or_magic(self):
        for signature in [b"", bytes(275), bytes(276)]:
            self.assertFalse(analysis.verify_signature(signature, "FRAMEXML.TOC.SIG", bytes(256)))


@unittest.skipUnless(os.environ.get("PORTALKEEPER_CP5_EXE"), "Supply your own verified executable")
class SuppliedExecutableTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = Path(os.environ["PORTALKEEPER_CP5_EXE"])
        cls.before = cls.path.stat()
        cls.data = cls.path.read_bytes()
        cls.report = analysis.analyze(cls.data)

    def test_exact_identity_anchors_and_call_sites(self):
        self.assertEqual(self.report["source_sha256"], analysis.SOURCE_SHA256)
        self.assertEqual(len(self.report["anchors"]), 7)
        self.assertEqual(self.report["shared_verifier_direct_call_candidates"], ["0x4da7dd", "0x52abd1", "0x5f7b41"])

    def test_candidate_changes_only_frame_status_two(self):
        # A byte-array preview, not a transformed executable file.
        data = bytearray(self.data)
        off = analysis.CANDIDATE_OFFSET
        data[off:off + 4] = analysis.REPLACEMENT
        self.assertEqual(hashlib.sha256(data).hexdigest(), analysis.CANDIDATE_SHA256)
        self.assertEqual([i for i, (a, b) in enumerate(zip(self.data, data)) if a != b], [off, off + 1])
        layout = analysis.sections(self.data)
        table_off, _ = analysis.location(layout, analysis.TABLE_VA, 16)
        old = struct.unpack_from("<4I", self.data, table_off)
        new = struct.unpack_from("<4I", data, table_off)
        self.assertEqual(new, (old[0], old[1], old[3], old[3]))
        for name in ("frame_postload_guard", "glue_status_table", "frame_call_shared_verifier"):
            va, expected = analysis.ANCHORS[name]
            size = len(bytes.fromhex(expected))
            start, _ = analysis.location(layout, va, size)
            self.assertEqual(self.data[start:start + size], data[start:start + size])

    def test_already_modified_input_rejected(self):
        data = bytearray(self.data)
        data[analysis.CANDIDATE_OFFSET:analysis.CANDIDATE_OFFSET + 4] = analysis.REPLACEMENT
        with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
            analysis.analyze(bytes(data))

    def test_source_bytes_and_identity_unchanged(self):
        now = self.path.stat()
        self.assertEqual((now.st_dev, now.st_ino, now.st_mtime_ns),
                         (self.before.st_dev, self.before.st_ino, self.before.st_mtime_ns))
        self.assertEqual(hashlib.sha256(self.path.read_bytes()).hexdigest(), analysis.SOURCE_SHA256)

    @unittest.skipUnless(os.environ.get("PORTALKEEPER_CP5_STOCK_CLIENT"), "Supply stock MPQs and installed StormLib")
    def test_offline_signature_digest_and_error_chain(self):
        evidence = analysis.verify_stock(self.data, Path(os.environ["PORTALKEEPER_CP5_STOCK_CLIENT"]), "enUS")
        for part in ("FrameXML", "GlueXML"):
            self.assertTrue(evidence[part]["rsa_sha1_verified"])
            self.assertTrue(evidence[part]["signature_bit_flip_rejected"])
            self.assertTrue(evidence[part]["signed_digest_bit_flip_rejected"])
        self.assertEqual(evidence["FrameXML"]["signed_and_reconstructed_md5"], "51b264400410d3409c4e79f6f4257711")
        self.assertEqual(evidence["GlueXML"]["signed_and_reconstructed_md5"], "b44247235136edcfbbc314797eb2c705")
        self.assertEqual(evidence["FrameXML"]["friends_frame_lua_visits"], 1)
        self.assertEqual(evidence["in_memory_comment_experiment"]["md5"], "63d5ecdf57fcff752f957243344bb041")
        self.assertEqual(evidence["startup_errors"]["10"]["key"], "MSG_FRAMEXML_UI_CORRUPT")


if __name__ == "__main__":
    unittest.main()
