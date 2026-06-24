// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure;

// Generated-style client for the ADC Global API OpenAPI v1 surface:
//   https://management.azuredevcompute.io/openapi/v1.json
// Keep this internal and intentionally narrow until the sandbox data-plane API stabilizes.
internal sealed class AzureDevComputeClient(HttpClient httpClient, TokenCredential credential, ILogger logger, TimeSpan? forbiddenRetryDelay = null)
{
    internal const string AuthorizationScope = "https://management.azuredevcompute.io/.default";

    private const string ApiVersion = "2026-02-01-preview";
    private const int ForbiddenRetryCount = 6;
    private static readonly string[] s_authorizationScopes = [AuthorizationScope];
    private static readonly JsonSerializerOptions s_jsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private AccessToken _accessToken;

    public Task<AzureDevComputeDiskImage> CreateDiskImageAsync(AzureDevComputeResourceScope scope, AzureDevComputeCreateDiskImageRequest request, CancellationToken cancellationToken)
    {
        return SendAsync<AzureDevComputeDiskImage>(
            scope,
            HttpMethod.Put,
            $"{GetSandboxGroupPath(scope)}/diskimages",
            request,
            cancellationToken);
    }

    public Task<List<AzureDevComputeDiskImage>> ListDiskImagesAsync(AzureDevComputeResourceScope scope, string? labels, CancellationToken cancellationToken)
    {
        var path = $"{GetSandboxGroupPath(scope)}/diskimages?Page=1&PageSize=100";
        if (!string.IsNullOrWhiteSpace(labels))
        {
            path += $"&labels={WebUtility.UrlEncode(labels)}";
        }

        return SendAsync<List<AzureDevComputeDiskImage>>(
            scope,
            HttpMethod.Get,
            path,
            content: null,
            cancellationToken);
    }

    public Task<AzureDevComputeDiskImage> GetDiskImageAsync(AzureDevComputeResourceScope scope, string diskImageId, CancellationToken cancellationToken)
    {
        return SendAsync<AzureDevComputeDiskImage>(
            scope,
            HttpMethod.Get,
            $"{GetSandboxGroupPath(scope)}/diskimages/{Escape(diskImageId)}",
            content: null,
            cancellationToken);
    }

    public Task DeleteDiskImageAsync(AzureDevComputeResourceScope scope, string diskImageId, CancellationToken cancellationToken)
    {
        return SendAsync(
            scope,
            HttpMethod.Delete,
            $"{GetSandboxGroupPath(scope)}/diskimages/{Escape(diskImageId)}",
            content: null,
            cancellationToken);
    }

    public Task<List<AzureDevComputeSandbox>> ListSandboxesAsync(AzureDevComputeResourceScope scope, string? labels, CancellationToken cancellationToken)
    {
        var path = $"{GetSandboxGroupPath(scope)}/sandboxes?Page=1&PageSize=100";
        if (!string.IsNullOrWhiteSpace(labels))
        {
            path += $"&labels={WebUtility.UrlEncode(labels)}";
        }

        return SendAsync<List<AzureDevComputeSandbox>>(
            scope,
            HttpMethod.Get,
            path,
            content: null,
            cancellationToken);
    }

    public Task<AzureDevComputeSandbox> CreateSandboxAsync(AzureDevComputeResourceScope scope, AzureDevComputeSandboxRequest request, CancellationToken cancellationToken)
    {
        return SendAsync<AzureDevComputeSandbox>(
            scope,
            HttpMethod.Put,
            $"{GetSandboxGroupPath(scope)}/sandboxes",
            request,
            cancellationToken);
    }

    public Task<AzureDevComputeSandbox> SetLifecycleAsync(AzureDevComputeResourceScope scope, string sandboxId, AzureDevComputeSandboxLifecyclePolicy lifecycle, CancellationToken cancellationToken)
    {
        return SendAsync<AzureDevComputeSandbox>(
            scope,
            HttpMethod.Post,
            $"{GetSandboxGroupPath(scope)}/sandboxes/{Escape(sandboxId)}/lifecycle",
            lifecycle,
            cancellationToken);
    }

    public async Task<List<AzureDevComputeSandboxPort>> AddPortAsync(AzureDevComputeResourceScope scope, string sandboxId, AzureDevComputeAddPortRequest request, CancellationToken cancellationToken)
    {
        var response = await SendAsync<AzureDevComputePortsList>(
            scope,
            HttpMethod.Post,
            $"{GetSandboxGroupPath(scope)}/sandboxes/{Escape(sandboxId)}/ports/add",
            request,
            cancellationToken).ConfigureAwait(false);

        return response.Ports;
    }

