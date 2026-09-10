using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Portalkeeper.Models;
namespace Portalkeeper.Services;

public sealed class RealmConfigurationStore
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Portalkeeper", "realms");
    private readonly string _directory;
    private readonly RealmConfigurationUpdateService _updates;
    public RealmConfigurationStore(string? directory = null, RealmConfigurationUpdateService? updates = null)
    { _directory = directory ?? DefaultDirectory; _updates = updates ?? new(); }
    public IReadOnlyList<string> Discover(params string[] roots)
    {
        var existing = Directory.Exists(_directory) ? Files(_directory).ToArray() : Array.Empty<string>();
        // A successful migration wins even if an earlier release copied a legacy
        // file into the store. Multiple valid v1 realms still require selection.
        var schemaFiles = existing.Where(IsValidSchemaFile).ToArray();
        if (schemaFiles.Length > 0) return schemaFiles;
        if (existing.Length > 0) return existing;
        var sources = roots.Where(Directory.Exists).SelectMany(root => new[] { root, Path.Combine(root, "config") })
            .Where(Directory.Exists).SelectMany(Files).Select(Path.GetFullPath).Distinct().ToArray();
        // Do not silently select one of several realms.
        if (sources.Length != 1) return sources;
        var bytes = File.ReadAllBytes(sources[0]);
        // Legacy sources remain untouched and usable even when persistence fails.
        // Only a validated migration is written to the persistent store.
        if (RealmConfigurationService.IsLegacy(RealmConfigurationService.ReadIni(System.Text.Encoding.UTF8.GetString(bytes))))
            return sources;
        Directory.CreateDirectory(_directory);
        var target = Path.Combine(_directory, Path.GetFileName(sources[0]));
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (!File.ReadAllBytes(temp).AsSpan().SequenceEqual(bytes)) throw new IOException("Realm import verification failed.");
            File.Move(temp, target, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new[] { target };
    }
    // Selection includes usable persistent legacy realms as well as Schema v1.
    // Persistent data retains precedence over adjacent bootstrap files.
    public IReadOnlyList<RealmChoice> GetAvailableRealms(params string[] roots)
    {
        var persistent = Directory.Exists(_directory) ? Files(_directory).ToArray() : Array.Empty<string>();
        var candidates = persistent.Length > 0 ? persistent : Discover(roots);
        var choices = new List<RealmChoice>();
        var parser = new RealmConfigurationService();
        foreach (var path in candidates)
        {
            try
            {
                var text = File.ReadAllText(path);
                var realm = RealmConfigurationService.IsLegacy(RealmConfigurationService.ReadIni(text))
                    ? parser.ParseLegacy(text) : parser.Parse(text);
                if (realm.IsConfigured && !choices.Any(c => RealmChoice.SamePath(c.Path, path)))
                    choices.Add(new RealmChoice(Path.GetFullPath(path), realm));
            }
            catch (Exception) { /* Invalid/minimum-version-incompatible files are not selectable. */ }
        }
        return choices.OrderBy(c => c.IsLegacy).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Path, StringComparer.Ordinal).ToArray();
    }

    private static bool IsValidSchemaFile(string path)
    {
        try { return new RealmConfigurationService().Load(path).SchemaVersion == 1; }
        catch (Exception) { return false; }
    }
    private static IEnumerable<string> Files(string directory) => Directory.EnumerateFiles(directory, "*.realm.conf")
        .Where(p => !Path.GetFileName(p).Equals("example.realm.conf", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p);

    private string MigrationDestination(string source)
    {
        var name = Path.GetFileName(source);
        var candidate = Path.Combine(_directory, name);
        var stem = name.EndsWith(".realm.conf", StringComparison.OrdinalIgnoreCase)
            ? name[..^".realm.conf".Length] : Path.GetFileNameWithoutExtension(name);
        for (int suffix = 1; File.Exists(candidate) || Directory.Exists(candidate); suffix++)
            candidate = Path.Combine(_directory, stem + ".schema-v1" + (suffix == 1 ? "" : "-" + suffix) + ".realm.conf");
        return candidate;
    }
    private static string LegacyStatus(string detail) =>
        "Legacy compatibility mode: " + detail + " Original configuration preserved. You can still Enter Realm with a valid WoW client.";

    public async Task<(RealmInfo? Realm, string Status)> LoadAsync(string path, bool refresh = true, Action<string>? migrated = null)
    {
        var parser = new RealmConfigurationService();
        RealmInfo? realm = null;
        try
        {
            var text = await File.ReadAllTextAsync(path);
            var ini = RealmConfigurationService.ReadIni(text);
            if (RealmConfigurationService.IsLegacy(ini))
            {
                realm = parser.ParseLegacy(text);
                var url = ini.TryGetValue("Updates", out var fields) && fields.TryGetValue("UpdateURL", out var found) ? found : "";
                if (url.Length == 0) return (realm, LegacyStatus("Automatic Schema v1 upgrade is pending; no [Updates] UpdateURL is configured."));
                if (!refresh) return (realm, LegacyStatus("Automatic Schema v1 upgrade is pending; refresh was not requested."));
                var migration = await _updates.CheckForUpdateAsync(path, url);
                if (migration.Status == UpdateCheckStatus.UpdateAvailable)
                {
                    Directory.CreateDirectory(_directory);
                    var target = MigrationDestination(path);
                    // Never replace the source or another realm, even if a file
                    // appears between choosing the destination and committing it.
                    migration = await _updates.ApplyUpdateAsync(target, migration.RemoteBytes!, overwrite: false);
                    if (migration.Status == UpdateCheckStatus.UpdateApplied)
                    {
                        var migratedRealm = parser.Load(target);
                        migrated?.Invoke(Path.GetFullPath(target));
                        return (migratedRealm, "Legacy realm migrated to persistent Schema v1 configuration. Original legacy file preserved.");
                    }
                }
                return (realm, LegacyStatus("Automatic Schema v1 upgrade failed. " + migration.Message));
            }
            realm = parser.Parse(text);
            var configUrl = realm.ConfigUrl;
            if (!refresh || configUrl.Length == 0) return (realm, "Schema v1 configuration loaded.");
            var result = await _updates.CheckForUpdateAsync(path, configUrl);
            if (result.Status == UpdateCheckStatus.UpdateAvailable)
                result = await _updates.ApplyUpdateAsync(path, result.RemoteBytes!);
            if (result.Status == UpdateCheckStatus.UpdateApplied)
                return (parser.Load(path), "Realm configuration refreshed and backed up.");
            if (result.Status == UpdateCheckStatus.NoUpdateAvailable) return (realm, "Realm configuration is current.");
            return (realm, "Using last known-good realm configuration. " + result.Message);
        }
        catch (Exception ex)
        {
            return (realm, realm?.IsLegacyCompatibility == true
                ? LegacyStatus("Automatic Schema v1 upgrade failed. " + ex.Message)
                : "Realm configuration needs attention: " + ex.Message);
        }
    }
}
