// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;

namespace Aspire.Hosting.Azure.Tests;

public class DefaultArmClientProviderTests
{
    private const string SubscriptionId = "12345678-1234-1234-1234-123456789012";
    private const string RootDeploymentId = $"/subscriptions/{SubscriptionId}/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/root";
    private const string NestedADeploymentId = $"/subscriptions/{SubscriptionId}/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/nested-a";
    private const string NestedBDeploymentId = $"/subscriptions/{SubscriptionId}/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/nested-b";
    private const string KeyVaultResourceId = $"/subscriptions/{SubscriptionId}/resourceGroups/test-rg/providers/Microsoft.KeyVault/vaults/kv-test";
    private const string DeletedKeyVaultPath = $"/subscriptions/{SubscriptionId}/providers/Microsoft.KeyVault/locations/westus2/deletedVaults/kv-test";
    private const string DeletedKeyVaultPurgePath = $"/subscriptions/{SubscriptionId}/providers/Microsoft.KeyVault/locations/westus2/deletedVaults/kv-test/purge";
    private static readonly TimeSpan s_keyVaultPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_keyVaultDeleteTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_keyVaultPurgeTimeout = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task GetSupportedLocationsAsyncUsesConfiguredArmEnvironment()
    {
        var transport = new ProviderMetadataTransport();
        var credential = new CapturingTokenCredential();
        var environment = new ArmEnvironment(
            new Uri("https://management.contoso.example"),
            "https://management.contoso.example");
        var provider = new DefaultArmClientProvider(new ArmClientOptions
        {
            Environment = environment,
            Transport = transport
        }, TimeProvider.System);

        var armClient = provider.GetArmClient(credential, SubscriptionId);

        var locations = await armClient.GetSupportedLocationsAsync(
            SubscriptionId,
            "Microsoft.Search/searchServices",
            CancellationToken.None);

        Assert.Equal(["eastus", "westus3"], locations);
        Assert.All(transport.RequestUris, static uri => Assert.Equal("management.contoso.example", uri.Host));
        Assert.Contains("https://management.contoso.example/.default", credential.Scopes);
        Assert.DoesNotContain(credential.Scopes, static scope => scope.StartsWith("https://management.azure.com", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            transport.RequestUris,
            static uri => uri.AbsolutePath.EndsWith($"/subscriptions/{SubscriptionId}/providers/Microsoft.Search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAvailableLocationsAsyncSortsWithAzureLocationComparer()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUICulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

            var transport = new ProviderMetadataTransport(
                locations:
                [
                    ("asia", "Äsia"),
                    ("zulu", "Zulu"),
                    ("alpha", "alpha")
                ]);
            var provider = new DefaultArmClientProvider(new ArmClientOptions
            {
                Transport = transport
            }, TimeProvider.System);
            var armClient = provider.GetArmClient(new CapturingTokenCredential(), SubscriptionId);

            var locations = await armClient.GetAvailableLocationsAsync(SubscriptionId, CancellationToken.None);

            Assert.Equal(["alpha", "zulu", "asia"], locations.Select(static location => location.Name));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUICulture;
        }
    }

    [Fact]
    public async Task GetDeploymentOperationsAsyncFetchesNestedDeploymentOperationsInParallel()
    {
        var transport = new ProviderMetadataTransport();
        var provider = new DefaultArmClientProvider(new ArmClientOptions
        {
            Transport = transport
        }, TimeProvider.System);
        var armClient = provider.GetArmClient(new CapturingTokenCredential(), SubscriptionId);
        var operations = new List<AzureDeploymentOperationDetails>();

        await foreach (var operation in armClient.GetDeploymentOperationsAsync(RootDeploymentId, recursive: true, CancellationToken.None))
        {
            operations.Add(operation);
        }

        Assert.Equal(["nested-a", "nested-b", "storage-a", "storage-b"], operations.Select(static operation => operation.OperationId));
        Assert.Equal(2, transport.MaxConcurrentNestedDeploymentOperationRequests);
    }

    [Fact]
    public async Task GetDeploymentAsyncReturnsProvisioningStateAndOutputs()
    {
        var transport = new ProviderMetadataTransport();
        var provider = new DefaultArmClientProvider(new ArmClientOptions
        {
            Transport = transport
        }, TimeProvider.System);
        var armClient = provider.GetArmClient(new CapturingTokenCredential(), SubscriptionId);

        var deployment = await armClient.GetDeploymentAsync(RootDeploymentId, CancellationToken.None);

        Assert.NotNull(deployment);
        Assert.Equal(AzureDeploymentOperationDetails.SucceededState, deployment.ProvisioningState);
        Assert.Equal("https://storage.blob.core.windows.net/", deployment.Outputs?["blobEndpoint"]?["value"]?.GetValue<string>());
    }

    [Fact]
    public async Task DeleteResourceAsyncDeletesKeyVaultAndPurgeDeletedKeyVaultAsyncPurgesDeletedKeyVault()
    {
        var transport = new ProviderMetadataTransport();
        var provider = new DefaultArmClientProvider(new ArmClientOptions
        {
            Transport = transport
        }, TimeProvider.System);
        var armClient = provider.GetArmClient(new CapturingTokenCredential(), SubscriptionId);

        await armClient.DeleteResourceAsync(KeyVaultResourceId, cancellationToken: CancellationToken.None);
        var purged = await armClient.PurgeDeletedKeyVaultAsync(KeyVaultResourceId, "westus2", CancellationToken.None);

        Assert.True(purged);
        Assert.Contains(transport.Requests, static request => request.Method == RequestMethod.Get.ToString() && request.Uri.AbsolutePath == KeyVaultResourceId);
        Assert.Contains(transport.Requests, static request => request.Method == RequestMethod.Delete.ToString() && request.Uri.AbsolutePath == KeyVaultResourceId);
        Assert.Contains(transport.Requests, static request =>
            request.Method == RequestMethod.Post.ToString() &&
            request.Uri.AbsolutePath == DeletedKeyVaultPurgePath &&
            request.Uri.Query.Contains("api-version=", StringComparison.Ordinal));
        Assert.Contains(transport.Requests, static request =>
            request.Method == RequestMethod.Get.ToString() &&
            request.Uri.AbsolutePath == DeletedKeyVaultPath);
    }

    [Fact]
    public async Task PurgeDeletedKeyVaultAsyncReturnsFalseWhenDeletedKeyVaultIsAbsentFromLocation()
    {
        var transport = new ProviderMetadataTransport();
        var provider = new DefaultArmClientProvider(new ArmClientOptions
        {
            Transport = transport
        }, TimeProvider.System);
        var armClient = provider.GetArmClient(new CapturingTokenCredential(), SubscriptionId);

        var purged = await armClient.PurgeDeletedKeyVaultAsync(KeyVaultResourceId, "ukwest", CancellationToken.None);

        Assert.False(purged);
        Assert.DoesNotContain(transport.Requests, static request => request.Method == RequestMethod.Delete.ToString() && request.Uri.AbsolutePath == KeyVaultResourceId);
        Assert.Contains(transport.Requests, static request =>
            request.Method == RequestMethod.Post.ToString() &&
            request.Uri.AbsolutePath.Contains("/locations/ukwest/deletedVaults/kv-test/purge", StringComparison.Ordinal));
        Assert.DoesNotContain(transport.Requests, static request => request.Uri.AbsolutePath == DeletedKeyVaultPurgePath);
    }

    [Fact]
    public async Task DeleteResourceAsyncAndPurgeDeletedKeyVaultAsyncPollWithConfiguredTimeProvider()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new ProviderMetadataTransport(keyVaultDeletePollsBeforeDeleted: 1, deletedKeyVaultPurgePollsBeforePurged: 1);
        var provider = new DefaultArmClientProvider(new ArmClientOptions
        {
            Transport = transport
        }, timeProvider);
        var armClient = provider.GetArmClient(new CapturingTokenCredential(), SubscriptionId);

        var deleteTask = armClient.DeleteResourceAsync(KeyVaultResourceId, cancellationToken: CancellationToken.None);

        await transport.WaitForKeyVaultDeletePollAsync();
        await timeProvider.WaitForTimerAsync(s_keyVaultPollInterval);
        Assert.False(deleteTask.IsCompleted);

        timeProvider.FireTimer(s_keyVaultPollInterval);

        await deleteTask;

        var purgeTask = armClient.PurgeDeletedKeyVaultAsync(KeyVaultResourceId, "westus2", CancellationToken.None);

        await transport.WaitForDeletedKeyVaultPurgePollAsync();
        await timeProvider.WaitForTimerAsync(s_keyVaultPollInterval);
        Assert.False(purgeTask.IsCompleted);

        timeProvider.FireTimer(s_keyVaultPollInterval);

        var purged = await purgeTask;

        Assert.True(purged);
        Assert.Contains(transport.Requests, static request =>
            request.Method == RequestMethod.Post.ToString() &&
            request.Uri.AbsolutePath == DeletedKeyVaultPurgePath &&
            request.Uri.Query.Contains("api-version=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteResourceAsyncUsesConfiguredTimeProviderForKeyVaultDeleteTimeout()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new ProviderMetadataTransport(keyVaultDeletePollsBeforeDeleted: int.MaxValue);
        var provider = new DefaultArmClientProvider(new ArmClientOptions
        {
            Transport = transport
        }, timeProvider);
        var armClient = provider.GetArmClient(new CapturingTokenCredential(), SubscriptionId);

        var deleteTask = armClient.DeleteResourceAsync(KeyVaultResourceId, cancellationToken: CancellationToken.None);

        await transport.WaitForKeyVaultDeletePollAsync();
        await timeProvider.WaitForTimerAsync(s_keyVaultDeleteTimeout);

        timeProvider.FireTimer(s_keyVaultDeleteTimeout);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => deleteTask);
        Assert.Contains("to be deleted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PurgeDeletedKeyVaultAsyncUsesConfiguredTimeProviderForKeyVaultPurgeTimeout()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new ProviderMetadataTransport(deletedKeyVaultPurgePollsBeforePurged: int.MaxValue);
        var provider = new DefaultArmClientProvider(new ArmClientOptions
        {
            Transport = transport
        }, timeProvider);
        var armClient = provider.GetArmClient(new CapturingTokenCredential(), SubscriptionId);

        var purgeTask = armClient.PurgeDeletedKeyVaultAsync(KeyVaultResourceId, "westus2", CancellationToken.None);

        await transport.WaitForDeletedKeyVaultPurgePollAsync();
        await timeProvider.WaitForTimerAsync(s_keyVaultPurgeTimeout);

        timeProvider.FireTimer(s_keyVaultPurgeTimeout);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => purgeTask);
        Assert.Contains("to be purged", exception.Message, StringComparison.Ordinal);
    }

    private sealed class CapturingTokenCredential : TokenCredential
    {
        private readonly object _lock = new();
        private readonly List<string> _scopes = [];

        public IReadOnlyList<string> Scopes
        {
            get
            {
                lock (_lock)
                {
                    return _scopes.ToArray();
                }
            }
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken = default)
        {
            CaptureScopes(requestContext);
            return CreateToken();
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken = default)
        {
            CaptureScopes(requestContext);
            return ValueTask.FromResult(CreateToken());
        }

        private void CaptureScopes(TokenRequestContext requestContext)
        {
            lock (_lock)
            {
                _scopes.AddRange(requestContext.Scopes);
            }
        }

        private static AccessToken CreateToken()
            => new("token", DateTimeOffset.UtcNow.AddHours(1));
    }

    private sealed class ProviderMetadataTransport : HttpPipelineTransport
    {
        private readonly object _lock = new();
        private readonly List<Uri> _requestUris = [];
        private readonly List<CapturedRequest> _requests = [];
        private readonly IReadOnlyList<(string Name, string DisplayName)> _locations;
        private readonly IReadOnlyList<string> _providerLocations;
        private readonly int _keyVaultGetStatus;
        private readonly int _keyVaultDeleteStatus;
        private readonly int _keyVaultDeletePollsBeforeDeleted;
        private readonly int _deletedKeyVaultPurgePollsBeforePurged;
        private bool _keyVaultDeleteRequested;
        private bool _deletedKeyVaultPurgeRequested;
        private int _keyVaultDeletePollCount;
        private int _deletedKeyVaultPurgePollCount;
        private readonly TaskCompletionSource _nestedDeploymentOperationRequestsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _keyVaultDeletePollStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _deletedKeyVaultPurgePollStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeNestedDeploymentOperationRequests;
        private int _maxConcurrentNestedDeploymentOperationRequests;
        private int _nestedDeploymentOperationRequestCount;

        public ProviderMetadataTransport(
            IReadOnlyList<(string Name, string DisplayName)>? locations = null,
            IReadOnlyList<string>? providerLocations = null,
            int keyVaultGetStatus = 200,
            int keyVaultDeleteStatus = 200,
            int keyVaultDeletePollsBeforeDeleted = 0,
            int deletedKeyVaultPurgePollsBeforePurged = 0)
        {
            _locations = locations ??
            [
                ("eastus", "East US"),
                ("westus3", "West US 3")
            ];
            _providerLocations = providerLocations ?? ["East US", "West US 3"];
            _keyVaultGetStatus = keyVaultGetStatus;
            _keyVaultDeleteStatus = keyVaultDeleteStatus;
            _keyVaultDeletePollsBeforeDeleted = keyVaultDeletePollsBeforeDeleted;
            _deletedKeyVaultPurgePollsBeforePurged = deletedKeyVaultPurgePollsBeforePurged;
        }

        public IReadOnlyList<Uri> RequestUris
        {
            get
            {
                lock (_lock)
                {
                    return _requestUris.ToArray();
                }
            }
        }

        public IReadOnlyList<CapturedRequest> Requests
        {
            get
            {
                lock (_lock)
                {
                    return _requests.ToArray();
                }
            }
        }

        public int MaxConcurrentNestedDeploymentOperationRequests => Volatile.Read(ref _maxConcurrentNestedDeploymentOperationRequests);

        public Task WaitForKeyVaultDeletePollAsync()
            => _keyVaultDeletePollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public Task WaitForDeletedKeyVaultPurgePollAsync()
            => _deletedKeyVaultPurgePollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public override Request CreateRequest()
            => new TestRequest();

        public override void Process(HttpMessage message)
        {
            message.Response = CreateResponse(CaptureRequest(message.Request));
        }

        public override async ValueTask ProcessAsync(HttpMessage message)
        {
            var request = CaptureRequest(message.Request);
            if (IsNestedDeploymentOperationsPath(request.Uri.AbsolutePath))
            {
                await WaitForConcurrentNestedDeploymentOperationRequestAsync().ConfigureAwait(false);
            }

            message.Response = CreateResponse(request);
        }

        private CapturedRequest CaptureRequest(Request request)
        {
            var uri = request.Uri.ToUri();
            var capturedRequest = new CapturedRequest(request.Method.ToString(), uri);
            lock (_lock)
            {
                _requestUris.Add(uri);
                _requests.Add(capturedRequest);
            }

            return capturedRequest;
        }

        private Response CreateResponse(CapturedRequest request)
        {
            var uri = request.Uri;
            if (request.Method == RequestMethod.Get.ToString() &&
                string.Equals(uri.AbsolutePath, KeyVaultResourceId, StringComparison.Ordinal))
            {
                return CreateKeyVaultGetResponse();
            }

            if (request.Method == RequestMethod.Delete.ToString() &&
                string.Equals(uri.AbsolutePath, KeyVaultResourceId, StringComparison.Ordinal))
            {
                return CreateKeyVaultDeleteResponse();
            }

            if (request.Method == RequestMethod.Post.ToString() &&
                IsDeletedKeyVaultPurgePath(uri.AbsolutePath))
            {
                return CreateDeletedKeyVaultPurgeResponse(uri.AbsolutePath);
            }

            if (request.Method == RequestMethod.Get.ToString() &&
                IsDeletedKeyVaultPath(uri.AbsolutePath))
            {
                return CreateDeletedKeyVaultGetResponse(uri.AbsolutePath);
            }

            var content = uri.AbsolutePath switch
            {
                $"/subscriptions/{SubscriptionId}" => $$"""
                    {
                      "id": "/subscriptions/{{SubscriptionId}}",
                      "subscriptionId": "{{SubscriptionId}}",
                      "displayName": "Test Subscription",
                      "state": "Enabled",
                      "tenantId": "87654321-4321-4321-4321-210987654321"
                    }
                    """,
                $"/subscriptions/{SubscriptionId}/locations" => CreateLocationsContent(),
                $"/subscriptions/{SubscriptionId}/providers/Microsoft.Search" => CreateProviderContent(),
                $"/subscriptions/{SubscriptionId}/providers/Microsoft.KeyVault" => CreateKeyVaultProviderContent(),
                var path when string.Equals(path, RootDeploymentId, StringComparison.Ordinal) => CreateDeploymentContent(RootDeploymentId),
                var path when string.Equals(path, $"{RootDeploymentId}/operations", StringComparison.Ordinal) => CreateDeploymentOperationsContent(
                    RootDeploymentId,
                    ("nested-a", NestedADeploymentId, AzureDeploymentOperationDetails.DeploymentResourceType, "nested-a"),
                    ("nested-b", NestedBDeploymentId, AzureDeploymentOperationDetails.DeploymentResourceType, "nested-b")),
                var path when string.Equals(path, $"{NestedADeploymentId}/operations", StringComparison.Ordinal) => CreateDeploymentOperationsContent(
                    NestedADeploymentId,
                    ("storage-a", $"/subscriptions/{SubscriptionId}/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage-a", "Microsoft.Storage/storageAccounts", "storage-a")),
                var path when string.Equals(path, $"{NestedBDeploymentId}/operations", StringComparison.Ordinal) => CreateDeploymentOperationsContent(
                    NestedBDeploymentId,
                    ("storage-b", $"/subscriptions/{SubscriptionId}/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage-b", "Microsoft.Storage/storageAccounts", "storage-b")),
                _ => throw new InvalidOperationException($"Unexpected ARM request: {uri}")
            };

            return content is null ? CreateEmptyResponse(200) : CreateJsonResponse(content);
        }

        private Response CreateKeyVaultGetResponse()
        {
            if (_keyVaultGetStatus == 404)
            {
                return CreateEmptyResponse(404);
            }

            if (!_keyVaultDeleteRequested)
            {
                return CreateJsonResponse(CreateKeyVaultResourceContent());
            }

            var pollCount = Interlocked.Increment(ref _keyVaultDeletePollCount);
            if (pollCount <= _keyVaultDeletePollsBeforeDeleted)
            {
                _keyVaultDeletePollStarted.TrySetResult();
                return CreateJsonResponse(CreateKeyVaultResourceContent());
            }

            return CreateEmptyResponse(404);
        }

        private Response CreateKeyVaultDeleteResponse()
        {
            if (_keyVaultDeleteStatus == 404)
            {
                return CreateEmptyResponse(404);
            }

            _keyVaultDeleteRequested = true;
            return CreateEmptyResponse(200);
        }

        private Response CreateDeletedKeyVaultPurgeResponse(string path)
        {
            if (!string.Equals(path, DeletedKeyVaultPurgePath, StringComparison.Ordinal))
            {
                return CreateEmptyResponse(404);
            }

            _deletedKeyVaultPurgeRequested = true;
            return CreateEmptyResponse(200);
        }

        private Response CreateDeletedKeyVaultGetResponse(string path)
        {
            if (!string.Equals(path, DeletedKeyVaultPath, StringComparison.Ordinal))
            {
                return CreateEmptyResponse(404);
            }

            if (!_deletedKeyVaultPurgeRequested)
            {
                return CreateJsonResponse(CreateDeletedKeyVaultResourceContent(path));
            }

            var pollCount = Interlocked.Increment(ref _deletedKeyVaultPurgePollCount);
            if (pollCount <= _deletedKeyVaultPurgePollsBeforePurged)
            {
                _deletedKeyVaultPurgePollStarted.TrySetResult();
                return CreateJsonResponse(CreateDeletedKeyVaultResourceContent(path));
            }

            return CreateEmptyResponse(404);
        }

        private async Task WaitForConcurrentNestedDeploymentOperationRequestAsync()
        {
            var activeRequests = Interlocked.Increment(ref _activeNestedDeploymentOperationRequests);
            UpdateMaxConcurrentNestedDeploymentOperationRequests(activeRequests);

            try
            {
                if (Interlocked.Increment(ref _nestedDeploymentOperationRequestCount) == 2)
                {
                    _nestedDeploymentOperationRequestsStarted.TrySetResult();
                }

                await _nestedDeploymentOperationRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeNestedDeploymentOperationRequests);
            }
        }

        private void UpdateMaxConcurrentNestedDeploymentOperationRequests(int activeRequests)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maxConcurrentNestedDeploymentOperationRequests);
                if (activeRequests <= observed ||
                    Interlocked.CompareExchange(ref _maxConcurrentNestedDeploymentOperationRequests, activeRequests, observed) == observed)
                {
                    return;
                }
            }
        }

        private static bool IsNestedDeploymentOperationsPath(string path)
            => string.Equals(path, $"{NestedADeploymentId}/operations", StringComparison.Ordinal) ||
               string.Equals(path, $"{NestedBDeploymentId}/operations", StringComparison.Ordinal);

        private static bool IsDeletedKeyVaultPurgePath(string path)
            => path.Contains("/providers/Microsoft.KeyVault/locations/", StringComparison.Ordinal) &&
               path.EndsWith("/deletedVaults/kv-test/purge", StringComparison.Ordinal);

        private static bool IsDeletedKeyVaultPath(string path)
            => path.Contains("/providers/Microsoft.KeyVault/locations/", StringComparison.Ordinal) &&
               path.EndsWith("/deletedVaults/kv-test", StringComparison.Ordinal);

        private string CreateLocationsContent()
        {
            return JsonSerializer.Serialize(new
            {
                value = _locations.Select(static location => new
                {
                    id = $"/subscriptions/{SubscriptionId}/locations/{location.Name}",
                    name = location.Name,
                    displayName = location.DisplayName
                })
            });
        }

        private string CreateProviderContent()
        {
            return JsonSerializer.Serialize(new
            {
                id = $"/subscriptions/{SubscriptionId}/providers/Microsoft.Search",
                @namespace = "Microsoft.Search",
                registrationState = "Registered",
                resourceTypes = new[]
                {
                    new
                    {
                        resourceType = "searchServices",
                        locations = _providerLocations
                    }
                }
            });
        }

        private static string CreateKeyVaultProviderContent()
        {
            return JsonSerializer.Serialize(new
            {
                id = $"/subscriptions/{SubscriptionId}/providers/Microsoft.KeyVault",
                @namespace = "Microsoft.KeyVault",
                registrationState = "Registered",
                resourceTypes = new[]
                {
                    new
                    {
                        resourceType = "vaults",
                        locations = new[] { "West US 2" },
                        apiVersions = new[] { "2023-07-01" }
                    }
                }
            });
        }

        private static string CreateDeploymentContent(string deploymentId)
        {
            return JsonSerializer.Serialize(new
            {
                id = deploymentId,
                name = "root",
                type = AzureDeploymentOperationDetails.DeploymentResourceType,
                properties = new
                {
                    provisioningState = AzureDeploymentOperationDetails.SucceededState,
                    outputs = new
                    {
                        blobEndpoint = new
                        {
                            value = "https://storage.blob.core.windows.net/"
                        }
                    }
                }
            });
        }

        private static string CreateDeploymentOperationsContent(
            string deploymentId,
            params (string OperationId, string TargetResourceId, string TargetResourceType, string TargetResourceName)[] operations)
        {
            return JsonSerializer.Serialize(new
            {
                value = operations.Select(operation => new
                {
                    id = $"{deploymentId}/operations/{operation.OperationId}",
                    operationId = operation.OperationId,
                    properties = new
                    {
                        provisioningOperation = AzureDeploymentOperationDetails.CreateOperation,
                        provisioningState = AzureDeploymentOperationDetails.SucceededState,
                        statusCode = "OK",
                        targetResource = new
                        {
                            id = operation.TargetResourceId,
                            resourceType = operation.TargetResourceType,
                            resourceName = operation.TargetResourceName
                        }
                    }
                })
            });
        }

        private static string CreateKeyVaultResourceContent()
        {
            return JsonSerializer.Serialize(new
            {
                id = KeyVaultResourceId,
                name = "kv-test",
                type = "Microsoft.KeyVault/vaults",
                location = "westus2"
            });
        }

        private static string CreateDeletedKeyVaultResourceContent(string deletedVaultPath)
        {
            return JsonSerializer.Serialize(new
            {
                id = deletedVaultPath,
                name = "kv-test",
                type = "Microsoft.KeyVault/deletedVaults",
                properties = new
                {
                    vaultId = KeyVaultResourceId,
                    location = "westus2",
                    deletionDate = "2026-06-17T00:00:00Z",
                    scheduledPurgeDate = "2026-09-15T00:00:00Z"
                }
            });
        }

        private static MockResponse CreateJsonResponse(string content)
        {
            var response = new MockResponse(200)
            {
                ContentStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
            };
            return response;
        }

        private static MockResponse CreateEmptyResponse(int status)
            => new(status);

        public sealed record CapturedRequest(string Method, Uri Uri);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<ManualTimer> _timers = [];
        private TaskCompletionSource _timerCreated = CreateCompletionSource();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);

            lock (_lock)
            {
                _timers.Add(timer);
                _timerCreated.TrySetResult();
                _timerCreated = CreateCompletionSource();
            }

            return timer;
        }

        public async Task WaitForTimerAsync(TimeSpan dueTime)
        {
            while (true)
            {
                Task timerCreatedTask;
                lock (_lock)
                {
                    if (_timers.Any(timer => timer.IsActiveFor(dueTime)))
                    {
                        return;
                    }

                    timerCreatedTask = _timerCreated.Task;
                }

                await timerCreatedTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        public void FireTimer(TimeSpan dueTime)
        {
            ManualTimer? timer;
            lock (_lock)
            {
                timer = _timers.FirstOrDefault(timer => timer.IsActiveFor(dueTime));
            }

            if (timer is null)
            {
                throw new InvalidOperationException($"No active timer exists for '{dueTime}'.");
            }

            timer.Fire();
        }

        private static TaskCompletionSource CreateCompletionSource()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private readonly object _lock = new();
            private TimeSpan _dueTime = dueTime;
            private bool _disposed;
            private bool _fired;

            public bool IsActiveFor(TimeSpan dueTime)
            {
                lock (_lock)
                {
                    return !_disposed && !_fired && _dueTime == dueTime;
                }
            }

            public void Fire()
            {
                lock (_lock)
                {
                    if (_disposed || _fired)
                    {
                        return;
                    }

                    _fired = true;
                }

                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_lock)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    _dueTime = dueTime;
                    _fired = false;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    _disposed = true;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class TestRequest : Request
    {
        private readonly Dictionary<string, List<string>> _headers = new(StringComparer.OrdinalIgnoreCase);

        public override string ClientRequestId { get; set; } = Guid.NewGuid().ToString();

        protected override void AddHeader(string name, string value)
        {
            if (!_headers.TryGetValue(name, out var values))
            {
                values = [];
                _headers[name] = values;
            }

            values.Add(value);
        }

        protected override bool ContainsHeader(string name) => _headers.ContainsKey(name);

        protected override IEnumerable<HttpHeader> EnumerateHeaders()
            => _headers.Select(static header => new HttpHeader(header.Key, string.Join(",", header.Value)));

        protected override bool RemoveHeader(string name) => _headers.Remove(name);

        protected override void SetHeader(string name, string value)
            => _headers[name] = [value];

        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
        {
            if (_headers.TryGetValue(name, out var values))
            {
                value = string.Join(",", values);
                return true;
            }

            value = null;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = _headers.TryGetValue(name, out var headerValues)
                ? headerValues
                : null;
            return values is not null;
        }

        public override void Dispose()
        {
        }
    }
}
