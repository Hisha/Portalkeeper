using System.Security.Cryptography;
using System.Text;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

// Evermore fork: the guild bank's Wow.exe edit (GuildBankSlotsService) on a stand-in PE laid out like
// build 12340's .text, and `guild-bank-exe <Wow.exe>` to apply it to a copy of a real client.
internal static partial class Program
{
    /// <summary>A PE32 stand-in for Wow.exe with .text where build 12340 has it, stock bytes at every place.</summary>
    private static byte[] GuildBankStandInClient(uint textRaw = 0x400, uint imageBase = 0x400000)
    {
        const int textSize = 0x420000; // up to the live bag sizes' and the model cache's places too
        var bytes = new byte[textRaw + textSize];
        new Random(98).NextBytes(bytes.AsSpan((int)textRaw, textSize));
        void U16(int at, int value) => BitConverter.GetBytes((ushort)value).CopyTo(bytes, at);
        void U32(int at, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, at);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        U32(0x3C, 0x80);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(bytes, 0x80);
        U16(0x84, 0x014C); // i386
        U16(0x86, 1); // .text
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
        U32(optional + 56, 0x1000 + textSize);
        U32(optional + 60, 0x400);
        U16(optional + 68, 2);
        U32(optional + 92, 16);
        const int text = optional + 0xE0;
        Encoding.ASCII.GetBytes(".text\0\0\0").CopyTo(bytes, text);
        U32(text + 8, textSize);
        U32(text + 12, 0x1000);
        U32(text + 16, textSize);
        U32(text + 20, textRaw);
        U32(text + 36, 0x60000020);
        foreach (var place in GuildBankSlotsService.Places)
            if (place.Offset + place.StockBytes.Length <= bytes.Length) place.StockBytes.CopyTo(bytes, place.Offset);
        // The character limit's guard, the live bag sizes' places and the model cache's too, so the edits
        // can be tried together.
        Convert.FromHexString("807DFF0AC745F800000000760B").CopyTo(bytes, CharacterListLimitService.GuardOffset);
        foreach (var place in LiveBagSlotsService.Places) place.StockBytes.CopyTo(bytes, place.Offset);
        if (M2CacheFullPathsService.Offset + M2CacheFullPathsService.StockBytes.Length <= bytes.Length)
            M2CacheFullPathsService.StockBytes.CopyTo(bytes, M2CacheFullPathsService.Offset);
        return bytes;
    }

    private static void GuildBankSlotsChecks(string root, Action<string, bool> check)
    {
        var client = Path.Combine(root, "guild-bank");
        Directory.CreateDirectory(client);
        var exe = Path.Combine(client, "Wow.exe");
        var stock = GuildBankStandInClient();
        File.WriteAllBytes(exe, stock);
        string[] Backups() => Directory.Exists(Path.Combine(client, ".portalkeeper", "backups", "executable"))
            ? Directory.EnumerateFiles(Path.Combine(client, ".portalkeeper", "backups", "executable"), "*", SearchOption.AllDirectories).ToArray()
            : [];

        check("guild bank: 30 places", GuildBankSlotsService.Places.Length == 30);
        check("guild bank: a stock client is not set up yet", !GuildBankSlotsService.IsSetUp(exe) && Backups().Length == 0);
        check("guild bank: a stock client is changed", GuildBankSlotsService.Ensure(client, exe) && GuildBankSlotsService.IsSetUp(exe));
        var edited = File.ReadAllBytes(exe);
        var changed = Enumerable.Range(0, stock.Length).Where(i => stock[i] != edited[i]).ToArray();
        check("guild bank: only the places change, 71 bytes", changed.Length == 71
            && changed.All(i => GuildBankSlotsService.Places.Any(p => i >= p.Offset && i < p.Offset + p.StockBytes.Length)));
        check("guild bank: a tab's array is sized 196",
            edited.AsSpan(0x1A6720, 5).SequenceEqual(Convert.FromHexString("BBC4000000")));
        check("guild bank: the file it replaced is kept", Backups().Length == 1 && File.ReadAllBytes(Backups()[0]).AsSpan().SequenceEqual(stock));
        check("guild bank: a client that's set up is left alone", !GuildBankSlotsService.Ensure(client, exe)
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(edited) && Backups().Length == 1);

        // With the character limit, either order gives the same file.
        File.WriteAllBytes(exe, stock);
        var limitFirst = CharacterListLimitService.Ensure(client, exe) && GuildBankSlotsService.Ensure(client, exe);
        var both = File.ReadAllBytes(exe);
        File.WriteAllBytes(exe, stock);
        var bankFirst = GuildBankSlotsService.Ensure(client, exe) && CharacterListLimitService.Ensure(client, exe);
        check("guild bank: with the character limit, either order gives the same file", limitFirst && bankFirst
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(both) && GuildBankSlotsService.IsSetUp(exe) && CharacterListLimitService.IsSetUp(exe));

        // Anything else is refused and left as it was.
        var foreign = Path.Combine(client, "Other.exe");
        bool Refused(byte[] bytes)
        {
            File.WriteAllBytes(foreign, bytes);
            var ensured = false;
            var asked = false;
            try { GuildBankSlotsService.Ensure(client, foreign); } catch (InvalidDataException) { ensured = true; }
            try { GuildBankSlotsService.IsSetUp(foreign); } catch (InvalidDataException) { asked = true; }
            return ensured && asked && File.ReadAllBytes(foreign).AsSpan().SequenceEqual(bytes);
        }
        var partly = (byte[])stock.Clone();
        GuildBankSlotsService.Places[0].EditedBytes.CopyTo(partly, GuildBankSlotsService.Places[0].Offset);
        check("guild bank: a partly edited client is refused", Refused(partly));
        var otherCode = (byte[])stock.Clone();
        otherCode[0x2DACAD] ^= 0x01; // mov dl, 62h at 0x6DB8AD
        check("guild bank: different code at a place is refused", Refused(otherCode));
        check("guild bank: a truncated file is refused", Refused(stock.AsSpan(0, 0x2DACA0).ToArray()));
        check("guild bank: a PE with another image base is refused", Refused(GuildBankStandInClient(imageBase: 0x10000000)));
        check("guild bank: a PE whose .text sits elsewhere in the file is refused", Refused(GuildBankStandInClient(textRaw: 0x600)));
        check("guild bank: a file that isn't a PE is refused", Refused(Encoding.ASCII.GetBytes("not a client").Concat(new byte[0x2E0400]).ToArray()));
    }

    /// <summary>`guild-bank-exe Wow.exe`: applies the edit to a copy next to it and prints both SHA-256s.</summary>
    private static int RunGuildBankExe(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: guild-bank-exe <Wow.exe>"); return 2; }
        var source = Path.GetFullPath(args[1]);
        var root = Path.Combine(Path.GetTempPath(), "pk-guild-bank-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "Wow.exe");
        File.Copy(source, exe);
        Console.WriteLine(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant() + "  " + source);
        var changed = GuildBankSlotsService.Ensure(root, exe);
        Console.WriteLine(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant() + "  edited" + (changed ? "" : " (already)"));
        Directory.Delete(root, true);
        return 0;
    }
}
