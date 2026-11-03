using System.Security.Cryptography;
using System.Text;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

// Evermore fork: the moons' Wow.exe edit (MoonPhasesService) on a stand-in PE laid out like build 12340 (.text
// and .zdata), and `moon-phases-exe <Wow.exe>` to apply it to a copy of a real client.
internal static partial class Program
{
    /// <summary>A PE32 stand-in for Wow.exe with .text and .zdata where build 12340 has them, stock bytes at every place.</summary>
    private static byte[] MoonStandInClient(uint zdataRaw = 0x72CE00, uint imageBase = 0x400000)
    {
        const int textSize = 0x530000;
        var bytes = new byte[zdataRaw + 0x1000];
        new Random(29).NextBytes(bytes.AsSpan(0x400, textSize));
        void U16(int at, int value) => BitConverter.GetBytes((ushort)value).CopyTo(bytes, at);
        void U32(int at, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, at);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        U32(0x3C, 0x80);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(bytes, 0x80);
        U16(0x84, 0x014C); // i386
        U16(0x86, 2); // .text, .zdata
        U16(0x94, 0xE0);
        U16(0x96, 0x0102);
        const int optional = 0x98;
        U16(optional, 0x10B); // PE32
        U32(optional + 4, textSize);
        U32(optional + 16, 0x1000);
        U32(optional + 20, 0x1000);
        U32(optional + 28, imageBase);
        U32(optional + 32, 0x1000);
        U32(optional + 36, 0x200);
        U16(optional + 40, 4);
        U16(optional + 48, 4);
        U32(optional + 56, WarbandSelectService.ZdataRva + 0x1000);
        U32(optional + 60, 0x400);
        U16(optional + 68, 2);
        U32(optional + 92, 16);
        const int text = optional + 0xE0;
        Encoding.ASCII.GetBytes(".text\0\0\0").CopyTo(bytes, text);
        U32(text + 8, textSize);
        U32(text + 12, 0x1000);
        U32(text + 16, textSize);
        U32(text + 20, 0x400);
        U32(text + 36, 0x60000020);
        const int zdata = text + 40;
        Encoding.ASCII.GetBytes(".zdata\0\0").CopyTo(bytes, zdata);
        U32(zdata + 8, 0x1000);
        U32(zdata + 12, WarbandSelectService.ZdataRva);
        U32(zdata + 16, 0x1000);
        U32(zdata + 20, zdataRaw);
        U32(zdata + 36, 0xE0000040);
        foreach (var place in MoonPhasesService.Places)
            if (place.Offset + place.StockBytes.Length <= bytes.Length) place.StockBytes.CopyTo(bytes, place.Offset);
        // The warband edit's and the model cache's places too, so the edits can be tried together.
        foreach (var place in WarbandSelectService.Places)
            if (place.Offset + place.StockBytes.Length <= bytes.Length) place.StockBytes.CopyTo(bytes, place.Offset);
        M2CacheFullPathsService.StockBytes.CopyTo(bytes, M2CacheFullPathsService.Offset);
        return bytes;
    }

