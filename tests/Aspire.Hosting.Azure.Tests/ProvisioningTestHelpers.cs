// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable CS0618 // Type or member is obsolete

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.Tests;

/// <summary>
/// Test helpers for creating testable provisioning services.
/// </summary>
internal static class ProvisioningTestHelpers
{
    /// <summary>
    /// Creates a test-friendly ProvisioningContext.
    /// </summary>
    public static ProvisioningContext CreateTestProvisioningContext(
        TokenCredential? credential = null,
        IArmClient? armClient = null,
        ISubscriptionResource? subscription = null,
        IResourceGroupResource? resourceGroup = null,
        ITenantResource? tenant = null,
        AzureLocation? location = null,
        UserPrincipal? principal = null,
        DistributedApplicationExecutionContext? executionContext = null)
    {
        return new ProvisioningContext(
            credential ?? new TestTokenCredential(),
            armClient ?? new TestArmClient(),
            subscription ?? new TestSubscriptionResource(),
            resourceGroup ?? new TestResourceGroupResource(),
            tenant ?? new TestTenantResource(),
            location ?? AzureLocation.WestUS2,
            principal ?? new UserPrincipal(Guid.NewGuid(), "test@example.com"),
            executionContext ?? new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));
    }

    // Factory methods for test implementations of provisioning services interfaces
    public static IArmClientProvider CreateArmClientProvider() => new TestArmClientProvider();
    public static IArmClientProvider CreateArmClientProvider(Dictionary<string, object> deploymentOutputs) => new TestArmClientProvider(deploymentOutputs);
    public static IArmClientProvider CreateArmClientProvider(Func<string, Dictionary<string, object>> deploymentOutputsProvider) => new TestArmClientProvider(deploymentOutputsProvider);
    public static ITokenCredentialProvider CreateTokenCredentialProvider() => new TestTokenCredentialProvider();
    public static ISecretClientProvider CreateSecretClientProvider() => new TestSecretClientProvider(CreateTokenCredentialProvider());
    public static IBicepCompiler CreateBicepCompiler() => new TestBicepCompiler();
    public static IDeploymentStateManager CreateUserSecretsManager() => new TestUserSecretsManager();
    public static IUserPrincipalProvider CreateUserPrincipalProvider() => new TestUserPrincipalProvider();
    public static TokenCredential CreateTokenCredential() => new TestTokenCredential();

    /// <summary>
    /// Creates test options for Azure provisioner.
    /// </summary>
    public static IOptions<AzureProvisionerOptions> CreateOptions(
        string? subscriptionId = "12345678-1234-1234-1234-123456789012",
        string? location = "westus2",
        string? resourceGroup = "test-rg")
    {
        var options = new AzureProvisionerOptions
        {
            SubscriptionId = subscriptionId,
            Location = location,
            ResourceGroup = resourceGroup
        };
        return Options.Create(options);
    }

    public static IOptions<PublishingOptions> CreatePublishingOptions(
        string? outputPath = null)
    {
        var options = new PublishingOptions
        {
            OutputPath = outputPath,
        };
        return Options.Create(options);
    }

    /// <summary>
    /// Creates a test host environment.
    /// </summary>
    public static IHostEnvironment CreateEnvironment()
    {
        var environment = new TestHostEnvironment
        {
            ApplicationName = "TestApp"
        };
        return environment;
    }

    /// <summary>
    /// Creates a test logger for RunModeProvisioningContextProvider.
    /// </summary>
    public static ILogger<RunModeProvisioningContextProvider> CreateLogger()
    {
        return NullLogger<RunModeProvisioningContextProvider>.Instance;
    }

    /// <summary>
    /// Creates a test logger for the specified type.
    /// </summary>
    public static ILogger<T> CreateLogger<T>() where T : class
    {
        return NullLogger<T>.Instance;
    }
}

