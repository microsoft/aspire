// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Azure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.Tests;

public class DefaultTokenCredentialProviderTests
{
    [Fact]
    public void Constructor_PublishMode_NoCredentialSource_UsesAzureCli()
    {
        // Arrange
        var azureOptions = CreateAzureOptions(credentialSource: null);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);

        // Act
        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            azureOptions,
            executionContext);

        // Assert
        Assert.IsType<AzureCliCredential>(provider.TokenCredential);
    }

    [Fact]
    public void Constructor_RunMode_NoCredentialSource_UsesDefaultAzureCredential()
    {
        // Arrange
        var azureOptions = CreateAzureOptions(credentialSource: null);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        // Act
        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            azureOptions,
            executionContext);

        // Assert
        Assert.IsType<DefaultAzureCredential>(provider.TokenCredential);
    }

    [Fact]
    public void Constructor_PublishMode_ExplicitDefaultCredentialSource_UsesAzureCli()
    {
        // Arrange
        var azureOptions = CreateAzureOptions(credentialSource: "Default");
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);

        // Act
        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            azureOptions,
            executionContext);

        // Assert
        Assert.IsType<AzureCliCredential>(provider.TokenCredential);
    }

    [Fact]
    public void Constructor_RunMode_ExplicitDefaultCredentialSource_UsesDefaultAzureCredential()
    {
        // Arrange
        var azureOptions = CreateAzureOptions(credentialSource: "Default");
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        // Act
        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            azureOptions,
            executionContext);

        // Assert
        Assert.IsType<DefaultAzureCredential>(provider.TokenCredential);
    }

    [Fact]
    public void Constructor_PublishMode_ExplicitNonDefaultCredentialSource_RespectsSource()
    {
        // Arrange
        var azureOptions = CreateAzureOptions(credentialSource: "AzurePowerShell");
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);

        // Act
        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            azureOptions,
            executionContext);

        // Assert
        Assert.IsType<AzurePowerShellCredential>(provider.TokenCredential);
    }

    [Fact]
    public void Constructor_RunMode_ExplicitCredentialSource_RespectsSource()
    {
        // Arrange
        var azureOptions = CreateAzureOptions(credentialSource: "VisualStudio");
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        // Act
        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            azureOptions,
            executionContext);

        // Assert
        Assert.IsType<VisualStudioCredential>(provider.TokenCredential);
    }

    [Fact]
    public void Constructor_RunMode_InvalidCredentialSource_UsesDefaultAzureCredential()
    {
        // Arrange
        var azureOptions = CreateAzureOptions(credentialSource: "InvalidSource");
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        // Act
        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            azureOptions,
            executionContext);

        // Assert
        Assert.IsType<DefaultAzureCredential>(provider.TokenCredential);
    }

    [Fact]
    public void Constructor_RunMode_AzureCliCredentialSource_UsesAzureCliCredential()
    {
        // Arrange
        var azureOptions = CreateAzureOptions(credentialSource: "AzureCli");
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        // Act
        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            azureOptions,
            executionContext);

        // Assert
        Assert.IsType<AzureCliCredential>(provider.TokenCredential);
    }

    [Fact]
    public void Constructor_RunMode_AzureDeveloperCliCredentialSource_UsesAzureDeveloperCliCredential()
    {
        // Arrange
        var azureOptions = CreateAzureOptions(credentialSource: "AzureDeveloperCli");
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        // Act
        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            azureOptions,
            executionContext);

        // Assert
        Assert.IsType<AzureDeveloperCliCredential>(provider.TokenCredential);
    }

    [Fact]
    public void Constructor_RunMode_InteractiveBrowserCredentialSource_UsesInteractiveBrowserCredential()
    {
        // Arrange
        var azureOptions = CreateAzureOptions(credentialSource: "InteractiveBrowser");
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        // Act
        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            azureOptions,
            executionContext);

        // Assert
        Assert.IsType<InteractiveBrowserCredential>(provider.TokenCredential);
    }

    [Fact]
    public void TokenCredential_TenantIdChanges_RecreatesCredential()
    {
        // Arrange
        var azureOptions = new AzureProvisionerOptions
        {
            CredentialSource = "AzureCli",
            TenantId = "tenant-1"
        };
        var options = new TestOptions<AzureProvisionerOptions>(azureOptions);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);

        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            options,
            executionContext);

        // Act - Access credential with first tenant
        var credential1 = provider.TokenCredential;
        Assert.IsType<AzureCliCredential>(credential1);

        // Change tenant ID
        azureOptions.TenantId = "tenant-2";

        // Access credential again
        var credential2 = provider.TokenCredential;

        // Assert - Should get a new credential instance
        Assert.IsType<AzureCliCredential>(credential2);
        Assert.NotSame(credential1, credential2);
    }

    [Fact]
    public void TokenCredential_TenantIdUnchanged_ReturnsSameCredential()
    {
        // Arrange
        var azureOptions = new AzureProvisionerOptions
        {
            CredentialSource = "AzureCli",
            TenantId = "tenant-1"
        };
        var options = new TestOptions<AzureProvisionerOptions>(azureOptions);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);

        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            options,
            executionContext);

        // Act - Access credential multiple times without changing tenant
        var credential1 = provider.TokenCredential;
        var credential2 = provider.TokenCredential;
        var credential3 = provider.TokenCredential;

        // Assert - Should get the same credential instance
        Assert.Same(credential1, credential2);
        Assert.Same(credential2, credential3);
    }

    [Fact]
    public void TokenCredential_TenantIdSetFromNull_RecreatesCredential()
    {
        // Arrange
        var azureOptions = new AzureProvisionerOptions
        {
            CredentialSource = "AzureDeveloperCli",
            TenantId = null
        };
        var options = new TestOptions<AzureProvisionerOptions>(azureOptions);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);

        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            options,
            executionContext);

        // Act - Access credential with null tenant
        var credential1 = provider.TokenCredential;
        Assert.IsType<AzureDeveloperCliCredential>(credential1);

        // Set tenant ID
        azureOptions.TenantId = "70a036f6-8e4d-4615-bad6-149c02e7720d";

        // Access credential again
        var credential2 = provider.TokenCredential;

        // Assert - Should get a new credential instance
        Assert.IsType<AzureDeveloperCliCredential>(credential2);
        Assert.NotSame(credential1, credential2);
    }

    [Fact]
    public void TokenCredential_MultipleCredentialTypes_RespectsCurrentTenantId()
    {
        // Arrange
        var azureOptions = new AzureProvisionerOptions
        {
            CredentialSource = "AzurePowerShell",
            TenantId = "tenant-1"
        };
        var options = new TestOptions<AzureProvisionerOptions>(azureOptions);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            options,
            executionContext);

        // Act - Access credential
        var credential1 = provider.TokenCredential;
        Assert.IsType<AzurePowerShellCredential>(credential1);

        // Change both credential source and tenant ID
        azureOptions.CredentialSource = "VisualStudio";
        azureOptions.TenantId = "tenant-2";

        // Access credential again
        var credential2 = provider.TokenCredential;

        // Assert - Should get a new credential of the new type
        Assert.IsType<VisualStudioCredential>(credential2);
        Assert.NotSame(credential1, credential2);
    }

    [Fact]
    public void TokenCredential_LazyInitialization_DoesNotCreateUntilAccessed()
    {
        // Arrange
        var azureOptions = new AzureProvisionerOptions
        {
            CredentialSource = "AzureCli",
            TenantId = "tenant-1"
        };
        var options = new TestOptions<AzureProvisionerOptions>(azureOptions);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);

        // Act - Create provider but don't access TokenCredential property
        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            options,
            executionContext);

        // Change tenant ID before first access
        azureOptions.TenantId = "tenant-2";

        // Now access the credential
        var credential = provider.TokenCredential;

        // Assert - Should create credential with the current (second) tenant ID
        Assert.IsType<AzureCliCredential>(credential);
        // The credential should have been created with tenant-2, not tenant-1
        // We can't directly verify the tenant ID in the credential, but we verified
        // it was created after the tenant ID change
    }

    private static IOptions<AzureProvisionerOptions> CreateAzureOptions(string? credentialSource)
    {
        var options = new AzureProvisionerOptions
        {
            // Intentionally allow CredentialSource to be null for tests that verify null handling.
            CredentialSource = credentialSource!
        };
        return Options.Create(options);
    }

    /// <summary>
    /// Test implementation of IOptions that allows modifying the underlying options object.
    /// </summary>
    private sealed class TestOptions<T>(T value) : IOptions<T> where T : class
    {
        public T Value => value;
    }

}
