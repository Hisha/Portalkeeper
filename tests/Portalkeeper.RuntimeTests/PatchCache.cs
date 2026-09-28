using System.Security.Cryptography;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

// Patch changes clear the client's WDB cache so stale data is not reused.
internal static partial class Program
{
    private static async Task<int> RunPatchCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "pk-cache-" + Guid.NewGuid().ToString("N"));
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name); if (!ok) failures++; }
        try
        {
            var bytes = new byte[] { 77, 80, 81, 67 };
            var patch = new PatchDefinition { Id = "cache", Name = "cache", InstallDirectory = "Data", FileName = "patch-Z.MPQ",
                SourceUrl = "https://fixture.invalid/patch", Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
            using var http = new HttpClient(new FixtureContentHandler(bytes, bytes, bytes));
            var patches = new PatchService(http);
            var cacheFile = Path.Combine(root, "Cache/WDB/enUS/itemcache.wdb");
            WriteSimple(cacheFile, "stale");
            await patches.InstallAsync(root, patch);
            Check("patch install clears client cache",
                File.Exists(Path.Combine(root, "Data/patch-Z.MPQ")) && !Directory.Exists(Path.Combine(root, "Cache")));
            WriteSimple(cacheFile, "current");
            await patches.InstallAsync(root, patch);
            Check("already-valid patch keeps client cache", File.Exists(cacheFile));
            patches.Remove(root, patch);
            Check("patch removal clears client cache",
                !File.Exists(Path.Combine(root, "Data/patch-Z.MPQ")) && !Directory.Exists(Path.Combine(root, "Cache")));
            patches.Remove(root, patch);
            Check("removing an absent patch is a no-op", !Directory.Exists(Path.Combine(root, "Cache")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        return failures == 0 ? 0 : 2;
    }
}
