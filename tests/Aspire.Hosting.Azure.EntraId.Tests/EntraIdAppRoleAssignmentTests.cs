// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIREAZURE003
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Aspire.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.EntraId.Tests;

public class EntraIdAppRoleAssignmentTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ExplicitAssignments_UsePrincipalIdsAndStableRoleIds()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var first = builder.AddAzureUserAssignedIdentity("worker-identity");
        var second = builder.AddAzureUserAssignedIdentity("other-identity")
            .AsExisting(builder.AddParameter("identity-name"), builder.AddParameter("identity-group"));
        var api = builder.AddEntraIdApplication("entra-api")
            .WithApplicationIdUri()
            .WithAppRole(new EntraIdAppRole("Orders.Read", "Read", "Read orders.", ["Application"]))
            .WithAppRole(new EntraIdAppRole("Orders.Write", "Write", "Write orders.", ["User", "Application"],
                new Guid("11112222-aaaa-3333-bbbb-4444cccc5555")))
            .PublishWithAppRoleAssignment(first, "Orders.Read", "Orders.Write", "Orders.Read")
            .PublishWithAppRoleAssignment(first, "Orders.Read")
            .PublishWithAppRoleAssignment(second, "Orders.Read");

        var bicep = api.Resource.GetBicepTemplateString();
        Assert.Equal(bicep, api.Resource.GetBicepTemplateString());
        Assert.Equal(3, api.Resource.GetManagedIdentityRoleAssignments(requireIdentity: true).Count);
        Assert.Empty(api.Resource.ClientCredentials);
        Assert.Empty(api.Resource.Consumers);
        Assert.Equal(
            new[] { "{other-identity.outputs.principalId}", "{worker-identity.outputs.principalId}" },
            api.Resource.Parameters.Values.OfType<BicepOutputReference>().Select(p => p.ValueExpression).Order());
        await Verify(bicep, "bicep");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown")]
    [InlineData("orders.read")]
    [InlineData("UserOnly")]
    [InlineData("Disabled")]
    public void InvalidRoles_AreRejectedWithoutMutation(string role)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var identity = builder.AddAzureUserAssignedIdentity("identity");
        var worker = builder.AddContainer("worker", "image");
        var api = builder.AddEntraIdApplication("entra-api")
            .WithAppRole(new EntraIdAppRole("Orders.Read", "Read", "Read.", ["Application"]))
            .WithAppRole(new EntraIdAppRole("UserOnly", "User", "User."))
            .WithAppRole(new EntraIdAppRole("Disabled", "Disabled", "Disabled.", ["Application"], isEnabled: false));

        Assert.Throws<ArgumentException>(() => api.PublishWithAppRoleAssignment(identity, "Orders.Read", role));
        Assert.Throws<ArgumentException>(() => api.PublishWithAppRoleAssignment(worker, "Orders.Read", role));
        Assert.Empty(api.Resource.AppRoleAssignments);
        Assert.Empty(worker.Resource.Annotations.OfType<ManagedIdentityRequiredAnnotation>());
    }

    [Fact]
    public void NullAndEmptyArguments_AreRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var identity = builder.AddAzureUserAssignedIdentity("identity");
        var worker = builder.AddContainer("worker", "image");
        var api = builder.AddEntraIdApplication("entra-api");

        Assert.Throws<ArgumentNullException>(() => EntraIdResourceExtensions.PublishWithAppRoleAssignment(null!, identity, "Read"));
        Assert.Throws<ArgumentNullException>(() => EntraIdResourceExtensions.PublishWithAppRoleAssignment(null!, worker, "Read"));
        Assert.Throws<ArgumentNullException>(() => api.PublishWithAppRoleAssignment((IResourceBuilder<AzureUserAssignedIdentityResource>)null!, "Read"));
        Assert.Throws<ArgumentNullException>(() => api.PublishWithAppRoleAssignment((IResourceBuilder<ContainerResource>)null!, "Read"));
        Assert.Throws<ArgumentNullException>(() => api.PublishWithAppRoleAssignment(identity, null!));
        Assert.Throws<ArgumentNullException>(() => api.PublishWithAppRoleAssignment(worker, null!));
        Assert.Throws<ArgumentException>(() => api.PublishWithAppRoleAssignment(identity));
        Assert.Throws<ArgumentException>(() => api.PublishWithAppRoleAssignment(worker));
    }

    [Fact]
    public void ExistingApplications_AreRejectedInEitherCallOrder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        const string TenantId = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
        const string ClientId = "00001111-aaaa-2222-bbbb-3333cccc4444";
        var identity = builder.AddAzureUserAssignedIdentity("identity");
        var worker = builder.AddContainer("worker", "image");
        var existing = builder.AddExistingEntraApplication("existing", TenantId, ClientId);
        Assert.Throws<InvalidOperationException>(() => existing.PublishWithAppRoleAssignment(identity, "Read"));
        Assert.Throws<InvalidOperationException>(() => existing.PublishWithAppRoleAssignment(worker, "Read"));
        var api = builder.AddEntraIdApplication("entra-api")
            .WithAppRole(new EntraIdAppRole("Read", "Read", "Read.", ["Application"]))
            .PublishWithAppRoleAssignment(identity, "Read");
        Assert.Throws<InvalidOperationException>(() => api.AsExistingApplication(TenantId, ClientId));
        Assert.False(api.Resource.IsExistingApplication);
    }

    [Fact]
    public void RoleValues_AreCopiedAndMemberTypesAreRevalidated()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var identity = builder.AddAzureUserAssignedIdentity("identity");
        var role = new EntraIdAppRole("Read", "Read", "Read.", ["Application"]);
        var api = builder.AddEntraIdApplication("entra-api").WithAppRole(role);
        string[] values = ["Read"];
        api.PublishWithAppRoleAssignment(identity, values);
        values[0] = "unknown";
        Assert.Same(role, Assert.Single(api.Resource.GetManagedIdentityRoleAssignments(requireIdentity: true)).Role);

        // The public read-only view is backed by an array; generation must not trust earlier validation.
        Assert.IsType<string[]>(role.AllowedMemberTypes)[0] = "User";
        Assert.Throws<ArgumentException>(api.Resource.GetBicepTemplateString);
        Assert.Throws<ArgumentException>(api.Resource.ValidateManagedIdentities);
    }

    [Fact]
    public async Task LocalRun_DoesNotRequestAnIdentityOrPublishAssignments()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var identity = builder.AddAzureUserAssignedIdentity("identity");
        var worker = builder.AddContainer("worker", "image");
        var api = builder.AddEntraIdApplication("entra-api")
            .WithAppRole(new EntraIdAppRole("Read", "Read", "Read.", ["Application"]))
            .PublishWithAppRoleAssignment(identity, "Read")
            .PublishWithAppRoleAssignment(worker, "Read");

        Assert.Empty(api.Resource.AppRoleAssignments);
        Assert.Empty(worker.Resource.Annotations.OfType<ManagedIdentityRequiredAnnotation>());
        await Verify(api.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void SharedComputeIdentity_IsAssignedOncePerRole()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var identity = builder.AddAzureUserAssignedIdentity("identity");
        var worker = builder.AddContainer("worker", "image").WithAzureUserAssignedIdentity(identity);
        var other = builder.AddContainer("other", "image").WithAzureUserAssignedIdentity(identity);
        var api = builder.AddEntraIdApplication("entra-api")
            .WithAppRole(new EntraIdAppRole("Read", "Read", "Read.", ["Application"]))
            .PublishWithAppRoleAssignment(worker, "Read")
            .PublishWithAppRoleAssignment(other, "Read")
            .PublishWithAppRoleAssignment(identity, "Read");

        var assignment = Assert.Single(api.Resource.GetManagedIdentityRoleAssignments(requireIdentity: true));
        Assert.Same(identity.Resource, assignment.Identity);
        Assert.Single(worker.Resource.Annotations.OfType<ManagedIdentityRequiredAnnotation>());
        Assert.Empty(api.Resource.Consumers);
    }

    [Fact]
    public void ExcludedCompute_DoesNotRequireOrAssignAnIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var worker = builder.AddContainer("worker", "image").ExcludeFromManifest();
        var api = builder.AddEntraIdApplication("entra-api")
            .WithAppRole(new EntraIdAppRole("Read", "Read", "Read.", ["Application"]))
            .PublishWithAppRoleAssignment(worker, "Read");

        api.Resource.ValidateManagedIdentities();
        Assert.Empty(api.Resource.GetManagedIdentityRoleAssignments(requireIdentity: true));
    }

    [Fact]
    public void Regeneration_DropsStaleIdentityParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var first = builder.AddAzureUserAssignedIdentity("first-identity");
        var second = builder.AddAzureUserAssignedIdentity("second-identity");
        var worker = builder.AddContainer("worker", "image").WithAzureUserAssignedIdentity(first);
        var api = builder.AddEntraIdApplication("entra-api")
            .WithAppRole(new EntraIdAppRole("Read", "Read", "Read.", ["Application"]));
        var baseline = api.Resource.GetBicepTemplateString();
        api.PublishWithAppRoleAssignment(worker, "Read");
        _ = api.Resource.GetBicepTemplateString();
        worker.WithAzureUserAssignedIdentity(second);
        _ = api.Resource.GetBicepTemplateString();
        Assert.Equal("{second-identity.outputs.principalId}",
            Assert.Single(api.Resource.Parameters.Values.OfType<BicepOutputReference>()).ValueExpression);

        worker.ExcludeFromManifest();
        Assert.Equal(baseline, api.Resource.GetBicepTemplateString());
        Assert.Empty(api.Resource.Parameters.Values.OfType<BicepOutputReference>());
    }

    [Fact]
    public async Task BeforeStart_UnsupportedComputeEnvironmentFailsClearly()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var worker = builder.AddContainer("worker", "image");
        builder.AddEntraIdApplication("entra-api")
            .WithAppRole(new EntraIdAppRole("Read", "Read", "Read.", ["Application"]))
            .PublishWithAppRoleAssignment(worker, "Read");

        using var app = builder.Build();
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteBeforeStartHooksAsync(app, default));
        Assert.Contains("Resource 'worker'", exception.ToString());
        Assert.Contains("no user-assigned managed identity", exception.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_ResolvesIdentityDependenciesAfterPreparation(bool explicitIdentity)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper, workspace.Path);
        builder.AddAzureContainerAppEnvironment("env");
        var worker = builder.AddContainer("worker", "image");
        var api = builder.AddEntraIdApplication("entra-api")
            .WithApplicationIdUri()
            .WithAppRole(new EntraIdAppRole("Read", "Read", "Read.", ["Application"]));
        if (explicitIdentity)
        {
            var identity = builder.AddAzureUserAssignedIdentity("caller-identity");
            worker.WithAzureUserAssignedIdentity(identity);
            api.PublishWithAppRoleAssignment(identity, "Read");
        }
        else
        {
            api.PublishWithAppRoleAssignment(worker, "Read");
        }

        // Pipeline discovery sees the template before compute identity creation. The final template must be regenerated.
        _ = api.Resource.GetBicepTemplateString();
        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var identityResource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());
        Assert.Same(identityResource, Assert.Single(api.Resource.GetManagedIdentityRoleAssignments(requireIdentity: true)).Identity);
        var firstTemplate = api.Resource.GetBicepTemplateString();
        Assert.Equal(firstTemplate, api.Resource.GetBicepTemplateString());
        Assert.Equal($"{{{identityResource.Name}.outputs.principalId}}",
            Assert.Single(api.Resource.Parameters.Values.OfType<BicepOutputReference>()).ValueExpression);
        Assert.Empty(api.Resource.ClientCredentials);
        Assert.Empty(api.Resource.Consumers);

        await using var step = await new TestPipelineActivityReporter(outputHelper).CreateStepAsync("Publishing UAMI role assignment");
        var context = new AzurePublishingContext(
            workspace.Path, app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>().Value,
            app.Services, NullLogger<AzurePublishingContext>.Instance, step);
        await context.WriteModelAsync(model, Assert.Single(model.Resources.OfType<AzureEnvironmentResource>()));

        await Verify(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "main.bicep")), "bicep")
            .UseParameters(explicitIdentity)
            .AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "entra-api", "entra-api.bicep")), "bicep")
            .AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "worker", "worker.bicep")), "bicep");
    }

    [Fact]
    public async Task Symbols_KeepCollidingIdentityAndRoleNamesDistinct()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var identity = builder.AddAzureUserAssignedIdentity("worker");
        var other = builder.AddAzureUserAssignedIdentity("workerRead");
        var api = builder.AddEntraIdApplication("entra-api")
            .WithAppRole(new EntraIdAppRole("Read.All", "Read", "Read.", ["Application"]))
            .WithAppRole(new EntraIdAppRole("Read-All", "Read", "Read.", ["Application"]))
            .WithAppRole(new EntraIdAppRole("All", "All", "All.", ["Application"]))
            .WithRedirectUris(builder.AddParameter("workerPrincipalId"))
            .PublishWithAppRoleAssignment(identity, "Read.All", "Read-All")
            .PublishWithAppRoleAssignment(other, "All");

        var first = api.Resource.GetBicepTemplateString();
        Assert.Equal(first, api.Resource.GetBicepTemplateString());
        await Verify(first, "bicep");
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);
}