/// <summary>
/// Test implementation of <see cref="TokenCredential"/>.
/// </summary>
internal sealed class TestTokenCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = CreateTestJwtToken();
        return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = CreateTestJwtToken();
        return ValueTask.FromResult(new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1)));
    }

    private static string CreateTestJwtToken()
    {
        var headerJson = JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" });
        var headerBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(headerJson))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var payload = new
        {
            oid = "11111111-2222-3333-4444-555555555555",
            upn = "test@example.com",
            exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var payloadJson = JsonSerializer.Serialize(payload);
        var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var signatureBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-signature"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return $"{headerBase64}.{payloadBase64}.{signatureBase64}";
    }
}

/// <summary>
/// Test implementation of <see cref="IArmClient"/>.
/// </summary>
internal sealed class TestArmClient : IArmClient
{
    private readonly Dictionary<string, object>? _deploymentOutputs;
    private readonly Func<string, Dictionary<string, object>>? _deploymentOutputsProvider;
    private readonly TestResourceGroupResource? _resourceGroup;

    public TestArmClient(Dictionary<string, object> deploymentOutputs, TestResourceGroupResource? resourceGroup = null)
    {
        _deploymentOutputs = deploymentOutputs;
        _resourceGroup = resourceGroup;
    }

    public TestArmClient(Func<string, Dictionary<string, object>> deploymentOutputsProvider)
    {
        _deploymentOutputsProvider = deploymentOutputsProvider;
    }

    public TestArmClient() : this([])
    {
    }

    public Task<(ISubscriptionResource subscription, ITenantResource tenant)> GetSubscriptionAndTenantAsync(CancellationToken cancellationToken = default)
    {
        ISubscriptionResource subscription;
        if (_deploymentOutputsProvider is not null)
        {
            subscription = new TestSubscriptionResource(_deploymentOutputsProvider);
        }
        else
        {
            subscription = new TestSubscriptionResource(_deploymentOutputs!, _resourceGroup);
        }
        var tenant = new TestTenantResource();
        return Task.FromResult<(ISubscriptionResource, ITenantResource)>((subscription, tenant));
    }

    public Task<IEnumerable<ITenantResource>> GetAvailableTenantsAsync(CancellationToken cancellationToken = default)
    {
        var tenants = new List<ITenantResource>
        {
            new TestTenantResource()
        };
        return Task.FromResult<IEnumerable<ITenantResource>>(tenants);
    }

    public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        var subscriptions = new List<ISubscriptionResource>
        {
            new TestSubscriptionResource()
        };
        return Task.FromResult<IEnumerable<ISubscriptionResource>>(subscriptions);
    }

    public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(string? tenantId, CancellationToken cancellationToken = default)
    {
        // For testing, return the same subscription regardless of tenant filtering
        _ = tenantId; // Suppress unused parameter warning
        _ = cancellationToken; // Suppress unused parameter warning
        var subscriptions = new List<ISubscriptionResource>
        {
            new TestSubscriptionResource()
        };
        return Task.FromResult<IEnumerable<ISubscriptionResource>>(subscriptions);
    }

    public Task<IEnumerable<(string Name, string DisplayName)>> GetAvailableLocationsAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        var locations = new List<(string Name, string DisplayName)>
        {
            ("eastus", "East US"),
            ("westus", "West US"),
            ("westus2", "West US 2")
        };
        return Task.FromResult<IEnumerable<(string, string)>>(locations);
    }

    public Task<IEnumerable<(string Name, string Location)>> GetAvailableResourceGroupsWithLocationAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        var resourceGroups = new List<(string Name, string Location)>
        {
            ("rg-test-1", "eastus"),
            ("rg-test-2", "westus"),
            ("rg-aspire-dev", "westus2")
        };
        return Task.FromResult<IEnumerable<(string, string)>>(resourceGroups);
    }
}

