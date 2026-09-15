using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

// Client-local state follows the existing .portalkeeper metadata convention.
// Keep all realms in one ledger so even absent/stale entries reserve their slots.
public sealed class WowPatchAllocationStore
{
    // Stock 3.3.5a uses patch.MPQ, patch-2.MPQ and patch-3.MPQ.
    // Custom numeric names have a single digit; never fall back to stock or letters.
    public const int FirstSlot = 4;
    public const int LastSlot = 9;
    public const string StateRelativePath = ".portalkeeper/wow-patches.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _root;
    private readonly string _data;
    private readonly string _realmId;
    private readonly HashSet<string> _fileDestinations;
    private readonly List<Allocation> _allocations;

    public WowPatchAllocationStore(string root, RealmInfo realm)
    {
        // Reuse the client validator, including the realm's executable/hash requirements.
        var client = new ClientService().ValidateClient(root, realm.Client);
        if (!realm.IsConfigured || !client.IsSupportedClient)
            throw new InvalidDataException("Select a valid WoW client and realm before managing WowPatch files. " + client.StatusMessage);
        _root = client.DirectoryPath;
        _data = ExistingEntry(_root, "Data") ?? throw new InvalidDataException("The WoW Data directory was not found.");
        _data = ManagedPath.Resolve(_root, Path.GetFileName(_data));
        if (!Directory.Exists(_data)) throw new InvalidDataException("The WoW Data directory is not a directory.");
        // Logical realm identity deliberately excludes source URLs, hashes and config location.
        _realmId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] {
            realm.Name.ToUpperInvariant(), realm.Address.ToUpperInvariant(),
            realm.AuthPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            realm.WorldPort.ToString(System.Globalization.CultureInfo.InvariantCulture) }))));
        _fileDestinations = realm.Patches.Where(p => p.InstallMode == PatchInstallMode.File)
            .Select(p => (ManagedPath.Relative(p.InstallDirectory) + "/" + ManagedPath.Relative(p.FileName, true)).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var statePath = StatePath();
        try
        {
            _allocations = File.Exists(statePath)
                ? JsonSerializer.Deserialize<List<Allocation>>(File.ReadAllText(statePath))
                    ?? throw new InvalidDataException("Ownership ledger cannot be null.")
                : new();
            var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _allocations)
            {
                if (entry is null || string.IsNullOrEmpty(entry.RealmId) || entry.RealmId.Length != 64 || !entry.RealmId.All(Uri.IsHexDigit) ||
                    !ValidKey(entry.PatchKey) || !owners.Add(entry.RealmId + "/" + entry.PatchKey))
                    throw new InvalidDataException("Invalid or duplicate ownership identity.");
                ValidateDestination(entry.Destination);
                if (!destinations.Add(entry.Destination)) throw new InvalidDataException("Two managed patches claim the same slot.");
                ResolvePath(entry.Destination); // Validate paths for every realm, including stale records.
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidDataException("Invalid WowPatch ownership metadata in " + StateRelativePath + ": " + ex.Message, ex);
        }
    }

    // This persistent lock file is never deleted: deleting locks can let two processes
    // lock different inodes. Hold the handle throughout download/commit/removal.
    public static FileStream AcquireLock(string root)
    {
        var path = ManagedPath.Resolve(root, ".portalkeeper/wow-patches.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("Another WowPatch operation is using this client. Try again when it finishes.", ex); }
    }

    public string? Find(string key)
    {
        ValidateKey(key);
        var entry = _allocations.SingleOrDefault(e => e.RealmId.Equals(_realmId, StringComparison.OrdinalIgnoreCase) &&
            e.PatchKey.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        if (_fileDestinations.Contains(entry.Destination))
            throw new InvalidDataException("An ordinary File patch conflicts with the owned WowPatch destination " + entry.Destination + ".");
        return ResolvePath(entry.Destination);
    }

    public string Choose(string key)
    {
        var existing = Find(key);
        if (existing is not null) return existing;
        for (int slot = FirstSlot; slot <= LastSlot; slot++)
        {
            var relative = $"Data/patch-{slot}.MPQ";
            if (_fileDestinations.Contains(relative) || _allocations.Any(e => e.Destination.Equals(relative, StringComparison.OrdinalIgnoreCase))) continue;
            // Files, directories and dangling links all occupy a logical slot.
            if (ExistingEntry(_data, Path.GetFileName(relative)) is not null) continue;
            return ResolvePath(relative);
        }
        throw new InvalidDataException("No available WoW patch slot exists in the client Data directory (patch-4.MPQ through patch-9.MPQ). Free a custom slot or use another client installation; unowned files will not be overwritten.");
    }

    public void Record(string key, string destination)
    {
        ValidateKey(key);
        var relative = "Data/" + Path.GetFileName(destination);
        ValidateDestination(relative);
        if (!string.Equals(ResolvePath(relative), destination, StringComparison.Ordinal))
            throw new InvalidDataException("WowPatch destination changed during installation.");
        if (_allocations.Any(e => e.Destination.Equals(relative, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("WowPatch destination is already owned.");
        _allocations.Add(new Allocation { RealmId = _realmId, PatchKey = key, Destination = relative });
        var path = StatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, _allocations, JsonOptions);
                stream.Flush(true);
            }
            ManagedPath.Resolve(_root, StateRelativePath);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private string StatePath() => ManagedPath.Resolve(_root, StateRelativePath);
    private static bool ValidKey(string? key) => key is not null && Regex.IsMatch(key, @"\A[A-Za-z0-9][A-Za-z0-9_-]*\z");
    private static void ValidateKey(string key)
    {
        if (!ValidKey(key)) throw new InvalidDataException("Invalid WowPatch key.");
    }
    private static void ValidateDestination(string? relative)
    {
        // Accept casing, but only the exact two-component, portable relative format.
        if (relative is null || !Enumerable.Range(FirstSlot, LastSlot - FirstSlot + 1)
            .Any(slot => relative.Equals($"Data/patch-{slot}.MPQ", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Owned WowPatch destination must be Data/patch-N.MPQ in the supported custom numeric range.");
    }
    private string ResolvePath(string relative)
    {
        ValidateDestination(relative);
        var name = relative.Split('/')[1];
        var existing = ExistingEntry(_data, name);
        var path = ManagedPath.Resolve(_root, Path.Combine(Path.GetFileName(_data), existing is null ? name : Path.GetFileName(existing)));
        if (Directory.Exists(path)) throw new InvalidDataException("Owned WowPatch destination is a directory.");
        return path;
    }
    private static string? ExistingEntry(string directory, string name)
    {
        var entries = Directory.EnumerateFileSystemEntries(directory)
            .Where(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (entries.Length > 1) throw new InvalidDataException("Ambiguous case-insensitive WoW path: " + name + ". Keep only one case variant.");
        return entries.SingleOrDefault();
    }
    public sealed class Allocation
    {
        public string RealmId { get; set; } = "";
        public string PatchKey { get; set; } = "";
        public string Destination { get; set; } = "";
    }
}
