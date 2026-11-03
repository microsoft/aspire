// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Verifies role assignments and resource dependencies.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Azure.Provisioning.KeyVault;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Abstractions;

namespace Aspire.Hosting.Azure.EntraId.Tests;

public class EntraIdKeyVaultCertificateTests
{
    private const string TenantId = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
    private const string ClientId = "00001111-aaaa-2222-bbbb-3333cccc4444";

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, false)]
    [InlineData(DistributedApplicationOperation.Run, true)]
    [InlineData(DistributedApplicationOperation.Publish, false)]
    [InlineData(DistributedApplicationOperation.Publish, true)]
    public async Task ResourceCredential_PreservesVaultReference(
        DistributedApplicationOperation operation, bool configureCredentialAfterReference)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var vault = builder.AddAzureKeyVault("vault");
        vault.Resource.Outputs["vaultUri"] = "https://contoso.vault.usgovcloudapi.net/";
        var entra = builder.AddExistingEntraApplication("entra", TenantId, ClientId);
        var web = builder.AddResource(new ProjectResource("web"));
        if (configureCredentialAfterReference)
        {
            web.WithReference(entra);
            entra.WithCertificateFromKeyVault(vault, "web-certificate");
        }
        else
        {
            entra.WithCertificateFromKeyVault(vault, "web-certificate");
            web.WithReference(entra);
        }

        var credential = Assert.IsType<EntraIdKeyVaultCertificateCredential>(Assert.Single(entra.Resource.ClientCredentials));
        Assert.Same(vault.Resource, credential.KeyVault);
        Assert.Null(credential.KeyVaultUrl);
        var dependencies = await web.Resource.GetResourceDependenciesAsync(
            builder.ExecutionContext, ResourceDependencyDiscoveryMode.DirectOnly);
        Assert.Contains(vault.Resource, dependencies);
        Assert.Single(dependencies);

        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            web.Resource, operation, TestServiceProvider.Instance);
        var expectedVaultUrl = operation == DistributedApplicationOperation.Run
            ? "https://contoso.vault.usgovcloudapi.net/"
            : "{vault.outputs.vaultUri}";
        Assert.Equal(new Dictionary<string, string>
        {
            ["AzureAd__Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd__TenantId"] = TenantId,
            ["AzureAd__ClientId"] = ClientId,
            ["AzureAd__ClientCredentials__0__SourceType"] = "KeyVault",
            ["AzureAd__ClientCredentials__0__KeyVaultUrl"] = expectedVaultUrl,
            ["AzureAd__ClientCredentials__0__KeyVaultCertificateName"] = "web-certificate"
        }, environment);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(environment.Select(pair =>
            new KeyValuePair<string, string?>(pair.Key.Replace("__", ":", StringComparison.Ordinal), pair.Value))).Build();
        var options = configuration.GetSection("AzureAd")
            .Get<MicrosoftEntraApplicationOptions>(binder => binder.ErrorOnUnknownConfiguration = true);
        Assert.NotNull(options);
        var boundCredential = Assert.Single(options.ClientCredentials!);
        Assert.Equal(CredentialSource.KeyVault, boundCredential.SourceType);
        Assert.Equal(operation == DistributedApplicationOperation.Run
            ? "https://contoso.vault.usgovcloudapi.net/"
            : "{vault.outputs.vaultUri}", boundCredential.KeyVaultUrl);
        Assert.Equal("web-certificate", boundCredential.KeyVaultCertificateName);
    }

    [Fact]
    public async Task ResourceCredential_UsesEmulatorEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var vault = builder.AddAzureKeyVault("vault").WithAnnotation(new ContainerImageAnnotation { Image = "emulator" });
        vault.WithEndpoint("https", endpoint =>
        {
            endpoint.UriScheme = "https";
            endpoint.AllocatedEndpoint = new(endpoint, "localhost", 8443);
        });
        var entra = builder.AddExistingEntraApplication("entra", TenantId, ClientId)
            .WithCertificateFromKeyVault(vault, "web-certificate");
        var web = builder.AddResource(new ProjectResource("web")).WithReference(entra);

        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            web.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("https://localhost:8443", environment["AzureAd__ClientCredentials__0__KeyVaultUrl"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Publish_ConsumersReceiveCertificateAccess(bool configureCredentialAfterReference)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vault = builder.AddAzureKeyVault("vault");
        var entra = builder.AddExistingEntraApplication("entra", TenantId, ClientId);
        var web = builder.AddResource(new ProjectResource("web"));
        if (configureCredentialAfterReference)
        {
            web.WithReference(entra);
            entra.WithCertificateFromKeyVault(vault, "web-certificate");
        }
        else
        {
            entra.WithCertificateFromKeyVault(vault, "web-certificate");
            web.WithReference(entra);
        }

        var annotation = Assert.Single(web.Resource.Annotations.OfType<RoleAssignmentAnnotation>());
        Assert.Same(vault.Resource, annotation.Target);
        var role = Assert.Single(annotation.Roles);
        Assert.Equal(KeyVaultBuiltInRole.KeyVaultCertificateUser.ToString(), role.Id);
    }

    [Fact]
    public void Publish_WithCredentialAlsoDeclaresCertificateAccess()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vault = builder.AddAzureKeyVault("vault");
        var entra = builder.AddExistingEntraApplication("entra", TenantId, ClientId)
            .WithCredential(new EntraIdKeyVaultCertificateCredential
            {
                KeyVault = vault.Resource,
                CertificateNameInKeyVault = "web-certificate"
            });
        var web = builder.AddResource(new ProjectResource("web")).WithReference(entra);

        Assert.Same(vault.Resource,
            Assert.Single(web.Resource.Annotations.OfType<RoleAssignmentAnnotation>()).Target);
    }

    [Fact]
    public void Publish_ExplicitRoleAssignmentsOnConsumerAreRespected()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vault = builder.AddAzureKeyVault("vault");
        var entra = builder.AddExistingEntraApplication("entra", TenantId, ClientId)
            .WithCertificateFromKeyVault(vault, "certificate");
        var web = builder.AddResource(new ProjectResource("web"))
            .WithRoleAssignments(vault, [])
            .WithReference(entra);

        var annotation = Assert.Single(web.Resource.Annotations.OfType<RoleAssignmentAnnotation>());
        Assert.Same(vault.Resource, annotation.Target);
        Assert.Empty(annotation.Roles);
    }

    [Fact]
    public void Publish_MultipleConsumersAndCertificatesDeduplicateAccess()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vault = builder.AddAzureKeyVault("vault");
        var entra = builder.AddExistingEntraApplication("entra", TenantId, ClientId);
        var first = builder.AddResource(new ProjectResource("first")).WithReference(entra);
        var second = builder.AddResource(new ProjectResource("second")).WithReference(entra);
        entra.WithCertificateFromKeyVault(vault, "first-certificate")
            .WithCertificateFromKeyVault(vault, "second-certificate");

        foreach (var consumer in new[] { first.Resource, second.Resource })
        {
            var annotation = Assert.Single(consumer.Annotations.OfType<RoleAssignmentAnnotation>());
            Assert.Same(vault.Resource, annotation.Target);
            Assert.Equal(KeyVaultBuiltInRole.KeyVaultCertificateUser.ToString(), Assert.Single(annotation.Roles).Id);
        }
    }

    [Fact]
    public void Run_CertificateAccessExtendsTheVaultDefaultRolesOnce()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var vault = builder.AddAzureKeyVault("vault");
        var entra = builder.AddExistingEntraApplication("entra", TenantId, ClientId)
            .WithCertificateFromKeyVault(vault, "first-certificate")
            .WithCertificateFromKeyVault(vault, "second-certificate");
        var web = builder.AddResource(new ProjectResource("web")).WithReference(entra);

        var defaults = Assert.Single(vault.Resource.Annotations.OfType<DefaultRoleAssignmentsAnnotation>());
        Assert.Equal(
            [KeyVaultBuiltInRole.KeyVaultSecretsUser.ToString(), KeyVaultBuiltInRole.KeyVaultCertificateUser.ToString()],
            defaults.Roles.Select(r => r.Id).Order());
        Assert.Empty(web.Resource.Annotations.OfType<RoleAssignmentAnnotation>());
    }

    [Fact]
    public void Run_ClearedDefaultRoleAssignmentsAreNotRestored()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var vault = builder.AddAzureKeyVault("vault").ClearDefaultRoleAssignments();
        builder.AddExistingEntraApplication("entra", TenantId, ClientId)
            .WithCertificateFromKeyVault(vault, "certificate");

        Assert.Empty(vault.Resource.Annotations.OfType<DefaultRoleAssignmentsAnnotation>());
    }

    [Fact]
    public async Task Publish_EnvironmentWithoutRoleAssignmentSupportFails()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vault = builder.AddAzureKeyVault("vault");
        var entra = builder.AddExistingEntraApplication("entra", TenantId, ClientId)
            .WithCertificateFromKeyVault(vault, "web-certificate");
        builder.AddContainer("web", "image").WithReference(entra);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAzureResourcesAsync(builder));
        Assert.Contains("does not support role assignments", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_PlainExecutableConsumerGetsCertificateUserRole()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var vault = builder.AddAzureKeyVault("vault");
        var entra = builder.AddExistingEntraApplication("entra", TenantId, ClientId)
            .WithCertificateFromKeyVault(vault, "web-certificate");
        builder.AddExecutable("web", "node", ".").WithReference(entra);

        var roleAssignments = await PrepareAzureResourcesAsync(builder);

        Assert.Contains(roleAssignments, t => t.Contains(KeyVaultBuiltInRole.KeyVaultCertificateUser.ToString(), StringComparison.Ordinal));
    }

    private static async Task<string[]> PrepareAzureResourcesAsync(IDistributedApplicationBuilder builder)
    {
        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        return app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<AzureRoleAssignmentResource>()
            .Select(r => r.GetBicepTemplateString())
            .ToArray();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);

    [Fact]
    public async Task ResourceCredential_RejectsInvalidInputs()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var vault = builder.AddAzureKeyVault("vault");
        var entra = builder.AddExistingEntraApplication("entra", TenantId, ClientId);

        Assert.Throws<ArgumentNullException>(() =>
            EntraIdResourceExtensions.WithCertificateFromKeyVault(null!, vault, "certificate"));
        Assert.Throws<ArgumentNullException>(() =>
            entra.WithCertificateFromKeyVault((IResourceBuilder<AzureKeyVaultResource>)null!, "certificate"));
        Assert.Throws<ArgumentException>(() => entra.WithCertificateFromKeyVault(vault, ""));
        Assert.Throws<ArgumentNullException>(() => entra.WithCertificateFromKeyVault(vault, null!));
        Assert.Empty(entra.Resource.ClientCredentials);

        entra.WithCredential(new EntraIdKeyVaultCertificateCredential
        {
            KeyVault = vault.Resource,
            KeyVaultUrl = "https://vault.vault.azure.net",
            CertificateNameInKeyVault = "certificate"
        });
        var web = builder.AddResource(new ProjectResource("web")).WithReference(entra);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            web.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance));
        Assert.Throws<InvalidOperationException>(() => new EntraIdKeyVaultCertificateCredential
        {
            CertificateNameInKeyVault = "certificate"
        }.SourceType);
    }
}
