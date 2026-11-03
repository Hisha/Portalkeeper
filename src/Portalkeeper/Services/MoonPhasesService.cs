using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;

namespace Portalkeeper.Services;

/// <summary>
/// Evermore fork: live moon phases and both moons on Toronto's real nights.
///
/// The same bytes as wow-server's tools/client-patches/moon_phases.py (build 12340; new code in .text padding
/// and in .zdata, below the warband edit's stubs):
/// <list type="bullet">
/// <item>The Blue Child gets the White Lady's colour each frame; a stock client leaves her alpha at 0 and never
/// shows her. Her orbit becomes 1 day, so she keeps the White Lady's hours.</item>
/// <item>Each frame both moons work out their phase from the server's time of day (the White Lady's month every
/// 30 minutes, the Blue Child's cycle every 20) and swap to Textures\Evm_MoonNN.blp or Evm_BlueNN.blp when it
/// changes, except while the sky is torn down.</item>
/// <item>They read a moon clock from a weekly table (rise 3 h after Toronto's dusk, set 1 h before sunrise,
/// inside the game's night of 21:00 to 05:30) instead of the sky clock.</item>
/// </list>
/// The textures are in patch-z97-night-sky.MPQ: this exe without them draws green moons, so the realm's patch
/// has to carry them before a launcher with this edit goes out.
///
/// This edit is optional: a stock client is safe on the realm and only shows Blizzard's one moon, so a Wow.exe
/// it can't edit or can't write still launches.
/// Every place has to hold its stock or its edited bytes, all the same way, and the PE has to map each where
/// build 12340 has it; anything else is refused and the file is left as it was.
/// </summary>
public static class MoonPhasesService
{
    /// <summary>File offset and RVA of a place, its stock bytes and its edited bytes.</summary>
    internal sealed record Place(int Offset, int Rva, string Stock, string Edited)
    {
        public byte[] StockBytes { get; } = Convert.FromHexString(Stock);
        public byte[] EditedBytes { get; } = Convert.FromHexString(Edited);
        public string Section => Rva < WarbandSelectService.ZdataRva ? ".text" : ".zdata";
    }

    // Generated from moon_phases.edits(): the colour hook 0x7F37C3 and its cave 0x9296A4, the orbit 0x7F29B9, the
    // frame hook 0x7EF5DA, the five reads of the sky clock, then the code at 0xDD1900, the table at 0xDD1C00 and
    // the data at 0xDD1D80.
    internal static readonly Place[] Places =
    [
        new(0x3F2BC3, 0x3F37C3, "D9EED905888BD300",
            "E9DC5E1300909090"),
        new(0x528AA4, 0x5296A4, "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
            "50A1548ED300A3748ED30058D9EED905888BD300E90EA1ECFF"),
        new(0x3F1DB9, 0x3F29B9, "D905A041A200",
            "D9E890909090"),
        new(0x3EE9DA, 0x3EF5DA, "D945ECD91C24",
            "E921235E0090"),
        new(0x3EE5BB, 0x3EF1BB, "048BD300",
            "FC1DDD00"),
        new(0x3EE5E2, 0x3EF1E2, "048BD300",
            "FC1DDD00"),
        new(0x3EE75D, 0x3EF35D, "048BD300",
            "FC1DDD00"),
        new(0x3EE7C3, 0x3EF3C3, "048BD300",
            "FC1DDD00"),
        new(0x3EE7EA, 0x3EF3EA, "048BD300",
            "FC1DDD00"),
        new(0x72D700, 0x9D1900, "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
            "60833DF008CF00000F84A4000000D905048BD300D80D9C1DDD00D905801DDD00D9C9D9F8D8F1DDD9D80D881DDD00D825901DDD00DB1D981DDD00A1981DDD0083F81C731C3A05668ED3007414A2668ED300BF488ED300BEA41DDD00E843010000D905048BD300D80DA01DDD00D905841DDD00D9C9D9F8D8F1DDD9D80D8C1DDD00D825901DDD00DB1D981DDD00A1981DDD0083F810731C3A05678ED3007414A2678ED300BF688ED300BEBC1DDD00E8F1000000D905088BD300D805048BD300D825E41DDD00D9FCD825D41DDD00D905D81DDD00D9C9D9F8DDD9D835DC1DDD00D9C0D825901DDD00DB1D981DDD00A1981DDD0083F834760231C0DA25981DDD008D0C85001CDD00DF4104DE21D8C9DE01D915F41DDD00DF4106DE6102D8CADE4102DEE1D91DF81DDD00DDD8D905048BD300D805901DDD00D815E41DDD00DFE09E7206D825E41DDD00D80DE01DDD00D825F41DDD00D9E4DFE09E7230D835F81DDD00D815E41DDD00DFE09E731FD80DEC1DDD00D805E81DDD00D815E41DDD00DFE09E7210D825E41DDD00EB08DDD8D905F01DDD00D91DFC1DDD0061D945ECD91C24E93DDBA1FF31D2B90A000000F7F1043080C2308846118856128B5F105689F9E8EEB5BDFF85DB740953E864A46AFF83C404C3"),
        new(0x72DA00, 0x9D1C00, "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
            "1C021A041C021A041C021A041C021A041C021A041F021A0428021A0431021A043A021A0443021A0488021A0491021A0499021A04A2021A04AA021A04B3021A04BC021104C5020604CE02FD03D602F503DF02EE03E602E903ED02E503F202E303F502E303F702E503F602E803F402ED03EF02F303E902FA03E1020104D8020904CD021104C2021804B5021A04A9021A049C021A048E021A0481021A0474021A0468021A045C021A0451021A0447021A041C021A041C021A041C021A041C021A041C021A041C021A041C021A041C021A041C021A041C021A04"),
        new(0x72DB80, 0x9D1D80, "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
            "A53EEC41000030410000E041000080410000003F354A2B4600000000FC2EB1440000464454657874757265735C45766D5F4D6F6F6E30302E626C700054657874757265735C45766D5F426C756530302E626C700000CC9F460A9FB6430000E0400000B4440000803FF2D26D3F07CE593E0000003F00000000000000000000003F"),
    ];

