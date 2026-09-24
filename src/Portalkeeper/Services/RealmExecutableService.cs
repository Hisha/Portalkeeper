using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Portalkeeper.Models;
using Portalkeeper.Models.Runtime;

namespace Portalkeeper.Services;

// Owns only independent baseline copies. No executable transformations belong here.
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

    public void Prepare(string root, string source, RealmInfo realm, string? expectedFinalRuntimePath = null)
    {
        if (realm.Client.RuntimeMode != ClientRuntimeMode.Isolated)
            throw new InvalidOperationException("Realm executable preparation requires an isolated realm.");
        var validation = new ManagedRuntimeValidator().Validate(root, realm, source,
            expectedFinalRuntimePath, validateRealmExecutable: false);
        if (!validation.IsValid)
            throw new InvalidOperationException("Cannot prepare realm executable: " + string.Join("\n", validation.Errors));
        var manifests = new ManagedRuntimeManifestService();
        var manifestPath = RuntimePaths.Resolve(root, ManagedRuntimeBuilder.ManifestRelativePath);
        var manifest = manifests.Load(manifestPath);
        var sourcePath = VerifiedSource(source, realm, manifest);
        var sourceHash = Hash(sourcePath);
        var relative = FileName(manifest.RealmName, manifest.RealmId);
        var target = RuntimePaths.Resolve(root, relative);
        RejectCaseCollision(root, relative);

        if (manifest.RealmExecutable is { } owned)
        {
            if (!owned.SourceRelativePath.Equals(Path.GetFileName(sourcePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Recorded realm executable has a different source; explicit repair is required.");
            if (File.Exists(target))
            {
                RequireValid(root, source, realm, manifest);
                return; // Never rewrite or replace an existing valid executable.
            }
            if (Directory.Exists(target)) throw new IOException("Realm executable path contains a directory; nothing was replaced.");
        }
        else
        {
            if (File.Exists(target) || Directory.Exists(target) || manifest.Files.Any(e =>
                e.RuntimeRelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Realm executable filename collides with an unmanaged or baseline file; nothing was replaced: " + relative);
        }

        var temporary = RuntimePaths.Resolve(root, ".portalkeeper/realm-executable-" + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try
        {
            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            if (Hash(temporary) != sourceHash || Hash(sourcePath) != sourceHash ||
                !new HardLinkService().IsIndependentFile(temporary))
                throw new IOException("Realm executable copy validation failed; source and existing executables were not modified.");
            // Verify against the existing client rules again before claiming ownership.
            VerifiedSource(source, realm, manifest);
            RejectCaseCollision(root, relative);
            if (File.Exists(target) || Directory.Exists(target))
                throw new IOException("Realm executable destination appeared during preparation; nothing was replaced.");

            if (manifest.RealmExecutable is null)
            {
                manifest.RealmExecutable = new ManagedRealmExecutable
                {
                    SourceRelativePath = Path.GetFileName(sourcePath), SourceSha256 = sourceHash,
                    RuntimeRelativePath = relative, Sha256 = sourceHash
                };
                manifest.Files.Add(new ManagedRuntimeFileEntry
                { RuntimeRelativePath = relative, Kind = ManagedRuntimeFileKind.RealmOwned, Sha256 = sourceHash });
                manifest.LaunchExecutableRelativePath = relative;
                // Reserve ownership only after validating the copy. If interrupted after
                // this atomic save, the missing file is not ready and preparation can retry.
                manifests.Save(manifest, manifestPath);
            }
            // Never overwrite: a late collision, even at a recorded path, fails closed.
            File.Move(temporary, target, overwrite: false);
            RequireValid(root, source, realm, manifest);
        }
        finally
        {
            if (created && File.Exists(temporary)) File.Delete(temporary);
        }
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
        if (!Hash(path).Equals(executable.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !new HardLinkService().IsIndependentFile(path))
            throw new InvalidOperationException("Managed realm executable is corrupt or shares file identity. Launch refused; preserve/move the unexpected file outside the runtime for explicit repair, then prepare again.");
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