/// <summary>
/// Test implementation of <see cref="ISubscriptionResource"/>.
/// </summary>
internal sealed class TestSubscriptionResource : ISubscriptionResource
{
    private readonly Dictionary<string, object>? _deploymentOutputs;
    private readonly Func<string, Dictionary<string, object>>? _deploymentOutputsProvider;
    private readonly TestResourceGroupResource? _resourceGroup;

    public TestSubscriptionResource(Dictionary<string, object> deploymentOutputs, TestResourceGroupResource? resourceGroup = null)
    {
        _deploymentOutputs = deploymentOutputs;
        _resourceGroup = resourceGroup;
    }

    public TestSubscriptionResource(Func<string, Dictionary<string, object>> deploymentOutputsProvider)
    {
        _deploymentOutputsProvider = deploymentOutputsProvider;
    }

    public TestSubscriptionResource() : this([])
    {
    }

    public ResourceIdentifier Id { get; } = new ResourceIdentifier("/subscriptions/12345678-1234-1234-1234-123456789012");
    public string? DisplayName { get; } = "Test Subscription";
    public Guid? TenantId { get; } = Guid.Parse("87654321-4321-4321-4321-210987654321");

    public IArmDeploymentCollection GetArmDeployments()
    {
        if (_deploymentOutputsProvider is not null)
        {
            return new TestArmDeploymentCollection(_deploymentOutputsProvider);
        }
        return new TestArmDeploymentCollection(_deploymentOutputs!);
    }

    public IResourceGroupCollection GetResourceGroups()
    {
        if (_deploymentOutputsProvider is not null)
        {
            return new TestResourceGroupCollection(_deploymentOutputsProvider);
        }
        return new TestResourceGroupCollection(_deploymentOutputs!, _resourceGroup);
    }
}

/// <summary>
/// Test implementation of <see cref="IResourceGroupCollection"/>.
/// </summary>
internal sealed class TestResourceGroupCollection : IResourceGroupCollection
{
    private readonly Dictionary<string, object>? _deploymentOutputs;
    private readonly Func<string, Dictionary<string, object>>? _deploymentOutputsProvider;
    private readonly TestResourceGroupResource? _resourceGroup;

    public TestResourceGroupCollection(Dictionary<string, object> deploymentOutputs, TestResourceGroupResource? resourceGroup = null)
    {
        _deploymentOutputs = deploymentOutputs;
        _resourceGroup = resourceGroup;
    }

    public TestResourceGroupCollection(Func<string, Dictionary<string, object>> deploymentOutputsProvider)
    {
        _deploymentOutputsProvider = deploymentOutputsProvider;
    }

    public TestResourceGroupCollection() : this([])
    {
    }

    public Task<Response<IResourceGroupResource>> GetAsync(string resourceGroupName, CancellationToken cancellationToken = default)
    {
        if (_resourceGroup is not null)
        {
            return Task.FromResult(Response.FromValue<IResourceGroupResource>(_resourceGroup, new MockResponse(200)));
        }

        IResourceGroupResource resourceGroup;
        if (_deploymentOutputsProvider is not null)
        {
            resourceGroup = new TestResourceGroupResource(resourceGroupName, _deploymentOutputsProvider);
        }
        else
        {
            resourceGroup = new TestResourceGroupResource(resourceGroupName, _deploymentOutputs!);
        }
        return Task.FromResult(Response.FromValue<IResourceGroupResource>(resourceGroup, new MockResponse(200)));
    }

    public Task<ArmOperation<IResourceGroupResource>> CreateOrUpdateAsync(WaitUntil waitUntil, string resourceGroupName, ResourceGroupData data, CancellationToken cancellationToken = default)
    {
        IResourceGroupResource resourceGroup;
        if (_deploymentOutputsProvider is not null)
        {
            resourceGroup = new TestResourceGroupResource(resourceGroupName, _deploymentOutputsProvider);
        }
        else
        {
            resourceGroup = new TestResourceGroupResource(resourceGroupName, _deploymentOutputs!);
        }
        var operation = new TestArmOperation<IResourceGroupResource>(resourceGroup);
        return Task.FromResult<ArmOperation<IResourceGroupResource>>(operation);
    }
}

