using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;

namespace Portalkeeper.Services;

/// <summary>
/// Evermore fork: stops the client mixing up two models that have the same file name.
///
/// The same byte as wow-server's tools/client-patches/m2_cache_full_paths.py. Build 12340's model cache
/// (0x81C390, .\M2Cache.cpp) is keyed by the file name without the folder unless the caller passes flag
/// 0x10, so Creature\RidingRaptor\RidingRaptor.m2 and Creature\RetailLibrary\Ridingraptor\Ridingraptor.m2
/// are one model to it: whichever loaded first is drawn for both for as long as it stays cached, wearing the
/// other one's textures. <c>and eax, 10h</c> at 0x81C44A becomes <c>or eax, 10h</c>, so every lookup takes the
/// full path branch (no direct caller passes 0x10, so it only runs once edited).
///
/// This edit is optional: a stock client is safe on the realm and only draws those models wrong, so a
/// Wow.exe it can't edit or can't write still launches.
/// The place has to hold its stock or its edited bytes, and the PE has to map it where build 12340 has it
/// in .text; anything else is refused and the file is left as it was.
/// </summary>
public static class M2CacheFullPathsService
{
    // mov eax, [ebp+0Ch] / and eax, 10h / mov [ebp-0Ch], eax / je +39h, from m2_cache_full_paths.py
    // (VA - 0x400C00 = file offset, VA - 0x400000 = RVA).
    internal const int Offset = 0x41B847;
    internal const int Rva = 0x41C447;
    internal static readonly byte[] StockBytes = Convert.FromHexString("8B450C83E0108945F47439");
    internal static readonly byte[] EditedBytes = Convert.FromHexString("8B450C83C8108945F47439");

    /// <summary>
    /// True when <paramref name="executable"/> already keys its model cache by the full path. Throws when
    /// the file isn't a client this knows how to edit.
    /// </summary>
    public static bool IsSetUp(string executable) =>
        Edited(File.ReadAllBytes(executable), Path.GetFileName(executable)) is null;

    /// <summary>
    /// Makes <paramref name="executable"/> (inside the client <paramref name="root"/>) key its model cache
    /// by the full path. Returns true when it changed the file, false when it did already. The file it
    /// replaces is kept under .portalkeeper/backups (<see cref="ClientExecutableEdit"/>). Throws when the
    /// file isn't a client this knows how to edit; a failure leaves the file as it was.
    /// </summary>
    public static bool Ensure(string root, string executable)
    {
        var original = File.ReadAllBytes(executable);
        var edited = Edited(original, Path.GetFileName(executable));
        if (edited is null) return false;

        ClientExecutableEdit.Replace(root, executable, original, edited);
        return true;
    }

    /// <summary><see cref="IsSetUp"/>, except that a client it can't edit counts as done.</summary>
    public static bool IsSetUpOrRefused(string executable)
    {
        try { return IsSetUp(executable); }
        catch (InvalidDataException) { return true; }
    }

    /// <summary>
    /// <see cref="Ensure"/>, except that a client it can't edit or can't write is left alone (false) rather
    /// than refused, so an edit that's only cosmetic never keeps a player out of the game.
    /// </summary>
    public static bool TryEnsure(string root, string executable)
    {
        var original = File.ReadAllBytes(executable);
        byte[]? edited;
        try { edited = Edited(original, Path.GetFileName(executable)); }
        catch (InvalidDataException) { return false; }
        if (edited is null) return false;

        try { ClientExecutableEdit.Replace(root, executable, original, edited); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
        return true;
    }

    /// <summary>The file with the edit, or null when it has it already.</summary>
    internal static byte[]? Edited(byte[] original, string name)
    {
        var refused = new InvalidDataException(name + " isn't a 3.3.5a client that can be set up to tell same-named models apart.");
        CheckLayout(original, refused);
        var found = original.AsSpan(Offset, StockBytes.Length);
        if (found.SequenceEqual(EditedBytes)) return null;
        if (!found.SequenceEqual(StockBytes)) throw refused;

        var bytes = (byte[])original.Clone();
        EditedBytes.CopyTo(bytes, Offset);
        return bytes;
    }

    /// <summary>Throws unless the image maps the place where build 12340 has it, in .text.</summary>
    private static void CheckLayout(byte[] bytes, Exception refused)
    {
        PEHeaders headers;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEReader(stream);
            headers = pe.PEHeaders;
        }
        catch (BadImageFormatException) { throw refused; }
        if (headers.CoffHeader.Machine != Machine.I386 || headers.PEHeader?.Magic != PEMagic.PE32 ||
            headers.PEHeader.ImageBase != 0x400000)
            throw refused;
        var length = StockBytes.Length;
        var matches = headers.SectionHeaders.Where(s => Rva >= s.VirtualAddress &&
            (long)Rva + length <= (long)s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData)).ToArray();
        if (matches.Length != 1 || matches[0].Name != ".text") throw refused;
        var section = matches[0];
        var delta = (long)Rva - section.VirtualAddress;
        if (delta + length > section.SizeOfRawData || section.PointerToRawData + delta != Offset ||
            Offset + length > bytes.Length)
            throw refused;
    }
}
