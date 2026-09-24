using System.Text.Json.Nodes;
using Portalkeeper.Models;
using Portalkeeper.Models.Runtime;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

internal static partial class Program
{
    private static async Task<int> RunCheckpoint4()
    {
        var root = Path.Combine(Path.GetTempPath(), "pk-cp4-" + Guid.NewGuid().ToString("N"));
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
            var legacy = CreateFixtureClient(source);
            var sourceExe = Path.Combine(source, "Wow.exe");
            var before = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);
            var sourceBytes = File.ReadAllBytes(sourceExe);
            var storage = Path.Combine(root, "runtimes");
            var preparation = new RealmRuntimePreparationService(storage);
            var resolver = new RealmRuntimeResolver(storage);
            var manifests = new ManagedRuntimeManifestService();
            var links = new HardLinkService();
            RealmInfo Realm(string name, string? hash = null) => new()
            {
                SchemaVersion = 1, Name = name, Address = "fixture.invalid", GameRealmName = "Fixture",
                Client = new() { RuntimeMode = ClientRuntimeMode.Isolated, ExecutableSha256 = hash ?? legacy.Client.ExecutableSha256 }
            };
            ManagedRuntimeManifest Load(string runtime) => manifests.Load(Path.Combine(runtime, ManagedRuntimeBuilder.ManifestRelativePath));
            string Executable(string runtime) => RuntimePaths.Resolve(runtime, Load(runtime).RealmExecutable!.RuntimeRelativePath);
            string Baseline(string runtime) => Path.Combine(runtime, "Wow.exe");

