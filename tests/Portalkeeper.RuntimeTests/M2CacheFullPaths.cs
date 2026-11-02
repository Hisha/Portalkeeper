using System.Security.Cryptography;
using System.Text;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

// Evermore fork: the model cache's Wow.exe edit (M2CacheFullPathsService) on the guild bank's stand-in PE,
// and `m2-cache-exe <Wow.exe>` to apply it to a copy of a real client.
internal static partial class Program
{
    private static void M2CacheFullPathsChecks(string root, Action<string, bool> check)
    {
        var client = Path.Combine(root, "m2-cache");
        Directory.CreateDirectory(client);
        var exe = Path.Combine(client, "Wow.exe");
        var stock = GuildBankStandInClient();
        File.WriteAllBytes(exe, stock);
        string[] Backups() => Directory.Exists(Path.Combine(client, ".portalkeeper", "backups", "executable"))
            ? Directory.EnumerateFiles(Path.Combine(client, ".portalkeeper", "backups", "executable"), "*", SearchOption.AllDirectories).ToArray()
            : [];

        check("m2 cache: a stock client is not set up yet", !M2CacheFullPathsService.IsSetUp(exe) && !M2CacheFullPathsService.IsSetUpOrRefused(exe)
            && Backups().Length == 0);
        check("m2 cache: a stock client is changed", M2CacheFullPathsService.Ensure(client, exe) && M2CacheFullPathsService.IsSetUp(exe));
        var edited = File.ReadAllBytes(exe);
        var changed = Enumerable.Range(0, stock.Length).Where(i => stock[i] != edited[i]).ToArray();
        check("m2 cache: one byte changes, and turns the and into an or", changed.SequenceEqual([0x41B84B])
            && stock[0x41B84B] == 0xE0 && edited[0x41B84B] == 0xC8);
        check("m2 cache: the file it replaced is kept", Backups().Length == 1 && File.ReadAllBytes(Backups()[0]).AsSpan().SequenceEqual(stock));
        check("m2 cache: a client that's set up is left alone", !M2CacheFullPathsService.Ensure(client, exe) && !M2CacheFullPathsService.TryEnsure(client, exe)
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(edited) && Backups().Length == 1);

        // With the other edits, any order gives the same file.
        File.WriteAllBytes(exe, stock);
        var cacheLast = CharacterListLimitService.Ensure(client, exe) && GuildBankSlotsService.Ensure(client, exe)
            && LiveBagSlotsService.TryEnsure(client, exe) && M2CacheFullPathsService.TryEnsure(client, exe);
        var all = File.ReadAllBytes(exe);
        File.WriteAllBytes(exe, stock);
        var cacheFirst = M2CacheFullPathsService.Ensure(client, exe) && LiveBagSlotsService.Ensure(client, exe)
            && GuildBankSlotsService.Ensure(client, exe) && CharacterListLimitService.Ensure(client, exe);
        check("m2 cache: with the character limit, guild bank and live bags, either order gives the same file", cacheLast && cacheFirst
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(all) && M2CacheFullPathsService.IsSetUp(exe) && LiveBagSlotsService.IsSetUp(exe));

        // A folder it can't write to leaves the client as it is, and doesn't stop the launch.
        if (!OperatingSystem.IsWindows() && Environment.UserName != "root") // root ignores the folder's mode
        {
            File.WriteAllBytes(exe, stock);
            var mode = File.GetUnixFileMode(client);
            File.SetUnixFileMode(client, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                check("m2 cache: a client it can't write is left alone", !M2CacheFullPathsService.TryEnsure(client, exe)
                    && File.ReadAllBytes(exe).AsSpan().SequenceEqual(stock));
            }
            finally { File.SetUnixFileMode(client, mode); }
        }

        // Anything else is refused by Ensure and IsSetUp, left alone by TryEnsure, and never changed.
        var foreign = Path.Combine(client, "Other.exe");
        bool Refused(byte[] bytes)
        {
            File.WriteAllBytes(foreign, bytes);
            var ensured = false;
            var asked = false;
            try { M2CacheFullPathsService.Ensure(client, foreign); } catch (InvalidDataException) { ensured = true; }
            try { M2CacheFullPathsService.IsSetUp(foreign); } catch (InvalidDataException) { asked = true; }
            return ensured && asked && !M2CacheFullPathsService.TryEnsure(client, foreign) && M2CacheFullPathsService.IsSetUpOrRefused(foreign)
                && File.ReadAllBytes(foreign).AsSpan().SequenceEqual(bytes);
        }
        var otherCode = (byte[])stock.Clone();
        otherCode[0x41B84B] = 0xF0;
        check("m2 cache: different code at the place is refused", Refused(otherCode));
        var otherAround = (byte[])stock.Clone();
        otherAround[0x41B850] ^= 0x01; // the je after it
        check("m2 cache: different code around the place is refused", Refused(otherAround));
        check("m2 cache: a truncated file is refused", Refused(stock.AsSpan(0, 0x41B800).ToArray()));
        check("m2 cache: a PE with another image base is refused", Refused(GuildBankStandInClient(imageBase: 0x10000000)));
        check("m2 cache: a PE whose .text sits elsewhere in the file is refused", Refused(GuildBankStandInClient(textRaw: 0x600)));
        check("m2 cache: a file that isn't a PE is refused", Refused(Encoding.ASCII.GetBytes("not a client").Concat(new byte[0x420400]).ToArray()));
    }

    /// <summary>`m2-cache-exe Wow.exe`: applies the edit to a copy next to it and prints both SHA-256s.</summary>
    private static int RunM2CacheExe(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: m2-cache-exe <Wow.exe>"); return 2; }
        var source = Path.GetFullPath(args[1]);
        var root = Path.Combine(Path.GetTempPath(), "pk-m2-cache-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "Wow.exe");
        File.Copy(source, exe);
        Console.WriteLine(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant() + "  " + source);
        var changed = M2CacheFullPathsService.Ensure(root, exe);
        Console.WriteLine(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant() + "  edited" + (changed ? "" : " (already)"));
        Directory.Delete(root, true);
        return 0;
    }
}
