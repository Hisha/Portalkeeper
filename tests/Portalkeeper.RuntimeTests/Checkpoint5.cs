using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Portalkeeper.Models;
using Portalkeeper.Models.Runtime;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

internal static partial class Program
{
    // Copies only the explicitly supplied local executable into disposable synthetic client trees.
    private static RealmInfo CreateSupportedFixtureClient(string source)
    {
        CreateFixtureClient(source);
        var input = Environment.GetEnvironmentVariable("PORTALKEEPER_CP5_EXE")
            ?? throw new InvalidOperationException("Set PORTALKEEPER_CP5_EXE to the verified local executable.");
        var bytes = File.ReadAllBytes(input);
        FrameXmlDigestOverrideRecipe.ValidateSource(bytes);
        File.WriteAllBytes(Path.Combine(source, "Wow.exe"), bytes);
        return new RealmInfo
        {
            SchemaVersion = 1, Name = "Fixture Realm", GameRealmName = "Fixture", Address = "fixture.invalid",
            Client = new() { ExecutableSha256 = FrameXmlDigestOverrideRecipe.SourceSha256 }
        };
    }

    private static byte[] SyntheticRecipeLayout()
    {
        var bytes = new byte[FrameXmlDigestOverrideRecipe.SourceSize];
        void W16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
        void W32(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), value);
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; W32(0x3C, 0x80);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
        W16(0x84, 0x14C); W16(0x86, 6); W16(0x94, 224);
        W16(0x98, 0x10B); W32(0x98 + 28, 0x400000);
        W32(0x98 + 32, 0x1000); W32(0x98 + 36, 0x200);
        W32(0x98 + 56, 0x800000); W32(0x98 + 60, 0x400);
        var section = 0x178;
        Encoding.ASCII.GetBytes(".text").CopyTo(bytes, section);
        W32(section + 8, 0x5DD3B3); W32(section + 12, 0x1000);
        W32(section + 16, 0x5DD400); W32(section + 20, 0x400);
        Convert.FromHexString("E5AB5200F2AB5200FFAB52001CAC5200").CopyTo(bytes, 0x12A2B4);
        Convert.FromHexString("FF2485B4AE5200").CopyTo(bytes, 0x129FDE);
        Convert.FromHexString("E80ABA2E00").CopyTo(bytes, 0x129FD1);
        return bytes;
    }

    private static int RunCheckpoint5Crash(string[] args)
    {
        // This child process must only operate inside its parent's disposable test fixture.
        if (args.Length != 6 || !Path.GetFileName(args[1]).StartsWith("pk-cp5-", StringComparison.Ordinal) ||
            !RuntimePaths.IsWithin(Path.GetTempPath(), args[1]) || !RuntimePaths.IsWithin(args[1], args[2]) ||
            !RuntimePaths.IsWithin(args[1], args[3])) return 74;
        var realm = new RealmInfo
        {
            SchemaVersion = 1, Name = args[4], Address = "fixture.invalid", GameRealmName = "Fixture",
            Client = new() { RuntimeMode = ClientRuntimeMode.Isolated, ExecutableSha256 = FrameXmlDigestOverrideRecipe.SourceSha256 }
        };
        new RealmExecutableService(at => { if (at == args[5]) Environment.Exit(73); }).Prepare(args[3], args[2], realm);
        return 75;
    }

    private static async Task<int> RunCheckpoint5()
    {
        var root = Path.Combine(Path.GetTempPath(), "pk-cp5-" + Guid.NewGuid().ToString("N"));
        var failures = 0;
        var checks = 0;
        void Check(string name, bool ok)
        {
            checks++; Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name); if (!ok) failures++;
        }
        void Reject(string name, Action action) => Check(name, Throws(action));
        try
        {
            var synthetic = SyntheticRecipeLayout();
            Check("synthetic PE guards accepted without transforming", !Throws(() => FrameXmlDigestOverrideRecipe.ValidateLayoutAndGuards(synthetic)));
            Reject("synthetic source hash rejected by production generation", () => FrameXmlDigestOverrideRecipe.Generate(synthetic));
            Reject("wrong source size rejected", () => FrameXmlDigestOverrideRecipe.Generate(new byte[42]));
            Reject("unexpected output identity rejected", () => FrameXmlDigestOverrideRecipe.ValidateOutput(synthetic));
            void BadLayout(string name, Action<byte[]> mutate)
            {
                var bytes = (byte[])synthetic.Clone(); mutate(bytes);
                Reject(name, () => FrameXmlDigestOverrideRecipe.ValidateLayoutAndGuards(bytes));
            }
            BadLayout("wrong PE machine", b => b[0x84] = 0x64);
            BadLayout("wrong PE optional type", b => b[0x98] = 0x0B + 1);
            BadLayout("missing text section", b => b[0x178] = (byte)'x');
            BadLayout("wrong text RVA", b => b[0x178 + 13] ^= 1);
            BadLayout("RVA-to-offset mapping mismatch", b => b[0x178 + 20] ^= 1);
            BadLayout("unbacked text mapping", b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x178 + 16), 0x100));
            BadLayout("ambiguous RVA mapping", b =>
            {
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x1A0 + 12), 0x1000);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x1A0 + 8), 0x5DD400);
            });
            BadLayout("original entry mismatch", b => b[0x12A2BC] ^= 1);
            BadLayout("surrounding table mismatch", b => b[0x12A2B4] ^= 1);
            BadLayout("dispatch instruction mismatch", b => b[0x129FDE] ^= 1);
            BadLayout("verifier call mismatch", b => b[0x129FD1] ^= 1);
            Reject("truncated PE", () => FrameXmlDigestOverrideRecipe.ValidateLayoutAndGuards(synthetic[..0x190]));

            // Unsupported-source policy is exercised without requiring any proprietary input.
            var unsupported = Path.Combine(root, "unsupported");
            var unsupportedLegacy = CreateFixtureClient(unsupported);
            var unsupportedRealm = new RealmInfo
            {
                Name = "Unsupported", Address = "fixture.invalid", GameRealmName = "Fixture",
                Client = new() { RuntimeMode = ClientRuntimeMode.Isolated, ExecutableSha256 = unsupportedLegacy.Client.ExecutableSha256 }
            };
            var unsupportedRoot = Path.Combine(root, "unsupported-runtimes");
            Reject("unsupported isolated source fails closed", () => new RealmRuntimePreparationService(unsupportedRoot).PrepareAsync(unsupported, unsupportedRealm).GetAwaiter().GetResult());
            Check("unsupported runtime never promoted", !Directory.Exists(new RealmRuntimeResolver(unsupportedRoot).GetRuntimePath(unsupportedRealm)));
            Check("Legacy unchanged for unsupported recipe source", await new RealmRuntimePreparationService(unsupportedRoot).PrepareAsync(unsupported, unsupportedLegacy) == unsupported);

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PORTALKEEPER_CP5_EXE")))
            {
                Console.WriteLine("SKIP  exact executable and lifecycle tests: set PORTALKEEPER_CP5_EXE");
                return failures == 0 ? 0 : 2;
            }
            var supplied = Environment.GetEnvironmentVariable("PORTALKEEPER_CP5_EXE")!;
            var originalHash = Hash(supplied);
            var originalTime = File.GetLastWriteTimeUtc(supplied);
            var source = Path.Combine(root, "source");
            CreateSupportedFixtureClient(source);
            var sourceExe = Path.Combine(source, "Wow.exe");
            var before = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);
            var sourceBytes = File.ReadAllBytes(sourceExe);
            var generated = FrameXmlDigestOverrideRecipe.Generate(sourceBytes);
            Check("exact output identity", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(generated)) == FrameXmlDigestOverrideRecipe.OutputSha256);
            Check("only two expected bytes differ", Enumerable.Range(0, sourceBytes.Length).Where(i => sourceBytes[i] != generated[i]).SequenceEqual(new[] { 0x12A2BC, 0x12A2BD }));
            Check("generation does not mutate input bytes", sourceBytes.SequenceEqual(File.ReadAllBytes(sourceExe)));
            Reject("already transformed source refused", () => FrameXmlDigestOverrideRecipe.Generate(generated));
            var damaged = (byte[])generated.Clone(); damaged[0x100] ^= 1;
            Reject("internally changed output rejected", () => FrameXmlDigestOverrideRecipe.ValidateOutput(damaged));

            var storage = Path.Combine(root, "runtimes");
            var prep = new RealmRuntimePreparationService(storage);
            var manifests = new ManagedRuntimeManifestService();
            var resolver = new RealmRuntimeResolver(storage);
            var links = new HardLinkService();
            RealmInfo Realm(string name) => new()
            {
                SchemaVersion = 1, Name = name, Address = "fixture.invalid", GameRealmName = "Fixture",
                Client = new() { RuntimeMode = ClientRuntimeMode.Isolated, ExecutableSha256 = FrameXmlDigestOverrideRecipe.SourceSha256 }
            };
            string ManifestPath(string runtime) => Path.Combine(runtime, ManagedRuntimeBuilder.ManifestRelativePath);
            ManagedRuntimeManifest Load(string runtime) => manifests.Load(ManifestPath(runtime));
            string Exe(string runtime) => Path.Combine(runtime, Load(runtime).RealmExecutable!.RuntimeRelativePath);
            string Baseline(string runtime) => Path.Combine(runtime, "Wow.exe");
            string Pending(string runtime) => Path.Combine(runtime, RealmExecutableService.PendingRelativePath);
            string Cp4(RealmInfo realm)
            {
                var result = new ManagedRuntimeBuilder().Build(new() { SourceClientPath = source, Realm = realm, RuntimeRoot = storage });
                var m = result.Manifest;
                var relative = RealmExecutableService.FileName(m.RealmName, m.RealmId);
                File.Copy(sourceExe, Path.Combine(result.RuntimePath, relative));
                m.RealmExecutable = new() { SourceRelativePath = "Wow.exe", SourceSha256 = FrameXmlDigestOverrideRecipe.SourceSha256,
                    RuntimeRelativePath = relative, Sha256 = FrameXmlDigestOverrideRecipe.SourceSha256 };
                m.Files.Add(new() { RuntimeRelativePath = relative, Kind = ManagedRuntimeFileKind.RealmOwned, Sha256 = FrameXmlDigestOverrideRecipe.SourceSha256 });
                m.LaunchExecutableRelativePath = relative;
                m.ProvisionedConfiguration = RealmRuntimePreparationService.ConfigurationKey(realm);
                manifests.Save(m, ManifestPath(result.RuntimePath));
                return result.RuntimePath;
            }
            var realm = Realm("CP4 migration");
            var runtime = Cp4(realm);
            var oldManifest = Load(runtime);
            var exe = Exe(runtime);
            Check("generation 1 still validates for migration", new ManagedRuntimeValidator().Validate(runtime, realm, source).IsValid);
            Reject("generation 1 cannot launch or fall back", () => new RealmLaunchService().PrepareAndLaunch(runtime, realm, source));
            foreach (var path in new[] { "Data/patch-4.MPQ", "Interface/AddOns/Personal/Personal.toc", "WTF/Config.wtf", "Data/enUS/realmlist.wtf" })
                WriteSimple(Path.Combine(runtime, path), "preserve CP4 state " + path);
            var preserved = Directory.GetFiles(runtime, "*", SearchOption.AllDirectories)
                .Where(p => p != exe && p != ManifestPath(runtime)).ToDictionary(p => p, p => (Hash(p), File.GetLastWriteTimeUtc(p)));
            var oldExeWitness = Path.Combine(root, "old-exe-independent-copy"); File.Copy(exe, oldExeWitness);
            Check("normal preparation upgrades in same runtime", await prep.PrepareAsync(source, realm) == runtime);
            var m2 = Load(runtime);
            Check("generation 2 recipe metadata", m2.RealmExecutable is { Generation: 2, State: RealmExecutableState.FrameXmlDigestOverride,
                RecipeId: FrameXmlDigestOverrideRecipe.Id, RecipeVersion: 1 } && m2.RealmExecutable.Sha256 == FrameXmlDigestOverrideRecipe.OutputSha256);
            Check("executable filename and runtime identity retained", Exe(runtime) == exe && m2.RuntimePath == oldManifest.RuntimePath && m2.CreatedUtc == oldManifest.CreatedUtc && m2.RealmId == oldManifest.RealmId);
            Check("all baseline/content/personal files and timestamps preserved", preserved.All(p => File.Exists(p.Key) && Hash(p.Key) == p.Value.Item1 && File.GetLastWriteTimeUtc(p.Key) == p.Value.Item2));
            Check("provisioned configuration preserved", m2.ProvisionedConfiguration == oldManifest.ProvisionedConfiguration);
            Check("baseline and source stay unchanged", Hash(Baseline(runtime)) == FrameXmlDigestOverrideRecipe.SourceSha256 && Hash(sourceExe) == FrameXmlDigestOverrideRecipe.SourceSha256);
            Check("generation 2 independent from source/baseline", links.IsIndependentFile(exe) && !links.AreSameFile(exe, sourceExe) && !links.AreSameFile(exe, Baseline(runtime)));
            var unchanged = File.ReadAllBytes(ManifestPath(runtime)); var exeTime = File.GetLastWriteTimeUtc(exe);
            await prep.PrepareAsync(source, realm);
            Check("valid generation 2 reused without rewriting manifest/file", unchanged.SequenceEqual(File.ReadAllBytes(ManifestPath(runtime))) && File.GetLastWriteTimeUtc(exe) == exeTime);
            Check("resolver selects generation 2 runtime", resolver.ResolveEffectiveClientPath(source, realm) == runtime && RealmExecutableService.RequireValid(runtime, source, realm, m2) == exe);

            var validJson = manifests.Serialize(m2);
            void BadMetadata(string name, Action<JsonNode> mutate)
            {
                var node = JsonNode.Parse(validJson)!; mutate(node);
                Reject(name, () => manifests.Deserialize(node.ToJsonString()));
            }
            BadMetadata("generation 2 missing recipe", n => n["RealmExecutable"]!["RecipeId"] = null);
            BadMetadata("generation 2 unknown recipe", n => n["RealmExecutable"]!["RecipeId"] = "unknown");
            BadMetadata("generation 2 wrong version", n => n["RealmExecutable"]!["RecipeVersion"] = 2);
            BadMetadata("generation 2 missing version", n => n["RealmExecutable"]!["RecipeVersion"] = null);
            BadMetadata("generation 2 baseline hash", n => n["RealmExecutable"]!["Sha256"] = FrameXmlDigestOverrideRecipe.SourceSha256);
            BadMetadata("generation 2 arbitrary hash even when ledger agrees", n =>
            {
                n["RealmExecutable"]!["Sha256"] = new string('A', 64);
                n["Files"]!.AsArray().Single(f => f!["RuntimeRelativePath"]!.GetValue<string>() == Path.GetFileName(exe))!["Sha256"] = new string('A', 64);
            });
            BadMetadata("unsupported recipe source identity", n => { n["RealmExecutable"]!["SourceSha256"] = new string('B', 64); n["SourceExecutableSha256"] = new string('B', 64); });
            BadMetadata("generation 1 plus recipe metadata", n => n["RealmExecutable"]!["Generation"] = 1);
            BadMetadata("generation 1 transformed hash without recipe", n =>
            {
                var e = n["RealmExecutable"]!; e["Generation"] = 1; e["State"] = 0; e["RecipeId"] = null; e["RecipeVersion"] = null;
            });
            BadMetadata("generation 2 wrong state", n => n["RealmExecutable"]!["State"] = 0);
            BadMetadata("wrong launch path", n => n["LaunchExecutableRelativePath"] = "Wow.exe");
            BadMetadata("owned ledger mismatch", n => n["Files"]!.AsArray().Single(f => f!["RuntimeRelativePath"]!.GetValue<string>() == Path.GetFileName(exe))!["Sha256"] = FrameXmlDigestOverrideRecipe.SourceSha256);
            var baselineJson = JsonNode.Parse(manifests.Serialize(oldManifest))!;
            baselineJson["RealmExecutable"]!["RecipeVersion"] = 1;
            Reject("baseline generation rejects recipe version alone", () => manifests.Deserialize(baselineJson.ToJsonString()));

            File.Delete(exe);
            Reject("missing generation 2 no launch fallback", () => new RealmLaunchService().PrepareAndLaunch(runtime, realm, source));
            await prep.PrepareAsync(source, realm);
            Check("missing owned generation 2 regenerated", Hash(exe) == FrameXmlDigestOverrideRecipe.OutputSha256);
            File.WriteAllBytes(exe, damaged);
            Reject("same-size corrupt generation 2 preserved", () => prep.PrepareAsync(source, realm).GetAwaiter().GetResult());
            Check("corrupt generation 2 never overwritten", File.ReadAllBytes(exe).SequenceEqual(damaged));
            Reject("corrupt generation 2 no launch fallback", () => new RealmLaunchService().PrepareAndLaunch(runtime, realm, source));
            File.Move(exe, Path.Combine(root, "preserved-corrupt.exe"));
            await prep.PrepareAsync(source, realm);
            var witness = Path.Combine(root, "gen2-hardlink");
            if (!links.TryCreateHardLink(exe, witness, out _)) throw new IOException("fixture link failed");
            Reject("non-independent generation 2 rejected", () => prep.PrepareAsync(source, realm).GetAwaiter().GetResult());
            File.Delete(witness);
            var collisionRealm = Realm("unmanaged collision");
            var collision = new ManagedRuntimeBuilder().Build(new() { SourceClientPath = source, Realm = collisionRealm, RuntimeRoot = storage });
            var collisionExe = Path.Combine(collision.RuntimePath, RealmExecutableService.FileName(collisionRealm.Name, RealmIdentity.FromRealm(collisionRealm)));
            File.WriteAllBytes(collisionExe, generated);
            Reject("unmanaged exact transformed bytes never adopted", () => prep.PrepareAsync(source, collisionRealm).GetAwaiter().GetResult());
            Check("collision preserved", File.ReadAllBytes(collisionExe).SequenceEqual(generated) && Load(collision.RuntimePath).RealmExecutable is null);

            foreach (var stage in new[] { "temporary-created", "temporary-validated", "journal-saved", "executable-promoted", "manifest-promoted" })
            {
                var interruptedRealm = Realm("interruption " + stage);
                var interrupted = Cp4(interruptedRealm);
                var old = File.ReadAllText(ManifestPath(interrupted));
                var oldExe = Exe(interrupted);
                Reject("injected interruption at " + stage, () => new RealmExecutableService(at =>
                {
                    if (at == stage) throw new IOException("simulated interruption");
                }).Prepare(interrupted, source, interruptedRealm));
                var promoted = stage is "executable-promoted" or "manifest-promoted";
                Check(stage + " retains recoverable previous executable", promoted
                    ? Directory.GetFiles(Path.Combine(interrupted, ".portalkeeper"), "*.bak").Any(p => Hash(p) == FrameXmlDigestOverrideRecipe.SourceSha256)
                    : Hash(oldExe) == FrameXmlDigestOverrideRecipe.SourceSha256);
                Check(stage + " retains prior manifest or journal copy", stage == "manifest-promoted"
                    ? JsonNode.Parse(File.ReadAllText(Pending(interrupted)))!["PriorManifest"]!.GetValue<string>() == old
                    : File.ReadAllText(ManifestPath(interrupted)) == old);
                Reject(stage + " not launchable", () => new RealmLaunchService().PrepareAndLaunch(interrupted, interruptedRealm, source));
                if (File.Exists(Pending(interrupted)))
                    Check(stage + " pending transaction fails runtime validation", !new ManagedRuntimeValidator().Validate(interrupted, interruptedRealm, source).IsValid);
                Check(stage + " cleans only its temporary output", !Directory.GetFiles(Path.Combine(interrupted, ".portalkeeper"), "realm-executable-*.tmp").Any());
                await prep.PrepareAsync(source, interruptedRealm);
                Check(stage + " recovers deterministically in place", Hash(Exe(interrupted)) == FrameXmlDigestOverrideRecipe.OutputSha256 && !File.Exists(Pending(interrupted)) && resolver.ResolveEffectiveClientPath(source, interruptedRealm) == interrupted);
            }
            var linkRealm = Realm("replacement link window"); var linkRuntime = Cp4(linkRealm);
            Reject("interrupt inside modeled Unix backup-link window", () => new RealmExecutableService(at =>
            {
                if (at != "journal-saved") return;
                var pending = JsonNode.Parse(File.ReadAllText(Pending(linkRuntime)))!;
                var backup = RuntimePaths.Resolve(linkRuntime, pending["BackupRelativePath"]!.GetValue<string>());
                if (!links.TryCreateHardLink(Exe(linkRuntime), backup, out _)) throw new IOException("fixture backup link failed");
                throw new IOException("interrupted before replacement rename");
            }).Prepare(linkRuntime, source, linkRealm));
            Check("modeled backup window has two links", links.HasLinkCount(Exe(linkRuntime), 2));
            await prep.PrepareAsync(source, linkRealm);
            Check("backup-link window recovers without modifying old inode", Hash(Exe(linkRuntime)) == FrameXmlDigestOverrideRecipe.OutputSha256 && links.IsIndependentFile(Exe(linkRuntime)));

            foreach (var stage in new[] { "temporary-created", "executable-promoted", "manifest-promoted" })
            {
                var crashRealm = Realm("process crash " + stage); var crashRuntime = Cp4(crashRealm);
                var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false };
                foreach (var arg in new[] { typeof(Program).Assembly.Location, "checkpoint5-crash", root, source, crashRuntime, crashRealm.Name, stage })
                    start.ArgumentList.Add(arg);
                using var child = System.Diagnostics.Process.Start(start)!;
                if (!child.WaitForExit(30000)) { child.Kill(entireProcessTree: true); throw new IOException("Crash fixture timed out."); }
                Check("abrupt process exit at " + stage, child.ExitCode == 73);
                var orphans = Directory.GetFiles(Path.Combine(crashRuntime, ".portalkeeper"), "realm-executable-*.tmp");
                Check(stage + " abrupt exit has expected temporary state", orphans.Length == (stage == "temporary-created" ? 1 : 0));
                await prep.PrepareAsync(source, crashRealm);
                Check(stage + " abrupt crash recovers", Hash(Exe(crashRuntime)) == FrameXmlDigestOverrideRecipe.OutputSha256 && !File.Exists(Pending(crashRuntime)));
                Check(stage + " unreferenced crash temporaries are not swept", orphans.All(File.Exists));
            }

            // Altered recovery records and unexpected promoted bytes must never acquire ownership.
            var tamperRealm = Realm("bad journal"); var tamper = Cp4(tamperRealm);
            Reject("create interrupted promotion", () => new RealmExecutableService(at => { if (at == "journal-saved") throw new IOException("stop"); }).Prepare(tamper, source, tamperRealm));
            var journalText = File.ReadAllText(Pending(tamper));
            var journal = JsonNode.Parse(journalText)!; journal["BackupRelativePath"] = "../escape";
            File.WriteAllText(Pending(tamper), journal.ToJsonString());
            Reject("journal path tampering fails closed", () => prep.PrepareAsync(source, tamperRealm).GetAwaiter().GetResult());
            File.WriteAllText(Pending(tamper), journalText);
            File.WriteAllText(Exe(tamper), "unmanaged replacement during interruption");
            Reject("unexpected file during recovery preserved", () => prep.PrepareAsync(source, tamperRealm).GetAwaiter().GetResult());
            Check("unexpected recovery file unchanged", File.ReadAllText(Exe(tamper)) == "unmanaged replacement during interruption");

            if (OperatingSystem.IsLinux()) TestCheckpoint4Wine(root, source, runtime, realm, exe, Check);
            else if (OperatingSystem.IsWindows())
            {
                var start = RealmLaunchService.CreateLaunchStartInfo(exe, runtime, source);
                Check("Windows native generation 2 launch arguments", start.FileName == exe && start.WorkingDirectory == runtime && !start.UseShellExecute);
            }
            Check("entire disposable source preserved", before.All(p => Hash(p.Key) == p.Value));
            Check("supplied source hash and timestamp unchanged", Hash(supplied) == originalHash && File.GetLastWriteTimeUtc(supplied) == originalTime);
            Console.WriteLine($"Checkpoint 5: {checks} checks, {failures} failure(s)");
            return failures == 0 ? 0 : 2;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 2; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
