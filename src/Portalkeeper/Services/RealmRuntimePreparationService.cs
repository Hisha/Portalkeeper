using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

// Required content is installed before a new runtime is promoted. Existing
// baseline-only runtimes may be provisioned in place, but are never effective
// until the readiness marker and current requirements both pass validation.
public sealed class RealmRuntimePreparationService
{
    private readonly PatchService _patches;
    private readonly AddonInstallerService _installer;
    private readonly string? _runtimeRoot;

    public RealmRuntimePreparationService(string? runtimeRoot = null,
        PatchService? patches = null, AddonInstallerService? installer = null)
    {
        _runtimeRoot = runtimeRoot;
        _patches = patches ?? new();
        _installer = installer ?? new();
    }

    public static string ConfigurationKey(RealmInfo realm) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { realm.Client, realm.Addons, realm.Patches }))));

    public static void RequireContent(string root, RealmInfo realm)
    {
        var patches = new PatchService();
        foreach (var patch in realm.Patches.Where(p => p.Requirement == ComponentRequirement.Required))
        {
            var info = patches.Inspect(root, patch, realm);
            if (!info.IsValid) throw new InvalidOperationException($"Required patch {patch.Name}: {info.Status}");
        }
        var addons = new AddonService().InspectAddons(root,
            new AddonManifest { Addons = realm.Addons.Where(a => a.Required).ToList() });
        if (addons.Count != realm.Addons.Count(a => a.Required) ||
            addons.Any(a => !a.IsInstalled || a.IsUpdateAvailable))
            throw new InvalidOperationException("Required runtime addons need preparation.");
    }

    public async Task<string> PrepareAsync(string source, RealmInfo realm, IProgress<string>? progress = null)
    {
        var resolver = new RealmRuntimeResolver(_runtimeRoot);
        if (realm.Client.RuntimeMode == ClientRuntimeMode.Legacy)
            return resolver.ResolveEffectiveClientPath(source, realm);

        progress?.Report("Validating source client...");
        realm.Client.ThrowIfUnsupportedRequirement();
        var client = new ClientService().ValidateClient(source, realm.Client);
        if (!client.IsSupportedClient) throw new InvalidOperationException(client.StatusMessage);
        var generation2 = RealmExecutableService.RequiresGeneration2(realm);
        // Reject unsupported executable identities before downloads or runtime mutations.
        if (generation2)
            FrameXmlDigestOverrideRecipe.ValidateSource(File.ReadAllBytes(client.ExecutablePath));
        var path = resolver.GetRuntimePath(realm);
        if (Directory.Exists(path))
        {
            var validation = new ManagedRuntimeValidator().Validate(path, realm, source, validateRealmExecutable: false);
            if (!validation.IsValid)
                throw new InvalidOperationException("Existing isolated runtime needs explicit repair/rebuild: " +
                    string.Join(Environment.NewLine, validation.Errors));
            // Complete executable migration/recovery before changing content metadata.
            // This preserves the prior manifest and avoids re-provisioning a ready CP4 runtime.
            if (generation2)
            {
                progress?.Report("Preparing verified FrameXML realm executable...");
                new RealmExecutableService().Prepare(path, source, realm);
            }
            try { return resolver.ResolveEffectiveClientPath(source, realm); }
            catch (InvalidOperationException) { /* Valid baseline, content needs preparation. */ }
            var manifests = new ManagedRuntimeManifestService();
            var manifestPath = Path.Combine(path, ManagedRuntimeBuilder.ManifestRelativePath);
            var manifest = manifests.Load(manifestPath);
            manifest.ProvisionedConfiguration = "";
            manifests.Save(manifest, manifestPath);
            await ProvisionAsync(path, realm, progress).ConfigureAwait(false);
            manifest = manifests.Load(manifestPath);
            validation = new ManagedRuntimeValidator().Validate(path, realm, source);
            if (!validation.IsValid) throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));
            manifest.ProvisionedConfiguration = ConfigurationKey(realm);
            manifests.Save(manifest, manifestPath);
        }
        else
        {
            progress?.Report("Constructing isolated realm client; your original installation is unchanged...");
            await new ManagedRuntimeBuilder().BuildAsync(new ManagedRuntimeBuildOptions
            { SourceClientPath = source, Realm = realm, RuntimeRoot = _runtimeRoot },
                async staging =>
                {
                    await ProvisionAsync(staging, realm, progress).ConfigureAwait(false);
                    if (generation2)
                    {
                        progress?.Report("Preparing verified realm executable...");
                        new RealmExecutableService().Prepare(staging, source, realm,
                            expectedFinalRuntimePath: resolver.GetRuntimePath(realm));
                    }
                }).ConfigureAwait(false);
        }
        progress?.Report("Validating isolated realm client...");
        return resolver.ResolveEffectiveClientPath(source, realm);
    }

    private async Task ProvisionAsync(string root, RealmInfo realm, IProgress<string>? progress)
    {
        progress?.Report("Provisioning required realm content...");
        foreach (var patch in realm.Patches.Where(p => p.Requirement == ComponentRequirement.Required))
            if (!_patches.Inspect(root, patch, realm).IsValid)
                await _patches.InstallAsync(root, patch, realm).ConfigureAwait(false);
        foreach (var definition in realm.Addons.Where(a => a.Required))
        {
            var addon = definition.IsGitHubSource
                ? await new GitHubAddonSourceService().ResolveAsync(definition).ConfigureAwait(false) : definition;
            var info = new AddonService().InspectAddons(root,
                new AddonManifest { Addons = new() { addon } }).Single();
            if (!info.IsInstalled || info.IsUpdateAvailable)
                await _installer.InstallOrUpdateAsync(root, addon).ConfigureAwait(false);
        }
        RequireContent(root, realm);
    }
}
