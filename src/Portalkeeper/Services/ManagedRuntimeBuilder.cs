using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Portalkeeper.Models;
using Portalkeeper.Models.Runtime;

namespace Portalkeeper.Services;

public sealed class ManagedRuntimeBuildOptions
{
    // The user's existing WoW installation. Read-only during construction.
    public string SourceClientPath { get; init; } = string.Empty;

    public RealmInfo Realm { get; init; } = new();

    // Portalkeeper-owned runtime storage root. Defaults to the application-data
    // runtime root, which may be on a different volume than the source client;
    // in that case construction reports that a copy fallback would be required
    // and does not silently copy gigabytes. Callers may select a root on the
    // same volume as the source (useful for the developer/test path).
    public string? RuntimeRoot { get; init; }

    // Optional explicit locale; otherwise discovered from the source client.
    public string? Locale { get; init; }

    // Optional explicit baseline allowlist; otherwise the well-known explicit
    // 3.3.5a allowlist for the selected locale is used.
    public BaselineManifest? BaselineManifest { get; init; }
}

public sealed class ManagedRuntimeBuildResult
{
    public string RuntimePath { get; init; } = string.Empty;

    public string Locale { get; init; } = string.Empty;

    public ManagedRuntimeManifest Manifest { get; init; } = new();

    // Allowlisted-but-optional assets absent from the source, skipped by design.
    public IReadOnlyList<string> SkippedAssets { get; init; } = Array.Empty<string>();
}

// Transactional construction of an isolated per-realm managed runtime from a
// verified source client.
//
//  1. validate source client (existing ClientService + realm requirements)
//  2. load/validate baseline allowlist
//  3. determine locale
//  4. determine stable realm identity and final runtime path
//  5. create a unique staging runtime under the runtime root
//  6. copy explicit root runtime files
//  7. hard-link eligible baseline Data files
//  8. hard-link eligible locale files
//  9. hard-link eligible Blizzard baseline addon files
// 10. write the managed runtime manifest (Complete) into staging
//     optionally provision required realm content and record its configuration
// 11. validate the constructed staging runtime
// 12. atomically promote staging to the final runtime location
//
// If ANY step fails the source remains untouched, the final path is never
// considered a runtime, and the staging directory (Portalkeeper-owned) is
// removed. If removal itself fails the orphaned staging path is reported in
// the exception message.
//
// A hard-linked baseline file is the SAME underlying file as its source; it
// is immutable and Portalkeeper never opens it for modification here.
public sealed class ManagedRuntimeBuilder
{
    public const string ManagedRuntimeManifestFileName = "managed-runtime.json";
    public const string ManifestRelativePath = ".portalkeeper/" + ManagedRuntimeManifestFileName;

    private readonly ClientService _clientService = new();
    private readonly BaselineManifestService _baselineManifestService = new();
    private readonly BaselineInventoryProvider _inventory = new();
    private readonly LocaleDiscovery _localeDiscovery = new();
    private readonly HardLinkService _hardLinks = new();
    private readonly ManagedRuntimeManifestService _manifestService = new();
    private readonly ManagedRuntimeValidator _validator = new();

    public ManagedRuntimeBuildResult Build(ManagedRuntimeBuildOptions options) =>
        BuildAsync(options).GetAwaiter().GetResult();

    public async Task<ManagedRuntimeBuildResult> BuildAsync(ManagedRuntimeBuildOptions options,
        Func<string, Task>? provision = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SourceClientPath);
        ArgumentNullException.ThrowIfNull(options.Realm);
        if (!options.Realm.IsConfigured)
            throw new InvalidOperationException("The realm configuration is incomplete.");

        var sourceClientPath = Path.GetFullPath(options.SourceClientPath);

        // 1. Validate the source with the existing client validator and the
        //    realm's client requirements. Never weakens validation.
        var validated = _clientService.ValidateClient(sourceClientPath, options.Realm.Client);
        if (!validated.IsSupportedClient)
            throw new InvalidOperationException(
                "The source client is not supported: " + validated.StatusMessage);

        var wowExecutable = ClientService.FindWowExecutable(sourceClientPath, options.Realm.Client.Executable)
            ?? throw new FileNotFoundException(
                "Wow.exe was not found in the source client.");

        var sourceWowHash = ComputeSha256(wowExecutable);

        // 3. Determine locale.
        var locale = options.Locale;
        if (string.IsNullOrWhiteSpace(locale))
            locale = _localeDiscovery.Discover(sourceClientPath);
        else
            ManagedPath.Relative(locale, fileName: true);

