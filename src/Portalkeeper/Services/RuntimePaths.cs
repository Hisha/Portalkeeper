using System;
using System.IO;

namespace Portalkeeper.Services;

// Minimal filesystem helpers for managed runtime construction. Low-level path
// safety reuses ManagedPath; these helpers add canonicalization and containment
// checks without introducing overlays, junctions, or runtime swapping.
public static class RuntimePaths
{
    private static StringComparison Comparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static string Canonical(string path) => Path.GetFullPath(path);

    public static string NormalizeRelative(string value, bool fileName = false) =>
        ManagedPath.Relative(value, fileName);

    public static string Resolve(string root, string relative) =>
        ManagedPath.Resolve(root, relative);

    public static bool IsWithin(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
            return false;

        try
        {
            var fullRoot =
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;

            return Path.GetFullPath(candidate).StartsWith(fullRoot, Comparison);
        }
        catch
        {
            return false;
        }
    }

    public static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
            return true;

        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return string.Equals(Canonical(left!), Canonical(right!), Comparison);
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------
    // Managed runtime storage layout
    //
    // <runtime-root>/
    //     <realm-id>/
    //         .portalkeeper/managed-runtime.json   <- runtime manifest
    //         Wow.exe ...
    //         Data/
    //         Data/<locale>/
    //         Interface/AddOns/
    //     <realm-id>.staging-<unique-id>/          <- construction staging
    // ---------------------------------------------------------

    // Default Portalkeeper-owned runtime root, kept outside the source WoW
    // directory so runtime construction can never touch the user's install.
    // Because hard links require a common volume with the source client, the
    // builder validates volume compatibility before constructing; callers may
    // supply a different root (for example one on the same volume as the
    // source client) through ManagedRuntimeBuilder options.
    public static string DefaultRuntimeRoot()
    {
        var applicationData =
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);

        return Path.Combine(applicationData, "Portalkeeper", "runtimes");
    }

    public static string RuntimeRootOfRealm(string runtimeRoot, string realmId)
    {
        ManagedPath.Relative(realmId, fileName: true);
        return Path.Combine(Path.GetFullPath(runtimeRoot), realmId);
    }

    // A fresh staging directory name inside the runtime root. The staging
    // directory is Portalkeeper-owned and may be deleted if a build fails.
    public static string NewStagingDirectoryName(string realmId)
    {
        ManagedPath.Relative(realmId, fileName: true);
        return realmId + ".staging-" + Guid.NewGuid().ToString("N")[..12];
    }

    public static bool IsStagingDirectoryName(string directoryName) =>
        directoryName.Length > 0 && directoryName.Contains(".staging-", StringComparison.Ordinal);
}