    /// <summary>
    /// True when <paramref name="executable"/> already has the moon edits. Throws when the file isn't a client
    /// this knows how to edit.
    /// </summary>
    public static bool IsSetUp(string executable) =>
        Edited(File.ReadAllBytes(executable), Path.GetFileName(executable)) is null;

    /// <summary>
    /// Gives <paramref name="executable"/> (inside the client <paramref name="root"/>) the moon edits. Returns
    /// true when it changed the file, false when it had them already. The file it replaces is kept under
    /// .portalkeeper/backups (<see cref="ClientExecutableEdit"/>). Throws when the file isn't a client this
    /// knows how to edit; a failure leaves the file as it was.
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException) { return false; }
        return true;
    }

    /// <summary>The file with the edits, or null when it has them already.</summary>
    internal static byte[]? Edited(byte[] original, string name)
    {
        var refused = new InvalidDataException(name + " isn't a 3.3.5a client that can be set up for the moon phases.");
        CheckLayout(original, refused);
        bool? edited = null;
        foreach (var place in Places)
        {
            var found = original.AsSpan(place.Offset, place.StockBytes.Length);
            var isEdited = found.SequenceEqual(place.EditedBytes);
            if (!isEdited && !found.SequenceEqual(place.StockBytes)) throw refused;
            // Partly edited: some places stock, some edited. Not touched.
            if (edited is bool state && state != isEdited) throw refused;
            edited = isEdited;
        }
        if (edited == true) return null;

        var bytes = (byte[])original.Clone();
        foreach (var place in Places) place.EditedBytes.CopyTo(bytes, place.Offset);
        return bytes;
    }

    /// <summary>Throws unless the image maps every place where build 12340 has it, in the section it belongs to.</summary>
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
        foreach (var place in Places)
        {
            var length = place.StockBytes.Length;
            var matches = headers.SectionHeaders.Where(s => place.Rva >= s.VirtualAddress &&
                (long)place.Rva + length <= (long)s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData)).ToArray();
            if (matches.Length != 1 || matches[0].Name != place.Section) throw refused;
            var section = matches[0];
            var delta = (long)place.Rva - section.VirtualAddress;
            if (delta + length > section.SizeOfRawData || section.PointerToRawData + delta != place.Offset ||
                place.Offset + length > bytes.Length)
                throw refused;
        }
    }
}