/// <summary>
/// Test implementation of <see cref="IResourceGroupResource"/>.
/// </summary>
internal sealed class TestResourceGroupResource : IResourceGroupResource
{
    private readonly Dictionary<string, object>? _deploymentOutputs;
    private readonly Func<string, Dictionary<string, object>>? _deploymentOutputsProvider;
    private readonly string _name;

    public TestResourceGroupResource(string name, Dictionary<string, object> deploymentOutputs)
    {
        _name = name;
        _deploymentOutputs = deploymentOutputs;
    }

    public TestResourceGroupResource(string name, Func<string, Dictionary<string, object>> deploymentOutputsProvider)
    {
        _name = name;
        _deploymentOutputsProvider = deploymentOutputsProvider;
    }

    public TestResourceGroupResource(string name = "test-rg") : this(name, [])
    {
    }

    public ResourceIdentifier Id { get; } = new ResourceIdentifier("/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg");
    public string Name => _name;

    public IArmDeploymentCollection GetArmDeployments()
    {
        if (_deploymentOutputsProvider is not null)
        {
            return new TestArmDeploymentCollection(_deploymentOutputsProvider);
        }
        return new TestArmDeploymentCollection(_deploymentOutputs!);
    }

    public bool WasDeleteCalled { get; private set; }
    public bool WasGetResourcesCalled { get; private set; }

    public Task DeleteAsync(WaitUntil waitUntil, CancellationToken cancellationToken = default)
    {
        WasDeleteCalled = true;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<(string Name, string ResourceType)> GetResourcesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        WasGetResourcesCalled = true;
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>
/// Test implementation of <see cref="IArmDeploymentCollection"/>.
/// </summary>
internal sealed class TestArmDeploymentCollection : IArmDeploymentCollection
{
    private readonly Dictionary<string, object>? _deploymentOutputs;
    private readonly Func<string, Dictionary<string, object>>? _deploymentOutputsProvider;

    public TestArmDeploymentCollection(Dictionary<string, object> deploymentOutputs)
    {
        _deploymentOutputs = deploymentOutputs;
    }

    public TestArmDeploymentCollection(Func<string, Dictionary<string, object>> deploymentOutputsProvider)
    {
        _deploymentOutputsProvider = deploymentOutputsProvider;
    }

    public TestArmDeploymentCollection() : this([])
    {
    }

    public Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
        WaitUntil waitUntil,
        string deploymentName,
        ArmDeploymentContent content,
        CancellationToken cancellationToken = default)
    {
        TestArmDeploymentResource deployment;
        if (_deploymentOutputsProvider is not null)
        {
            deployment = new TestArmDeploymentResource(deploymentName, _deploymentOutputsProvider);
        }
        else
        {
            deployment = new TestArmDeploymentResource(deploymentName, _deploymentOutputs!);
        }
        var operation = new TestArmOperation<ArmDeploymentResource>(deployment);
        return Task.FromResult<ArmOperation<ArmDeploymentResource>>(operation);
    }
}

/// <summary>
/// Test implementation of <see cref="ITenantResource"/>.
/// </summary>
internal sealed class TestTenantResource : ITenantResource
{
    public Guid? TenantId { get; } = Guid.Parse("87654321-4321-4321-4321-210987654321");
    public string? DisplayName { get; } = "Test Tenant";
    public string? DefaultDomain { get; } = "testdomain.onmicrosoft.com";
}

/// <summary>
/// Test implementation of ArmOperation for testing.
/// </summary>
internal sealed class TestArmOperation<T>(T value) : ArmOperation<T>
{
    public override string Id { get; } = Guid.NewGuid().ToString();
    public override T Value { get; } = value;
    public override bool HasCompleted { get; } = true;
    public override bool HasValue { get; } = true;

