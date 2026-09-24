#!/usr/bin/env python3
"""Read-only, exact-build CP5 investigation. Never writes an executable or asset.

Core PE analysis uses only Python's standard library. Optional stock archive
checks require installed StormLib and read ONLY the explicit stock MPQ list.
Candidate bytes/hashes are a preview, NOT proof of successful game startup.
"""
import argparse
import ctypes as ct
import ctypes.util
import hashlib
import json
from pathlib import Path
import posixpath
import re
import struct
import sys

SOURCE_SIZE = 7_704_216
SOURCE_SHA256 = "aa63a5750d60ef16746c686b3d5e26876d98953eab08b1c026cd0faf78e88cb8"
CANDIDATE_SHA256 = "15c47945d5c461fda32a34645a23a9f3488bbb2b10855fe328c0e866d9ca8001"
IMAGE_BASE = 0x400000
TABLE_VA = 0x52AEB4
CANDIDATE_VA = 0x52AEBC
CANDIDATE_OFFSET = 0x12A2BC
ORIGINAL = bytes.fromhex("ff ab 52 00")
REPLACEMENT = bytes.fromhex("1c ac 52 00")
ANCHORS = {
    "frame_call_shared_verifier": (0x52ABD1, "e8 0a ba 2e 00"),
    "frame_status_range_check": (0x52ABD9, "83 f8 03 77 34"),
    "frame_status_dispatch": (0x52ABDE, "ff 24 85 b4 ae 52 00"),
    "frame_status_table": (TABLE_VA, "e5 ab 52 00 f2 ab 52 00 ff ab 52 00 1c ac 52 00"),
    "frame_preload_failure": (0x52AC12, "6a 0a e8 a7 87 ed ff"),
    "frame_postload_guard": (0x52AD43, "74 0a 6a 0a e8 74 86 ed ff"),
    "glue_status_table": (0x4DA9B4, "f1 a7 4d 00 fe a7 4d 00 0b a8 4d 00 35 a8 4d 00"),
}


def validate_identity(data):
    if len(data) != SOURCE_SIZE:
        raise ValueError(f"Source size mismatch: expected {SOURCE_SIZE}, got {len(data)}")
    if hashlib.sha256(data).hexdigest() != SOURCE_SHA256:
        raise ValueError("Source SHA-256 mismatch; stop this investigation for this executable")


def sections(data):
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[:2] != b"MZ" or data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("Not a PE image")
    machine, count = struct.unpack_from("<HH", data, pe + 4)
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    optional = pe + 24
    if machine != 0x14C or struct.unpack_from("<H", data, optional)[0] != 0x10B:
        raise ValueError("Expected x86 PE32")
    if struct.unpack_from("<I", data, optional + 28)[0] != IMAGE_BASE:
        raise ValueError("Unexpected image base")
    result = []
    for i in range(count):
        off = optional + optional_size + i * 40
        name = data[off:off + 8].rstrip(b"\0").decode("ascii")
        vsize, rva, size, raw = struct.unpack_from("<4I", data, off + 8)
        if raw + size > len(data):
            raise ValueError("Section extends past input")
        result.append(dict(name=name, rva=rva, virtual_size=vsize, raw_offset=raw, raw_size=size))
    return result


def location(layout, va, size=1):
    rva = va - IMAGE_BASE
    matches = [s for s in layout if s["rva"] <= rva and
               rva + size <= s["rva"] + min(s["raw_size"], s["virtual_size"])]
    if len(matches) != 1:
        raise ValueError("VA has no unique file-backed section")
    s = matches[0]
    return s["raw_offset"] + rva - s["rva"], s["name"]


def direct_call_candidates(data, layout, target):
    # Raw E8/rel32 hits are only candidate xrefs; inspected call sites are also
    # pinned below. This deliberately makes no claim to be a full disassembler.
    text = next(s for s in layout if s["name"] == ".text")
    part = data[text["raw_offset"]:text["raw_offset"] + text["raw_size"]]
    return [IMAGE_BASE + text["rva"] + i for i in range(len(part) - 4)
            if part[i] == 0xE8 and IMAGE_BASE + text["rva"] + i + 5 +
            struct.unpack_from("<i", part, i + 1)[0] == target]


