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

// Narrow client for the ADC Global API OpenAPI v1 surface:
//   https://management.azuredevcompute.io/openapi/v1.json
// Keep this internal and intentionally narrow until the sandbox data-plane API stabilizes.
internal interface IAzureDevComputeClient
{
    Task<AzureDevComputeDiskImage> CreateDiskImageAsync(AzureDevComputeResourceScope scope, AzureDevComputeCreateDiskImageRequest request, CancellationToken cancellationToken);

    Task<List<AzureDevComputeDiskImage>> ListDiskImagesAsync(AzureDevComputeResourceScope scope, string? labels, CancellationToken cancellationToken);

    Task<AzureDevComputeDiskImage> GetDiskImageAsync(AzureDevComputeResourceScope scope, string diskImageId, CancellationToken cancellationToken);

    Task DeleteDiskImageAsync(AzureDevComputeResourceScope scope, string diskImageId, CancellationToken cancellationToken);

    Task<List<AzureDevComputeSandbox>> ListSandboxesAsync(AzureDevComputeResourceScope scope, string? labels, CancellationToken cancellationToken);

    Task<AzureDevComputeSandbox> CreateSandboxAsync(AzureDevComputeResourceScope scope, AzureDevComputeSandboxRequest request, CancellationToken cancellationToken);

    Task<AzureDevComputeSandbox> SetLifecycleAsync(AzureDevComputeResourceScope scope, string sandboxId, AzureDevComputeSandboxLifecyclePolicy lifecycle, CancellationToken cancellationToken);

    Task<List<AzureDevComputeSandboxPort>> AddPortAsync(AzureDevComputeResourceScope scope, string sandboxId, AzureDevComputeAddPortRequest request, CancellationToken cancellationToken);

    Task<List<AzureDevComputeSandboxPort>> RemovePortAsync(AzureDevComputeResourceScope scope, string sandboxId, AzureDevComputeRemovePortRequest request, CancellationToken cancellationToken);

    Task DeleteSandboxAsync(AzureDevComputeResourceScope scope, string sandboxId, CancellationToken cancellationToken);
}

internal sealed class AzureDevComputeClient(HttpClient httpClient, TokenCredential credential, ILogger logger, TimeSpan? retryDelay = null) : IAzureDevComputeClient
{
    internal const string AuthorizationScope = "https://management.azuredevcompute.io/.default";

    private const string ApiVersion = "2026-02-01-preview";
    private const int MaxRetryCount = 6;
    private const int MaxForbiddenRetryCount = 20;
    private const int PageSize = 100;
    private static readonly TimeSpan s_defaultRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_defaultForbiddenRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_maxRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_maxForbiddenRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly string[] s_authorizationScopes = [AuthorizationScope];
    private static readonly JsonSerializerOptions s_jsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private AccessToken _accessToken;

    public Task<AzureDevComputeDiskImage> CreateDiskImageAsync(AzureDevComputeResourceScope scope, AzureDevComputeCreateDiskImageRequest request, CancellationToken cancellationToken)
    {
        return SendCreateAsync<AzureDevComputeDiskImage>(
            scope,
            HttpMethod.Put,
            $"{GetSandboxGroupPath(scope)}/diskimages/v2",
            request,
            cancellationToken);
    }

