using System;
using System.IO;
using Portalkeeper.Models;
using Portalkeeper.Models.Runtime;

namespace Portalkeeper.Services;

public sealed class RealmRuntimeResolver
{
    private readonly string? _runtimeRoot;
    public RealmRuntimeResolver(string? runtimeRoot = null) => _runtimeRoot = runtimeRoot;

    // Candidate path is for read-only inspection/preparation, never a launch decision.
    public string GetRuntimePath(RealmInfo realm) => RuntimePaths.RuntimeRootOfRealm(
        _runtimeRoot ?? RuntimePaths.DefaultRuntimeRoot(), RealmIdentity.FromRealm(realm));

    public string ResolveEffectiveClientPath(string sourceClientPath, RealmInfo? realm = null)
    {
        if (realm?.Client.RuntimeMode != ClientRuntimeMode.Isolated)
            return string.IsNullOrWhiteSpace(sourceClientPath) ? sourceClientPath : Path.GetFullPath(sourceClientPath);
        var path = GetRuntimePath(realm);
        RequireReady(path, sourceClientPath, realm);
        return path;
    }

    public static void RequireReady(string path, string source, RealmInfo realm)
    {
        if (RuntimePaths.IsStagingDirectoryName(Path.GetFileName(Path.TrimEndingDirectorySeparator(path))))
            throw new InvalidOperationException("A staging runtime cannot be used for launch.");
        var validation = new ManagedRuntimeValidator().Validate(path, realm, source);
        if (!validation.IsValid)
            throw new InvalidOperationException("Isolated runtime needs preparation or repair: " +
                string.Join(Environment.NewLine, validation.Errors));
        var manifest = new ManagedRuntimeManifestService().Load(Path.Combine(path, ManagedRuntimeBuilder.ManifestRelativePath));
        if (manifest.ProvisionedConfiguration != RealmRuntimePreparationService.ConfigurationKey(realm))
            throw new InvalidOperationException("Isolated runtime requires realm content preparation.");
        RealmRuntimePreparationService.RequireContent(path, realm);
    }

    public RealmClientRoots ResolveRoots(string sourceClientPath, RealmInfo? realm = null) =>
        new(sourceClientPath, ResolveEffectiveClientPath(sourceClientPath, realm));
}
