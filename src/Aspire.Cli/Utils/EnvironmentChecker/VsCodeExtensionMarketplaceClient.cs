// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text;
using System.Text.Json;
using Semver;

namespace Aspire.Cli.Utils.EnvironmentChecker;

internal sealed class VsCodeExtensionMarketplaceClient : IVsCodeExtensionMarketplaceClient
{
    internal const string ExtensionId = VsCodeExtensionCheck.ExtensionId;

    private const string MarketplaceQueryUrl = "https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery";

    // The gallery is an Azure DevOps service, so every request must name an API version or the
    // service rejects it with HTTP 400 VssVersionNotSpecifiedException before running the query.
    // The version travels in the Accept header rather than the query string so the request URI
    // stays a bare endpoint.
    // See https://learn.microsoft.com/azure/devops/integrate/concepts/rest-api-versioning.
    private const string MarketplaceAcceptHeader = "application/json; api-version=3.0-preview.1";

    private const int MaximumResponseSize = 1024 * 1024;
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeout;

    public VsCodeExtensionMarketplaceClient(HttpClient httpClient)
        : this(httpClient, TimeProvider.System)
    {
    }

    internal VsCodeExtensionMarketplaceClient(
        HttpClient httpClient,
        TimeProvider timeProvider,
        TimeSpan? timeout = null)
    {
        _httpClient = httpClient;
        _timeProvider = timeProvider;
        _timeout = timeout ?? s_defaultTimeout;
    }

    public async Task<VsCodeExtensionMarketplaceVersions> GetLatestVersionsAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest();
        using var timeoutCancellation = new CancellationTokenSource(_timeout, _timeProvider);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        try
        {
            // ResponseHeadersRead returns as soon as the response headers arrive, so the body is
            // still streaming after SendAsync completes. Both the send and the body read observe the
            // private timeout token, so the whole operation has to sit inside the translation: a
            // server that returns headers and then stalls would otherwise surface a bare
            // OperationCanceledException, and doctor drops the check on cancellation instead of
            // reporting the documented timeout warning.
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCancellation.Token);

            response.EnsureSuccessStatusCode();
            var responseBytes = await ReadBoundedResponseAsync(response.Content, linkedCancellation.Token);

