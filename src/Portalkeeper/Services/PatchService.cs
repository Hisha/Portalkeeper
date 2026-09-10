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
    public string Destination(string root, PatchDefinition patch) => ManagedPath.Resolve(root,
        Path.Combine(ManagedPath.Relative(patch.InstallDirectory), ManagedPath.Relative(patch.FileName, true)));
    public PatchInfo Inspect(string root, PatchDefinition patch)
    {
        try
        {
            var path = Destination(root, patch);
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
    public async Task InstallAsync(string root, PatchDefinition patch)
    {
        if (patch.SourceType != "HTTP") throw new InvalidDataException("Unsupported patch source.");
        ManagedPath.Url(patch.SourceUrl);
        var destination = Destination(root, patch);
        // Explicit action may update a hashless patch; hashed valid files need no download.
        if (patch.Sha256.Length > 0 && Inspect(root, patch).IsValid) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var response = await Http.GetAsync(patch.SourceUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var output = File.Create(temp)) { await response.Content.CopyToAsync(output); }
            if (!Matches(temp, patch.Sha256)) throw new InvalidDataException("Downloaded patch failed SHA-256 validation. Existing patch preserved.");
            destination = Destination(root, patch);
            Backup(root, destination);
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Remove(string root, PatchDefinition patch)
    {
        var destination = Destination(root, patch);
        if (!File.Exists(destination)) return;
        Backup(root, destination);
        File.Delete(destination);
    }
    private static void Backup(string root, string destination)
    {
        if (!File.Exists(destination)) return;
        var backup = ManagedPath.Resolve(root, Path.Combine(".portalkeeper", "backups", "patches", Guid.NewGuid().ToString("N"), Path.GetFileName(destination)));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(destination, backup);
    }
}
