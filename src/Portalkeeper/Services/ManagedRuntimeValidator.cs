using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Portalkeeper.Models;
using Portalkeeper.Models.Runtime;

namespace Portalkeeper.Services;

public sealed class ManagedRuntimeValidationResult
{
    public bool IsValid { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

// Validates a constructed managed runtime against its persisted manifest.
// An incomplete manifest is never considered launchable; missing baseline
// files, escaping paths, missing directories or missing launch executable all
// fail validation. Authoritative hashes recorded in the manifest are verified;
// assets that are path-allowlist only are verified for existence, containment
// and (for links where the platform supports identity checks) file identity.
public sealed class ManagedRuntimeValidator
{
    private readonly ManagedRuntimeManifestService _manifestService = new();
    private readonly HardLinkService _hardLinks = new();

    public ManagedRuntimeValidationResult Validate(
        string runtimePath,
        RealmInfo realm,
        string sourceClientPath,
        string? expectedFinalRuntimePath = null,
        bool validateRealmExecutable = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePath);
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceClientPath);

        var errors = new List<string>();
        var fullRuntimePath = Path.GetFullPath(runtimePath);

        if (!Directory.Exists(fullRuntimePath))
        {
            errors.Add($"Runtime directory does not exist: {fullRuntimePath}");
            return new ManagedRuntimeValidationResult { IsValid = false, Errors = errors };
        }

        string manifestPath;
        try { manifestPath = RuntimePaths.Resolve(fullRuntimePath, ManagedRuntimeBuilder.ManifestRelativePath); }
        catch (Exception ex)
        {
            return new ManagedRuntimeValidationResult { Errors = new[] { ex.Message } };
        }

        if (!File.Exists(manifestPath))
        {
            errors.Add($"Managed runtime manifest is missing: {manifestPath}");
            return new ManagedRuntimeValidationResult { IsValid = false, Errors = errors };
        }

        ManagedRuntimeManifest manifest;

        try
        {
            manifest = _manifestService.Load(manifestPath);
        }
        catch (Exception ex)
        {
            errors.Add("Managed runtime manifest is invalid: " + ex.Message);
            return new ManagedRuntimeValidationResult { IsValid = false, Errors = errors };
        }

        if (manifest.State != ManagedRuntimeState.Complete)
            errors.Add("The runtime manifest is not marked complete; the runtime is not launchable.");

        var expectedRealmId = RealmIdentity.FromRealm(realm);
        if (!string.Equals(manifest.RealmId, expectedRealmId, StringComparison.OrdinalIgnoreCase))
            errors.Add(
                $"Runtime realm identity {manifest.RealmId} does not match the expected realm {expectedRealmId}.");

        if (!string.Equals(manifest.RealmName, realm.Name, StringComparison.Ordinal))
            errors.Add(
                $"Runtime realm name '{manifest.RealmName}' does not match the expected realm '{realm.Name}'.");

        if (!RuntimePaths.SamePath(manifest.SourceClientPath, sourceClientPath))
            errors.Add(
                $"Runtime source client '{manifest.SourceClientPath}' does not match the expected source '{sourceClientPath}'.");

        var desiredFinalPath = expectedFinalRuntimePath ?? fullRuntimePath;
        if (!RuntimePaths.SamePath(manifest.RuntimePath, desiredFinalPath))
            errors.Add(
                $"Runtime manifest records path '{manifest.RuntimePath}' but the runtime is at '{desiredFinalPath}'.");

        if (string.IsNullOrWhiteSpace(manifest.Locale))
            errors.Add("The runtime manifest does not record a locale.");
        else if (!Directory.Exists(Path.Combine(fullRuntimePath, "Data", manifest.Locale)))
            errors.Add($"The runtime locale directory Data/{manifest.Locale} does not exist.");

        foreach (var requiredDirectory in new[] { "Data", "Interface", "Interface/AddOns" })
        {
            if (!Directory.Exists(Path.Combine(fullRuntimePath, requiredDirectory)))
                errors.Add($"Required runtime directory {requiredDirectory} does not exist.");
        }

        if (manifest.LaunchExecutableRelativePath.Length == 0)
            errors.Add("The runtime manifest does not record a launch executable.");
        else if (validateRealmExecutable || manifest.RealmExecutable is null)
        {
            var launchPath = TryResolve(fullRuntimePath, manifest.LaunchExecutableRelativePath, errors, "Launch executable");
            if (launchPath is null || !File.Exists(launchPath))
                errors.Add($"The launch executable '{manifest.LaunchExecutableRelativePath}' does not exist in the runtime.");
        }

        ValidateFiles(fullRuntimePath, sourceClientPath, manifest, errors, validateRealmExecutable);
        if (validateRealmExecutable && manifest.RealmExecutable is not null)
        {
            try { RealmExecutableService.RequireValid(fullRuntimePath, sourceClientPath, realm, manifest); }
            catch (Exception ex) { errors.Add(ex.Message); }
        }

        return new ManagedRuntimeValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors
        };
    }

    private void ValidateFiles(
        string runtimePath,
        string sourceClientPath,
        ManagedRuntimeManifest manifest,
        List<string> errors,
        bool validateRealmExecutable)
    {
        foreach (var entry in manifest.Files)
        {
            ArgumentNullException.ThrowIfNull(entry);
            // Preparation may recover a missing executable, but never skips any baseline check.
            if (!validateRealmExecutable && manifest.RealmExecutable is { } executable &&
                entry.RuntimeRelativePath == executable.RuntimeRelativePath) continue;

            var runtimeFilePath = TryResolve(runtimePath, entry.RuntimeRelativePath, errors, "Runtime file");
            if (runtimeFilePath is null)
                continue;

            if (!File.Exists(runtimeFilePath))
            {
                errors.Add($"Recorded runtime file does not exist: {entry.RuntimeRelativePath}");
                continue;
            }

            if (entry.Sha256.Length != 0)
            {
                if (!VerifySha256(runtimeFilePath, entry.Sha256))
                    errors.Add($"Runtime file SHA-256 mismatch: {entry.RuntimeRelativePath}");
            }

            if (entry.Kind is ManagedRuntimeFileKind.LinkedBaseline
                or ManagedRuntimeFileKind.CopiedBaseline)
            {
                if (entry.SourceRelativePath.Length == 0)
                {
                    errors.Add($"Baseline entry lacks a source-relative path: {entry.RuntimeRelativePath}");
                    continue;
                }

                var sourceFilePath = TryResolve(sourceClientPath, entry.SourceRelativePath, errors, "Source file");
                if (sourceFilePath is null)
                    continue;

                if (!File.Exists(sourceFilePath))
                {
                    errors.Add($"Baseline source file no longer exists: {entry.SourceRelativePath}");
                    continue;
                }

                if (entry.Kind == ManagedRuntimeFileKind.LinkedBaseline)
                {
                    VerifyLinkedIdentity(runtimeFilePath, sourceFilePath, entry.RuntimeRelativePath, errors);
                }
                else
                {
                    VerifyCopiedIndependence(runtimeFilePath, sourceFilePath, entry.RuntimeRelativePath, errors);
                }
            }
        }
    }

    private void VerifyLinkedIdentity(string runtimeFile, string sourceFile, string relative, List<string> errors)
    {
        if (_hardLinks.CanVerifyFileIdentity)
        {
            if (!_hardLinks.AreSameFile(runtimeFile, sourceFile))
                errors.Add(
                    $"Recorded linked baseline file does not share file identity with its source: {relative}");
        }
        else
        {
            // Platform cannot check file identity today; existence and
            // containment have already been verified.
        }
    }

    private void VerifyCopiedIndependence(string runtimeFile, string sourceFile, string relative, List<string> errors)
    {
        if (_hardLinks.CanVerifyFileIdentity)
        {
            if (_hardLinks.AreSameFile(runtimeFile, sourceFile))
                errors.Add(
                    $"Recorded copied baseline file unexpectedly shares identity with its source: {relative}");
        }
    }

    private static string? TryResolve(
        string root,
        string relative,
        List<string> errors,
        string label)
    {
        try
        {
            var full = RuntimePaths.Resolve(root, relative);
            if (!RuntimePaths.IsWithin(root, full))
            {
                errors.Add($"{label} escapes its root: {relative}");
                return null;
            }
            return full;
        }
        catch (Exception ex)
        {
            errors.Add($"{label} path is invalid ({relative}): {ex.Message}");
            return null;
        }
    }

    private static bool VerifySha256(string path, string expected)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}