            return ParseVersions(responseBytes);
        }
        catch (OperationCanceledException exception) when (
            timeoutCancellation.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The VS Code Marketplace request timed out after {_timeout.TotalSeconds:g} seconds.",
                exception);
        }
    }

    private static HttpRequestMessage CreateRequest()
    {
        // The Marketplace extension query API accepts a static anonymous payload. Keep this request
        // free of machine, user, installation, and path data so doctor does not introduce a new
        // telemetry or privacy surface.
        const string requestBody = """
            {
              "filters": [
                {
                  "criteria": [
                    {
                      "filterType": 7,
                      "value": "microsoft-aspire.aspire-vscode"
                    },
                    {
                      "filterType": 8,
                      "value": "Microsoft.VisualStudio.Code"
                    },
                    {
                      "filterType": 12,
                      "value": "4096"
                    }
                  ]
                }
              ],
              "assetTypes": [],
              "flags": 65584
            }
            """;
        var request = new HttpRequestMessage(HttpMethod.Post, MarketplaceQueryUrl)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd(MarketplaceAcceptHeader);

        // VS Code identifies itself with X-Market-Client-Id and a per-installation X-Market-User-Id.
        // The gallery does not require either for an anonymous query, so they are deliberately
        // omitted: sending them would make doctor either impersonate VS Code or emit an identifier
        // the CLI does not otherwise send.
        request.Headers.Add("X-TFS-FedAuthRedirect", "Suppress");
        request.Headers.UserAgent.ParseAdd($"Aspire-CLI/{GetAssemblyVersion()}");

        return request;
    }

    private static string GetAssemblyVersion()
        => typeof(VsCodeExtensionMarketplaceClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+', 2)[0]
            ?? "0.0.0";

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseSize)
        {
            throw new InvalidDataException(
                $"The VS Code Marketplace response exceeded the {MaximumResponseSize} byte limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var bytes = new byte[MaximumResponseSize + 1];
        var totalBytesRead = 0;

        while (totalBytesRead < bytes.Length)
        {
            var bytesRead = await stream.ReadAsync(
                bytes.AsMemory(totalBytesRead, bytes.Length - totalBytesRead),
                cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;
        }

        if (totalBytesRead > MaximumResponseSize)
        {
            throw new InvalidDataException(
                $"The VS Code Marketplace response exceeded the {MaximumResponseSize} byte limit.");
        }

        return bytes.AsMemory(0, totalBytesRead);
    }

    private static VsCodeExtensionMarketplaceVersions ParseVersions(ReadOnlyMemory<byte> responseBytes)
    {
        // The gallery response nests the matched extension under results[].extensions[], and the
        // requested flags limit "versions" to the latest stable and latest pre-release entries:
        //   { "results": [ { "extensions": [ {
        //       "publisher": { "publisherName": "microsoft-aspire" },
        //       "extensionName": "aspire-vscode",
        //       "versions": [
        //         { "version": "1.16.0", "properties": [] },
        //         { "version": "1.0.0", "properties": [
        //             { "key": "Microsoft.VisualStudio.Code.PreRelease", "value": "true" } ] } ] } ] } ] }
        // Stable entries omit the PreRelease property rather than setting it to "false", the two
        // entries arrive in no guaranteed order, and the latest pre-release can be older than the
        // latest stable, so each channel is reduced independently by precedence.
        using var document = JsonDocument.Parse(responseBytes);
        if (!TryFindExtension(document.RootElement, out var extension))
        {
            throw new InvalidDataException(
                $"The VS Code Marketplace response did not contain extension '{ExtensionId}'.");
        }

        SemVersion? latestStableVersion = null;
        SemVersion? latestPreReleaseVersion = null;
        if (extension.TryGetProperty("versions", out var versions) &&
            versions.ValueKind == JsonValueKind.Array)
        {
            foreach (var versionEntry in versions.EnumerateArray())
            {
                if (!versionEntry.TryGetProperty("version", out var versionElement) ||
                    versionElement.ValueKind != JsonValueKind.String ||
                    !SemVersion.TryParse(versionElement.GetString(), SemVersionStyles.Strict, out var version))
                {
                    continue;
                }

                if (IsPreReleaseVersion(versionEntry))
                {
                    latestPreReleaseVersion = SelectLaterVersion(latestPreReleaseVersion, version);
                }
                else
                {
                    latestStableVersion = SelectLaterVersion(latestStableVersion, version);
                }
            }
        }

        return new VsCodeExtensionMarketplaceVersions(latestStableVersion, latestPreReleaseVersion);
    }

    private static bool TryFindExtension(JsonElement root, out JsonElement extension)
    {
        if (root.TryGetProperty("results", out var results) &&
            results.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("extensions", out var extensions) ||
                    extensions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var candidate in extensions.EnumerateArray())
                {
                    var extensionName = candidate.TryGetProperty("extensionName", out var extensionNameElement)
                        ? extensionNameElement.GetString()
                        : null;
                    var publisherName =
                        candidate.TryGetProperty("publisher", out var publisher) &&
                        publisher.TryGetProperty("publisherName", out var publisherNameElement)
                            ? publisherNameElement.GetString()
                            : null;
                    if (string.Equals(extensionName, "aspire-vscode", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(publisherName, "microsoft-aspire", StringComparison.OrdinalIgnoreCase))
                    {
                        extension = candidate;
                        return true;
                    }
                }
            }
        }

        extension = default;
        return false;
    }

    private static bool IsPreReleaseVersion(JsonElement versionEntry)
    {
        if (!versionEntry.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var property in properties.EnumerateArray())
        {
            if (property.TryGetProperty("key", out var key) &&
                property.TryGetProperty("value", out var value) &&
                string.Equals(
                    key.GetString(),
                    "Microsoft.VisualStudio.Code.PreRelease",
                    StringComparison.OrdinalIgnoreCase) &&
                bool.TryParse(value.GetString(), out var isPreRelease))
            {
                return isPreRelease;
            }
        }

        return false;
    }

    private static SemVersion SelectLaterVersion(SemVersion? current, SemVersion candidate)
        => current is null || SemVersion.ComparePrecedence(candidate, current) > 0
            ? candidate
            : current;
}