            Check("Legacy preparation creates no runtime", await preparation.PrepareAsync(source, legacy) == source && !Directory.Exists(storage));
            var realm = Realm("Realm / : Unsafe * Name");
            var runtime = await preparation.PrepareAsync(source, realm);
            var exe = Executable(runtime);
            var manifest = Load(runtime);
            Check("isolated runtime creates realm executable", File.Exists(exe) && exe != Baseline(runtime));
            Check("baseline Wow.exe remains present and unchanged", Hash(Baseline(runtime)) == Hash(sourceExe));
            Check("realm executable has source contents and hash", Hash(exe) == Hash(sourceExe) && File.ReadAllBytes(exe).SequenceEqual(sourceBytes));
            Check("realm executable is an independent file", links.IsIndependentFile(exe) && !links.AreSameFile(exe, sourceExe) && !links.AreSameFile(exe, Baseline(runtime)));
            Check("ownership, provenance, hashes, launch target and baseline state persist", manifest.RealmExecutable is
                { SourceRelativePath: "Wow.exe", Generation: 1, State: RealmExecutableState.BaselineCopy } &&
                manifest.RealmExecutable.SourceSha256 == Hash(sourceExe) && manifest.RealmExecutable.Sha256 == Hash(exe) &&
                manifest.Files.Single(e => e.RuntimeRelativePath == manifest.RealmExecutable.RuntimeRelativePath).Kind == ManagedRuntimeFileKind.RealmOwned &&
                manifest.LaunchExecutableRelativePath == Path.GetFileName(exe));
            var exeTime = File.GetLastWriteTimeUtc(exe);
            var manifestBytes = File.ReadAllBytes(Path.Combine(runtime, ManagedRuntimeBuilder.ManifestRelativePath));
            await preparation.PrepareAsync(source, realm);
            Check("valid owned executable reused without rewriting file or manifest", File.GetLastWriteTimeUtc(exe) == exeTime &&
                manifestBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(runtime, ManagedRuntimeBuilder.ManifestRelativePath))));

            var name = RealmExecutableService.FileName(realm.Name, RealmIdentity.FromRealm(realm));
            Check("deterministic portable bounded naming", name == Path.GetFileName(exe) && name.Length <= 109 && ManagedPath.Relative(name, true) == name);
            var other = Realm("Realm : / Unsafe * Name");
            Check("sanitized label collisions have distinct stable identity suffixes", name != RealmExecutableService.FileName(other.Name, RealmIdentity.FromRealm(other)));
            foreach (var label in new[] { "CON", "NUL", "../..", "", "🧙", new string('A', 500) })
                Check("safe label: " + label[..Math.Min(label.Length, 20)], !Throws(() => ManagedPath.Relative(RealmExecutableService.FileName(label, RealmIdentity.FromRealm(realm)), true)));

            // Reproduce an actual CP3 manifest shape with no new property, retaining baseline/content state.
            var oldRealm = Realm("CP3 Upgrade");
            var old = new ManagedRuntimeBuilder().Build(new() { SourceClientPath = source, Realm = oldRealm, RuntimeRoot = storage });
            var oldManifestPath = Path.Combine(old.RuntimePath, ManagedRuntimeBuilder.ManifestRelativePath);
            var json = JsonNode.Parse(File.ReadAllText(oldManifestPath))!.AsObject();
            json.Remove("RealmExecutable");
            json["ProvisionedConfiguration"] = RealmRuntimePreparationService.ConfigurationKey(oldRealm);
            File.WriteAllText(oldManifestPath, json.ToJsonString());
            var oldBaselineTime = File.GetLastWriteTimeUtc(Baseline(old.RuntimePath));
            WriteSimple(Path.Combine(old.RuntimePath, "WTF/Config.wtf"), "personal runtime state");
            var wtfHash = Hash(Path.Combine(old.RuntimePath, "WTF/Config.wtf"));
            Check("CP3 manifests without executable field load and pass baseline validation", Load(old.RuntimePath).RealmExecutable is null &&
                new ManagedRuntimeValidator().Validate(old.RuntimePath, oldRealm, source).IsValid);
            await Reject("CP3 baseline cannot launch before executable preparation", () => Task.FromResult(resolver.ResolveEffectiveClientPath(source, oldRealm)));
            Check("CP3 upgrades in place via normal preparation", await preparation.PrepareAsync(source, oldRealm) == old.RuntimePath && File.Exists(Executable(old.RuntimePath)));
            Check("CP3 baseline, creation timestamp and personal state preserved", File.GetLastWriteTimeUtc(Baseline(old.RuntimePath)) == oldBaselineTime &&
                Load(old.RuntimePath).CreatedUtc == old.Manifest.CreatedUtc && Hash(Path.Combine(old.RuntimePath, "WTF/Config.wtf")) == wtfHash);
            var cp2Realm = Realm("CP2 Upgrade");
            var cp2 = new ManagedRuntimeBuilder().Build(new() { SourceClientPath = source, Realm = cp2Realm, RuntimeRoot = storage });
            Check("CP2 baseline upgrades in place", await preparation.PrepareAsync(source, cp2Realm) == cp2.RuntimePath && File.Exists(Executable(cp2.RuntimePath)));

            File.Delete(exe);
            Check("missing executable invalidates runtime", !new ManagedRuntimeValidator().Validate(runtime, realm, source).IsValid);
            await Reject("missing executable has no resolver fallback", () => Task.FromResult(resolver.ResolveEffectiveClientPath(source, realm)));
            await Reject("missing executable has no launch fallback", () => Task.FromResult(new RealmLaunchService().PrepareAndLaunch(runtime, realm, source)));
            await preparation.PrepareAsync(source, realm);
            Check("missing owned executable regenerates safely", File.Exists(exe) && Hash(exe) == Hash(sourceExe) && links.IsIndependentFile(exe));
            File.WriteAllText(exe, "unexpected changed executable");
            var corruptHash = Hash(exe);
            Check("modifying generated file cannot mutate source or baseline", Hash(sourceExe) == legacy.Client.ExecutableSha256 && Hash(Baseline(runtime)) == Hash(sourceExe));
            Check("corrupt executable detected", !new ManagedRuntimeValidator().Validate(runtime, realm, source).IsValid);
            await Reject("corrupt owned executable is preserved, never overwritten", () => preparation.PrepareAsync(source, realm));
            await Reject("corrupt executable has no baseline/source launch fallback", () => Task.FromResult(new RealmLaunchService().PrepareAndLaunch(runtime, realm, source)));
            Check("unexpected bytes preserved after failed preparation", Hash(exe) == corruptHash);
            File.Move(exe, Path.Combine(root, "preserved-corrupt.exe")); // Explicit test/user repair, not application cleanup.
            await preparation.PrepareAsync(source, realm);
            Check("explicit recovery from preserved corruption", Hash(exe) == Hash(sourceExe));

            File.Delete(exe);
            if (!links.TryCreateHardLink(sourceExe, exe, out _)) throw new IOException("Fixture hard link failed");
            await Reject("source hard link cannot be reused as realm executable", () => preparation.PrepareAsync(source, realm));
            File.Delete(exe);
            await preparation.PrepareAsync(source, realm);

            var collisionRealm = Realm("Collision");
            var collision = new ManagedRuntimeBuilder().Build(new() { SourceClientPath = source, Realm = collisionRealm, RuntimeRoot = storage });
            var collisionPath = Path.Combine(collision.RuntimePath, RealmExecutableService.FileName(collisionRealm.Name, RealmIdentity.FromRealm(collisionRealm)));
            File.WriteAllBytes(collisionPath, sourceBytes); // Even identical bytes do not confer ownership.
            await Reject("unmanaged identical executable collision is not adopted", () => preparation.PrepareAsync(source, collisionRealm));
            Check("collision preserved with no ownership claim", Hash(collisionPath) == Hash(sourceExe) && Load(collision.RuntimePath).RealmExecutable is null);
            if (!OperatingSystem.IsWindows())
            {
                File.Move(collisionPath, Path.Combine(collision.RuntimePath, Path.GetFileName(collisionPath).ToLowerInvariant()));
                await Reject("case-insensitive unmanaged collision fails closed", () => preparation.PrepareAsync(source, collisionRealm));
            }

            await Reject("content cannot overwrite owned executable", () => { ManagedRuntimeWriteGuard.Check(runtime, exe); return Task.CompletedTask; });
            var originalJson = File.ReadAllText(Path.Combine(runtime, ManagedRuntimeBuilder.ManifestRelativePath));
            foreach (var relative in new[] { "../outside.exe", "/outside.exe", "Data/custom.exe", "Wow.exe", "CON.exe" })
            {
                var unsafeJson = JsonNode.Parse(originalJson)!;
                unsafeJson["RealmExecutable"]!["RuntimeRelativePath"] = relative;
                await Reject("executable metadata path rejected: " + relative, () => Task.FromResult(manifests.Deserialize(unsafeJson.ToJsonString())));
            }
            var wrongState = JsonNode.Parse(originalJson)!; wrongState["RealmExecutable"]!["State"] = 99;
            await Reject("unknown executable transform state rejected", () => Task.FromResult(manifests.Deserialize(wrongState.ToJsonString())));
            var wrongSource = JsonNode.Parse(originalJson)!; wrongSource["RealmExecutable"]!["SourceRelativePath"] = "../Wow.exe";
            await Reject("source metadata traversal rejected", () => Task.FromResult(manifests.Deserialize(wrongSource.ToJsonString())));

            var validHash = Hash(exe);
            File.AppendAllText(sourceExe, "hash mismatch");
            await Reject("source mismatch blocks preparation", () => preparation.PrepareAsync(source, realm));
            await Reject("source mismatch blocks launch", () => Task.FromResult(new RealmLaunchService().PrepareAndLaunch(runtime, realm, source)));
            Check("generation failure preserves previous valid executable", Hash(exe) == validHash);
            File.WriteAllBytes(sourceExe, sourceBytes); // Restore test fixture only.

            if (OperatingSystem.IsLinux())
            {
                File.Delete(exe);
                File.CreateSymbolicLink(exe, sourceExe);
                await Reject("executable symlink cannot be adopted", () => preparation.PrepareAsync(source, realm));
                File.Delete(exe);
                await preparation.PrepareAsync(source, realm);
                // Force promotion failure AFTER the validated temp and ownership metadata exist.
                var interruptedRealm = Realm("Interrupted promotion");
                var interrupted = new ManagedRuntimeBuilder().Build(new() { SourceClientPath = source, Realm = interruptedRealm, RuntimeRoot = storage });
                var mode = File.GetUnixFileMode(interrupted.RuntimePath);
                try
                {
                    File.SetUnixFileMode(interrupted.RuntimePath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    await Reject("atomic promotion failure fails closed", () => preparation.PrepareAsync(source, interruptedRealm));
                    Check("failed promotion keeps recoverable ownership and no launch target", Load(interrupted.RuntimePath).RealmExecutable is not null && !File.Exists(Executable(interrupted.RuntimePath)));
                    Check("only owned temporary file cleaned after failure", !Directory.EnumerateFiles(Path.Combine(interrupted.RuntimePath, ".portalkeeper"), "realm-executable-*.tmp").Any());
                }
                finally { File.SetUnixFileMode(interrupted.RuntimePath, mode); }
                Check("interrupted generation retries in place", await preparation.PrepareAsync(source, interruptedRealm) == interrupted.RuntimePath && Hash(Executable(interrupted.RuntimePath)) == Hash(sourceExe));
                TestCheckpoint4Wine(root, source, runtime, realm, exe, Check);
            }
            else if (OperatingSystem.IsWindows())
            {
                var start = RealmLaunchService.CreateLaunchStartInfo(exe, runtime, source);
                Check("Windows launch uses owned executable natively with runtime cwd", start.FileName == exe && start.WorkingDirectory == runtime && !start.UseShellExecute && start.ArgumentList.Count == 0);
                var startLegacy = RealmLaunchService.CreateLaunchStartInfo(sourceExe, source);
                Check("Windows Legacy launch remains native source", startLegacy.FileName == sourceExe && startLegacy.WorkingDirectory == source);
            }
            var after = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);
            Check("entire source file set and contents unchanged", before.Count == after.Count && before.All(e => after.TryGetValue(e.Key, out var hash) && hash == e.Value));
            Console.WriteLine($"Checkpoint 4: {failures} failure(s)");
            return failures == 0 ? 0 : 2;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 2; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void TestCheckpoint4Wine(string root, string source, string runtime, RealmInfo realm, string exe, Action<string, bool> check)
    {
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldPrefix = Environment.GetEnvironmentVariable("WINEPREFIX");
        var oldXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            var bin = Path.Combine(root, "cp4-bin"); Directory.CreateDirectory(bin);
            var argsFile = Path.Combine(root, "launch-args"); var cwdFile = Path.Combine(root, "launch-cwd"); var envFile = Path.Combine(root, "launch-prefix");
            var wine = Path.Combine(bin, "wine");
            File.WriteAllText(wine, $"#!/bin/sh\nprintf '%s' \"$1\" > '{argsFile}'\npwd > '{cwdFile}'\nprintf '%s' \"$WINEPREFIX\" > '{envFile}'\n");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(wine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var prefix = Path.Combine(root, "cp4-prefix"); Directory.CreateDirectory(prefix);
            var xdg = Path.Combine(root, "cp4-desktop"); Directory.CreateDirectory(Path.Combine(xdg, "applications"));
            File.WriteAllText(Path.Combine(xdg, "applications/wow.desktop"), $"[Desktop Entry]\nExec=env WINEPREFIX={prefix} wine {source}/Wow.exe\n");
            Environment.SetEnvironmentVariable("PATH", bin); Environment.SetEnvironmentVariable("WINEPREFIX", null); Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdg);
            var legacy = RealmLaunchService.CreateLaunchStartInfo(Path.Combine(source, "Wow.exe"), source);
            check("Legacy Wine still targets source with source cwd and prefix", legacy.ArgumentList.Single() == Path.Combine(source, "Wow.exe") && legacy.WorkingDirectory == source && legacy.Environment["WINEPREFIX"] == prefix);
            var launch = new RealmLaunchService().PrepareAndLaunch(runtime, realm, source);
            launch.Process!.WaitForExit(); launch.Process.Dispose();
            check("actual isolated launch invokes realm executable", launch.ExecutablePath == exe && File.ReadAllText(argsFile) == exe);
            check("actual isolated process cwd is runtime root", File.ReadAllText(cwdFile).Trim() == runtime);
            check("actual Wine prefix is discovered from SOURCE executable", File.ReadAllText(envFile) == prefix);
            Environment.SetEnvironmentVariable("WINEPREFIX", prefix);
            check("explicit Wine environment precedence preserved", RealmLaunchService.CreateLaunchStartInfo(exe, runtime, source).Environment["WINEPREFIX"] == prefix);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath); Environment.SetEnvironmentVariable("WINEPREFIX", oldPrefix); Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldXdg);
        }
    }
}