    private static void MoonPhasesChecks(string root, Action<string, bool> check)
    {
        var client = Path.Combine(root, "moon-phases");
        Directory.CreateDirectory(client);
        var exe = Path.Combine(client, "Wow.exe");
        var stock = MoonStandInClient();
        File.WriteAllBytes(exe, stock);
        string[] Backups() => Directory.Exists(Path.Combine(client, ".portalkeeper", "backups", "executable"))
            ? Directory.EnumerateFiles(Path.Combine(client, ".portalkeeper", "backups", "executable"), "*", SearchOption.AllDirectories).ToArray()
            : [];

        check("moons: a stock client is not set up yet", !MoonPhasesService.IsSetUp(exe) && !MoonPhasesService.IsSetUpOrRefused(exe)
            && Backups().Length == 0);
        check("moons: a stock client is changed", MoonPhasesService.Ensure(client, exe) && MoonPhasesService.IsSetUp(exe));
        var edited = File.ReadAllBytes(exe);
        var changed = Enumerable.Range(0, stock.Length).Where(i => stock[i] != edited[i]).ToArray();
        check("moons: only the places change", changed.Length > 0
            && changed.All(i => MoonPhasesService.Places.Any(p => i >= p.Offset && i < p.Offset + p.StockBytes.Length)));
        check("moons: the frame hook jumps to the code at 0xDD1900",
            edited.AsSpan(0x3EE9DA, 6).SequenceEqual(Convert.FromHexString("E921235E0090")));
        check("moons: the file it replaced is kept", Backups().Length == 1 && File.ReadAllBytes(Backups()[0]).AsSpan().SequenceEqual(stock));
        check("moons: a client that's set up is left alone", !MoonPhasesService.Ensure(client, exe) && !MoonPhasesService.TryEnsure(client, exe)
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(edited) && Backups().Length == 1);

        // With the other edits in .zdata and .text, any order gives the same file.
        File.WriteAllBytes(exe, stock);
        var moonLast = WarbandSelectService.Ensure(client, exe) && M2CacheFullPathsService.Ensure(client, exe) && MoonPhasesService.TryEnsure(client, exe);
        var all = File.ReadAllBytes(exe);
        File.WriteAllBytes(exe, stock);
        var moonFirst = MoonPhasesService.Ensure(client, exe) && M2CacheFullPathsService.Ensure(client, exe) && WarbandSelectService.Ensure(client, exe);
        check("moons: with the warband and model cache edits, either order gives the same file", moonLast && moonFirst
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(all) && MoonPhasesService.IsSetUp(exe) && WarbandSelectService.IsSetUp(exe));

        // Anything else is refused by Ensure and IsSetUp, left alone by TryEnsure, and never changed.
        var foreign = Path.Combine(client, "Other.exe");
        bool Refused(byte[] bytes)
        {
            File.WriteAllBytes(foreign, bytes);
            var ensured = false;
            var asked = false;
            try { MoonPhasesService.Ensure(client, foreign); } catch (InvalidDataException) { ensured = true; }
            try { MoonPhasesService.IsSetUp(foreign); } catch (InvalidDataException) { asked = true; }
            return ensured && asked && !MoonPhasesService.TryEnsure(client, foreign) && MoonPhasesService.IsSetUpOrRefused(foreign)
                && File.ReadAllBytes(foreign).AsSpan().SequenceEqual(bytes);
        }
        var otherCode = (byte[])stock.Clone();
        otherCode[0x3EE9DA] ^= 0x01;
        check("moons: different code at a place is refused", Refused(otherCode));
        var partly = (byte[])stock.Clone();
        MoonPhasesService.Places[0].EditedBytes.CopyTo(partly, MoonPhasesService.Places[0].Offset);
        check("moons: a partly edited client is refused", Refused(partly));
        var usedCave = (byte[])stock.Clone();
        usedCave[0x72D700] = 0x90; // another tool's code where the moon code goes
        check("moons: something else in the code's place is refused", Refused(usedCave));
        check("moons: a truncated file is refused", Refused(stock.AsSpan(0, 0x500000).ToArray()));
        check("moons: a PE with another image base is refused", Refused(MoonStandInClient(imageBase: 0x10000000)));
        check("moons: a PE whose .zdata sits elsewhere in the file is refused", Refused(MoonStandInClient(zdataRaw: 0x72D000)));
        check("moons: a file that isn't a PE is refused", Refused(Encoding.ASCII.GetBytes("not a client").Concat(new byte[0x100]).ToArray()));
    }

    /// <summary>`moon-phases-exe Wow.exe`: applies the edit to a copy next to it and prints both SHA-256s.</summary>
    private static int RunMoonPhasesExe(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: moon-phases-exe <Wow.exe>"); return 2; }
        var source = Path.GetFullPath(args[1]);
        var root = Path.Combine(Path.GetTempPath(), "pk-moon-phases-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "Wow.exe");
        File.Copy(source, exe);
        Console.WriteLine(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant() + "  " + source);
        var changed = MoonPhasesService.Ensure(root, exe);
        Console.WriteLine(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant() + "  edited" + (changed ? "" : " (already)"));
        Directory.Delete(root, true);
        return 0;
    }
}