        // 2. Load/validate the explicit baseline allowlist.
        var manifest = options.BaselineManifest ?? _inventory.BuildForLocale(locale);
        if (manifest is null)
            throw new InvalidDataException("A baseline manifest must be supplied.");
        _baselineManifestService.Validate(manifest);

        // 4. Stable realm identity and final runtime path.
        var realmId = RealmIdentity.FromRealm(options.Realm);
        var runtimeRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(options.RuntimeRoot)
                ? RuntimePaths.DefaultRuntimeRoot()
                : options.RuntimeRoot);

        if (RuntimePaths.SamePath(sourceClientPath, runtimeRoot) ||
            RuntimePaths.IsWithin(sourceClientPath, runtimeRoot) ||
            RuntimePaths.IsWithin(runtimeRoot, sourceClientPath))
        {
            throw new InvalidOperationException(
                "The runtime storage root must be separate from the source client.");
        }

        var finalPath = RuntimePaths.RuntimeRootOfRealm(runtimeRoot, realmId);

        if (Directory.Exists(finalPath))
        {
            throw new InvalidOperationException(
                $"A runtime already exists at {finalPath}. Portalkeeper does not overwrite " +
                "an existing runtime automatically; use an explicit Repair/Rebuild in a " +
                "future checkpoint.");
        }

        // 5. Preflight: hard links require a common volume. Never fall back to
        //    an automatic multi-gigabyte copy.
        Directory.CreateDirectory(runtimeRoot);

        var stagingPath = CreateUniqueStagingDirectory(runtimeRoot, realmId);

        try
        {
            if (_hardLinks.SameFilesystem(sourceClientPath, stagingPath) == false)
            {
                throw new InvalidOperationException(
                    _hardLinks.DescribeFailure(HardLinkFailure.CrossDevice));
            }

            BuildIntoStaging(sourceClientPath, stagingPath, manifest, options.Realm,
                out var entries, out var skipped);

            var runtimeManifest = BuildManifest(
                options.Realm, realmId, sourceClientPath, sourceWowHash,
                finalPath, locale, entries);

            _manifestService.Save(
                runtimeManifest,
                Path.Combine(stagingPath, ManifestRelativePath));

            if (provision is not null)
            {
                await provision(stagingPath).ConfigureAwait(false);
                runtimeManifest.ProvisionedConfiguration = RealmRuntimePreparationService.ConfigurationKey(options.Realm);
                _manifestService.Save(runtimeManifest, Path.Combine(stagingPath, ManifestRelativePath));
            }

            var stagedValidation = _validator.Validate(
                stagingPath, options.Realm, sourceClientPath, expectedFinalRuntimePath: finalPath);

            if (!stagedValidation.IsValid)
            {
                throw new InvalidOperationException(
                    "The constructed staging runtime failed validation: " +
                    Environment.NewLine + string.Join(Environment.NewLine, stagedValidation.Errors));
            }

            // 15. Atomic promotion (rename within the same volume).
            Directory.Move(stagingPath, finalPath);

            // The runtime's manifest now physically resides at the final path;
            // revalidate there to confirm the promoted state.
            var finalValidation = _validator.Validate(
                finalPath, options.Realm, sourceClientPath, expectedFinalRuntimePath: finalPath);

            if (!finalValidation.IsValid)
            {
                throw new InvalidOperationException(
                    "The promoted runtime failed validation: " +
                    Environment.NewLine + string.Join(Environment.NewLine, finalValidation.Errors));
            }

            return new ManagedRuntimeBuildResult
            {
                RuntimePath = finalPath,
                Locale = locale,
                Manifest = runtimeManifest,
                SkippedAssets = skipped
            };
        }
        catch
        {
            if (Directory.Exists(stagingPath))
            {
                try
                {
                    Directory.Delete(stagingPath, recursive: true);
                }
                catch (Exception cleanupEx)
                {
                    throw new InvalidOperationException(
                        "Runtime construction failed and the staging directory could not be removed: " +
                        stagingPath + ". Remove it manually. Original error: " + cleanupEx.Message,
                        cleanupEx);
                }
            }

            throw;
        }
    }

    private void BuildIntoStaging(
        string sourceClientPath,
        string stagingPath,
        BaselineManifest manifest,
        RealmInfo realm,
        out List<ManagedRuntimeFileEntry> entries,
        out List<string> skipped)
    {
        entries = new List<ManagedRuntimeFileEntry>();
        skipped = new List<string>();

        foreach (var asset in manifest.Assets)
        {
            if (asset.Kind != BaselineAssetKind.File)
                throw new InvalidDataException(
                    "Runtime construction currently supports file baseline assets only: " +
                    asset.RelativePath);

            var sourcePath = ResolveInside(sourceClientPath, asset.RelativePath, "source");
            var stagingFilePath = ResolveInside(stagingPath, asset.RelativePath, "staging");

            if (!File.Exists(sourcePath))
            {
                if (asset.Required)
                {
                    throw new InvalidDataException(
                        $"Required baseline asset is missing from the source client: {asset.RelativePath}");
                }

                skipped.Add(asset.RelativePath);
                continue;
            }

            var directory = Path.GetDirectoryName(stagingFilePath)
                ?? throw new InvalidOperationException("Unable to determine the runtime directory.");
            Directory.CreateDirectory(directory);

            if (Directory.Exists(stagingFilePath))
                throw new InvalidDataException(
                    "Runtime destination collides with a directory: " + asset.RelativePath);

            var kind = CreateAssetFile(sourcePath, stagingFilePath, asset);

            var sha256 = asset.Sha256;
            if (sha256.Length == 0 && string.Equals(
                    asset.RelativePath, realm.Client.Executable, StringComparison.OrdinalIgnoreCase) &&
                realm.Client.ExecutableSha256.Length != 0)
            {
                // The realm's authoritative Wow.exe hash becomes the expected
                // hash for the copied launch executable in the runtime.
                sha256 = realm.Client.ExecutableSha256;
            }

            // Copied files have no link identity to validate; record their bytes
            // even when the realm does not publish an executable hash.
            if (sha256.Length == 0 && kind == ManagedRuntimeFileKind.CopiedBaseline)
                sha256 = ComputeSha256(sourcePath);

            entries.Add(new ManagedRuntimeFileEntry
            {
                RuntimeRelativePath = asset.RelativePath,
                SourceRelativePath = asset.RelativePath,
                Kind = kind,
                Sha256 = sha256
            });
        }
    }

    private ManagedRuntimeFileKind CreateAssetFile(
        string sourcePath,
        string stagingFilePath,
        BaselineAsset asset)
    {
        if (asset.Category == BaselineAssetCategory.Root)
        {
            File.Copy(sourcePath, stagingFilePath, overwrite: false);
            return ManagedRuntimeFileKind.CopiedBaseline;
        }

        if (!_hardLinks.TryCreateHardLink(sourcePath, stagingFilePath, out var failure))
        {
            throw new InvalidOperationException(
                $"Cannot hard-link baseline asset {asset.RelativePath} into the runtime: " +
                _hardLinks.DescribeFailure(failure));
        }

        if (!File.Exists(stagingFilePath))
            throw new IOException(
                $"Hard link was reported successful but the runtime file is absent: {asset.RelativePath}");

        if (_hardLinks.CanVerifyFileIdentity &&
            !_hardLinks.AreSameFile(sourcePath, stagingFilePath))
        {
            throw new IOException(
                $"Hard link identity verification failed for {asset.RelativePath}.");
        }

        return ManagedRuntimeFileKind.LinkedBaseline;
    }

    private string CreateUniqueStagingDirectory(string runtimeRoot, string realmId)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Path.Combine(runtimeRoot, RuntimePaths.NewStagingDirectoryName(realmId));

            if (Directory.Exists(candidate) || File.Exists(candidate))
                continue;

            Directory.CreateDirectory(candidate);
            return candidate;
        }

        throw new IOException("Unable to allocate a unique staging directory under " + runtimeRoot);
    }

    private static ManagedRuntimeManifest BuildManifest(
        RealmInfo realm,
        string realmId,
        string sourceClientPath,
        string sourceWowHash,
        string runtimePath,
        string locale,
        List<ManagedRuntimeFileEntry> entries)
    {
        var now = DateTimeOffset.UtcNow;

        var runtimeManifest = new ManagedRuntimeManifest
        {
            SchemaVersion = ManagedRuntimeManifest.CurrentSchemaVersion,
            RealmId = realmId,
            RealmName = realm.Name,
            Generation = "1",
            SourceClientPath = Path.GetFullPath(sourceClientPath),
            SourceExecutableSha256 = sourceWowHash,
            RuntimePath = Path.GetFullPath(runtimePath),
            Locale = locale,
            LaunchExecutableRelativePath = realm.Client.Executable,
            State = ManagedRuntimeState.Complete,
            CreatedUtc = now,
            CompletedUtc = now,
            Files = entries
        };

        return runtimeManifest;
    }

    private static string ResolveInside(string root, string relative, string label)
    {
        var path = RuntimePaths.Resolve(root, relative);
        if (!RuntimePaths.IsWithin(root, path))
            throw new InvalidDataException($"{label} path escapes its root: {relative}");
        return path;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}