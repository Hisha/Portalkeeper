using System;
using System.IO;
using System.Security.Cryptography;

namespace Portalkeeper.Services;

/// <summary>
/// Puts an edited Wow.exe in place of the client's (LongPatchNameService, CharacterListLimitService,
/// WarbandSelectService, GuildBankSlotsService, LiveBagSlotsService, M2CacheFullPathsService, MoonPhasesService).
/// The file it replaces is kept under .portalkeeper/backups/executable, keyed by its own SHA-256, so
/// every version is kept once however often an edit is retried. A failure leaves the file as it was.
/// </summary>
public static class ClientExecutableEdit
{
    /// <summary>
    /// Replaces <paramref name="executable"/> (inside the client <paramref name="root"/>), whose bytes
    /// are <paramref name="original"/>, with <paramref name="edited"/>.
    /// </summary>
    public static void Replace(string root, string executable, byte[] original, byte[] edited)
    {
        var name = Path.GetFileName(executable);
        ManagedRuntimeWriteGuard.Check(root, executable);
        var temp = executable + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Written fresh, not copied: a copy of a read-only Wow.exe couldn't be written to.
            File.WriteAllBytes(temp, edited);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(executable));

            var backup = ManagedPath.Resolve(root, Path.Combine(".portalkeeper", "backups", "executable",
                Convert.ToHexString(SHA256.HashData(original))[..16].ToLowerInvariant(), name));
            if (!File.Exists(backup) || new FileInfo(backup).Length != original.Length)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                if (File.Exists(backup)) File.SetAttributes(backup, FileAttributes.Normal);
                File.Copy(executable, backup, true);
            }

            // Windows won't replace a read-only file; elsewhere the folder decides.
            var attributes = File.GetAttributes(executable);
            var readOnly = OperatingSystem.IsWindows() && (attributes & FileAttributes.ReadOnly) != 0;
            if (readOnly) File.SetAttributes(executable, attributes & ~FileAttributes.ReadOnly);
            try { File.Move(temp, executable, true); }
            finally { if (readOnly) File.SetAttributes(executable, File.GetAttributes(executable) | FileAttributes.ReadOnly); }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