def analyze(data):
    validate_identity(data)  # Must precede PE parsing or any candidate calculation.
    layout = sections(data)
    anchors = {}
    for name, (va, expected_hex) in ANCHORS.items():
        expected = bytes.fromhex(expected_hex)
        off, section = location(layout, va, len(expected))
        if data[off:off + len(expected)] != expected:
            raise ValueError(f"Expected original bytes differ at {name}")
        anchors[name] = dict(va=hex(va), rva=hex(va - IMAGE_BASE),
                             file_offset=hex(off), section=section, bytes=expected_hex)
    calls = direct_call_candidates(data, layout, 0x8165E0)
    if calls != [0x4DA7DD, 0x52ABD1, 0x5F7B41]:
        raise ValueError("Shared verifier call locations differ or are ambiguous")
    off, section = location(layout, CANDIDATE_VA, 4)
    if off != CANDIDATE_OFFSET or section != ".text" or data[off:off + 4] != ORIGINAL:
        raise ValueError("Candidate locator does not match exact original bytes")
    # In-memory calculation only; there is intentionally no output-binary option.
    preview = data[:off] + REPLACEMENT + data[off + 4:]
    post_hash = hashlib.sha256(preview).hexdigest()
    if post_hash != CANDIDATE_SHA256:
        raise ValueError("Deterministic candidate hash differs")
    optional = struct.unpack_from("<I", data, 0x3C)[0] + 24
    certificate_offset, certificate_size = struct.unpack_from("<II", data, optional + 96 + 4 * 8)
    return {
        "pe_header_limitations": {
            "certificate_file_offset": hex(certificate_offset), "certificate_size": certificate_size,
            "stored_checksum": hex(struct.unpack_from("<I", data, optional + 64)[0]),
            "note": "Candidate changes the signed image and leaves its PE checksum stale; no certificate removal, re-signing, or checksum update is proposed",
        },
        "classification": "STATIC CANDIDATE; game startup experiment NOT performed",
        "source_size": len(data), "source_sha256": SOURCE_SHA256,
        "image_base": hex(IMAGE_BASE), "sections": layout, "anchors": anchors,
        "shared_verifier_direct_call_candidates": [hex(v) for v in calls],
        "frame_return_status_targets": dict(zip(["0_missing", "1_corrupt", "2_digest_mismatch", "3_success"],
            [hex(v) for v in struct.unpack_from("<4I", data, location(layout, TABLE_VA, 16)[0])])),
        "candidate_preview": {
            "va": hex(CANDIDATE_VA), "rva": hex(CANDIDATE_VA - IMAGE_BASE),
            "file_offset": hex(off), "section": section,
            "expected": ORIGINAL.hex(" "), "replacement": REPLACEMENT.hex(" "),
            "hypothetical_sha256": post_hash,
            "changed_offsets": [hex(i) for i, (a, b) in enumerate(zip(data, preview)) if a != b],
            "executable_written": False,
        },
    }