    public async Task<List<AzureDevComputeSandboxPort>> RemovePortAsync(AzureDevComputeResourceScope scope, string sandboxId, AzureDevComputeRemovePortRequest request, CancellationToken cancellationToken)
    {
        var response = await SendAsync<AzureDevComputePortsList>(
            scope,
            HttpMethod.Post,
            $"{GetSandboxGroupPath(scope)}/sandboxes/{Escape(sandboxId)}/ports/remove",
            request,
            cancellationToken).ConfigureAwait(false);

        return response.Ports;
    }

    public Task DeleteSandboxAsync(AzureDevComputeResourceScope scope, string sandboxId, CancellationToken cancellationToken)
    {
        return SendAsync(
            scope,
            HttpMethod.Delete,
            $"{GetSandboxGroupPath(scope)}/sandboxes/{Escape(sandboxId)}",
            content: null,
            cancellationToken);
    }

    private async Task SendAsync(AzureDevComputeResourceScope scope, HttpMethod method, string path, object? content, CancellationToken cancellationToken)
    {
        using var response = await SendWithForbiddenRetryAsync(scope, method, path, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, method, path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(AzureDevComputeResourceScope scope, HttpMethod method, string path, object? content, CancellationToken cancellationToken)
    {
        using var response = await SendWithForbiddenRetryAsync(scope, method, path, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, method, path, cancellationToken).ConfigureAwait(false);

        var result = await response.Content.ReadFromJsonAsync<T>(s_jsonSerializerOptions, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException($"ADC request '{method} {path}' returned an empty response.");
    }

    private async Task<HttpResponseMessage> SendWithForbiddenRetryAsync(AzureDevComputeResourceScope scope, HttpMethod method, string path, object? content, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await SendCoreAsync(scope, method, path, content, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Forbidden || attempt >= ForbiddenRetryCount)
            {
                return response;
            }

            // Sandbox data-plane role grants are ARM RBAC role assignments, but ADC authorizes
            // requests through its own data-plane endpoint. The ARM deployment can complete a
            // few seconds before ADC observes the new role assignment, so retry only 403s here;
            // request shape errors and service failures should still surface immediately.
            response.Dispose();
            logger.LogInformation("ADC request {Method} {Path} returned 403. Retrying after sandbox role propagation delay.", method.Method, path);
            await Task.Delay(forbiddenRetryDelay ?? TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(AzureDevComputeResourceScope scope, HttpMethod method, string path, object? content, CancellationToken cancellationToken)
    {
        var uri = CreateRequestUri(scope, path);
        using var request = new HttpRequestMessage(method, uri);
        var accessToken = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (content is not null)
        {
            request.Content = JsonContent.Create(content, options: s_jsonSerializerOptions);
        }

        logger.LogInformation("Sending ADC request: {Method} {Path}", method.Method, uri.PathAndQuery);
        return await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken.Token is not null &&
            _accessToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return _accessToken.Token;
        }

        _accessToken = await credential.GetTokenAsync(new TokenRequestContext(s_authorizationScopes), cancellationToken).ConfigureAwait(false);
        return _accessToken.Token;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, HttpMethod method, string path, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = await GetErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException($"ADC request '{method} {path}' failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {message}");
    }

    private static async Task<string> GetErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength == 0)
        {
            return string.Empty;
        }

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<AzureDevComputeProblemDetails>(s_jsonSerializerOptions, cancellationToken).ConfigureAwait(false);
            if (problem is not null)
            {
                return string.Join(
                    " ",
                    new[] { problem.Title, problem.Detail }
                        .Where(static value => !string.IsNullOrWhiteSpace(value)));
            }
        }
        catch (JsonException)
        {
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string GetSandboxGroupPath(AzureDevComputeResourceScope scope)
    {
        return $"subscriptions/{Escape(scope.SubscriptionId)}/resourceGroups/{Escape(scope.ResourceGroupName)}/sandboxGroups/{Escape(scope.SandboxGroupName)}";
    }

    private static Uri CreateRequestUri(AzureDevComputeResourceScope scope, string path)
    {
        var host = $"management.{scope.Region}.azuredevcompute.io";
        var queryStart = path.IndexOf('?');
        var pathOnly = queryStart >= 0 ? path[..queryStart] : path;
        var query = queryStart >= 0 ? path[(queryStart + 1)..] : string.Empty;
        query = string.IsNullOrEmpty(query)
            ? $"api-version={ApiVersion}"
            : $"{query}&api-version={ApiVersion}";

        // The published OpenAPI lists the global management host, but the `aca` CLI sends
        // sandbox group data-plane requests to the regional host with this preview API version:
        //   https://management.westus3.azuredevcompute.io/.../diskimages?api-version=2026-02-01-preview
        return new UriBuilder(Uri.UriSchemeHttps, host)
        {
            Path = pathOnly,
            Query = query
        }.Uri;
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}

internal sealed record AzureDevComputeResourceScope(string SubscriptionId, string ResourceGroupName, string SandboxGroupName, string Region);

internal sealed class AzureDevComputeCreateDiskImageRequest
{
    public string? Name { get; init; }

    public Dictionary<string, string> Labels { get; init; } = [];

    public required AzureDevComputeDiskImageSpec Image { get; init; }

    public AzureDevComputeRegistryCredentials? RegistryCredentials { get; init; }
}

internal sealed class AzureDevComputeDiskImageSpec
{
    public required string Base { get; init; }
}

internal sealed class AzureDevComputeRegistryCredentials
{
    public required string Username { get; init; }

    public required string Token { get; init; }
}

internal sealed class AzureDevComputeDiskImage
{
    public required string Id { get; init; }

    public Dictionary<string, string> Labels { get; init; } = [];

    public required AzureDevComputeDiskImageStatus Status { get; init; }
}

internal sealed class AzureDevComputeDiskImageStatus
{
    public required string State { get; init; }

    public string? ErrorMessage { get; init; }
}

internal sealed class AzureDevComputeSandboxRequest
{
    public Dictionary<string, string> Labels { get; init; } = [];

    public List<string>? Entrypoint { get; init; }

    public List<string>? Cmd { get; init; }

    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public List<AzureDevComputeIdentitySetting>? IdentitySettings { get; init; }

    public bool? SkipEgressProxy { get; init; }

    public AzureDevComputeSandboxEgressPolicy? EgressPolicy { get; init; }

    public required AzureDevComputeSandboxSource SourcesRef { get; init; }

    public required AzureDevComputeSandboxResources Resources { get; init; }

    public List<AzureDevComputeSandboxVolume>? Volumes { get; init; }
}

internal sealed class AzureDevComputeSandboxSource
{
    public required AzureDevComputeSandboxDiskImageSource DiskImage { get; init; }
}

internal sealed class AzureDevComputeSandboxDiskImageSource
{
    public required string Id { get; init; }

    public bool IsPublic { get; init; }
}

internal sealed class AzureDevComputeSandboxResources
{
    public string Cpu { get; init; } = "1000m";

    public string Memory { get; init; } = "2048Mi";

    public string Disk { get; init; } = "20480Mi";
}

internal sealed class AzureDevComputeIdentitySetting
{
    public required string Identity { get; init; }

    public string Lifecycle { get; init; } = "All";
}

internal sealed class AzureDevComputeSandboxEgressPolicy
{
    public string DefaultAction { get; init; } = "Allow";

    public string? TrafficInspection { get; init; }
}

internal sealed class AzureDevComputeSandboxVolume
{
    public required string VolumeName { get; init; }

    public required string Mountpoint { get; init; }

    public bool ReadOnly { get; init; }
}

internal sealed class AzureDevComputeSandbox
{
    public required string Id { get; init; }

    public Dictionary<string, string> Labels { get; init; } = [];

    public List<AzureDevComputeSandboxPort> Ports { get; init; } = [];
}

internal sealed class AzureDevComputeSandboxLifecyclePolicy
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public AzureDevComputeSandboxAutoSuspendPolicy? AutoSuspendPolicy { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public AzureDevComputeSandboxAutoDeletePolicy? AutoDeletePolicy { get; init; }
}

internal sealed class AzureDevComputeSandboxAutoSuspendPolicy
{
    public required bool Enabled { get; init; }

    public int? Interval { get; init; }

    public string? Mode { get; init; }
}

internal sealed class AzureDevComputeSandboxAutoDeletePolicy
{
    public required bool Enabled { get; init; }

    public int? DeleteIntervalInDays { get; init; }

    public long? DeleteIntervalInSeconds { get; init; }

    public string? Trigger { get; init; }
}

internal sealed class AzureDevComputeAddPortRequest
{
    public string? Name { get; init; }

    public required int Port { get; init; }

    public AzureDevComputePortAuthConfig? Auth { get; init; }

    public required string Protocol { get; init; }
}

internal sealed class AzureDevComputeRemovePortRequest
{
    public required int Port { get; init; }
}

internal sealed class AzureDevComputePortAuthConfig
{
    public bool Anonymous { get; init; }
}

internal sealed class AzureDevComputePortsList
{
    public List<AzureDevComputeSandboxPort> Ports { get; init; } = [];
}

internal sealed class AzureDevComputeSandboxPort
{
    public string? Name { get; init; }

    public required int Port { get; init; }

    public required Uri Url { get; init; }
}

internal sealed class AzureDevComputeProblemDetails
{
    public string? Title { get; init; }

    public string? Detail { get; init; }
}
