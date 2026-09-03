using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class AddonManifestService
{
    private static readonly HttpClient HttpClient = new();

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    public async Task<AddonManifest> LoadAsync(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            throw new ArgumentException(
                "No addon manifest location was provided.");
        }

        string json;

        if (Uri.TryCreate(
                location,
                UriKind.Absolute,
                out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp ||
             uri.Scheme == Uri.UriSchemeHttps))
        {
            json =
                await HttpClient.GetStringAsync(uri);
        }
        else
        {
            var path =
                Path.GetFullPath(location);

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "Addon manifest was not found.",
                    path);
            }

            json =
                await File.ReadAllTextAsync(path);
        }

        var manifest =
            JsonSerializer.Deserialize<AddonManifest>(
                json,
                JsonOptions);

        if (manifest is null)
        {
            throw new InvalidDataException(
                "Addon manifest could not be parsed.");
        }

        if (manifest.ManifestVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported addon manifest version: " +
                $"{manifest.ManifestVersion}");
        }

        return manifest;
    }
}