class StockArchives:
    """Read-only archive handles; no extraction and no custom MPQ enumeration."""
    def __init__(self, root, locale):
        if not re.fullmatch(r"[a-z]{2}[A-Z]{2}", locale):
            raise ValueError("Expected a locale such as enUS")
        library = ctypes.util.find_library("storm")
        if not library:
            raise ValueError("Optional archive verification requires installed StormLib")
        self.lib = ct.CDLL(library)
        specs = [
            ("SFileOpenArchive", [ct.c_char_p, ct.c_uint32, ct.c_uint32, ct.POINTER(ct.c_void_p)], ct.c_bool),
            ("SFileOpenFileEx", [ct.c_void_p, ct.c_char_p, ct.c_uint32, ct.POINTER(ct.c_void_p)], ct.c_bool),
            ("SFileGetFileSize", [ct.c_void_p, ct.POINTER(ct.c_uint32)], ct.c_uint32),
            ("SFileReadFile", [ct.c_void_p, ct.c_void_p, ct.c_uint32, ct.POINTER(ct.c_uint32), ct.c_void_p], ct.c_bool),
            ("SFileCloseFile", [ct.c_void_p], ct.c_bool),
            ("SFileCloseArchive", [ct.c_void_p], ct.c_bool),
        ]
        for name, args, result in specs:
            function = getattr(self.lib, name)
            function.argtypes, function.restype = args, result
        self.handles, self.cache = [], {}
        order = [f"{locale}/patch-{locale}-3.MPQ", "patch-3.MPQ", f"{locale}/patch-{locale}-2.MPQ",
                 "patch-2.MPQ", f"{locale}/patch-{locale}.MPQ", "patch.MPQ",
                 f"{locale}/lichking-locale-{locale}.MPQ", f"{locale}/expansion-locale-{locale}.MPQ",
                 f"{locale}/locale-{locale}.MPQ", "lichking.MPQ", "expansion.MPQ", "common-2.MPQ", "common.MPQ"]
        try:
            for relative in order:
                path = Path(root) / "Data" / relative
                if not path.is_file():
                    continue
                handle = ct.c_void_p()
                # MPQ_OPEN_READ_ONLY = STREAM_FLAG_READ_ONLY = 0x100.
                if not self.lib.SFileOpenArchive(str(path).encode(), 0, 0x100, ct.byref(handle)):
                    raise ValueError(f"Cannot open archive read-only: {relative}")
                self.handles.append((relative, handle))
        except Exception:
            self.close()
            raise

    def read(self, name):
        key = name.replace("/", "\\").lower()
        if key in self.cache:
            return self.cache[key][0]
        for archive, handle in self.handles:
            member = ct.c_void_p()
            if not self.lib.SFileOpenFileEx(handle, name.replace("/", "\\").encode(), 0, ct.byref(member)):
                continue
            try:
                high = ct.c_uint32()
                size = self.lib.SFileGetFileSize(member, ct.byref(high))
                if high.value or size > 16_000_000:
                    raise ValueError("Analysis member is too large")
                buffer = ct.create_string_buffer(size)
                count = ct.c_uint32()
                if not self.lib.SFileReadFile(member, buffer, size, ct.byref(count), None) or count.value != size:
                    raise ValueError("Archive read failed")
                self.cache[key] = (buffer.raw, archive)
                return buffer.raw
            finally:
                self.lib.SFileCloseFile(member)
        raise ValueError("Required stock member missing: " + name)

    def close(self):
        for _, handle in self.handles:
            self.lib.SFileCloseArchive(handle)
        self.handles.clear()


def verify_signature(signature, basename, modulus_bytes):
    if len(signature) != 276 or signature[16:20] != b"NGIS":
        return False
    message_digest = hashlib.sha1(signature[:16] + basename.upper().encode("ascii")).digest()
    expected = message_digest + b"\xbb" * 235 + b"\x0b"
    modulus = int.from_bytes(modulus_bytes, "little")
    if not modulus:
        return False
    decoded = pow(int.from_bytes(signature[20:], "little"), 65537, modulus).to_bytes(256, "little")
    return decoded == expected


def resource_digest(archives, part, altered_friends=False):
    # This narrow reconstruction is validated against BOTH stock signed digests.
    # It is not a general XML parser or an implementation of every malformed-input behavior.
    digest = hashlib.md5()
    visits = []

    def visit(path, stack=()):
        path = posixpath.normpath(path.replace("\\", "/").rstrip(" "))
        if path.startswith("../") or path.startswith("/") or path.lower() in stack or len(stack) > 100:
            raise ValueError("Unsafe/cyclic resource reference")
        data = archives.read(path)
        if altered_friends and path.lower() == "interface/framexml/friendsframe.lua":
            data += b"\n-- Portalkeeper CP5 digest-only experiment\n"
        digest.update(data)
        visits.append(path)
        if path.lower().endswith(".toc"):
            for line in data.decode("utf-8-sig").splitlines():
                line = line.strip()
                if line and not line.startswith("#"):
                    visit(posixpath.dirname(path) + "/" + line, stack + (path.lower(),))
        elif path.lower().endswith(".xml"):
            for token in re.findall(rb"<([^>]+)>", data):
                if re.match(rb"\s*(Script|Include)\s", token, re.I):
                    match = re.search(rb'\bfile\s*=\s*"([^"\r\n\t]+)"', token, re.I)
                    if match:
                        visit(posixpath.dirname(path) + "/" + match[1].decode(), stack + (path.lower(),))

    visit(f"Interface/{part}/{part}.toc")
    if part == "FrameXML":
        digest.update(archives.read("Interface/FrameXML/Bindings.xml"))
    return digest.hexdigest(), len(visits), sum(p.lower() == "interface/framexml/friendsframe.lua" for p in visits)


