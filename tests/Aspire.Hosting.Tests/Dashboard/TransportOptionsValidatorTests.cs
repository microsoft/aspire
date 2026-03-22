// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dashboard;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Tests.Dashboard;

[Trait("Partition", "3")]
public class TransportOptionsValidatorTests
{
    [Fact]
    public void ValidationFailsWhenHttpUrlSpecifiedWithAllowUnsecureTransportSetToFalse()
    {
        var distributedApplicationOptions = new DistributedApplicationOptions();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();
        options.AllowUnsecureTransport = false;

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = "http://localhost:1234";

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Equal(
            $"The 'applicationUrl' setting must be an https address unless the '{KnownConfigNames.AllowUnsecuredTransport}' environment variable is set to true. This configuration is commonly set in the launch profile. See https://aka.ms/dotnet/aspire/allowunsecuredtransport for more details.",
            result.FailureMessage
            );
    }

    [Fact]
    public void InvalidTransportOptionSucceedValidationInPublishMode()
    {
        var distributedApplicationOptions = new DistributedApplicationOptions();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);
        var options = new TransportOptions();
        options.AllowUnsecureTransport = false;

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = "http://localhost:1234";

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void InvalidTransportOptionSucceedValidationWithDashboardDisabled()
    {
        var distributedApplicationOptions = new DistributedApplicationOptions()
        {
            DisableDashboard = true
        };
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = "http://localhost:1234";

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void InvalidTransportOptionSucceedValidationWithDistributedAppOptionsFlag()
    {
        var distributedApplicationOptions = new DistributedApplicationOptions()
        {
            AllowUnsecuredTransport = true
        };
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = "http://localhost:1234";

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void ValidationFailsWithInvalidUrl()
    {
        var distributedApplicationOptions = new DistributedApplicationOptions();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();
        options.AllowUnsecureTransport = false;

        var invalidUrl = "...invalid...url...";
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = invalidUrl;

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Equal(
            $"The 'applicationUrl' setting of the launch profile has value '{invalidUrl}' which could not be parsed as a URI.",
            result.FailureMessage
            );
    }

    [Fact]
    public void ValidationFailsWithMissingUrl()
    {
        var distributedApplicationOptions = new DistributedApplicationOptions();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();
        options.AllowUnsecureTransport = false;

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Equal(
            $"AppHost does not have applicationUrl in launch profile, or {KnownConfigNames.AspNetCoreUrls} environment variable set.",
            result.FailureMessage
            );
    }

    [Fact]
    public void ValidationFailsWithStringEmptyUrl()
    {
        var distributedApplicationOptions = new DistributedApplicationOptions();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();
        options.AllowUnsecureTransport = false;

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = string.Empty;

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Equal(
            $"AppHost does not have applicationUrl in launch profile, or {KnownConfigNames.AspNetCoreUrls} environment variable set.",
            result.FailureMessage
            );
    }

    [Theory]
    [InlineData(KnownConfigNames.ResourceServiceEndpointUrl)]
    [InlineData(KnownConfigNames.Legacy.ResourceServiceEndpointUrl)]
    public void ValidationFailsWhenResourceUrlNotDefined(string resourceServiceEndpointUrlKey)
    {
        var distributedApplicationOptions = new DistributedApplicationOptions();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();
        options.AllowUnsecureTransport = false;

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = "https://localhost:1234";
        config[resourceServiceEndpointUrlKey] = string.Empty;
        config[KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "https://localhost:1236";

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Equal(
            $"AppHost does not have the {KnownConfigNames.ResourceServiceEndpointUrl} setting defined.",
            result.FailureMessage
            );
    }

    [Theory]
    [InlineData(KnownConfigNames.DashboardOtlpGrpcEndpointUrl)]
    [InlineData(KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl)]
    public void ValidationFailsWhenOtlpUrlNotDefined(string dashboardOtlpGrpcEndpointUrlKey)
    {
        var distributedApplicationOptions = new DistributedApplicationOptions();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();
        options.AllowUnsecureTransport = false;

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = "https://localhost:1234";
        config[KnownConfigNames.ResourceServiceEndpointUrl] = "https://localhost:1235";
        config[dashboardOtlpGrpcEndpointUrlKey] = string.Empty;

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Equal(
            $"AppHost does not have the {KnownConfigNames.DashboardOtlpGrpcEndpointUrl} or {KnownConfigNames.DashboardOtlpHttpEndpointUrl} settings defined. At least one OTLP endpoint must be provided.",
            result.FailureMessage
            );
    }

    [Theory]
    [InlineData(KnownConfigNames.ResourceServiceEndpointUrl)]
    [InlineData(KnownConfigNames.Legacy.ResourceServiceEndpointUrl)]
    public void ValidationFailsWhenResourceServiceUrlMalformed(string resourceServiceEndpointUrlKey)
    {
        var distributedApplicationOptions = new DistributedApplicationOptions();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();
        options.AllowUnsecureTransport = false;

        var invalidUrl = "...invalid...url...";
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = "https://localhost:1234";
        config[resourceServiceEndpointUrlKey] = invalidUrl;
        config[KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "https://localhost:1236";

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Equal(
            $"The {KnownConfigNames.ResourceServiceEndpointUrl} setting with a value of '{invalidUrl}' could not be parsed as a URI.",
            result.FailureMessage
            );
    }

    [Theory]
    [InlineData(KnownConfigNames.DashboardOtlpGrpcEndpointUrl, KnownConfigNames.DashboardOtlpGrpcEndpointUrl)]
    [InlineData(KnownConfigNames.DashboardOtlpHttpEndpointUrl, KnownConfigNames.DashboardOtlpHttpEndpointUrl)]
    [InlineData(KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl, KnownConfigNames.DashboardOtlpGrpcEndpointUrl)]
    [InlineData(KnownConfigNames.Legacy.DashboardOtlpHttpEndpointUrl, KnownConfigNames.DashboardOtlpHttpEndpointUrl)]
    public void ValidationFailsWhenOtlpUrlMalformed(string otlpEndpointConfigName, string msgName)
    {
        var distributedApplicationOptions = new DistributedApplicationOptions();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();
        options.AllowUnsecureTransport = false;

        var invalidUrl = "...invalid...url...";
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = "https://localhost:1234";
        config[KnownConfigNames.ResourceServiceEndpointUrl] = "https://localhost:1235";
        config[otlpEndpointConfigName] = invalidUrl;

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Equal(
            $"The {msgName} setting with a value of '{invalidUrl}' could not be parsed as a URI.",
            result.FailureMessage
            );
    }

    [Theory]
    [InlineData(KnownConfigNames.DashboardOtlpGrpcEndpointUrl)]
    [InlineData(KnownConfigNames.DashboardOtlpHttpEndpointUrl)]
    public void ValidationFailsWhenDashboardOtlpUrlIsHttp(string otlpEndpointConfigName)
    {
        var distributedApplicationOptions = new DistributedApplicationOptions();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();
        options.AllowUnsecureTransport = false;

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = "https://localhost:1234";
        config[KnownConfigNames.ResourceServiceEndpointUrl] = "https://localhost:1235";
        config[otlpEndpointConfigName] = "http://localhost:1236";

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Equal(
            $"The '{otlpEndpointConfigName}' setting must be an https address unless the '{KnownConfigNames.AllowUnsecuredTransport}' environment variable is set to true. This configuration is commonly set in the launch profile. See https://aka.ms/dotnet/aspire/allowunsecuredtransport for more details.",
            result.FailureMessage
            );
    }

    [Theory]
    [InlineData(KnownConfigNames.ResourceServiceEndpointUrl)]
    [InlineData(KnownConfigNames.Legacy.ResourceServiceEndpointUrl)]
    public void ValidationFailsWhenResourceServiceUrlIsHttp(string resourceServiceEndpointUrlKey)
    {
        var distributedApplicationOptions = new DistributedApplicationOptions();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();
        options.AllowUnsecureTransport = false;

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = "https://localhost:1234";
        config[resourceServiceEndpointUrlKey] = "http://localhost:1235";
        config[KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "https://localhost:1236";

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Equal(
            $"The '{KnownConfigNames.ResourceServiceEndpointUrl}' setting must be an https address unless the '{KnownConfigNames.AllowUnsecuredTransport}' environment variable is set to true. This configuration is commonly set in the launch profile. See https://aka.ms/dotnet/aspire/allowunsecuredtransport for more details.",
            result.FailureMessage
            );
    }

    [Fact]
    public void ValidationSucceedsWhenHttpUrlSpecifiedWithAllowUnsecureTransportSetToTrue()
    {
        var distributedApplicationOptions = new DistributedApplicationOptions();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();
        options.AllowUnsecureTransport = true;

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = "http://localhost:1234";

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void ValidationSucceedsWhenHttpsUrlSpecifiedWithAllowUnsecureTransportSetToTrue()
    {
        var distributedApplicationOptions = new DistributedApplicationOptions();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();
        options.AllowUnsecureTransport = true;

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = "https://localhost:1234";

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Theory]
    [InlineData(KnownConfigNames.DashboardOtlpHttpEndpointUrl, KnownConfigNames.ResourceServiceEndpointUrl)]
    [InlineData(KnownConfigNames.Legacy.DashboardOtlpHttpEndpointUrl, KnownConfigNames.ResourceServiceEndpointUrl)]
    [InlineData(KnownConfigNames.DashboardOtlpHttpEndpointUrl, KnownConfigNames.Legacy.ResourceServiceEndpointUrl)]
    [InlineData(KnownConfigNames.Legacy.DashboardOtlpHttpEndpointUrl, KnownConfigNames.Legacy.ResourceServiceEndpointUrl)]
    public void ValidationSucceedsWhenHttpsUrlSpecifiedWithAllowUnsecureTransportSetToFalse(string dashboardOtlpHttpEndpointUrlKey, string resourceServiceEndpointUrlKey)
    {
        var distributedApplicationOptions = new DistributedApplicationOptions();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();
        options.AllowUnsecureTransport = false;

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = "https://localhost:1234";
        config[dashboardOtlpHttpEndpointUrlKey] = "https://localhost:1235";
        config[resourceServiceEndpointUrlKey] = "https://localhost:1236";

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void ValidationSucceedsWithValidBindingAddressThatFailsUriParsing()
    {
        var distributedApplicationOptions = new DistributedApplicationOptions();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();
        options.AllowUnsecureTransport = false;

        // This is a valid Kestrel binding address but fails Uri.TryCreate validation
        var bindingAddress = "https://0:0:0:0:17008";
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = bindingAddress;
        config[KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "https://localhost:1236";
        config[KnownConfigNames.ResourceServiceEndpointUrl] = "https://localhost:1237";

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        
        // This should succeed after the fix
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Theory]
    [InlineData(KnownConfigNames.DashboardOtlpGrpcEndpointUrl)]
    [InlineData(KnownConfigNames.DashboardOtlpHttpEndpointUrl)]
    public void ValidationSucceedsWithValidGrpcBindingAddressThatFailsUriParsing(string otlpEndpointConfigName)
    {
        var distributedApplicationOptions = new DistributedApplicationOptions();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var options = new TransportOptions();
        options.AllowUnsecureTransport = false;

        // This is a valid Kestrel binding address but fails Uri.TryCreate validation
        var grpcBindingAddress = "https://0:0:0:0:18001";
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[KnownConfigNames.AspNetCoreUrls] = "https://localhost:1234";
        config[otlpEndpointConfigName] = grpcBindingAddress;
        config[KnownConfigNames.ResourceServiceEndpointUrl] = "https://localhost:1237";

        var validator = new TransportOptionsValidator(config, executionContext, distributedApplicationOptions);
        var result = validator.Validate(null, options);
        
        // This should succeed after the fix
        Assert.True(result.Succeeded, result.FailureMessage);
    }
}
