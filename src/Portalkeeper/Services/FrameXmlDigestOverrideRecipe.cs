using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Portalkeeper.Services;

// One fixed recipe. No caller-supplied offsets, bytes, hashes, or recipe definitions.
public static class FrameXmlDigestOverrideRecipe
{
    public const string Id = "wow-12340-framexml-digest-acceptance";
    public const int Version = 1;
    public const int SourceSize = 7_704_216;
    public const string SourceSha256 = "AA63A5750D60EF16746C686B3D5E26876D98953EAB08B1C026CD0FAF78E88CB8";
    public const string OutputSha256 = "15C47945D5C461FDA32A34645A23A9F3488BBB2B10855FE328C0E866D9CA8001";
    public const int TargetRva = 0x12AEBC;
    public const int TargetOffset = 0x12A2BC;

    public static void ValidateSource(byte[] bytes)
    {
        if (bytes.Length != SourceSize) throw new InvalidDataException("FrameXML recipe source size is unsupported.");
        if (Convert.ToHexString(SHA256.HashData(bytes)) != SourceSha256)
            throw new InvalidDataException("FrameXML recipe requires the exact verified build 12340 source SHA-256.");
        ValidateLayoutAndGuards(bytes);
    }

    // Kept separate for synthetic malformed-PE/guard tests; does not transform anything.
    internal static void ValidateLayoutAndGuards(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var headers = pe.PEHeaders;
        if (headers.CoffHeader.Machine != Machine.I386 || headers.PEHeader?.Magic != PEMagic.PE32 ||
            headers.PEHeader.ImageBase != 0x400000 || headers.SectionHeaders.Length != 6)
            throw new InvalidDataException("FrameXML recipe requires the expected PE32/i386 image.");
        var text = headers.SectionHeaders.Where(s => s.Name == ".text").ToArray();
        if (text.Length != 1 || text[0].VirtualAddress != 0x1000 || text[0].PointerToRawData != 0x400 ||
            text[0].SizeOfRawData != 0x5DD400 || text[0].VirtualSize != 0x5DD3B3)
            throw new InvalidDataException("FrameXML recipe .text mapping differs from the verified image.");
        int Map(int rva, int count)
        {
            var matches = headers.SectionHeaders.Where(s => rva >= s.VirtualAddress &&
                (long)rva + count <= (long)s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData)).ToArray();
            if (matches.Length != 1 || matches[0].Name != ".text")
                throw new InvalidDataException("FrameXML RVA mapping is missing or ambiguous.");
            var section = matches[0];
            var delta = (long)rva - section.VirtualAddress;
            var offset = (long)section.PointerToRawData + delta;
            if (delta + count > section.SizeOfRawData || offset < 0 || offset + count > bytes.Length)
                throw new InvalidDataException("FrameXML RVA is not file-backed.");
            return checked((int)offset);
        }
        void Guard(int rva, int expectedOffset, string expectedHex, string label)
        {
            var expected = Convert.FromHexString(expectedHex);
            var offset = Map(rva, expected.Length);
            if (offset != expectedOffset || !bytes.AsSpan(offset, expected.Length).SequenceEqual(expected))
                throw new InvalidDataException("FrameXML recipe guard mismatch: " + label);
        }
        Guard(TargetRva, TargetOffset, "FFAB5200", "result-2 entry");
        Guard(0x12AEB4, 0x12A2B4, "E5AB5200F2AB5200FFAB52001CAC5200", "dispatch table");
        Guard(0x12ABDE, 0x129FDE, "FF2485B4AE5200", "indexed dispatch");
        Guard(0x12ABD1, 0x129FD1, "E80ABA2E00", "verifier call");
    }

    // In-memory result only; the owning service alone writes/promotes an independent temp file.
    public static byte[] Generate(byte[] source)
    {
        ValidateSource(source);
        var result = (byte[])source.Clone();
        Convert.FromHexString("1CAC5200").CopyTo(result, TargetOffset);
        ValidateOutput(result);
        return result;
    }

    public static void ValidateOutput(byte[] bytes)
    {
        if (bytes.Length != SourceSize || Convert.ToHexString(SHA256.HashData(bytes)) != OutputSha256)
            throw new InvalidDataException("FrameXML recipe output differs from the fixed deterministic identity.");
    }
}
