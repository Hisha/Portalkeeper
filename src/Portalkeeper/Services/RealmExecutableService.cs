using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using Portalkeeper.Models;
using Portalkeeper.Models.Runtime;

namespace Portalkeeper.Services;

// Owns the independent realm executable and the fixed CP5 generation transaction.
public sealed class RealmExecutableService
{
    public static string FileName(string realmName, string realmId)
    {
        if (!RealmIdentity.IsValid(realmId)) throw new InvalidDataException("Invalid realm executable identity.");
        var label = Regex.Replace(realmName, "[^A-Za-z0-9]+", "-").Trim('-');
        if (label.Length > 40) label = label[..40].TrimEnd('-');
        if (label.Length == 0) label = "Realm";
        // Full identity disambiguates sanitized names; the suffix also avoids DOS device names.
        var identity = realmId.Length == 64 ? realmId.ToUpperInvariant() : Guid.Parse(realmId).ToString("N");
        return $"{label}-{identity}.exe";
    }

    public const string PendingRelativePath = ".portalkeeper/realm-executable-generation2.json";
    private readonly Action<string>? _checkpoint;
    public RealmExecutableService() { }
    // Fault injection observes transaction boundaries only; it cannot substitute a recipe.
    internal RealmExecutableService(Action<string> checkpoint) => _checkpoint = checkpoint;

    public static bool RequiresGeneration2(RealmInfo realm) =>
        realm.Client.RuntimeMode == ClientRuntimeMode.Isolated && realm.Client.RequiresProtectedFrameXml;

    private sealed record GenerationTransaction(string PriorManifest, string NextManifest, string BackupRelativePath);

