// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations;
using Aspire.Hosting.Azure.Provisioning;

namespace Aspire.Hosting.Azure.Tests;

public class AzureProvisionerOptionsTests
{
    [Fact]
    public void CredentialProcessTimeoutSeconds_DefaultsTo60()
    {
        var options = new AzureProvisionerOptions();

        Assert.Equal(60, options.CredentialProcessTimeoutSeconds);
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
}
