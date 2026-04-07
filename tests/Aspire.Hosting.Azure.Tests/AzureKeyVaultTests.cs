// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Azure.Provisioning;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.Tests;

public class AzureKeyVaultTests
{
    [Fact]
    public async Task AddKeyVaultViaRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var mykv = builder.AddAzureKeyVault("mykv");

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(mykv.Resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");

    }

    [Fact]
    public async Task AddKeyVaultViaPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var mykv = builder.AddAzureKeyVault("mykv");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(model, mykv.Resource);
        var kvRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "mykv-roles");
        var (kvRolesManifest, kvRolesBicep) = await AzureManifestUtils.GetManifestWithBicep(kvRoles, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep")
              .AppendContentAsFile(kvRolesBicep, "bicep")
              .AppendContentAsFile(kvRolesManifest.ToString(), "json");

    }

    [Fact]
    public async Task WithEnvironment_AddsKeyVaultSecretReference()
    {
        // Arrange: Create a test application builder.
        using var builder = TestDistributedApplicationBuilder.Create();

        // Add a key vault resource.
        var kv = builder.AddAzureKeyVault("myKeyVault");

        kv.Resource.SecretResolver = (s, ct) =>
        {
            return Task.FromResult<string?>("my secret value");
        };

        // Get a secret reference from the key vault resource.
        var secretReference = kv.Resource.GetSecret("mySecret");

        // Add a container resource that supports environment variables.
        var containerBuilder = builder.AddContainer("myContainer", "nginx")
                                       .WithEnvironment("MY_SECRET", secretReference);

        var serviceProvider = builder.Services.BuildServiceProvider();

        var runEnv = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            containerBuilder.Resource,
            DistributedApplicationOperation.Run,
            serviceProvider);

        var publishEnv = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            containerBuilder.Resource,
            DistributedApplicationOperation.Publish,
            serviceProvider);

        var runKvp = Assert.Single(runEnv);
        var pubishKvp = Assert.Single(publishEnv);

        Assert.Equal("MY_SECRET", runKvp.Key);
        Assert.Same("my secret value", runKvp.Value);

        Assert.Equal("MY_SECRET", pubishKvp.Key);
        Assert.Equal("{myKeyVault.secrets.mySecret}", pubishKvp.Value);
    }

    [Fact]
    public async Task ConsumingAKeyVaultSecretInAnotherBicepModule()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var kv = builder.AddAzureKeyVault("myKeyVault");

        var secretReference = kv.Resource.GetSecret("mySecret");
        var secretReference2 = kv.Resource.GetSecret("mySecret2");

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            var secret = secretReference.AsKeyVaultSecret(infra);
            var secret2 = secretReference2.AsKeyVaultSecret(infra);

            // Should be idempotent
            _ = secretReference.AsKeyVaultSecret(infra);

            infra.Add(new ProvisioningOutput("secretUri1", typeof(string))
            {
                Value = secret.Properties.SecretUri
            });

            infra.Add(new ProvisioningOutput("secretUri2", typeof(string))
            {
                Value = secret2.Properties.SecretUri
            });
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");

    }

    [Fact]
    public async Task ConsumingSecretsFromExistingKeyVaultInAnotherBicepModule_WithParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingName = builder.AddParameter("existingKvName");
        var existingRg = builder.AddParameter("existingRgName");
        var kv = builder.AddAzureKeyVault("kv").PublishAsExisting(existingName, existingRg);

        var secretReference = kv.Resource.GetSecret("mySecret");
        var secretReference2 = kv.Resource.GetSecret("mySecret2");

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            var secret = secretReference.AsKeyVaultSecret(infra);
            var secret2 = secretReference2.AsKeyVaultSecret(infra);
            _ = secretReference.AsKeyVaultSecret(infra); // idempotent

            infra.Add(new ProvisioningOutput("secretUri1", typeof(string)) { Value = secret.Properties.SecretUri });
            infra.Add(new ProvisioningOutput("secretUri2", typeof(string)) { Value = secret2.Properties.SecretUri });
        });

        var module2 = builder.AddAzureInfrastructure("mymodule2", infra =>
        {
            var secret = secretReference.AsKeyVaultSecret(infra);
            var secret2 = secretReference2.AsKeyVaultSecret(infra);

            infra.Add(new ProvisioningOutput("secretUri1", typeof(string)) { Value = secret.Properties.SecretUri });
            infra.Add(new ProvisioningOutput("secretUri2", typeof(string)) { Value = secret2.Properties.SecretUri });
        });

        module2.Resource.Scope = new(existingRg.Resource);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);
        var (manifest2, bicep2) = await AzureManifestUtils.GetManifestWithBicep(module2.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep")
              .AppendContentAsFile(manifest.ToString(), "json")
              .AppendContentAsFile(bicep2, "bicep");
    }

    [Fact]
    public async Task ConsumingSecretsFromExistingKeyVaultInAnotherBicepModule_WithLiterals()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var kv = builder.AddAzureKeyVault("kv").PublishAsExisting("literalKvName", "literalRgName");

        var secretReference = kv.Resource.GetSecret("mySecret");
        var secretReference2 = kv.Resource.GetSecret("mySecret2");

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            var secret = secretReference.AsKeyVaultSecret(infra);
            var secret2 = secretReference2.AsKeyVaultSecret(infra);
            _ = secretReference.AsKeyVaultSecret(infra); // idempotent

            infra.Add(new ProvisioningOutput("secretUri1", typeof(string)) { Value = secret.Properties.SecretUri });
            infra.Add(new ProvisioningOutput("secretUri2", typeof(string)) { Value = secret2.Properties.SecretUri });
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public void GetSecret_ReturnsSecretReference()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var kv = builder.AddAzureKeyVault("myKeyVault");

        var secret = kv.GetSecret("mySecret");

        Assert.NotNull(secret);
        Assert.Equal("mySecret", secret.SecretName);
        Assert.Same(kv.Resource, secret.Resource);
    }

    [Fact]
    public void AddSecret_ReturnsSecretResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var secretParam = builder.AddParameter("secretParam", secret: true);
        var kv = builder.AddAzureKeyVault("myKeyVault");

        var secretResource = kv.AddSecret("mySecret", secretParam);

        Assert.NotNull(secretResource);
        Assert.IsType<AzureKeyVaultSecretResource>(secretResource.Resource);
        Assert.Equal("mySecret", secretResource.Resource.Name);
        Assert.Equal("mySecret", secretResource.Resource.SecretName);
        Assert.Same(kv.Resource, secretResource.Resource.Parent);
        Assert.Single(kv.Resource.Secrets);
        Assert.Same(secretResource.Resource, kv.Resource.Secrets[0]);
    }

    [Fact]
    public async Task AddSecret_WithParameterResource_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var secret = builder.AddParameter("my-secret-param", secret: true);
        var kv = builder.AddAzureKeyVault("mykv");
        var secretResource = kv.AddSecret("my-secret", secret);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(kv.Resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddSecret_WithReferenceExpression_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var pwd = builder.AddParameter("password", secret: true);
        var connectionString = ReferenceExpression.Create($"Server=localhost;Database=mydb;pwd={pwd}");
        var kv = builder.AddAzureKeyVault("mykv");
        var secretResource = kv.AddSecret("connection-string", connectionString);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(kv.Resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public void KvSecretResources_AreExcludedFromManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var parameter = builder.AddParameter("my-secret-param", secret: true);
        var kv = builder.AddAzureKeyVault("mykv");
        var secretResource = kv.AddSecret("my-secret", parameter);

        Assert.True(secretResource.Resource.TryGetAnnotationsOfType<ManifestPublishingCallbackAnnotation>(out var manifestAnnotations));
        var annotation = Assert.Single(manifestAnnotations);
        Assert.Equal(ManifestPublishingCallbackAnnotation.Ignore, annotation);
    }

    [Fact]
    public async Task AddSecret_WithMultipleSecrets_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var secretParam = builder.AddParameter("secret-param", secret: true);
        var apiKey = builder.AddParameter("api-key", secret: true);
        var connectionString = ReferenceExpression.Create($"Server=localhost;Database=mydb;User=user");

        var kv = builder.AddAzureKeyVault("mykv");
        kv.AddSecret("my-secret", secretParam);
        kv.AddSecret("app-api-key", apiKey);
        kv.AddSecret("connection-string", connectionString);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(kv.Resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddSecret_WithEmptySecretName_ThrowsArgumentException(string invalidSecretName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var secretParam = builder.AddParameter("secretParam", secret: true);
        var kv = builder.AddAzureKeyVault("myKeyVault");

        var exception = Assert.Throws<ArgumentException>(() => kv.AddSecret(invalidSecretName, secretParam));
        Assert.Contains("Secret name", exception.Message);
    }

    [Theory]
    [InlineData("secret_with_underscores")]
    [InlineData("secret.with.dots")]
    [InlineData("secret with spaces")]
    [InlineData("secret/with/slashes")]
    [InlineData("secret@with@symbols")]
    public void AddSecret_WithInvalidSecretName_ThrowsArgumentException(string invalidName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var secretParam = builder.AddParameter("secretParam", secret: true);
        var kv = builder.AddAzureKeyVault("myKeyVault");

        var exception = Assert.Throws<ArgumentException>(() => kv.AddSecret(invalidName, secretParam));
        Assert.Contains("Secret name can only contain", exception.Message);
    }

    [Theory]
    [InlineData("valid-secret")]
    [InlineData("VALID-SECRET")]
    [InlineData("valid123")]
    [InlineData("a")]
    public void AddSecret_WithValidSecretName_DoesNotThrow(string validSecretName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var secretParam = builder.AddParameter("secretParam", secret: true);
        var kv = builder.AddAzureKeyVault("myKeyVault");

        var exception = Record.Exception(() => kv.AddSecret(validSecretName, secretParam));
        Assert.Null(exception);
    }

    [Fact]
    public void AddSecret_WithTooLongSecretName_ThrowsArgumentException()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var secretParam = builder.AddParameter("secretParam", secret: true);
        var kv = builder.AddAzureKeyVault("myKeyVault");

        // Create a 128-character secret name (too long)
        var tooLongSecretName = new string('a', 128);

        var exception = Assert.Throws<ArgumentException>(() => kv.AddSecret(tooLongSecretName, secretParam));
        Assert.Contains("cannot be longer than 127 characters", exception.Message);
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureKeyVaultResource()
    {
        // Arrange
        var keyVaultResource = new AzureKeyVaultResource("test-keyvault", _ => { });
        var infrastructure = new AzureResourceInfrastructure(keyVaultResource, "test-keyvault");

        // Act - Call AddAsExistingResource twice
        var firstResult = keyVaultResource.AddAsExistingResource(infrastructure);
        var secondResult = keyVaultResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureKeyVaultResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingName = builder.AddParameter("existing-kv-name");
        var existingResourceGroup = builder.AddParameter("existing-kv-rg");

        var keyVault = builder.AddAzureKeyVault("test-keyvault")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = keyVault.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public void EmulatorSupport_IsEmulatorFalseByDefault()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var keyVault = builder.AddAzureKeyVault("kv");

        Assert.False(keyVault.Resource.IsEmulator);
    }

    [Fact]
    public void EmulatorSupport_IsEmulatorTrueWhenContainerPresent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var keyVault = builder.AddAzureKeyVault("kv");

        // Simulate emulator by adding container annotation
        keyVault.Resource.Annotations.Add(new ContainerImageAnnotation
        {
            Image = "mcr.microsoft.com/azure-key-vault/emulator:latest"
        });

        Assert.True(keyVault.Resource.IsEmulator);
    }

    [Fact]
    public async Task EmulatorSupport_ConnectionStringUsesEmulatorEndpointWhenIsEmulator()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var keyVault = builder.AddAzureKeyVault("kv");

        // Add container annotation to simulate emulator
        keyVault.Resource.Annotations.Add(new ContainerImageAnnotation
        {
            Image = "mcr.microsoft.com/azure-key-vault/emulator:latest"
        });

        // Add https endpoint for emulator
        keyVault.WithEndpoint("https", endpoint => endpoint.AllocatedEndpoint = new(endpoint, "localhost", 8443));

        var connectionString = keyVault.Resource.ConnectionStringExpression;

        Assert.True(keyVault.Resource.IsEmulator);
        Assert.Contains("localhost:8443", await connectionString.GetValueAsync(default) ?? string.Empty);
    }

    [Fact]
    public void EmulatorSupport_ConnectionStringUsesBicepOutputWhenNotEmulator()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var keyVault = builder.AddAzureKeyVault("kv");

        var connectionString = keyVault.Resource.ConnectionStringExpression;

        Assert.False(keyVault.Resource.IsEmulator);
        Assert.Contains("vaultUri", connectionString.ValueExpression);
    }

    [Fact]
    public async Task ConnectionStringRedirectAnnotation_RedirectsConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var keyVault = builder.AddAzureKeyVault("kv");

        // Create a connection string resource to redirect to
        var redirectTarget = new ConnectionStringResource("redirect-target",
            ReferenceExpression.Create($"https://redirected-vault.vault.azure.net"));

        // Add ConnectionStringRedirectAnnotation to redirect to another resource
        keyVault.Resource.Annotations.Add(new ConnectionStringRedirectAnnotation(redirectTarget));

        var connectionString = keyVault.Resource.ConnectionStringExpression;
        var connectionStringValue = await keyVault.Resource.GetConnectionStringAsync(default);

        Assert.Equal(redirectTarget.ConnectionStringExpression.ValueExpression, connectionString.ValueExpression);
        Assert.Equal("https://redirected-vault.vault.azure.net", connectionStringValue);
    }

    [Fact]
    public async Task ConnectionStringRedirectAnnotation_TakesPrecedenceOverEmulator()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var keyVault = builder.AddAzureKeyVault("kv");

        // Create a connection string resource to redirect to
        var redirectTarget = new ConnectionStringResource("redirect-target",
            ReferenceExpression.Create($"https://redirected-vault.vault.azure.net"));

        // Simulate emulator by adding container annotation
        keyVault.Resource.Annotations.Add(new ContainerImageAnnotation
        {
            Image = "mcr.microsoft.com/azure-key-vault/emulator:latest"
        });

        // Add https endpoint for emulator
        keyVault.WithEndpoint("https", endpoint => endpoint.AllocatedEndpoint = new(endpoint, "localhost", 8443));

        // Add ConnectionStringRedirectAnnotation - this should take precedence
        keyVault.Resource.Annotations.Add(new ConnectionStringRedirectAnnotation(redirectTarget));

        var connectionStringValue = await keyVault.Resource.GetConnectionStringAsync(default);

        // The redirect annotation should take precedence over emulator
        Assert.Equal("https://redirected-vault.vault.azure.net", connectionStringValue);
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_ValidRoles_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var kv = builder.AddAzureKeyVault("myKeyVault");
        var container = builder.AddContainer("myContainer", "nginx");

        var exception = Record.Exception(() =>
            container.WithRoleAssignments(kv, AzureKeyVaultRole.KeyVaultSecretsUser, AzureKeyVaultRole.KeyVaultReader));

        Assert.Null(exception);
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_SingleRole_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var kv = builder.AddAzureKeyVault("myKeyVault");
        var container = builder.AddContainer("myContainer", "nginx");

        var exception = Record.Exception(() =>
            container.WithRoleAssignments(kv, AzureKeyVaultRole.KeyVaultAdministrator));

        Assert.Null(exception);
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_InvalidRole_ThrowsArgumentException()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var kv = builder.AddAzureKeyVault("myKeyVault");
        var container = builder.AddContainer("myContainer", "nginx");
        var invalidRole = (AzureKeyVaultRole)(-1);

        var exception = Assert.Throws<ArgumentException>(() =>
            container.WithRoleAssignments(kv, invalidRole));

        Assert.Contains("is not a valid AzureKeyVaultRole value", exception.Message);
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_MixedValidAndInvalidRoles_ThrowsOnInvalid()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var kv = builder.AddAzureKeyVault("myKeyVault");
        var container = builder.AddContainer("myContainer", "nginx");
        var invalidRole = (AzureKeyVaultRole)123;

        var exception = Assert.Throws<ArgumentException>(() =>
            container.WithRoleAssignments(kv, AzureKeyVaultRole.KeyVaultReader, invalidRole));

        Assert.Contains("is not a valid AzureKeyVaultRole value", exception.Message);
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_DuplicateRoles_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var kv = builder.AddAzureKeyVault("myKeyVault");
        var container = builder.AddContainer("myContainer", "nginx");

        var exception = Record.Exception(() =>
            container.WithRoleAssignments(kv, AzureKeyVaultRole.KeyVaultSecretsUser, AzureKeyVaultRole.KeyVaultSecretsUser));

        Assert.Null(exception);
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_AllBuiltInRoles_AreAccepted()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var kv = builder.AddAzureKeyVault("myKeyVault");
        var container = builder.AddContainer("myContainer", "nginx");

        var roles = new[]
        {
            AzureKeyVaultRole.KeyVaultAdministrator,
            AzureKeyVaultRole.KeyVaultCertificateUser,
            AzureKeyVaultRole.KeyVaultCertificatesOfficer,
            AzureKeyVaultRole.KeyVaultContributor,
            AzureKeyVaultRole.KeyVaultCryptoOfficer,
            AzureKeyVaultRole.KeyVaultCryptoServiceEncryptionUser,
            AzureKeyVaultRole.KeyVaultCryptoServiceReleaseUser,
            AzureKeyVaultRole.KeyVaultCryptoUser,
            AzureKeyVaultRole.KeyVaultDataAccessAdministrator,
            AzureKeyVaultRole.KeyVaultReader,
            AzureKeyVaultRole.KeyVaultSecretsOfficer,
            AzureKeyVaultRole.KeyVaultSecretsUser,
            AzureKeyVaultRole.ManagedHsmContributor
        };

        foreach (var role in roles)
        {
            var exception = Record.Exception(() =>
                container.WithRoleAssignments(kv, role));

            Assert.Null(exception);
        }
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_EmptyRoles_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var kv = builder.AddAzureKeyVault("myKeyVault");
        var container = builder.AddContainer("myContainer", "nginx");

        var exception = Record.Exception(() =>
            container.WithRoleAssignments(kv, Array.Empty<AzureKeyVaultRole>()));

        Assert.Null(exception);
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_NullRoles_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var kv = builder.AddAzureKeyVault("myKeyVault");
        var container = builder.AddContainer("myContainer", "nginx");

        var exception = Record.Exception(() =>
            container.WithRoleAssignments(kv, (AzureKeyVaultRole[]?)null!));

        Assert.Null(exception);
    }
}
