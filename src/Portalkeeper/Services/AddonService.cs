using System;
using System.Collections.Generic;
using System.IO;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class AddonService
{
    private readonly AddonInstallStateService _installStateService = new();

    public IReadOnlyList<AddonInfo> InspectAddons(
        string clientDirectory,
        AddonManifest manifest)
    {
        var results = new List<AddonInfo>();

        var addonsDirectory = Path.Combine(
            clientDirectory,
            "Interface",
            "AddOns");

        foreach (var addon in manifest.Addons)
        {
            if (string.IsNullOrWhiteSpace(addon.Folder))
                continue;

            var addonDirectory = Path.Combine(
                addonsDirectory,
                addon.Folder);

            var installed = Directory.Exists(addonDirectory);
            var state = _installStateService.Load(
                clientDirectory,
                addon.Id);

            results.Add(
                new AddonInfo
                {
                    Definition = addon,
                    DirectoryPath = addonDirectory,
                    IsInstalled = installed,
                    InstalledVersion = installed
                        ? TryReadTocVersion(addonDirectory, addon.Folder)
                        : string.Empty,
                    InstalledSourceCommit = installed
                        ? state.SourceCommit
                        : string.Empty
                });
        }

        return results;
    }

    private static string TryReadTocVersion(
        string addonDirectory,
        string addonFolder)
    {
        try
        {
            var expectedToc = Path.Combine(
                addonDirectory,
                addonFolder + ".toc");

            string? tocPath = null;

            if (File.Exists(expectedToc))
            {
                tocPath = expectedToc;
            }
            else
            {
                var tocFiles = Directory.GetFiles(
                    addonDirectory,
                    "*.toc",
                    SearchOption.TopDirectoryOnly);

                if (tocFiles.Length > 0)
                    tocPath = tocFiles[0];
            }

            if (tocPath is null)
                return string.Empty;

            foreach (var rawLine in File.ReadLines(tocPath))
            {
                var line = rawLine.Trim();

                if (!line.StartsWith(
                        "## Version:",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return line["## Version:".Length..].Trim();
            }
        }
        catch
        {
            // Version metadata is optional at this stage.
        }

        return string.Empty;
    }
}
