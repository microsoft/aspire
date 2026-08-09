// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES002

using System.ComponentModel.DataAnnotations;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Azure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.Tests;

public class AzureProvisionerOptionsTests
{
    [Fact]
    public void CredentialProcessTimeoutSeconds_DefaultsToNull()
    {
        var options = new AzureProvisionerOptions();

        Assert.Null(options.CredentialProcessTimeoutSeconds);
    }

    [Fact]
    public void CredentialProcessTimeoutSeconds_WithinRange_PassesValidation()
    {
        var options = new AzureProvisionerOptions { CredentialProcessTimeoutSeconds = 120 };
        var context = new ValidationContext(options);
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(options, context, results, validateAllProperties: true);

        Assert.True(isValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(601)]
    [InlineData(-1)]
    public void CredentialProcessTimeoutSeconds_OutOfRange_FailsValidation(int timeoutSeconds)
    {
        var options = new AzureProvisionerOptions { CredentialProcessTimeoutSeconds = timeoutSeconds };
        var context = new ValidationContext(options);
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(options, context, results, validateAllProperties: true);

        Assert.False(isValid);
    }

    [Fact]
    public void CredentialProcessTimeoutSeconds_AtMinBoundary_PassesValidation()
    {
        var options = new AzureProvisionerOptions { CredentialProcessTimeoutSeconds = 5 };
        var context = new ValidationContext(options);
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(options, context, results, validateAllProperties: true);

        Assert.True(isValid);
    }

    [Fact]
    public void CredentialProcessTimeoutSeconds_AtMaxBoundary_PassesValidation()
    {
        var options = new AzureProvisionerOptions { CredentialProcessTimeoutSeconds = 600 };
        var context = new ValidationContext(options);
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(options, context, results, validateAllProperties: true);

        Assert.True(isValid);
    }

    [Fact]
    public void CredentialSource_DefaultsToDefault()
    {
        var options = new AzureProvisionerOptions();

        Assert.Equal("Default", options.CredentialSource);
    }

    [Fact]
    public void CustomTimeout_PublishMode_AzureCli_CreatesCredentialSuccessfully()
    {
        var azureOptions = new AzureProvisionerOptions
        {
            CredentialSource = "AzureCli",
            CredentialProcessTimeoutSeconds = 120
        };
        var options = Options.Create(azureOptions);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);

        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            options,
            executionContext);

        Assert.IsType<AzureCliCredential>(provider.TokenCredential);
    }

    [Fact]
    public void CustomTimeout_RunMode_DefaultCredentialSource_CreatesCredentialSuccessfully()
    {
        var azureOptions = new AzureProvisionerOptions
        {
            CredentialProcessTimeoutSeconds = 90
        };
        var options = Options.Create(azureOptions);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            options,
            executionContext);

        // Run mode with Default credential source uses DefaultAzureCredential (not affected by custom timeout)
        Assert.IsType<DefaultAzureCredential>(provider.TokenCredential);
    }

    [Theory]
    [InlineData("AzureCli")]
    [InlineData("AzurePowerShell")]
    [InlineData("VisualStudio")]
    [InlineData("AzureDeveloperCli")]
    public void CustomTimeout_AllProcessBackedCredentials_CreateSuccessfully(string credentialSource)
    {
        var azureOptions = new AzureProvisionerOptions
        {
            CredentialSource = credentialSource,
            CredentialProcessTimeoutSeconds = 180
        };
        var options = Options.Create(azureOptions);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);

        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            options,
            executionContext);

        // Verify credential is created without error when custom timeout is set
        Assert.NotNull(provider.TokenCredential);
    }

    [Fact]
    public void CustomTimeout_InteractiveBrowser_NotAffected()
    {
        var azureOptions = new AzureProvisionerOptions
        {
            CredentialSource = "InteractiveBrowser",
            CredentialProcessTimeoutSeconds = 120
        };
        var options = Options.Create(azureOptions);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);

        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            options,
            executionContext);

        // InteractiveBrowser doesn't support ProcessTimeout — should still create successfully
        Assert.IsType<InteractiveBrowserCredential>(provider.TokenCredential);
    }

    [Fact]
    public void MinTimeout_CreatesCredentialSuccessfully()
    {
        var azureOptions = new AzureProvisionerOptions
        {
            CredentialSource = "AzureCli",
            CredentialProcessTimeoutSeconds = 5
        };
        var options = Options.Create(azureOptions);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);

        var provider = new DefaultTokenCredentialProvider(
            NullLogger<DefaultTokenCredentialProvider>.Instance,
            options,
            executionContext);

        Assert.IsType<AzureCliCredential>(provider.TokenCredential);
    }
}
