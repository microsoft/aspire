// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;

namespace Aspire.Hosting.Azure.Tests;

public class DefaultArmClientProviderTests
{
    private const string SubscriptionId = "12345678-1234-1234-1234-123456789012";

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

        public override Request CreateRequest()
            => new TestRequest();

        public override void Process(HttpMessage message)
        {
            message.Response = CreateResponse(message.Request);
        }

        public override ValueTask ProcessAsync(HttpMessage message)
        {
            message.Response = CreateResponse(message.Request);
            return ValueTask.CompletedTask;
        }

        private Response CreateResponse(Request request)
        {
            var uri = request.Uri.ToUri();
            lock (_lock)
            {
                _requestUris.Add(uri);
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
                $"/subscriptions/{SubscriptionId}/locations" => $$"""
                    {
                      "value": [
                        {
                          "id": "/subscriptions/{{SubscriptionId}}/locations/eastus",
                          "name": "eastus",
                          "displayName": "East US"
                        },
                        {
                          "id": "/subscriptions/{{SubscriptionId}}/locations/westus3",
                          "name": "westus3",
                          "displayName": "West US 3"
                        }
                      ]
                    }
                    """,
                $"/subscriptions/{SubscriptionId}/providers/Microsoft.Search" => $$"""
                    {
                      "id": "/subscriptions/{{SubscriptionId}}/providers/Microsoft.Search",
                      "namespace": "Microsoft.Search",
                      "registrationState": "Registered",
                      "resourceTypes": [
                        {
                          "resourceType": "searchServices",
                          "locations": [ "East US", "West US 3" ]
                        }
                      ]
                    }
                    """,
                _ => throw new InvalidOperationException($"Unexpected ARM request: {uri}")
            };

            return CreateJsonResponse(content);
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
