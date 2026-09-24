using Portalkeeper.Models;
using Portalkeeper.Models.Runtime;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

internal static partial class Program
{
    private static async Task<int> RunCheckpoint6()
    {
        var root = Path.Combine(Path.GetTempPath(), "pk-cp6-" + Guid.NewGuid().ToString("N"));
        var failures = 0;
        var checks = 0;
        void Check(string name, bool ok)
        {
            checks++; Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name); if (!ok) failures++;
        }
        void Reject(string name, Action action) => Check(name, Throws(action));
        async Task RejectAsync(string name, Func<Task> action)
        {
            try { await action(); Check(name, false); }
            catch { Check(name, true); }
        }
        try
        {
            var provided = Environment.GetEnvironmentVariable("PORTALKEEPER_CP5_EXE");
            var source = Path.Combine(root, "source");
            string sourceHash;
            bool verifiedSource;
            if (string.IsNullOrEmpty(provided))
            {
                sourceHash = CreateFixtureClient(source).Client.ExecutableSha256;
                verifiedSource = false;
            }
            else
            {
                sourceHash = CreateSupportedFixtureClient(source).Client.ExecutableSha256;
                verifiedSource = true;
            }

            RealmInfo Realm(string name, params string[] requirements) => new()
            {
                SchemaVersion = 1, Name = name, Address = "fixture.invalid", GameRealmName = "Fixture",
                Client = new() { RuntimeMode = ClientRuntimeMode.Isolated, ExecutableSha256 = sourceHash, Requirements = requirements }
            };
            var preparation = new RealmRuntimePreparationService(Path.Combine(root, "runtimes"));
            var resolver = new RealmRuntimeResolver(Path.Combine(root, "runtimes"));
            var manifests = new ManagedRuntimeManifestService();
            ManagedRuntimeManifest Load(string runtime) => manifests.Load(Path.Combine(runtime, ManagedRuntimeBuilder.ManifestRelativePath));

            Check("empty requirements parse as empty", RealmConfigurationService.ParseClientRequirements("").Count == 0);
            var single = RealmConfigurationService.ParseClientRequirements("protected-framexml");
            Check("single requirement parsed", single.Count == 1 && single[0] == ClientRequirements.ProtectedFrameXmlRequirement);
            var messy = RealmConfigurationService.ParseClientRequirements("  Protected-FrameXml ; protected-framexml, PROTECTED-FRAMEXML ");
            Check("requirements normalize case, dedupe and order", messy.Count == 1 && messy[0] == ClientRequirements.ProtectedFrameXmlRequirement);
            var mixed = RealmConfigurationService.ParseClientRequirements("protected-framexml another, third");
            Check("multi-token requirements preserved", mixed.Count == 3 && mixed[0] == "another" && mixed[1] == "protected-framexml" && mixed[2] == "third");
            Reject("control characters in requirements rejected", () => RealmConfigurationService.ParseClientRequirements("bad\u0001req"));

            var example = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "config/example.realm.conf"));
            var parser = new RealmConfigurationService();
            var parsed = parser.Parse(example.Replace("[Client]", "[Client]\nRuntimeMode=Isolated\nRequirements=protected-framexml"));
            Check("realm.conf requirements parsed into model", parsed.Client.Requirements.Count == 1 &&
                parsed.Client.Requirements[0] == ClientRequirements.ProtectedFrameXmlRequirement && parsed.Client.RequiresProtectedFrameXml);
            Reject("unknown realm.conf requirement fails closed", () => parser.Parse(example.Replace("[Client]", "[Client]\nRequirements=protected-framexml;mystery")));
            Check("unset requirements default empty", parser.Parse(example).Client.Requirements.Count == 0);

            Check("case-insensitive protected-framexml detection", new ClientRequirements
            { Requirements = new[] { "Protected-FrameXml" } }.RequiresProtectedFrameXml);
            Check("known requirement accepted", !Throws(() => new ClientRequirements
            { Requirements = new[] { "protected-framexml" } }.ThrowIfUnsupportedRequirement()));
            Reject("unknown requirement rejected", () => new ClientRequirements
            { Requirements = new[] { "mystery" } }.ThrowIfUnsupportedRequirement());

            var plain = Realm("Plain");
            var requirement = Realm("Requirement", ClientRequirements.ProtectedFrameXmlRequirement);
            var unknown = Realm("Unknown", "mystery");
            Check("capability maps generation 2", !RealmExecutableService.RequiresGeneration2(plain) && RealmExecutableService.RequiresGeneration2(requirement));

            Reject("PrepareAsync rejects unknown requirement", () => preparation.PrepareAsync(source, unknown).GetAwaiter().GetResult());
            Reject("RequireReady rejects unknown requirement", () => new RealmRuntimeResolver().ResolveEffectiveClientPath(source, unknown));
            Reject("SelectLaunchExecutable rejects unknown requirement", () => RealmExecutableService.SelectLaunchExecutable(source, source, unknown, new ManagedRuntimeManifest()));
            Reject("generation preparation rejects unknown requirement", () => new RealmExecutableService().Prepare(source, source, unknown));
            Reject("generation preparation refuses optional generation", () => new RealmExecutableService().Prepare(source, source, plain));

            if (!verifiedSource)
            {
                var storage = Path.Combine(root, "runtimes");
                await RejectAsync("generation requirement rejects non-verified source",
                    () => new RealmRuntimePreparationService(storage).PrepareAsync(source, requirement));
                Check("failed generation leaves no runtime", !Directory.Exists(new RealmRuntimeResolver(storage).GetRuntimePath(requirement)));
            }

            var runtime = await preparation.PrepareAsync(source, plain);
            var manifest = Load(runtime);
            var baseline = Path.Combine(runtime, plain.Client.Executable);
            Check("plain Isolated runtime ready", resolver.ResolveEffectiveClientPath(source, plain) == runtime);
            Check("plain runtime has no generation 2 record", manifest.RealmExecutable is null);
            Check("plain runtime keeps copied baseline", File.Exists(baseline) && Hash(baseline) == sourceHash);
            Check("plain runtime validates", new ManagedRuntimeValidator().Validate(runtime, plain, source).IsValid);
            Check("plain launch selects the baseline executable",
                RealmExecutableService.SelectLaunchExecutable(runtime, source, plain, manifest) == baseline);
            Check("plain runtime reused without regrowth", await preparation.PrepareAsync(source, plain) == runtime);

            if (OperatingSystem.IsLinux())
                TestCheckpoint6Launch(root, runtime, source, plain, Check);
            else if (OperatingSystem.IsWindows())
            {
                var start = RealmLaunchService.CreateLaunchStartInfo(baseline, runtime, source, plain.Client.Executable);
                Check("Windows baseline launch uses runtime cwd", start.FileName == baseline && start.WorkingDirectory == runtime && !start.UseShellExecute);
            }

            if (verifiedSource)
            {
                var cycle = Realm("Cycle", ClientRequirements.ProtectedFrameXmlRequirement);
                var cycleRuntime = await preparation.PrepareAsync(source, cycle);
                var cycleManifest = Load(cycleRuntime);
                var gen2 = RuntimePaths.Resolve(cycleRuntime, cycleManifest.RealmExecutable!.RuntimeRelativePath);
                var cycleBaseline = Path.Combine(cycleRuntime, cycle.Client.Executable);
                var gen2Bytes = File.ReadAllBytes(gen2);
                Check("requirement realm prepares generation 2", cycleManifest.RealmExecutable is { Generation: 2 } && Hash(gen2) == FrameXmlDigestOverrideRecipe.OutputSha256);
                Check("requirement launch selects generation 2",
                    RealmExecutableService.SelectLaunchExecutable(cycleRuntime, source, cycle, cycleManifest) == gen2);
                Check("requirement realm reuses runtime", await preparation.PrepareAsync(source, cycle) == cycleRuntime);

                var downgraded = Realm("Cycle");
                Check("requirement change drills the configuration fingerprint",
                    RealmRuntimePreparationService.ConfigurationKey(downgraded) != RealmRuntimePreparationService.ConfigurationKey(cycle));
                Check("downgrade prepares the same runtime", await preparation.PrepareAsync(source, downgraded) == cycleRuntime);
                var downgradedManifest = Load(cycleRuntime);
                Check("downgrade preserves the generation 2 artifact", File.Exists(gen2) &&
                    File.ReadAllBytes(gen2).SequenceEqual(gen2Bytes) && downgradedManifest.RealmExecutable is { Generation: 2 });
                Check("downgrade selects the copied baseline",
                    RealmExecutableService.SelectLaunchExecutable(cycleRuntime, source, downgraded, downgradedManifest) == cycleBaseline);
                Check("downgrade resolves ready", resolver.ResolveEffectiveClientPath(source, downgraded) == cycleRuntime);

                var upgraded = Realm("Cycle", ClientRequirements.ProtectedFrameXmlRequirement);
                Check("re-upgrade prepares the same runtime", await preparation.PrepareAsync(source, upgraded) == cycleRuntime);
                Check("re-upgrade selects generation 2 again",
                    RealmExecutableService.SelectLaunchExecutable(cycleRuntime, source, upgraded, Load(cycleRuntime)) == gen2);
            }
            else
            {
                Console.WriteLine("SKIP  generation 2 lifecycle tests: set PORTALKEEPER_CP5_EXE");
            }

            Console.WriteLine($"Checkpoint 6: {checks} checks, {failures} failure(s)");
            return failures == 0 ? 0 : 2;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 2; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void TestCheckpoint6Launch(string root, string runtime, string source, RealmInfo realm, Action<string, bool> check)
    {
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var bin = Path.Combine(root, "cp6-bin"); Directory.CreateDirectory(bin);
            var wine = Path.Combine(bin, "wine");
            File.WriteAllText(wine, "#!/bin/sh\nexit 0\n");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(wine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("PATH", bin);
            var launch = new RealmLaunchService().PrepareAndLaunch(runtime, realm, source);
            launch.Process!.WaitForExit(); launch.Process.Dispose();
            check("actual launch invokes selected baseline executable", launch.ExecutablePath == Path.Combine(runtime, realm.Client.Executable));
        }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
    }
}