    public Task<List<AzureDevComputeDiskImage>> ListDiskImagesAsync(AzureDevComputeResourceScope scope, string? labels, CancellationToken cancellationToken)
    {
        return ListAllPagesAsync<AzureDevComputeDiskImage>(scope, "diskimages", labels, cancellationToken);
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
            cancellationToken,
            allowNotFound: true);
    }

    public Task<List<AzureDevComputeSandbox>> ListSandboxesAsync(AzureDevComputeResourceScope scope, string? labels, CancellationToken cancellationToken)
    {
        return ListAllPagesAsync<AzureDevComputeSandbox>(scope, "sandboxes", labels, cancellationToken);
    }

    public Task<AzureDevComputeSandbox> CreateSandboxAsync(AzureDevComputeResourceScope scope, AzureDevComputeSandboxRequest request, CancellationToken cancellationToken)
    {
        return SendCreateAsync<AzureDevComputeSandbox>(
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
            cancellationToken,
            notFoundFactory: static () => new AzureDevComputePortsList()).ConfigureAwait(false);

        return response.Ports;
    }

    public Task DeleteSandboxAsync(AzureDevComputeResourceScope scope, string sandboxId, CancellationToken cancellationToken)
    {
        return SendAsync(
            scope,
            HttpMethod.Delete,
            $"{GetSandboxGroupPath(scope)}/sandboxes/{Escape(sandboxId)}",
            content: null,
            cancellationToken,
            allowNotFound: true);
    }

    private async Task<List<T>> ListAllPagesAsync<T>(AzureDevComputeResourceScope scope, string resourceType, string? labels, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        for (var page = 1; ; page++)
        {
            var path = $"{GetSandboxGroupPath(scope)}/{resourceType}?Page={page}&PageSize={PageSize}";
            if (!string.IsNullOrWhiteSpace(labels))
            {
                path += $"&labels={WebUtility.UrlEncode(labels)}";
            }

            var pageResults = await SendAsync<List<T>>(
                scope,
                HttpMethod.Get,
                path,
                content: null,
                cancellationToken).ConfigureAwait(false);
            results.AddRange(pageResults);

            if (pageResults.Count < PageSize)
            {
                return results;
            }
        }
    }

    private async Task SendAsync(
        AzureDevComputeResourceScope scope,
        HttpMethod method,
        string path,
        object? content,
        CancellationToken cancellationToken,
        bool allowNotFound = false)
    {
        using var response = await SendWithRetryAsync(scope, method, path, content, cancellationToken).ConfigureAwait(false);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, method, path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(
        AzureDevComputeResourceScope scope,
        HttpMethod method,
        string path,
        object? content,
        CancellationToken cancellationToken,
        Func<T>? notFoundFactory = null)
    {
        using var response = await SendWithRetryAsync(scope, method, path, content, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound && notFoundFactory is not null)
        {
            return notFoundFactory();
        }

        await EnsureSuccessAsync(response, method, path, cancellationToken).ConfigureAwait(false);

        var result = await response.Content.ReadFromJsonAsync<T>(s_jsonSerializerOptions, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException($"ADC request '{method} {path}' returned an empty response.");
    }

    private async Task<T> SendCreateAsync<T>(
        AzureDevComputeResourceScope scope,
        HttpMethod method,
        string path,
        object content,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendWithRetryAsync(
                scope,
                method,
                path,
                content,
                cancellationToken,
                isCreateOperation: true).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AzureDevComputeCreateException(ex, responseMayHaveBeenLost: true);
        }
        catch (OperationCanceledException ex)
        {
            // SendCoreAsync wraps cancellation from HttpClient.SendAsync as ambiguous. A cancellation
            // that reaches here happened before dispatch, such as while acquiring the access token.
            throw new AzureDevComputeCreateException(ex, responseMayHaveBeenLost: false);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                try
                {
                    await EnsureSuccessAsync(response, method, path, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    // A collection create can be accepted even when a proxy or service returns a 5xx response.
                    // Reconcile by deployment labels rather than retrying the non-idempotent request.
                    throw new AzureDevComputeCreateException(ex, responseMayHaveBeenLost: (int)response.StatusCode >= 500);
                }
                catch (OperationCanceledException ex)
                {
                    throw new AzureDevComputeCreateException(ex, responseMayHaveBeenLost: (int)response.StatusCode >= 500);
                }
            }

            try
            {
                var result = await response.Content.ReadFromJsonAsync<T>(s_jsonSerializerOptions, cancellationToken).ConfigureAwait(false);
                if (result is null ||
                    (result is AzureDevComputeDiskImage diskImage && string.IsNullOrWhiteSpace(diskImage.Id)) ||
                    (result is AzureDevComputeDiskImage { Status: null }) ||
                    (result is AzureDevComputeDiskImage { Status.State: var state } && string.IsNullOrWhiteSpace(state)) ||
                    (result is AzureDevComputeSandbox sandbox && string.IsNullOrWhiteSpace(sandbox.Id)))
                {
                    throw new InvalidOperationException($"ADC request '{method} {path}' returned an incomplete response.");
                }

                return result;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or HttpRequestException or IOException or NotSupportedException)
            {
                // The service may have committed the create before returning an empty or malformed payload.
                throw new AzureDevComputeCreateException(ex, responseMayHaveBeenLost: true);
            }
            catch (OperationCanceledException ex)
            {
                throw new AzureDevComputeCreateException(ex, responseMayHaveBeenLost: true);
            }
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        AzureDevComputeResourceScope scope,
        HttpMethod method,
        string path,
        object? content,
        CancellationToken cancellationToken,
        bool isCreateOperation = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await SendCoreAsync(
                    scope,
                    method,
                    path,
                    content,
                    cancellationToken,
                    isCreateOperation).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetryCount && CanRetryAfterNetworkFailure(method))
            {
                var networkRetryDelay = ClampRetryDelay(retryDelay ?? s_defaultRetryDelay, s_maxRetryDelay);
                logger.LogInformation(ex, "ADC request {Method} {Path} failed with a transient network error. Retrying after {Delay}.", method.Method, path, networkRetryDelay);
                await Task.Delay(networkRetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                if (attempt >= MaxForbiddenRetryCount)
                {
                    return response;
                }

                var forbiddenRetryDelay = retryDelay is { } configuredRetryDelay
                    ? ClampRetryDelay(configuredRetryDelay, s_maxForbiddenRetryDelay)
                    : ClampRetryDelay(
                        GetRetryDelay(response, s_defaultForbiddenRetryDelay, DateTimeOffset.UtcNow),
                        s_maxForbiddenRetryDelay);
                _accessToken = default;
                response.Dispose();
                logger.LogWarning(
                    "ADC request {Method} {Path} returned HTTP 403. Refreshing the access token and waiting for the Container Apps SandboxGroup Data Owner role assignment to propagate (retry {RetryAttempt} of {MaxRetryAttempts}). If this persists, verify the role assignment on the sandbox group.",
                    method.Method,
                    path,
                    attempt + 1,
                    MaxForbiddenRetryCount);
                try
                {
                    await Task.Delay(forbiddenRetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (isCreateOperation)
                {
                    throw new AzureDevComputeCreateException(ex, responseMayHaveBeenLost: false);
                }
                continue;
            }

            if (!ShouldRetry(method, response.StatusCode) || attempt >= MaxRetryCount)
            {
                return response;
            }

            var delay = GetRetryDelay(response, retryDelay ?? s_defaultRetryDelay, DateTimeOffset.UtcNow);
            response.Dispose();
            logger.LogInformation("ADC request {Method} {Path} returned a transient HTTP response. Retrying after {Delay}.", method.Method, path, delay);
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (isCreateOperation)
            {
                throw new AzureDevComputeCreateException(ex, responseMayHaveBeenLost: false);
            }
        }
    }

    private static bool ShouldRetry(HttpMethod method, HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests ||
        ((int)statusCode >= 500 && CanRetryAfterNetworkFailure(method));

    private static bool CanRetryAfterNetworkFailure(HttpMethod method) =>
        method == HttpMethod.Get ||
        method == HttpMethod.Delete;

    internal static TimeSpan GetRetryDelay(
        HttpResponseMessage response,
        TimeSpan defaultDelay,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return ClampRetryDelay(delta, s_maxRetryDelay);
        }

        if (response.Headers.RetryAfter?.Date is { } retryDate)
        {
            return ClampRetryDelay(retryDate > now ? retryDate - now : TimeSpan.Zero, s_maxRetryDelay);
        }

        return ClampRetryDelay(defaultDelay, s_maxRetryDelay);
    }

    private static TimeSpan ClampRetryDelay(TimeSpan delay, TimeSpan maximum)
    {
        if (delay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay > maximum ? maximum : delay;
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        AzureDevComputeResourceScope scope,
        HttpMethod method,
        string path,
        object? content,
        CancellationToken cancellationToken,
        bool isCreateOperation)
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
        try
        {
            return await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (isCreateOperation)
        {
            throw new AzureDevComputeCreateException(ex, responseMayHaveBeenLost: true);
        }
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
        var permissionHint = response.StatusCode == HttpStatusCode.Forbidden
            ? " Verify that the calling principal has the Container Apps SandboxGroup Data Owner role on the sandbox group; newly-created role assignments can take a short time to propagate."
            : string.Empty;
        throw new InvalidOperationException($"ADC request '{method} {path}' failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {message}{permissionHint}");
    }

    private static Task<string> GetErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (response.Content.Headers.ContentLength == 0)
        {
            return Task.FromResult(string.Empty);
        }

        return Task.FromResult("The service returned an error response whose details were redacted.");
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

internal sealed class AzureDevComputeCreateException(Exception originalException, bool responseMayHaveBeenLost)
    : InvalidOperationException(originalException.Message, originalException)
{
    public Exception OriginalException { get; } = originalException;

    public bool ResponseMayHaveBeenLost { get; } = responseMayHaveBeenLost;
}

internal sealed record AzureDevComputeResourceScope(string SubscriptionId, string ResourceGroupName, string SandboxGroupName, string Region);

internal sealed class AzureDevComputeCreateDiskImageRequest
{
    public string? Name { get; init; }

    public Dictionary<string, string> Labels { get; init; } = [];

    public required AzureDevComputeDiskImageSource Source { get; init; }
}

internal sealed class AzureDevComputeDiskImageSource
{
    public string Kind { get; init; } = "registry";

    public required string ImageUrl { get; init; }

    public string? ManagedIdentityClientId { get; init; }
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

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public List<AzureDevComputeIdentitySetting>? IdentitySettings { get; init; }

    public bool? SkipEgressProxy { get; init; }

    public AzureDevComputeSandboxEgressPolicy? EgressPolicy { get; init; }

    public required AzureDevComputeSandboxSource SourcesRef { get; init; }

    public required AzureDevComputeSandboxResources Resources { get; init; }

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
    public string DefaultAction { get; init; } = "Deny";

    public string? TrafficInspection { get; init; }

    public List<AzureDevComputeSandboxEgressHostRule> HostRules { get; init; } = [];
}

internal sealed class AzureDevComputeSandboxEgressHostRule
{
    public string Action { get; init; } = "Allow";

    public required string Pattern { get; init; }
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
