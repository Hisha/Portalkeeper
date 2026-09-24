using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Portalkeeper.Models;
namespace Portalkeeper.Services;

public sealed class PatchService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly HttpClient _http;
    public PatchService(HttpClient? http = null) => _http = http ?? Http;

    private static WowPatchAllocationStore? Allocations(string root, PatchDefinition patch, RealmInfo? realm)
    {
        if (patch.InstallMode == PatchInstallMode.File) return null;
        if (patch.InstallMode != PatchInstallMode.WowPatch) throw new InvalidDataException("Unknown patch InstallMode.");
        if (patch.FileName.Length != 0 || patch.InstallDirectory.Length != 0)
            throw new InvalidDataException("WowPatch must omit FileName and InstallDirectory.");
        return new WowPatchAllocationStore(root, realm ?? throw new InvalidDataException("WowPatch requires an active realm."));
    }
    public string Destination(string root, PatchDefinition patch, RealmInfo? realm = null)
    {
        var allocations = Allocations(root, patch, realm);
        return allocations is null ? FileDestination(root, patch) : allocations.Find(patch.Id)
            ?? throw new InvalidDataException("Missing — install to allocate a WoW patch slot.");
    }
    private static string FileDestination(string root, PatchDefinition patch) => ManagedPath.Resolve(root,
        Path.Combine(ManagedPath.Relative(patch.InstallDirectory), ManagedPath.Relative(patch.FileName, true)));
    public PatchInfo Inspect(string root, PatchDefinition patch, RealmInfo? realm = null)
    {
        try
        {
            var path = Destination(root, patch, realm);
            bool exists = File.Exists(path), valid = exists && Matches(path, patch.Sha256);
            return new(patch, path, exists, valid, !exists ? "Missing" : valid ? (patch.Sha256.Length == 0 ? "Installed (no hash supplied)" : "SHA-256 verified") : "SHA-256 mismatch — repair required");
        }
        catch (Exception ex) { return new(patch, "", false, false, ex.Message); }
    }
    private static bool Matches(string path, string hash)
    {
        ManagedPath.Hash(hash);
        if (hash.Length == 0) return true;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
    public async Task InstallAsync(string root, PatchDefinition patch, RealmInfo? realm = null)
    {
        if (patch.SourceType != "HTTP") throw new InvalidDataException("Unsupported patch source.");
        ManagedPath.Url(patch.SourceUrl);
        ManagedPath.Hash(patch.Sha256);
        var allocations = Allocations(root, patch, realm); // Validate client before creating any metadata.
        using var operationLock = allocations is null ? null : WowPatchAllocationStore.AcquireLock(root);
        if (allocations is not null) allocations = Allocations(root, patch, realm); // Reload under the client lock.
        var owned = allocations?.Find(patch.Id);
        var destination = allocations is null ? FileDestination(root, patch) : owned ?? allocations.Choose(patch.Id);
        ManagedRuntimeWriteGuard.Check(root, destination);
        // Explicit action may update a hashless patch; hashed valid files need no download.
        if (patch.Sha256.Length > 0 && File.Exists(destination) && Matches(destination, patch.Sha256)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var response = await _http.GetAsync(patch.SourceUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await response.Content.CopyToAsync(output); }
            if (!Matches(temp, patch.Sha256)) throw new InvalidDataException("Downloaded patch failed SHA-256 validation. Existing patch preserved.");
            if (allocations is null) destination = FileDestination(root, patch);
            else
            {
                // Recheck paths, case aliases and ledger after the network wait.
                allocations = Allocations(root, patch, realm)!;
                var current = owned is null ? allocations.Choose(patch.Id) : allocations.Find(patch.Id);
                if (!string.Equals(current, destination, StringComparison.Ordinal))
                    throw new InvalidDataException("WoW patch destination changed during download. Retry installation.");
            }
            ManagedRuntimeWriteGuard.Check(root, destination);
            Backup(root, destination);
            // A newly chosen slot is never replaced, even if a foreign file appeared
            // after scanning. Existing allocations retain the shared replacement path.
            File.Move(temp, destination, allocations is null || owned is not null);
            if (allocations is not null && owned is null)
            {
                // Install first, then atomically record ownership. If persistence fails,
                // remove only our new file. A crash in this gap leaves an unowned file,
                // never permission to replace somebody else's MPQ on a later run.
                try { allocations.Record(patch.Id, destination); }
                catch
                {
                    ManagedPath.Resolve(root, Path.GetRelativePath(root, destination));
                    File.Delete(destination);
                    throw;
                }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Remove(string root, PatchDefinition patch, RealmInfo? realm = null)
    {
        var allocations = Allocations(root, patch, realm);
        using var operationLock = allocations is null ? null : WowPatchAllocationStore.AcquireLock(root);
        var destination = allocations is null ? FileDestination(root, patch) : Allocations(root, patch, realm)!.Find(patch.Id);
        if (destination is null || !File.Exists(destination)) return;
        ManagedRuntimeWriteGuard.Check(root, destination);
        Backup(root, destination);
        File.Delete(destination);
        // Explicit removal retains the allocation so repair reuses the same slot.
    }
    private static void Backup(string root, string destination)
    {
        if (!File.Exists(destination)) return;
        var backup = ManagedPath.Resolve(root, Path.Combine(".portalkeeper", "backups", "patches", Guid.NewGuid().ToString("N"), Path.GetFileName(destination)));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(destination, backup);
    }
}