    public void Prepare(string root, string source, RealmInfo realm, string? expectedFinalRuntimePath = null)
    {
        realm.Client.ThrowIfUnsupportedRequirement();
        if (realm.Client.RuntimeMode != ClientRuntimeMode.Isolated)
            throw new InvalidOperationException("Realm executable preparation requires an isolated realm.");
        if (!RequiresGeneration2(realm))
            throw new InvalidOperationException("Realm executable Generation 2 preparation requires the protected-framexml client requirement.");
        var validation = new ManagedRuntimeValidator().Validate(root, realm, source,
            expectedFinalRuntimePath, validateRealmExecutable: false);
        if (!validation.IsValid)
            throw new InvalidOperationException("Cannot prepare realm executable: " + string.Join("\n", validation.Errors));
        var lockPath = RuntimePaths.Resolve(root, ".portalkeeper/realm-executable.lock");
        // Inspect before taking the exclusive handle: Windows cannot reopen a FileShare.None file.
        if (File.Exists(lockPath) && !new HardLinkService().IsIndependentFile(lockPath))
            throw new IOException("Realm executable lock is not independently owned.");
        using var generationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var manifests = new ManagedRuntimeManifestService();
        var manifestPath = RuntimePaths.Resolve(root, ManagedRuntimeBuilder.ManifestRelativePath);
        var manifest = manifests.Load(manifestPath);
        var sourcePath = VerifiedSource(source, realm, manifest);
        var sourceBytes = File.ReadAllBytes(sourcePath);
        FrameXmlDigestOverrideRecipe.ValidateSource(sourceBytes);
        var relative = FileName(manifest.RealmName, manifest.RealmId);
        var target = RuntimePaths.Resolve(root, relative);
        var pendingPath = RuntimePaths.Resolve(root, PendingRelativePath);
        RejectCaseCollision(root, relative);

        if (Directory.Exists(pendingPath))
            throw new InvalidDataException("Executable transaction path contains a directory; explicit repair required.");
        GenerationTransaction? transaction = null;
        if (File.Exists(pendingPath))
        {
            if (!new HardLinkService().IsIndependentFile(pendingPath))
                throw new InvalidDataException("Executable transaction record is not independent; explicit repair required.");
            transaction = JsonSerializer.Deserialize<GenerationTransaction>(File.ReadAllText(pendingPath))
                ?? throw new InvalidDataException("Invalid executable transaction record.");
            var prior = manifests.Deserialize(transaction.PriorManifest);
            var next = manifests.Deserialize(transaction.NextManifest);
            // Recompute the only permitted transition. Journal fields never authorize arbitrary metadata.
            if (manifests.Serialize(NextGeneration(prior, sourcePath)) != manifests.Serialize(next) ||
                (manifests.Serialize(manifest) != manifests.Serialize(prior) && manifests.Serialize(manifest) != manifests.Serialize(next)) ||
                !Regex.IsMatch(transaction.BackupRelativePath,
                    @"^\.portalkeeper/realm-executable-generation1-[a-f0-9]{32}\.bak$"))
                throw new InvalidDataException("Executable transaction does not match this runtime; explicit repair required.");
            // Unix File.Replace may create the backup link before renaming the replacement.
            // A crash in that window leaves exactly these two journal-owned names for the old inode.
            var backupPath = RuntimePaths.Resolve(root, transaction.BackupRelativePath);
            var links = new HardLinkService();
            if (prior.RealmExecutable?.Generation == 1 && manifests.Serialize(manifest) == manifests.Serialize(prior) &&
                File.Exists(backupPath) && File.Exists(target) && links.AreSameFile(backupPath, target) &&
                links.HasLinkCount(target, 2) && Hash(target) == FrameXmlDigestOverrideRecipe.SourceSha256)
                File.Delete(backupPath);
            ValidateBackup(root, transaction, prior);
            if (File.Exists(target) && Hash(target) == FrameXmlDigestOverrideRecipe.OutputSha256)
            {
                // The journal reserved this path before promotion; without it an identical collision is never adopted.
                RequireValid(root, source, realm, next);
                Commit(next, transaction);
                return;
            }
            if (manifests.Serialize(manifest) != manifests.Serialize(prior))
                throw new InvalidDataException("Committed executable transaction is damaged; explicit repair required.");
            manifest = prior;
        }

        if (manifest.RealmExecutable is { } owned)
        {
            if (!owned.SourceRelativePath.Equals(Path.GetFileName(sourcePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Recorded realm executable has a different source; explicit repair required.");
            if (File.Exists(target))
            {
                RequireValid(root, source, realm, manifest);
                if (owned.Generation == 2 && transaction is null) return;
            }
            if (Directory.Exists(target)) throw new IOException("Realm executable path contains a directory; nothing was replaced.");
        }
        else if (File.Exists(target) || Directory.Exists(target) || manifest.Files.Any(e =>
                     e.RuntimeRelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Realm executable filename collides with unmanaged content; nothing was replaced: " + relative);

        var nextManifest = NextGeneration(manifest, sourcePath);
        manifests.Validate(nextManifest);
        var temporary = RuntimePaths.Resolve(root, ".portalkeeper/realm-executable-" + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try
        {
            // The source is never opened for writing. The existing realm file is never transformation input.
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                output.Write(sourceBytes);
                output.Flush(flushToDisk: true);
            }
            _checkpoint?.Invoke("temporary-created");
            if (!new HardLinkService().IsIndependentFile(temporary))
                throw new IOException("Realm executable temporary file is not independent.");
            var generated = FrameXmlDigestOverrideRecipe.Generate(File.ReadAllBytes(temporary));
            using (var output = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                output.Write(generated);
                output.Flush(flushToDisk: true);
            }
            FrameXmlDigestOverrideRecipe.ValidateOutput(File.ReadAllBytes(temporary));
            if (!new HardLinkService().IsIndependentFile(temporary))
                throw new IOException("Realm executable temporary file lost independence.");
            _checkpoint?.Invoke("temporary-validated");
            VerifiedSource(source, realm, manifest);
            RejectCaseCollision(root, relative);
            if (File.Exists(target))
            {
                if (manifest.RealmExecutable?.Generation != 1)
                    throw new IOException("Realm executable destination appeared during preparation; nothing was replaced.");
                RequireValid(root, source, realm, manifest);
            }
            if (Directory.Exists(target)) throw new IOException("Realm executable destination is a directory.");
            if (transaction is null)
            {
                transaction = new GenerationTransaction(manifests.Serialize(manifest), manifests.Serialize(nextManifest),
                    ".portalkeeper/realm-executable-generation1-" + Guid.NewGuid().ToString("N") + ".bak");
                var journalTemporary = temporary + ".json";
                try
                {
                    using (var output = new FileStream(journalTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        JsonSerializer.Serialize(output, transaction);
                        output.Flush(flushToDisk: true);
                    }
                    File.Move(journalTemporary, pendingPath, overwrite: false);
                }
                finally { if (File.Exists(journalTemporary)) File.Delete(journalTemporary); }
            }
            _checkpoint?.Invoke("journal-saved");
            if (File.Exists(target))
            {
                // Replace changes the directory entry, never the previous file's bytes.
                // The old manifest remains authoritative until the final atomic manifest save.
                var backup = RuntimePaths.Resolve(root, transaction.BackupRelativePath);
                if (File.Exists(backup) || Directory.Exists(backup))
                    throw new IOException("Executable backup collision; previous state preserved for explicit repair.");
                RequireValid(root, source, realm, manifest);
                File.Replace(temporary, target, backup);
            }
            else File.Move(temporary, target, overwrite: false);
            _checkpoint?.Invoke("executable-promoted");
            RequireValid(root, source, realm, nextManifest);
            Commit(nextManifest, transaction);
        }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }

        void Commit(ManagedRuntimeManifest next, GenerationTransaction pending)
        {
            manifests.Save(next, manifestPath);
            _checkpoint?.Invoke("manifest-promoted");
            RequireValid(root, source, realm, next);
            var prior = manifests.Deserialize(pending.PriorManifest);
            ValidateBackup(root, pending, prior);
            var backup = RuntimePaths.Resolve(root, pending.BackupRelativePath);
            if (File.Exists(backup)) File.Delete(backup);
            File.Delete(pendingPath);
        }
    }

    private static ManagedRuntimeManifest NextGeneration(ManagedRuntimeManifest prior, string sourcePath)
    {
        var manifests = new ManagedRuntimeManifestService();
        var next = manifests.Deserialize(manifests.Serialize(prior));
        var relative = FileName(next.RealmName, next.RealmId);
        next.RealmExecutable = new ManagedRealmExecutable
        {
            SourceRelativePath = Path.GetFileName(sourcePath), SourceSha256 = FrameXmlDigestOverrideRecipe.SourceSha256,
            RuntimeRelativePath = relative, Sha256 = FrameXmlDigestOverrideRecipe.OutputSha256,
            Generation = 2, State = RealmExecutableState.FrameXmlDigestOverride,
            RecipeId = FrameXmlDigestOverrideRecipe.Id, RecipeVersion = FrameXmlDigestOverrideRecipe.Version
        };
        next.Files.RemoveAll(e => e.RuntimeRelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
        next.Files.Add(new ManagedRuntimeFileEntry
        { RuntimeRelativePath = relative, Kind = ManagedRuntimeFileKind.RealmOwned, Sha256 = FrameXmlDigestOverrideRecipe.OutputSha256 });
        next.LaunchExecutableRelativePath = relative;
        return next;
    }

    private static void ValidateBackup(string root, GenerationTransaction transaction, ManagedRuntimeManifest prior)
    {
        var backup = RuntimePaths.Resolve(root, transaction.BackupRelativePath);
        if (Directory.Exists(backup) || (File.Exists(backup) &&
            (prior.RealmExecutable?.Generation != 1 || Hash(backup) != FrameXmlDigestOverrideRecipe.SourceSha256 ||
             !new HardLinkService().IsIndependentFile(backup))))
            throw new InvalidDataException("Executable recovery backup is unexpected; preserve it for explicit repair.");
    }

    public static string RequireValid(string root, string source, RealmInfo realm, ManagedRuntimeManifest manifest)
    {
        new ManagedRuntimeManifestService().Validate(manifest);
        var executable = manifest.RealmExecutable ??
            throw new InvalidOperationException("The isolated runtime needs realm executable preparation.");
        var sourcePath = VerifiedSource(source, realm, manifest);
        if (!Path.GetFileName(sourcePath).Equals(executable.SourceRelativePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Realm executable source does not match the configured source executable.");
        var path = RuntimePaths.Resolve(root, executable.RuntimeRelativePath);
        RejectCaseCollision(root, executable.RuntimeRelativePath);
        if (!File.Exists(path))
            throw new InvalidOperationException("Managed realm executable is missing; enter the realm again to prepare it.");
        if ((executable.Generation == 2 && new FileInfo(path).Length != FrameXmlDigestOverrideRecipe.SourceSize) ||
            !Hash(path).Equals(executable.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !new HardLinkService().IsIndependentFile(path))
            throw new InvalidOperationException("Managed realm executable is corrupt or shares file identity. Launch refused; preserve/move the unexpected file outside the runtime for explicit repair, then prepare again.");
        return path;
    }

    public static string SelectLaunchExecutable(string root, string source, RealmInfo realm, ManagedRuntimeManifest manifest)
    {
        realm.Client.ThrowIfUnsupportedRequirement();
        new ManagedRuntimeManifestService().Validate(manifest);
        if (RequiresGeneration2(realm))
            return RequireValid(root, source, realm, manifest);
        if (manifest.RealmExecutable is { Generation: 1 })
            return RequireValid(root, source, realm, manifest);
        var baseline = manifest.Files.FirstOrDefault(e =>
            e.Kind == ManagedRuntimeFileKind.CopiedBaseline &&
            e.RuntimeRelativePath.Equals(realm.Client.Executable, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The isolated runtime does not contain the configured launch executable.");
        RejectCaseCollision(root, baseline.RuntimeRelativePath);
        var path = RuntimePaths.Resolve(root, baseline.RuntimeRelativePath);
        if (!File.Exists(path))
            throw new InvalidOperationException("Managed launch executable is missing; enter the realm again to prepare it.");
        if (!Hash(path).Equals(baseline.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !new HardLinkService().IsIndependentFile(path))
            throw new InvalidOperationException("Managed launch executable is corrupt or shares file identity. Launch refused; preserve/move the unexpected file outside the runtime for explicit repair, then prepare again.");
        return path;
    }

    private static string VerifiedSource(string source, RealmInfo realm, ManagedRuntimeManifest manifest)
    {
        if (!RuntimePaths.SamePath(source, manifest.SourceClientPath) ||
            RuntimePaths.SamePath(source, manifest.RuntimePath) ||
            RuntimePaths.IsWithin(source, manifest.RuntimePath) || RuntimePaths.IsWithin(manifest.RuntimePath, source) ||
            File.Exists(RuntimePaths.Resolve(source, ManagedRuntimeBuilder.ManifestRelativePath)))
            throw new InvalidOperationException("Realm executable must originate from the configured source client, never a managed runtime.");
        var client = new ClientService().ValidateClient(source, realm.Client);
        if (!client.IsSupportedClient) throw new InvalidOperationException("Realm executable source verification failed: " + client.StatusMessage);
        var path = RuntimePaths.Resolve(source, Path.GetFileName(client.ExecutablePath));
        if (!Hash(path).Equals(manifest.SourceExecutableSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source executable differs from the verified runtime baseline; explicit baseline repair is required.");
        return path;
    }

    private static void RejectCaseCollision(string root, string relative)
    {
        if (Directory.EnumerateFileSystemEntries(root).Any(p =>
            Path.GetFileName(p).Equals(relative, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(p) != relative))
            throw new IOException("Realm executable filename has a case-insensitive collision; nothing was replaced.");
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