    public override Response GetRawResponse() => new MockResponse(200);
    public override Response UpdateStatus(CancellationToken cancellationToken = default) => new MockResponse(200);
    public override ValueTask<Response> UpdateStatusAsync(CancellationToken cancellationToken = default) => new ValueTask<Response>(new MockResponse(200));
    public override ValueTask<Response<T>> WaitForCompletionAsync(CancellationToken cancellationToken = default) => new ValueTask<Response<T>>(Response.FromValue(Value, new MockResponse(200)));
    public override ValueTask<Response<T>> WaitForCompletionAsync(TimeSpan pollingInterval, CancellationToken cancellationToken = default) => new ValueTask<Response<T>>(Response.FromValue(Value, new MockResponse(200)));
    public override Response<T> WaitForCompletion(CancellationToken cancellationToken = default) => Response.FromValue(Value, new MockResponse(200));
    public override Response<T> WaitForCompletion(TimeSpan pollingInterval, CancellationToken cancellationToken = default) => Response.FromValue(Value, new MockResponse(200));
}

/// <summary>
/// Test implementation of ArmDeploymentResource for testing.
/// </summary>
internal sealed class TestArmDeploymentResource : ArmDeploymentResource
{
    private readonly string _name;
    private readonly Dictionary<string, object>? _deploymentData;
    private readonly Func<string, Dictionary<string, object>>? _deploymentDataProvider;

    public TestArmDeploymentResource(string name, Dictionary<string, object> deploymentData)
    {
        _name = name;
        _deploymentData = deploymentData;
    }

    public TestArmDeploymentResource(string name, Func<string, Dictionary<string, object>> deploymentDataProvider)
    {
        _name = name;
        _deploymentDataProvider = deploymentDataProvider;
    }

    public override ResourceIdentifier Id => new ResourceIdentifier($"/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/{_name}");

    public override ArmDeploymentData Data
    {
        get
        {
            Dictionary<string, object> data;
            if (_deploymentDataProvider is not null)
            {
                data = _deploymentDataProvider(_name);
            }
            else
            {
                data = _deploymentData!;
            }
            return ArmResourcesModelFactory.ArmDeploymentData(Id, _name, properties: ArmResourcesModelFactory.ArmDeploymentPropertiesExtended(provisioningState: ResourcesProvisioningState.Succeeded, outputs: BinaryData.FromObjectAsJson(data)));
        }
    }

    public override bool HasData => true;
}

/// <summary>
/// Mock Response implementation for testing.
/// </summary>
internal sealed class MockResponse(int status) : Response
{
    public override int Status { get; } = status;
    public override string ReasonPhrase { get; } = "OK";
    public override Stream? ContentStream { get; set; }
    public override string ClientRequestId { get; set; } = Guid.NewGuid().ToString();

    protected override bool ContainsHeader(string name) => false;
    protected override IEnumerable<HttpHeader> EnumerateHeaders() => Enumerable.Empty<HttpHeader>();
    protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
    {
        value = default;
        return false;
    }
    protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
    {
        values = default;
        return false;
    }
    public override void Dispose() { }
}

internal sealed class TestArmClientProvider : IArmClientProvider
{
    private readonly Dictionary<string, object>? _deploymentOutputs;
    private readonly Func<string, Dictionary<string, object>>? _deploymentOutputsProvider;
    private readonly TestResourceGroupResource? _resourceGroup;

    public TestArmClientProvider(Dictionary<string, object> deploymentOutputs)
    {
        _deploymentOutputs = deploymentOutputs;
    }

    public TestArmClientProvider(Func<string, Dictionary<string, object>> deploymentOutputsProvider)
    {
        _deploymentOutputsProvider = deploymentOutputsProvider;
    }

    public TestArmClientProvider(TestResourceGroupResource resourceGroup)
    {
        _resourceGroup = resourceGroup;
        _deploymentOutputs = [];
    }

    public TestArmClientProvider() : this([])
    {
    }

