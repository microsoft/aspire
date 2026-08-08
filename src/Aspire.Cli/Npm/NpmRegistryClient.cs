// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Headers;
using System.Text.Json;
using Semver;

namespace Aspire.Cli.Npm;

internal sealed class NpmRegistryClient : INpmRegistryClient
{
    // The registry is whatever npm itself would resolve for this package, not a hardcoded address.
    // An update check is only meaningful against the registry the resulting install actually uses:
    // the remediation this notice prints is "npm install -g @microsoft/aspire-cli@latest", which npm
    // resolves against the *user's* configured registry. Enterprises commonly block
    // registry.npmjs.org and pin "registry=" to an internal proxy, so reading public npm
    // unconditionally would fail the check for precisely the users whose install would have worked.
    // INpmRegistryResolver applies npm's own precedence and falls back to public npm.
    //
    // This is a read-only, anonymous metadata GET; no package is ever installed from this URL, and
    // no credential from the user's .npmrc is read or sent. The approved-feed rule in AGENTS.md
    // governs NuGet.config restore sources for the build, not runtime endpoints, and
    // SigstoreNpmProvenanceChecker already reads a registry over HTTP the same way.

    // The abbreviated packument omits README, maintainer, and per-version metadata that the update
    // check never reads. It still carries "dist-tags", and it is roughly a third of the size of the
    // full document for @microsoft/aspire-cli (6 KB versus 18 KB measured against the live
    // registry). Registries that do not implement it fall back to the full document, which parses
    // identically here.
    // See https://github.com/npm/registry/blob/main/docs/responses/package-metadata.md.
    private const string AbbreviatedMetadataMediaType = "application/vnd.npm.install-v1+json";

    private const string LatestDistTag = "latest";
    private const int MaximumResponseSize = 1024 * 1024;
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly INpmRegistryResolver _registryResolver;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeout;

    public NpmRegistryClient(HttpClient httpClient, INpmRegistryResolver registryResolver)
        : this(httpClient, registryResolver, TimeProvider.System)
    {
    }

    internal NpmRegistryClient(
        HttpClient httpClient,
        INpmRegistryResolver registryResolver,
        TimeProvider timeProvider,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(registryResolver);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _httpClient = httpClient;
        _registryResolver = registryResolver;
        _timeProvider = timeProvider;
        _timeout = timeout ?? s_defaultTimeout;
    }

    public async Task<SemVersion> GetLatestVersionAsync(string packageName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);

        var registry = _registryResolver.Resolve(packageName);

        using var request = CreateRequest(registry, packageName);
        using var timeoutCancellation = new CancellationTokenSource(_timeout, _timeProvider);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        try
        {
            // ResponseHeadersRead returns as soon as the response headers arrive, so the body is
            // still streaming after SendAsync completes. Both the send and the body read observe the
            // private timeout token so a registry that returns headers and then stalls is still
            // bounded by the timeout rather than hanging the update check.
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCancellation.Token);

            response.EnsureSuccessStatusCode();
            var responseBytes = await ReadBoundedResponseAsync(packageName, response.Content, linkedCancellation.Token);

            return ParseLatestVersion(packageName, responseBytes);
        }
        catch (OperationCanceledException exception) when (
            timeoutCancellation.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out after {_timeout.TotalSeconds:g} seconds while resolving {NpmPackageInfo.FormatPackageSpecifier(packageName, LatestDistTag)} from {registry.DisplayUri}.",
                exception);
        }
    }

    private static HttpRequestMessage CreateRequest(NpmRegistryResolution registry, string packageName)
    {
        // Scoped names carry a '/' that has to be percent-encoded, because the registry addresses a
        // package as a single path segment: "@microsoft/aspire-cli" is requested as
        // "%40microsoft%2Faspire-cli".
        var requestUri = new Uri(registry.RegistryUri, Uri.EscapeDataString(packageName));
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AbbreviatedMetadataMediaType));

        return request;
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedResponseAsync(
        string packageName,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseSize)
        {
            throw new InvalidDataException(
                $"The npm registry response for {packageName} exceeded the {MaximumResponseSize} byte limit.");
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
                $"The npm registry response for {packageName} exceeded the {MaximumResponseSize} byte limit.");
        }

        return bytes.AsMemory(0, totalBytesRead);
    }

    private static SemVersion ParseLatestVersion(string packageName, ReadOnlyMemory<byte> responseBytes)
    {
        // Both the abbreviated and the full packument expose the dist-tag map at the root:
        //   { "name": "@microsoft/aspire-cli",
        //     "dist-tags": { "latest": "13.4.6" },
        //     "versions": { "13.4.6": { ... } } }
        // "latest" is the tag npm resolves for a bare "npm install -g <name>@latest", so it is the
        // only entry the update check reads. A package that has never been published to a tag can
        // omit the map entirely.
        using var document = JsonDocument.Parse(responseBytes);

        if (!document.RootElement.TryGetProperty("dist-tags", out var distTags) ||
            distTags.ValueKind is not JsonValueKind.Object ||
            !distTags.TryGetProperty(LatestDistTag, out var latest) ||
            latest.ValueKind is not JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"The npm registry response for {packageName} did not contain a '{LatestDistTag}' dist-tag.");
        }

        var latestVersion = latest.GetString();

        if (!SemVersion.TryParse(latestVersion, SemVersionStyles.Strict, out var version))
        {
            throw new InvalidDataException(
                $"The npm registry reported an unparsable '{LatestDistTag}' version '{latestVersion}' for {packageName}.");
        }

        return version;
    }
}