def verify_stock(data, root, locale):
    validate_identity(data)
    layout = sections(data)
    key_offset, _ = location(layout, 0x9E2C28, 256)
    key = data[key_offset:key_offset + 256]  # Never emitted or saved.
    archives = StockArchives(root, locale)
    result = {}
    try:
        for part in ("FrameXML", "GlueXML"):
            name = f"Interface/{part}/{part}.toc.sig"
            signature = archives.read(name)
            if not verify_signature(signature, f"{part}.toc.sig", key):
                raise ValueError(f"{part} stock RSA/SHA-1 signature did not verify")
            digest, visits, friends = resource_digest(archives, part)
            if digest != signature[:16].hex():
                raise ValueError(f"{part} reconstructed stock MD5 differs; do not infer experiment results")
            result[part] = {
                "signature_archive": archives.cache[name.lower().replace("/", "\\")][1],
                "signature_size": len(signature), "signature_sha256": hashlib.sha256(signature).hexdigest(),
                "rsa_sha1_verified": True, "signed_and_reconstructed_md5": digest,
                "resource_visits": visits, "friends_frame_lua_visits": friends,
                "signature_bit_flip_rejected": not verify_signature(signature[:-1] + bytes([signature[-1] ^ 1]), f"{part}.toc.sig", key),
                "signed_digest_bit_flip_rejected": not verify_signature(bytes([signature[0] ^ 1]) + signature[1:], f"{part}.toc.sig", key),
            }
        changed, _, friends = resource_digest(archives, "FrameXML", altered_friends=True)
        if friends != 1 or changed == result["FrameXML"]["signed_and_reconstructed_md5"]:
            raise ValueError("FriendsFrame in-memory experiment did not produce expected digest mismatch")
        result["in_memory_comment_experiment"] = {"md5": changed, "assets_written": False,
            "classification": "Offline digest mismatch only; NOT game startup proof"}
        dbc = archives.read("DBFilesClient/Startup_Strings.dbc")
        magic, count, fields, size, string_size = struct.unpack_from("<4s4I", dbc)
        if magic != b"WDBC" or fields < 3 or size != fields * 4 or 20 + count * size + string_size != len(dbc):
            raise ValueError("Unexpected Startup_Strings.dbc shape")
        strings = dbc[20 + count * size:]
        rows = {}
        for i in range(count):
            record_id, key_at, text_at = struct.unpack_from("<3I", dbc, 20 + i * size)
            if record_id not in (9, 10):
                continue
            def string_at(offset):
                if not 0 <= offset < len(strings) or b"\0" not in strings[offset:]:
                    raise ValueError("Invalid DBC string offset")
                return strings[offset:strings.index(0, offset)].decode("utf-8")
            rows[str(record_id)] = {"key": string_at(key_at), "message": string_at(text_at)}
        if set(rows) != {"9", "10"}:
            raise ValueError("Expected UI failure rows are missing")
        result["startup_errors"] = rows
        return result
    finally:
        archives.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path, help="User-supplied exact verified source Wow.exe (read only)")
    parser.add_argument("--stock-client", type=Path, help="Optional stock client root for read-only MPQ evidence")
    parser.add_argument("--locale", default="enUS")
    args = parser.parse_args()
    try:
        if args.executable.stat().st_size != SOURCE_SIZE:
            raise ValueError("Source size mismatch")
        data = args.executable.read_bytes()
        report = analyze(data)
        if args.stock_client:
            report["offline_archive_evidence"] = verify_stock(data, args.stock_client, args.locale)
        print(json.dumps(report, indent=2))
        return 0
    except (OSError, ValueError, struct.error) as error:
        print("STOP: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
