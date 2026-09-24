using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

internal static partial class Program
{
    private static async Task<int> RunCheckpoint3()
    {
        var root = Path.Combine(Path.GetTempPath(), "pk-cp3-" + Guid.NewGuid().ToString("N"));
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name); if (!ok) failures++; }
        async Task Reject(string name, Func<Task> action)
        {
            try { await action(); Check(name, false); }
            catch (Exception ex) { Check(name + " (" + ex.Message.Split('\n')[0] + ")", true); }
        }
        try
        {
            var source = Path.Combine(root, "source");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PORTALKEEPER_CP5_EXE")))
            {
                Console.WriteLine("SKIP  isolated preparation regression suite requires PORTALKEEPER_CP5_EXE under the exact CP5 source policy");
                return 0;
            }
            var legacy = CreateSupportedFixtureClient(source);
            var storage = Path.Combine(root, "runtimes");
            var resolver = new RealmRuntimeResolver(storage);
            var patchBytes = new byte[] { 77, 80, 81, 65 };
            var ptrBytes = new byte[] { 77, 80, 81, 66 };
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("RealmAddon/RealmAddon.toc").Open()))
                writer.Write("## Version: 1.0\n");
            using var http = new HttpClient(new FixtureContentHandler(patchBytes, ptrBytes, archive.ToArray()));
            var patches = new PatchService(http);
            var installer = new AddonInstallerService(http);
            var preparation = new RealmRuntimePreparationService(storage, patches, installer);
            RealmInfo Realm(string name, bool bad = false, bool badAddon = false) => new()
            {
                SchemaVersion = 1, Name = name, Address = name.ToLowerInvariant() + ".invalid", GameRealmName = name,
                Client = new ClientRequirements { RuntimeMode = ClientRuntimeMode.Isolated,
                    ExecutableSha256 = legacy.Client.ExecutableSha256 },
                Patches = new[] { new PatchDefinition { Id = "native", Name = name + " native", Requirement = ComponentRequirement.Required,
                    InstallMode = PatchInstallMode.WowPatch, SourceUrl = "https://fixture.invalid/" + (bad ? "fail" : name == "PTR" ? "ptr" : "patch"),
                    Sha256 = Convert.ToHexString(SHA256.HashData(name == "PTR" ? ptrBytes : patchBytes)) } },
                Addons = new[] { new AddonDefinition { Id = "realm-addon", Folder = "RealmAddon", Name = "Realm addon", Required = true,
                    DownloadUrl = "https://fixture.invalid/" + (badAddon ? "addon-fail" : "addon") } }
            };
            var a = Realm("Eitrigg"); var b = Realm("PTR");
            // Reproduce historic shared-source ownership and custom content.
            await patches.InstallAsync(source, a.Patches[0], a);
            await patches.InstallAsync(source, b.Patches[0], b);
            Check("shared source reproduces independent owners in patch-4 and patch-5",
                File.Exists(Path.Combine(source, "Data/patch-4.MPQ")) && File.Exists(Path.Combine(source, "Data/patch-5.MPQ")));
            var before = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                .ToDictionary(p => Path.GetRelativePath(source, p), Hash);
            var witnesses = Path.Combine(root, "identity-witnesses");
            Directory.CreateDirectory(witnesses);
            var links = new HardLinkService();
            var identities = new Dictionary<string, string>();
            foreach (var relative in before.Keys)
            {
                var witness = Path.Combine(witnesses, identities.Count.ToString());
                if (!links.TryCreateHardLink(Path.Combine(source, relative), witness, out _)) throw new IOException("Fixture link failed");
                identities[relative] = witness;
            }
            Check("legacy resolves source without runtime creation", await preparation.PrepareAsync(source, legacy) == source && !Directory.Exists(storage));
            await Reject("missing isolated runtime fails closed", () => Task.FromResult(resolver.ResolveEffectiveClientPath(source, a)));
            var runtimeA = await preparation.PrepareAsync(source, a);
            var runtimeB = await preparation.PrepareAsync(source, b);
            foreach (var (realm, runtime, other) in new[] { (a, runtimeA, b), (b, runtimeB, a) })
            {
                Check(realm.Name + " resolves complete provisioned runtime", resolver.ResolveEffectiveClientPath(source, realm) == runtime);
                Check(realm.Name + " independently allocates patch-4", patches.Destination(runtime, realm.Patches[0], realm).EndsWith("patch-4.MPQ"));
                Check(realm.Name + " has no patch-5 from source", !File.Exists(Path.Combine(runtime, "Data/patch-5.MPQ")));
                var ledger = File.ReadAllText(Path.Combine(runtime, WowPatchAllocationStore.StateRelativePath));
                Check(realm.Name + " ledger contains only its own identity", ledger.Contains(RealmIdentity.FromRealm(realm)) && !ledger.Contains(RealmIdentity.FromRealm(other)));
                Check(realm.Name + " addon is runtime-local", File.Exists(Path.Combine(runtime, "Interface/AddOns/RealmAddon/RealmAddon.toc")) &&
                    !Directory.Exists(Path.Combine(source, "Interface/AddOns/RealmAddon")));
                Check(realm.Name + " baseline shares source identity", links.AreSameFile(Path.Combine(source, "Data/common.MPQ"), Path.Combine(runtime, "Data/common.MPQ")));
                Check(realm.Name + " excludes personal addons", !Directory.Exists(Path.Combine(runtime, "Interface/AddOns/MyPersonalAddon")));
            }
            Check("distinct realm payloads stay separate", Hash(Path.Combine(runtimeA, "Data/patch-4.MPQ")) != Hash(Path.Combine(runtimeB, "Data/patch-4.MPQ")));
            Check("reuse ready runtime", await preparation.PrepareAsync(source, a) == runtimeA);
            var bad = Realm("Failed", true);
            await Reject("failed provisioning", () => preparation.PrepareAsync(source, bad));
            Check("failed provisioning never promotes", !Directory.Exists(resolver.GetRuntimePath(bad)) && !Directory.EnumerateDirectories(storage).Any(p => p.Contains(".staging-")));
            await Reject("failed realm has no effective fallback", () => Task.FromResult(resolver.ResolveEffectiveClientPath(source, bad)));
            var addonFailure = Realm("AddonFailure", badAddon: true);
            await Reject("failed addon provisioning after successful patch install", () => preparation.PrepareAsync(source, addonFailure));
            Check("addon failure never promotes runtime", !Directory.Exists(resolver.GetRuntimePath(addonFailure)));
            var brokenSource = Path.Combine(root, "broken-source");
            var brokenRealm = CreateFixtureClient(brokenSource);
            File.Delete(Path.Combine(brokenSource, "Data/common.MPQ"));
            var brokenBefore = Directory.GetFiles(brokenSource, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);
            await Reject("failed baseline construction", () => new ManagedRuntimeBuilder().BuildAsync(new()
                { SourceClientPath = brokenSource, Realm = brokenRealm, RuntimeRoot = Path.Combine(root, "broken-runtimes") }));
            Check("construction failure preserves source", brokenBefore.All(e => Hash(e.Key) == e.Value));
            var baselineRealm = Realm("Baseline");
            var baseline = new ManagedRuntimeBuilder().Build(new() { SourceClientPath = source, Realm = baselineRealm, RuntimeRoot = storage });
            await Reject("Checkpoint 2 baseline is not realm-ready", () => Task.FromResult(resolver.ResolveEffectiveClientPath(source, baselineRealm)));
            Check("existing Checkpoint 2 baseline provisions safely", await preparation.PrepareAsync(source, baselineRealm) == baseline.RuntimePath);
            var failedExisting = Realm("ExistingFailure", badAddon: true);
            var existing = new ManagedRuntimeBuilder().Build(new() { SourceClientPath = source, Realm = failedExisting, RuntimeRoot = storage });
            await Reject("failed provisioning of existing baseline", () => preparation.PrepareAsync(source, failedExisting));
            await Reject("existing failed baseline remains unavailable", () => Task.FromResult(resolver.ResolveEffectiveClientPath(source, failedExisting)));
            Check("existing failed baseline is preserved", Directory.Exists(existing.RuntimePath));
            var conflict = new PatchDefinition { Id = "conflict", Name = "conflict", InstallDirectory = "Data", FileName = "common.MPQ", SourceUrl = "https://fixture.invalid/patch" };
            await Reject("patch cannot overwrite linked baseline", () => patches.InstallAsync(runtimeA, conflict, a));
            await Reject("patch cannot remove linked baseline", () => { patches.Remove(runtimeA, conflict, a); return Task.CompletedTask; });
            await Reject("addon cannot remove baseline directory", () => { installer.Remove(runtimeA, new() { Id = "baseline", Folder = "Blizzard_AchievementUI" }); return Task.CompletedTask; });
            File.Delete(Path.Combine(runtimeA, "Data/common.MPQ"));
            await Reject("invalid runtime resolver rejection", () => Task.FromResult(resolver.ResolveEffectiveClientPath(source, a)));
            await Reject("invalid runtime requires explicit repair", () => preparation.PrepareAsync(source, a));
            Check("invalid runtime retained", Directory.Exists(runtimeA));
            var after = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(source, p), Hash);
            Check("source file set and every byte unchanged", before.Count == after.Count && before.All(e => after.TryGetValue(e.Key, out var hash) && hash == e.Value));
            Check("source file identities unchanged", identities.All(e => links.AreSameFile(Path.Combine(source, e.Key), e.Value)));
            var example = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "config/example.realm.conf"));
            var parser = new RealmConfigurationService();
            Check("missing mode defaults legacy", parser.Parse(example).Client.RuntimeMode == ClientRuntimeMode.Legacy);
            Check("isolated mode parsed", parser.Parse(example.Replace("[Client]", "[Client]\nRuntimeMode=Isolated")).Client.RuntimeMode == ClientRuntimeMode.Isolated);
            foreach (var mode in new[] { "", "1", "Unknown" })
                await Reject("invalid mode rejected: " + mode, () => Task.FromResult(parser.Parse(example.Replace("[Client]", "[Client]\nRuntimeMode=" + mode))));
            if (OperatingSystem.IsLinux()) TestWine(root, source, runtimeB, b, Check);
            Check("launch leaves source bytes unchanged", before.All(e => Hash(Path.Combine(source, e.Key)) == e.Value));
            Console.WriteLine($"Checkpoint 3: {failures} failure(s)");
            return failures == 0 ? 0 : 2;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 2; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void TestWine(string root, string source, string runtime, RealmInfo realm, Action<string, bool> check)
    {
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldPrefix = Environment.GetEnvironmentVariable("WINEPREFIX");
        var oldXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            var bin = Path.Combine(root, "bin"); Directory.CreateDirectory(bin); File.WriteAllText(Path.Combine(bin, "wine"), "#!/bin/sh\nexit 0\n");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Path.Combine(bin, "wine"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var prefix = Path.Combine(root, "known-good-prefix"); Directory.CreateDirectory(prefix);
            var xdg = Path.Combine(root, "desktop-data"); Directory.CreateDirectory(Path.Combine(xdg, "applications"));
            File.WriteAllText(Path.Combine(xdg, "applications/wow.desktop"), $"[Desktop Entry]\nExec=env WINEPREFIX={prefix} wine {source}/Wow.exe\n");
            Environment.SetEnvironmentVariable("PATH", bin);
            Environment.SetEnvironmentVariable("WINEPREFIX", null);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdg);
            var legacy = RealmLaunchService.CreateLaunchStartInfo(Path.Combine(source, "Wow.exe"), source);
            var isolated = RealmLaunchService.CreateLaunchStartInfo(Path.Combine(runtime, "Wow.exe"), runtime, source);
            check("legacy Linux source discovers desktop prefix", legacy.Environment["WINEPREFIX"] == prefix);
            check("isolated inherits source Wine executable and prefix", isolated.FileName == legacy.FileName && isolated.Environment["WINEPREFIX"] == prefix);
            check("isolated executes runtime with runtime working directory", isolated.ArgumentList.Single() == Path.Combine(runtime, "Wow.exe") && isolated.WorkingDirectory == runtime);
            var launch = new RealmLaunchService().PrepareAndLaunch(runtime, realm, source);
            launch.Process!.WaitForExit(); launch.Process.Dispose();
            check("launch realmlist and WTF are runtime-local",
                File.ReadAllText(Path.Combine(runtime, "Data/enUS/realmlist.wtf")).Contains(realm.Address) &&
                File.ReadAllText(Path.Combine(runtime, "WTF/Config.wtf")).Contains(realm.GameRealmName) &&
                !Directory.Exists(Path.Combine(source, "WTF")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath); Environment.SetEnvironmentVariable("WINEPREFIX", oldPrefix);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldXdg);
        }
    }

    private sealed class FixtureContentHandler(byte[] patch, byte[] ptr, byte[] addon) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(request.RequestUri!.AbsolutePath is "/fail" or "/addon-fail" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new ByteArrayContent(request.RequestUri.AbsolutePath == "/addon" ? addon : request.RequestUri.AbsolutePath == "/ptr" ? ptr : patch) });
    }
}
