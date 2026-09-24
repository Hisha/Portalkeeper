using System.Security.Cryptography;
using System.Text;
using Portalkeeper.Models;
using Portalkeeper.Models.Runtime;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

// Checkpoint 2 focused test harness.
//
//   Portalkeeper.RuntimeTests                          -> fixture battery
//   Portalkeeper.RuntimeTests real --source <path> --root <root>
//                  [--realm-conf <existing-realm-file>] -> build + validate a real
//                  3.3.5a client into a Portalkeeper-owned runtime
//
// The battery runs without a real WoW client (synthetic fixture client). The
// `real` subcommand exercises the actual client + realm wiring end to end.
internal static partial class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "real" or "--real")
            return RunRealClient(args);

        if (args.Length > 0 && args[0] == "checkpoint3")
            return RunCheckpoint3().GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == "checkpoint5-crash")
            return RunCheckpoint5Crash(args);
        if (args.Length > 0 && args[0] == "checkpoint5")
            return RunCheckpoint5().GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == "checkpoint4")
            return RunCheckpoint4().GetAwaiter().GetResult();
        return RunBattery();
    }

    private static int RunBattery()
    {
        var root = Path.Combine(Path.GetTempPath(), "pk-cp2-" + Guid.NewGuid().ToString("N")[..8]);
        var source = Path.Combine(root, "source");
        var runtimeRoot1 = Path.Combine(root, "runtime-root-1");
        var runtimeRoot2 = Path.Combine(root, "runtime-root-2");
        var failures = 0;

        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (ok || detail is null ? "" : "  (" + detail + ")"));
            if (!ok)
                failures++;
        }

        try
        {
            var realm = CreateFixtureClient(source);
            var builder = new ManagedRuntimeBuilder();
            var validator = new ManagedRuntimeValidator();
            var hardLinks = new HardLinkService();

            var resolvedSource = Path.GetFullPath(source);

            Console.WriteLine("== Checkpoint 2 fixture battery ==");
            Console.WriteLine("fixture source : " + resolvedSource);

            // 1. Construction succeeds.
            ManagedRuntimeBuildResult result;
            try { result = builder.Build(new ManagedRuntimeBuildOptions { SourceClientPath = source, Realm = realm, RuntimeRoot = runtimeRoot1 }); }
            catch (Exception ex) { Console.WriteLine(ex); Check("construct runtime", false); return 1; }
            var final = result.RuntimePath;
            var expectedFinal = RuntimePaths.RuntimeRootOfRealm(runtimeRoot1, RealmIdentity.FromRealm(realm));
            Check("construct runtime", Directory.Exists(final));
            Check("result runtime path is the computed final path", RuntimePaths.SamePath(final, expectedFinal));
            Check("runtime stores manifest", File.Exists(Path.Combine(final, ".portalkeeper", "managed-runtime.json")));

            // 2. Staging is cleaned / final promoted (no staging dirs remain).
            var staging = Directory.EnumerateDirectories(runtimeRoot1, "*staging*").ToArray();
            Check("no staging directories remain", staging.Length == 0);

            // 3. Unknown content is never inherited.
            foreach (var (label, rel) in new (string, string)[]
            {
                ("unknown root file", "random-user-file.txt"),
                ("unknown data patch", "Data/patch-99.MPQ"),
                ("unknown locale patch", "Data/enUS/patch-enUS-777.MPQ"),
                ("personal addon folder", "Interface/AddOns/MyPersonalAddon"),
                ("third-party addon folder", "Interface/AddOns/Dominos"),
            })
            {
                Check("runtime excludes " + label, !File.Exists(Path.Combine(final, rel)) && !Directory.Exists(Path.Combine(final, rel)));
                Check("source still has " + label, File.Exists(Path.Combine(source, rel)) || Directory.Exists(Path.Combine(source, rel)));
            }

            // 4. Source client is untouched (unchanged executable bytes).
            var sourceWow = Path.Combine(source, "Wow.exe");
            Check("source Wow.exe unchanged", Hash(sourceWow) == realm.Client.ExecutableSha256);

            // 5. Copied root baseline is byte-identical but independent.
            var runtimeWow = Path.Combine(final, "Wow.exe");
            Check("runtime Wow.exe matches realm hash", Hash(runtimeWow) == realm.Client.ExecutableSha256);
            Check("root baseline copied (not hard-linked)", !hardLinks.AreSameFile(sourceWow, runtimeWow));

            // 6. Data / locale / addon baselines are genuine hard links.
            Check("data baseline is a hard link", hardLinks.AreSameFile(Path.Combine(source, "Data", "common.MPQ"), Path.Combine(final, "Data", "common.MPQ")));
            Check("locale baseline is a hard link", hardLinks.AreSameFile(Path.Combine(source, "Data", "enUS", "locale-enUS.MPQ"), Path.Combine(final, "Data", "enUS", "locale-enUS.MPQ")));
            Check("baseline addon link (stub)", hardLinks.AreSameFile(
                Path.Combine(source, "Interface", "AddOns", "Blizzard_AchievementUI", "Blizzard_AchievementUI.pub"),
                Path.Combine(final, "Interface", "AddOns", "Blizzard_AchievementUI", "Blizzard_AchievementUI.pub")));

            // 7. Manifest accuracy.
            var manifest = new ManagedRuntimeManifestService().Load(Path.Combine(final, ".portalkeeper", "managed-runtime.json"));
            Check("manifest state is Complete", manifest.State == ManagedRuntimeState.Complete);
            Check("manifest realm id", manifest.RealmId == RealmIdentity.FromRealm(realm));
            Check("manifest realm name", manifest.RealmName == realm.Name);
            Check("manifest locale is enUS", manifest.Locale == "enUS");
            Check("manifest runtime path", RuntimePaths.SamePath(manifest.RuntimePath, expectedFinal));
            Check("manifest launch exe", manifest.LaunchExecutableRelativePath == "Wow.exe");
            Check("manifest source path", RuntimePaths.SamePath(manifest.SourceClientPath, resolvedSource));
            var wowEntry = manifest.Files.FirstOrDefault(f => string.Equals(f.RuntimeRelativePath, "Wow.exe", StringComparison.OrdinalIgnoreCase));
            Check("Wow.exe recorded as CopiedBaseline with realm hash",
                wowEntry is not null && wowEntry.Kind == ManagedRuntimeFileKind.CopiedBaseline &&
                wowEntry.Sha256 == realm.Client.ExecutableSha256);
            var commonEntry = manifest.Files.FirstOrDefault(f => string.Equals(f.RuntimeRelativePath, "Data/common.MPQ", StringComparison.OrdinalIgnoreCase));
            Check("Data/common.MPQ recorded as LinkedBaseline",
                commonEntry is not null && commonEntry.Kind == ManagedRuntimeFileKind.LinkedBaseline);
            // Root 8 + data 7 + locale (6 required + 5 optional) + 3 baseline addon stubs present.
            Check("manifest file entries complete (29)", manifest.Files.Count == 29, $"got {manifest.Files.Count}");

            // 8. Validator accepts the promoted runtime.
            var okCheck = validator.Validate(final, realm, source, expectedFinalRuntimePath: expectedFinal);
            Check("validator accepts complete runtime", okCheck.IsValid, string.Join(", ", okCheck.Errors));

            // 9. Incomplete/staging state is never treated as launchable.
            var badComplete = Directory.Exists(final);
            if (badComplete)
            {
                var tampered = Path.Combine(final, "Data", "common.MPQ");
                File.Delete(tampered);
                var badResult = validator.Validate(final, realm, source, expectedFinalRuntimePath: expectedFinal);
                Check("missing linked baseline fails validation", !badResult.IsValid &&
                    badResult.Errors.Any(e => e.Contains("does not exist", StringComparison.OrdinalIgnoreCase)));
            }

            // 10. Missing manifest fails validation.
            var bare = Path.Combine(root, "bare-runtime");
            Directory.CreateDirectory(Path.Combine(bare, "Data", "enUS"));
            File.WriteAllBytes(Path.Combine(bare, "Wow.exe"), CreateFixtureExecutableBytes());
            var bareResult = validator.Validate(bare, realm, source);
            Check("runtime without manifest fails validation", !bareResult.IsValid &&
                bareResult.Errors.Any(e => e.Contains("manifest is missing", StringComparison.OrdinalIgnoreCase)));

            // 11. Escape / traversal paths are rejected at the unit level.
            Check("ManagedManifest rejects traversal",
                Throws(() => new ManagedRuntimeManifestService().Validate(
                    new ManagedRuntimeManifest
                    {
                        Generation = "1",
                        State = ManagedRuntimeState.Complete,
                        RealmId = RealmIdentity.FromRealm(realm),
                        RealmName = realm.Name,
                        SourceClientPath = resolvedSource,
                        RuntimePath = expectedFinal,
                        LaunchExecutableRelativePath = "Wow.exe",
                        Files = new List<ManagedRuntimeFileEntry>
                        {
                            new() { RuntimeRelativePath = "../escape", SourceRelativePath = "Data/common.MPQ", Kind = ManagedRuntimeFileKind.LinkedBaseline }
                        }
                    })));
            Check("RuntimePaths rejects escape", Throws(() => RuntimePaths.Resolve(root, "../..")));
            Check("ManagedPath rejects traversal", Throws(() => ManagedPath.Relative("../escape", fileName: true)));
            Check("RuntimePaths rejects nested runtime inside source",
                Throws(() => builder.Build(new ManagedRuntimeBuildOptions { SourceClientPath = source, Realm = realm, RuntimeRoot = Path.Combine(source, "nested-root") })));

            // 12. An existing runtime is never overwritten.
            var dupThrew = Throws(
                () => builder.Build(new ManagedRuntimeBuildOptions { SourceClientPath = source, Realm = realm, RuntimeRoot = runtimeRoot1 }),
                out var dupEx);
            Check("second build refuses existing runtime",
                dupThrew && dupEx?.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true);

            // 13. Resolver behavior is unchanged (source-only, no runtime switching).
            var resolver = new RealmRuntimeResolver();
            Check("ResolveEffectiveClientPath still returns the source client",
                RuntimePaths.SamePath(resolver.ResolveEffectiveClientPath(resolvedSource, realm), resolvedSource));

            // 14. Hard-link service primitives.
            var volHome = Environment.GetEnvironmentVariable("PORTALKEEPER_TEST_CROSS_VOLUME_ROOT")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var srcPrime = Path.Combine(root, "link-src.bin");
            var dstPrime = Path.Combine(root, "link-dst.bin");
            File.WriteAllText(srcPrime, "prime");
            var linkOk = hardLinks.TryCreateHardLink(srcPrime, dstPrime, out var linkFail1);
            Check("hard link creation succeeds on same volume", linkOk);
            Check("linked files share identity", linkOk && hardLinks.AreSameFile(srcPrime, dstPrime));
            var destExistsOk = hardLinks.TryCreateHardLink(srcPrime, dstPrime, out var linkFail2);
            Check("hard link to an existing destination reports DestinationExists",
                !destExistsOk && linkFail2 == HardLinkFailure.DestinationExists);
            var sameVolume = hardLinks.SameFilesystem(root, root);
            Check("same-filesystem detection (temp root)", sameVolume == true);
            if (Directory.Exists(volHome))
            {
                var crossVolume = hardLinks.SameFilesystem(root, volHome);
                if (crossVolume == false)
                {
                    var crossProbe = Path.Combine(volHome, "pk-cp2-cross-" + Guid.NewGuid().ToString("N")[..6] + ".bin");
                    var crossOk = hardLinks.TryCreateHardLink(srcPrime, crossProbe, out var crossFail);
                    try { if (File.Exists(crossProbe)) File.Delete(crossProbe); } catch { }
                    Check("cross-volume hard link refused with CrossDevice", !crossOk && crossFail == HardLinkFailure.CrossDevice);
                }
                else
                {
                    Console.WriteLine("SKIP  cross-volume hard link (temp root and home share a filesystem here)");
                }
            }

            // 15. Cross-filesystem runtime construction refusal (fixture on tmpfs,
            //     runtime root in home on this machine). Skipped when same volume.
            var otherHomeRoot = Path.Combine(volHome, "pk-cp2-home-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                if (hardLinks.SameFilesystem(root, volHome) == false)
                {
                    try
                    {
                        builder.Build(new ManagedRuntimeBuildOptions { SourceClientPath = source, Realm = realm, RuntimeRoot = otherHomeRoot });
                        Check("cross-filesystem construction refused without copy fallback", false);
                    }
                    catch (Exception ex)
                    {
                        Check("cross-filesystem construction refused without copy fallback",
                            ex.Message.Contains("different", StringComparison.OrdinalIgnoreCase) ||
                            ex.Message.Contains("filesystem", StringComparison.OrdinalIgnoreCase));
                        Check("no runtime was created at the cross-filesystem target",
                            !Directory.Exists(RuntimePaths.RuntimeRootOfRealm(otherHomeRoot, RealmIdentity.FromRealm(realm))));
                    }
                }
                else
                {
                    Console.WriteLine("SKIP  cross-filesystem runtime refusal (single filesystem in this environment)");
                }
            }
            finally
            {
                if (Directory.Exists(otherHomeRoot))
                {
                    try { Directory.Delete(otherHomeRoot, recursive: true); } catch { }
                }
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
            return failures == 0 ? 0 : 2;
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static int RunRealClient(string[] args)
    {
        string GetArg(string name, string fallback)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        var source = GetArg("--source", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var root = GetArg("--root", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Portalkeeper-runtime-test"));
        var realmConf = GetArg("--realm-conf", Path.Combine(RealmConfigurationStore.DefaultDirectory, "eitrigg.realm.conf"));

        if (!Directory.Exists(source))
        {
            Console.Error.WriteLine("Source client path does not exist: " + source);
            return 2;
        }
        if (!Directory.Exists(root))
            Directory.CreateDirectory(root);

        var store = new RealmConfigurationStore();
        var (realm, status) = store.LoadAsync(realmConf, refresh: false).GetAwaiter().GetResult();
        if (realm is null)
        {
            Console.Error.WriteLine("Realm configuration could not be loaded: " + realmConf + "\n" + status);
            return 2;
        }

        Console.WriteLine("== Real-client runtime construction ==");
        Console.WriteLine("source      : " + source);
        Console.WriteLine("runtime root: " + root);
        Console.WriteLine("realm       : " + realm.Name + " (" + realm.Address + ":" + realm.AuthPort + ")");
        Console.WriteLine("realm conf  : " + realmConf);
        Console.WriteLine("client      : " + realm.Client.Version + " build " + realm.Client.Build +
            " sha256 " + realm.Client.ExecutableSha256);
        Console.WriteLine("realm config: " + status);

        var builder = new ManagedRuntimeBuilder();
        var validator = new ManagedRuntimeValidator();

        try
        {
            var result = builder.Build(new ManagedRuntimeBuildOptions
            {
                SourceClientPath = source,
                Realm = realm,
                RuntimeRoot = root,
            });

            Console.WriteLine();
            Console.WriteLine("BUILD OK");
            Console.WriteLine("runtime path : " + result.RuntimePath);
            Console.WriteLine("locale       : " + result.Locale);
            Console.WriteLine("file entries : " + result.Manifest.Files.Count);
            Console.WriteLine("skipped      : " + result.SkippedAssets.Count);
            foreach (var s in result.SkippedAssets)
                Console.WriteLine("  skip " + s);

            var check = validator.Validate(result.RuntimePath, realm, source, expectedFinalRuntimePath: result.RuntimePath);
            Console.WriteLine("validation   : " + (check.IsValid ? "PASS" : "FAIL"));
            foreach (var e in check.Errors)
                Console.WriteLine("  " + e);
            return check.IsValid ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("BUILD FAILED");
            Console.WriteLine(ex);
            return 2;
        }
    }

    private static RealmInfo CreateFixtureClient(string source)
    {
        var rootFiles = new[] { "WowError.exe", "Battle.net.dll", "DivxDecoder.dll", "dbghelp.dll", "ijl15.dll", "msvcr80.dll", "unicows.dll" };
        foreach (var f in rootFiles)
            WriteSimple(Path.Combine(source, f), f);

        var exeBytes = CreateFixtureExecutableBytes();
        File.WriteAllBytes(Path.Combine(source, "Wow.exe"), exeBytes);

        foreach (var data in new[] { "common.MPQ", "common-2.MPQ", "expansion.MPQ", "lichking.MPQ", "patch.MPQ", "patch-2.MPQ", "patch-3.MPQ" })
            WriteSimple(Path.Combine(source, "Data", data), data);
        WriteSimple(Path.Combine(source, "Data", "patch-99.MPQ"), "unknown-data");

        foreach (var loc in new[] { "locale-enUS.MPQ", "expansion-locale-enUS.MPQ", "lichking-locale-enUS.MPQ", "patch-enUS.MPQ", "patch-enUS-2.MPQ", "patch-enUS-3.MPQ" })
            WriteSimple(Path.Combine(source, "Data", "enUS", loc), loc);
        foreach (var loc in new[] { "speech-enUS.MPQ", "expansion-speech-enUS.MPQ", "lichking-speech-enUS.MPQ", "base-enUS.MPQ", "backup-enUS.MPQ" })
            WriteSimple(Path.Combine(source, "Data", "enUS", loc), loc);
        WriteSimple(Path.Combine(source, "Data", "enUS", "patch-enUS-777.MPQ"), "unknown-locale");

        foreach (var addon in new[]
                 {
                     "Blizzard_AchievementUI", "Blizzard_AuctionUI", "Blizzard_Calendar"
                 })
            WriteSimple(Path.Combine(source, "Interface", "AddOns", addon, addon + ".pub"), addon);

        WriteSimple(Path.Combine(source, "Interface", "AddOns", "MyPersonalAddon", "MyPersonalAddon.toc"), "mine");
        WriteSimple(Path.Combine(source, "Interface", "AddOns", "Dominos", "Dominos.toc"), "third-party");
        WriteSimple(Path.Combine(source, "random-user-file.txt"), "personal-data");
        WriteSimple(Path.Combine(source, "Data", "enUS", "realmlist.wtf"), "set realmlist fixture.invalid");

        var exeHash = Convert.ToHexString(SHA256.HashData(exeBytes));

        return new RealmInfo
        {
            SchemaVersion = 1,
            Name = "Fixture Realm",
            GameRealmName = "Fixture",
            Description = "Synthetic realm for Checkpoint 2 tests.",
            Address = "fixture.invalid",
            AuthPort = 3724,
            WorldPort = 8085,
            Client = new ClientRequirements
            {
                Version = "3.3.5a",
                Build = "12340",
                Executable = "Wow.exe",
                ExecutableSha256 = exeHash
            }
        };
    }

    private static byte[] CreateFixtureExecutableBytes()
    {
        var markers = new[]
        {
            "World of WarCraft (build 12340)",
            "WoW [Release] Build 12340",
            "WOWCOMSATCLIENT12340",
            "<version>12340</version>",
        };
        var sb = new StringBuilder("MZ");
        sb.Append("Portalkeeper Checkpoint 2 fixture executable\0");
        foreach (var m in markers) sb.Append(m).Append('\0');
        sb.Append("3.3.5");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static void WriteSimple(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "Portalkeeper fixture: " + content + Environment.NewLine);
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static bool Throws(Action action)
    {
        try { action(); return false; } catch { return true; }
    }

    private static bool Throws(Action action, out Exception? exception)
    {
        try { action(); exception = null; return false; }
        catch (Exception ex) { exception = ex; return true; }
    }
}