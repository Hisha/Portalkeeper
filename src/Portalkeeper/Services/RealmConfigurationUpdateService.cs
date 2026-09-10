using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
namespace Portalkeeper.Services;

public sealed class RealmConfigurationUpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly Func<string, Task<byte[]>> _download;
    public RealmConfigurationUpdateService(Func<string, Task<byte[]>>? download = null) => _download = download ?? Http.GetByteArrayAsync;
    public async Task<RealmConfigurationUpdateResult> CheckForUpdateAsync(string path, string configUrl)
    {
        if (string.IsNullOrWhiteSpace(configUrl)) return new(UpdateCheckStatus.NoConfigUrl, null, null, null);
        try
        {
            ManagedPath.Url(configUrl);
            var bytes = await _download(configUrl);
            new RealmConfigurationService().Parse(System.Text.Encoding.UTF8.GetString(bytes));
            var old = await File.ReadAllBytesAsync(path);
            return new(old.AsSpan().SequenceEqual(bytes) ? UpdateCheckStatus.NoUpdateAvailable : UpdateCheckStatus.UpdateAvailable, null, null, bytes);
        }
        catch (InvalidDataException ex) { return new(UpdateCheckStatus.ValidationFailure, null, null, null, ex.Message); }
        catch (Exception) { return new(UpdateCheckStatus.DownloadFailure, null, null, null, "Configuration download failed; the existing file is preserved."); }
    }
    public async Task<RealmConfigurationUpdateResult> ApplyUpdateAsync(string path, byte[] bytes, bool overwrite = true)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            new RealmConfigurationService().Load(temporary);
            if (overwrite && File.Exists(path))
            {
                var backups = Path.Combine(Path.GetDirectoryName(path)!, ".portalkeeper", "backups");
                Directory.CreateDirectory(backups);
                File.Copy(path, Path.Combine(backups, Path.GetFileName(path) + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "." + Guid.NewGuid().ToString("N") + ".bak"));
            }
            File.Move(temporary, path, overwrite);
            return new(UpdateCheckStatus.UpdateApplied, null, null, null);
        }
        catch (InvalidDataException ex) { return new(UpdateCheckStatus.ValidationFailure, null, null, null, ex.Message); }
        catch (Exception) { return new(UpdateCheckStatus.ReplacementFailure, null, null, null, "Could not safely replace the realm configuration; the existing file is preserved."); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
public sealed record RealmConfigurationUpdateResult(UpdateCheckStatus Status, string? LocalHash, string? RemoteHash, byte[]? RemoteBytes, string? Message = null);
public enum UpdateCheckStatus { NoConfigUrl, NoUpdateAvailable, UpdateAvailable, DownloadFailure, ValidationFailure, ReplacementFailure, UpdateApplied }
