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
        });

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
            });
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
        });
        var armClient = provider.GetArmClient(new CapturingTokenCredential(), SubscriptionId);
        var operations = new List<AzureDeploymentOperationDetails>();

        await foreach (var operation in armClient.GetDeploymentOperationsAsync(RootDeploymentId, recursive: true, CancellationToken.None))
        {
            operations.Add(operation);
        }

        Assert.Equal(["nested-a", "nested-b", "storage-a", "storage-b"], operations.Select(static operation => operation.OperationId));
        Assert.Equal(2, transport.MaxConcurrentNestedDeploymentOperationRequests);
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
        private readonly IReadOnlyList<(string Name, string DisplayName)> _locations;
        private readonly IReadOnlyList<string> _providerLocations;
        private readonly TaskCompletionSource _nestedDeploymentOperationRequestsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeNestedDeploymentOperationRequests;
        private int _maxConcurrentNestedDeploymentOperationRequests;
        private int _nestedDeploymentOperationRequestCount;

        public ProviderMetadataTransport(
            IReadOnlyList<(string Name, string DisplayName)>? locations = null,
            IReadOnlyList<string>? providerLocations = null)
        {
            _locations = locations ??
            [
                ("eastus", "East US"),
                ("westus3", "West US 3")
            ];
            _providerLocations = providerLocations ?? ["East US", "West US 3"];
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

        public int MaxConcurrentNestedDeploymentOperationRequests => Volatile.Read(ref _maxConcurrentNestedDeploymentOperationRequests);

        public override Request CreateRequest()
            => new TestRequest();

        public override void Process(HttpMessage message)
        {
            message.Response = CreateResponse(CaptureUri(message.Request));
        }

        public override async ValueTask ProcessAsync(HttpMessage message)
        {
            var uri = CaptureUri(message.Request);
            if (IsNestedDeploymentOperationsPath(uri.AbsolutePath))
            {
                await WaitForConcurrentNestedDeploymentOperationRequestAsync().ConfigureAwait(false);
            }

            message.Response = CreateResponse(uri);
        }

        private Uri CaptureUri(Request request)
        {
            var uri = request.Uri.ToUri();
            lock (_lock)
            {
                _requestUris.Add(uri);
            }

            return uri;
        }

        private Response CreateResponse(Uri uri)
        {
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

            return CreateJsonResponse(content);
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

        private static MockResponse CreateJsonResponse(string content)
        {
            var response = new MockResponse(200)
            {
                ContentStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
            };
            return response;
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