    public IArmClient GetArmClient(TokenCredential credential, string subscriptionId)
    {
        if (_deploymentOutputsProvider is not null)
        {
            return new TestArmClient(_deploymentOutputsProvider);
        }
        return new TestArmClient(_deploymentOutputs!, _resourceGroup);
    }

    public IArmClient GetArmClient(TokenCredential credential)
    {
        return new TestArmClient();
    }
}

internal sealed class TestSecretClientProvider(ITokenCredentialProvider tokenCredentialProvider) : ISecretClientProvider
{
    public SecretClient GetSecretClient(Uri vaultUri)
    {
        var credential = tokenCredentialProvider.TokenCredential;
        return new SecretClient(vaultUri, credential);
    }
}

/// <summary>
/// Test implementation of <see cref="IHostEnvironment"/>.
/// </summary>
internal sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "TestApp";
    public string ContentRootPath { get; set; } = "/test";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class TestBicepCompiler : IBicepCompiler
{
    public Task<string> CompileBicepToArmAsync(string bicepFilePath, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(@"{
  ""$schema"": ""https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#"",
  ""contentVersion"": ""1.0.0.0"",
  ""parameters"": {},
  ""resources"": []
}");
    }
}

internal sealed class TestUserSecretsManager : IDeploymentStateManager
{
    private readonly JsonObject _state = [];

    public string? StateFilePath => null;

    public Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default)
    {
        var sectionData = _state.TryGetPropertyValue(sectionName, out var node) && node is JsonObject obj
            ? obj
            : new JsonObject();
        return Task.FromResult(new DeploymentStateSection(sectionName, sectionData, 0));
    }

    public Task DeleteSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
    {
        _state[section.SectionName] = section.Data;
        return Task.CompletedTask;
    }

    public Task ClearAllStateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class TestUserPrincipalProvider : IUserPrincipalProvider
{
    public Task<UserPrincipal> GetUserPrincipalAsync(CancellationToken cancellationToken = default)
    {
        var principal = new UserPrincipal(Guid.Parse("11111111-2222-3333-4444-555555555555"), "test@example.com");
        return Task.FromResult(principal);
    }
}

internal sealed class TestTokenCredentialProvider : ITokenCredentialProvider
{
    public TokenCredential TokenCredential => new TestTokenCredential();
}

/// <summary>
/// Mock implementation of IProcessRunner for testing that captures executed commands.
/// </summary>
internal sealed class MockProcessRunner : IProcessRunner
{
    /// <summary>
    /// Gets the list of commands that were executed.
    /// </summary>
    public List<ExecutedCommand> ExecutedCommands { get; } = [];

    /// <summary>
    /// Gets or sets the configured results for specific commands.
    /// Key format: "{executablePath} {arguments}"
    /// </summary>
    public Dictionary<string, ProcessResult> CommandResults { get; set; } = [];

    /// <summary>
    /// Gets or sets the default process result to return when no specific result is configured.
    /// </summary>
    public ProcessResult DefaultResult { get; set; } = new(0);

    /// <summary>
    /// Represents a command that was executed.
    /// </summary>
    public sealed record ExecutedCommand(string ExecutablePath, string? Arguments, string? WorkingDirectory);

    public (Task<ProcessResult>, IAsyncDisposable) Run(ProcessSpec processSpec)
    {
        // Capture the executed command
        var executedCommand = new ExecutedCommand(processSpec.ExecutablePath, processSpec.Arguments, processSpec.WorkingDirectory);
        ExecutedCommands.Add(executedCommand);

        // Determine the result to return
        var commandKey = $"{processSpec.ExecutablePath} {processSpec.Arguments ?? ""}".Trim();
        var result = CommandResults.TryGetValue(commandKey, out var configuredResult) ? configuredResult : DefaultResult;

        // Create a task that completes immediately with the configured result
        var resultTask = Task.FromResult(result);

        // Create a no-op disposable
        var disposable = new NoOpAsyncDisposable();

        return (resultTask, disposable);
    }

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
