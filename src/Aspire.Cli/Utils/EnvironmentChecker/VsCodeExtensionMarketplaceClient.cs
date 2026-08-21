// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using Semver;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Queries the Visual Studio Marketplace for Aspire VS Code extension versions.
/// </summary>
internal sealed class VsCodeExtensionMarketplaceClient(IHttpClientFactory httpClientFactory) : IVsCodeExtensionMarketplaceClient
{
    internal const string HttpClientName = "VsCodeExtensionMarketplace";

    private const string MarketplaceQueryUrl = "https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery";
    private const string PreReleasePropertyName = "Microsoft.VisualStudio.Code.PreRelease";

    public async Task<VsCodeExtensionMarketplaceVersions> GetLatestVersionsAsync(CancellationToken cancellationToken)
    {
        // The numeric filter and flag values are the Marketplace protocol used by VS Code:
        // ExtensionName (7), Target (8), ExcludeWithFlags (12), IncludeVersionProperties (0x10),
        // ExcludeNonValidated (0x20), and IncludeLatestPrereleaseAndStableVersionOnly (0x10000).
        // See https://github.com/microsoft/vscode/blob/main/src/vs/platform/extensionManagement/common/extensionGalleryManifestService.ts.
        const string requestBody = """
            {
              "filters": [{
                "criteria": [
                  { "filterType": 7, "value": "microsoft-aspire.aspire-vscode" },
                  { "filterType": 8, "value": "Microsoft.VisualStudio.Code" },
                  { "filterType": 12, "value": "4096" }
                ]
              }],
              "assetTypes": [],
              "flags": 65584
            }
            """;
        using var httpClient = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, MarketplaceQueryUrl)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json; api-version=3.0-preview.1");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var responseJson = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

        return ParseVersions(responseJson.RootElement);
    }

    private static VsCodeExtensionMarketplaceVersions ParseVersions(JsonElement root)
    {
        // The response nests the two requested channel entries as:
        //   { "results": [{ "extensions": [{ "versions": [
        //       { "version": "1.3.0" },
        //       { "version": "1.4.0", "properties": [{
        //           "key": "Microsoft.VisualStudio.Code.PreRelease", "value": "true" }] }
        //   ] }] }] }
        SemVersion? stableVersion = null;
        SemVersion? preReleaseVersion = null;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The Marketplace response did not contain a results array.");
        }

        foreach (var result in results.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("extensions", out var extensions) ||
                extensions.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("A Marketplace result did not contain an extensions array.");
            }

            foreach (var extension in extensions.EnumerateArray())
            {
                if (extension.ValueKind != JsonValueKind.Object ||
                    !extension.TryGetProperty("versions", out var versions) ||
                    versions.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("A Marketplace extension did not contain a versions array.");
                }

                foreach (var versionEntry in versions.EnumerateArray())
                {
                    if (versionEntry.ValueKind != JsonValueKind.Object ||
                        !versionEntry.TryGetProperty("version", out var versionElement) ||
                        versionElement.ValueKind != JsonValueKind.String ||
                        !SemVersion.TryParse(versionElement.GetString(), SemVersionStyles.Strict, out var version) ||
                        !TryGetIsPreRelease(versionEntry, out var isPreRelease))
                    {
                        continue;
                    }

                    if (isPreRelease)
                    {
                        preReleaseVersion = SelectLatest(preReleaseVersion, version);
                    }
                    else
                    {
                        stableVersion = SelectLatest(stableVersion, version);
                    }
                }
            }
        }

        return new VsCodeExtensionMarketplaceVersions(stableVersion, preReleaseVersion);
    }

    private static bool TryGetIsPreRelease(JsonElement versionEntry, out bool isPreRelease)
    {
        isPreRelease = false;
        // Stable entries may omit the prerelease marker while still carrying other version properties.
        // When the prerelease marker is present, require the Marketplace's
        // `{ "key": "...PreRelease", "value": "true|false" }` shape and make duplicate markers
        // agree so response ordering cannot change the selected channel.
        if (!versionEntry.TryGetProperty("properties", out var properties))
        {
            return true;
        }

        if (properties.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        bool? marker = null;
        foreach (var property in properties.EnumerateArray())
        {
            if (property.ValueKind != JsonValueKind.Object ||
                !property.TryGetProperty("key", out var key) ||
                key.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (!string.Equals(key.GetString(), PreReleasePropertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!property.TryGetProperty("value", out var value) ||
                value.ValueKind != JsonValueKind.String ||
                !bool.TryParse(value.GetString(), out var propertyValue) ||
                marker is not null && marker.Value != propertyValue)
            {
                // Malformed or conflicting prerelease metadata makes this version entry's channel
                // unreadable, so skip the entry. Malformed top-level results, extensions, or versions
                // containers invalidate the protocol response and are reported as lookup unavailable.
                return false;
            }

            marker = propertyValue;
        }

        isPreRelease = marker ?? false;
        return true;
    }

    private static SemVersion SelectLatest(SemVersion? current, SemVersion candidate)
        => current is null || SemVersion.ComparePrecedence(candidate, current) > 0
            ? candidate
            : current;
}
