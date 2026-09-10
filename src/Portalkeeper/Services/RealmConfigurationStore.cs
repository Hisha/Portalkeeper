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
        Directory.CreateDirectory(_directory);
        var existing = Files(_directory).ToArray();
        if (existing.Length > 0) return existing;
        var sources = roots.Where(Directory.Exists).SelectMany(root => new[] { root, Path.Combine(root, "config") })
            .Where(Directory.Exists).SelectMany(Files).Select(Path.GetFullPath).Distinct().ToArray();
        // Do not silently select one of several realms.
        if (sources.Length != 1) return sources;
        var target = Path.Combine(_directory, Path.GetFileName(sources[0]));
        var bytes = File.ReadAllBytes(sources[0]);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (!File.ReadAllBytes(temp).AsSpan().SequenceEqual(bytes)) throw new IOException("Realm import verification failed.");
            File.Move(temp, target, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new[] { target }; // Originals are deliberately retained, including legacy files.
    }
    private static IEnumerable<string> Files(string directory) => Directory.EnumerateFiles(directory, "*.realm.conf")
        .Where(p => !Path.GetFileName(p).Equals("example.realm.conf", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p);
    public async Task<(RealmInfo? Realm, string Status)> LoadAsync(string path, bool refresh = true)
    {
        var parser = new RealmConfigurationService();
        RealmInfo? realm = null;
        string url;
        bool legacy;
        try
        {
            var text = await File.ReadAllTextAsync(path);
            var ini = RealmConfigurationService.ReadIni(text);
            legacy = !ini.ContainsKey("Config") && ini.ContainsKey("Server");
            if (legacy)
            {
                url = ini.TryGetValue("Updates", out var fields) && fields.TryGetValue("UpdateURL", out var found) ? found : "";
                if (url.Length == 0) return (null, "Legacy realm configuration preserved. Obtain a Schema v1 realm.conf from your realm administrator and place it in " + _directory + ".");
            }
            else { realm = parser.Parse(text); url = realm.ConfigUrl; }
            if ((!refresh && !legacy) || url.Length == 0) return (realm, "Schema v1 configuration loaded.");
            var result = await _updates.CheckForUpdateAsync(path, url);
            if (result.Status == UpdateCheckStatus.UpdateAvailable)
                result = await _updates.ApplyUpdateAsync(path, result.RemoteBytes!);
            if (result.Status == UpdateCheckStatus.UpdateApplied)
                return (parser.Load(path), legacy ? "Legacy realm migrated to Schema v1; original configuration backed up." : "Realm configuration refreshed and backed up.");
            if (result.Status == UpdateCheckStatus.NoUpdateAvailable) return (realm, "Realm configuration is current.");
            return (realm, (legacy ? "Legacy migration could not complete. Original configuration preserved. " : "Using last known-good realm configuration. ") + result.Message);
        }
        catch (Exception ex) { return (realm, "Realm configuration needs attention: " + ex.Message); }
    }
}
