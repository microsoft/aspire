// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Claims;
using System.Text.Json;
using Aspire.Dashboard.Configuration;
using Aspire.Hosting;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using OpenIdConnectOptions = Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions;

namespace Aspire.Dashboard.Tests;

public sealed class DashboardOptionsTests
{
    private static DashboardOptions GetValidOptions()
    {
        // The minimal set of options required to pass validation.
        return new()
        {
            Frontend =
            {
                AuthMode = FrontendAuthMode.Unsecured,
                EndpointUrls = "http://localhost:5000"
            },
            Otlp =
            {
                AuthMode = OtlpAuthMode.Unsecured,
                GrpcEndpointUrl = "http://localhost:4317"
            },
        };
    }

    [Fact]
    public void ValidOptions_AreValid()
    {
        var result = new ValidateDashboardOptions().Validate(null, GetValidOptions());

        Assert.Null(result.FailureMessage);
        Assert.True(result.Succeeded);
    }

    #region Frontend options

    [Fact]
    public void FrontendOptions_EmptyEndpointUrl()
    {
        var options = GetValidOptions();
        options.Frontend.EndpointUrls = "";

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal("One or more frontend endpoint URLs are not configured. Specify an ASPNETCORE_URLS value.", result.FailureMessage);
    }

    [Fact]
    public void FrontendOptions_InvalidUrl()
    {
        var options = GetValidOptions();
        options.Frontend.EndpointUrls = "invalid";

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal("Failed to parse frontend endpoint URLs 'invalid'.", result.FailureMessage);
    }

    [Fact]
    public void FrontendOptions_ValidAndInvalidUrl()
    {
        var options = GetValidOptions();
        options.Frontend.EndpointUrls = "http://localhost:5000;invalid";

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal("Failed to parse frontend endpoint URLs 'http://localhost:5000;invalid'.", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FrontendOptions_MaxConsoleLogCount(int limit)
    {
        var options = GetValidOptions();
        options.Frontend.MaxConsoleLogCount = limit;

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal($"{DashboardConfigNames.DashboardFrontendMaxConsoleLogCountName.ConfigKey} must be greater than zero.", result.FailureMessage);
    }

    #endregion

    #region Resource service client options

    [Fact]
    public void ResourceServiceClientOptions_InvalidUrl()
    {
        var options = GetValidOptions();
        options.ResourceServiceClient.Url = "invalid";

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal("Failed to parse resource service client endpoint URL 'invalid'.", result.FailureMessage);
    }

    [Fact]
    public void ResourceServiceClientOptions_ApiKeyMode_Empty()
    {
        var options = GetValidOptions();
        options.ResourceServiceClient.Url = "http://localhost";
        options.ResourceServiceClient.AuthMode = ResourceClientAuthMode.ApiKey;
        options.ResourceServiceClient.ApiKey = "";

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal($"{DashboardConfigNames.ResourceServiceClientAuthModeName.ConfigKey} is \"{nameof(ResourceClientAuthMode.ApiKey)}\", but no {DashboardConfigNames.ResourceServiceClientApiKeyName.ConfigKey} is configured.", result.FailureMessage);
    }

    [Fact]
    public void ResourceServiceClientOptions_CertificateMode_FileSource_FilePathEmpty()
    {
        var options = GetValidOptions();
        options.ResourceServiceClient.Url = "http://localhost";
        options.ResourceServiceClient.AuthMode = ResourceClientAuthMode.Certificate;
        options.ResourceServiceClient.ClientCertificate.Source = DashboardClientCertificateSource.File;
        options.ResourceServiceClient.ClientCertificate.FilePath = "";

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal($"{DashboardConfigNames.ResourceServiceClientCertificateSourceName.ConfigKey} is \"File\", but no {DashboardConfigNames.ResourceServiceClientCertificateFilePathName.ConfigKey} is configured.", result.FailureMessage);
    }

    [Fact]
    public void ResourceServiceClientOptions_CertificateMode_KeyStoreSource_SubjectEmpty()
    {
        var options = GetValidOptions();
        options.ResourceServiceClient.Url = "http://localhost";
        options.ResourceServiceClient.AuthMode = ResourceClientAuthMode.Certificate;
        options.ResourceServiceClient.ClientCertificate.Source = DashboardClientCertificateSource.KeyStore;
        options.ResourceServiceClient.ClientCertificate.Subject = "";

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal($"{DashboardConfigNames.ResourceServiceClientCertificateSourceName.ConfigKey} is \"KeyStore\", but no {DashboardConfigNames.ResourceServiceClientCertificateSubjectName.ConfigKey} is configured.", result.FailureMessage);
    }

    [Fact]
    public void ResourceServiceClientOptions_CertificateMode_NullSource()
    {
        var options = GetValidOptions();
        options.ResourceServiceClient.Url = "http://localhost";
        options.ResourceServiceClient.AuthMode = ResourceClientAuthMode.Certificate;
        options.ResourceServiceClient.ClientCertificate.Source = null;

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal($"The resource service client is configured to use certificates, but no certificate source is specified. Specify {DashboardConfigNames.ResourceServiceClientCertificateSourceName.ConfigKey}. Possible values: {string.Join(", ", typeof(DashboardClientCertificateSource).GetEnumNames())}", result.FailureMessage);
    }

    [Fact]
    public void ResourceServiceClientOptions_CertificateMode_InvalidSource()
    {
        var options = GetValidOptions();
        options.ResourceServiceClient.Url = "http://localhost";
        options.ResourceServiceClient.AuthMode = ResourceClientAuthMode.Certificate;
        options.ResourceServiceClient.ClientCertificate.Source = (DashboardClientCertificateSource)int.MaxValue;

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal($"Unexpected resource service client certificate source: {options.ResourceServiceClient.ClientCertificate.Source}", result.FailureMessage);
    }

    [Fact]
    public void ResourceServiceClientOptions_NullMode()
    {
        var options = GetValidOptions();
        options.ResourceServiceClient.Url = "http://localhost";
        options.ResourceServiceClient.AuthMode = null;

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal($"Resource service client authentication is not configured. Specify {DashboardConfigNames.ResourceServiceClientAuthModeName.ConfigKey}. Possible values: {string.Join(", ", typeof(ResourceClientAuthMode).GetEnumNames())}", result.FailureMessage);
    }

    [Fact]
    public void ResourceServiceClientOptions_InvalidMode()
    {
        var options = GetValidOptions();
        options.ResourceServiceClient.Url = "http://localhost";
        options.ResourceServiceClient.AuthMode = (ResourceClientAuthMode)int.MaxValue;

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal($"Unexpected resource service client authentication mode: {int.MaxValue}", result.FailureMessage);
    }

    #endregion

    #region OTLP options

    [Fact]
    public void OtlpOptions_NeitherEndpointSet()
    {
        var options = GetValidOptions();
        options.Otlp.GrpcEndpointUrl = null;
        options.Otlp.HttpEndpointUrl = null;

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal(
            $"Neither OTLP/gRPC or OTLP/HTTP endpoint URLs are configured. Specify either a {DashboardConfigNames.DashboardOtlpGrpcUrlName.EnvVarName} or {DashboardConfigNames.DashboardOtlpHttpUrlName.EnvVarName} value.",
            result.FailureMessage);
    }

    [Fact]
    public void OtlpOptions_gRPC_InvalidUrl()
    {
        var options = GetValidOptions();
        options.Otlp.GrpcEndpointUrl = "invalid";

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal("Failed to parse OTLP gRPC endpoint URL 'invalid'.", result.FailureMessage);
    }

    [Fact]
    public void OtlpOptions_HTTP_InvalidUrl()
    {
        var options = GetValidOptions();
        options.Otlp.HttpEndpointUrl = "invalid";

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal("Failed to parse OTLP HTTP endpoint URL 'invalid'.", result.FailureMessage);
    }

    [Fact]
    public async Task OtlpOptions_SuppressUnsecuredMessage_LegacyName()
    {
        await using var app = new DashboardWebApplication(builder => builder.Configuration.AddInMemoryCollection(
        [
            new("ASPNETCORE_URLS", "http://localhost:8000/"),
            new("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL", "http://localhost:4319/"),
            new(DashboardConfigNames.Legacy.DashboardOtlpSuppressUnsecuredTelemetryMessageName.ConfigKey, "true"),
        ]));
        var options = app.Services.GetService<IOptionsMonitor<DashboardOptions>>()!;

        Assert.True(options.CurrentValue.Otlp.SuppressUnsecuredMessage);
    }

    #endregion

    #region OpenIDConnect options

    [Fact]
    public void OpenIdConnectOptions_NoNameClaimType()
    {
        var options = GetValidOptions();
        options.Frontend.AuthMode = FrontendAuthMode.OpenIdConnect;
        options.Frontend.OpenIdConnect.NameClaimType = "";

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal("OpenID Connect claim type for name not configured. Specify a Dashboard:Frontend:OpenIdConnect:NameClaimType value.", result.FailureMessage);
    }

    [Fact]
    public void OpenIdConnectOptions_NoUserNameClaimType()
    {
        var options = GetValidOptions();
        options.Frontend.AuthMode = FrontendAuthMode.OpenIdConnect;
        options.Frontend.OpenIdConnect.UsernameClaimType = "";

        var result = new ValidateDashboardOptions().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal("OpenID Connect claim type for username not configured. Specify a Dashboard:Frontend:OpenIdConnect:UsernameClaimType value.", result.FailureMessage);
    }

    [Fact]
    public async Task OpenIdConnectOptions_ClaimActions_MapJsonKeyTestAsync()
    {
        await using var app = new DashboardWebApplication(builder => builder.Configuration.AddInMemoryCollection(
        [
            new("ASPNETCORE_URLS", "http://localhost:8000/"),
            new("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL", "http://localhost:4319/"),
            new("Authentication:Schemes:OpenIdConnect:Authority", "https://id.aspire.dev/"),
            new("Authentication:Schemes:OpenIdConnect:ClientId", "aspire-dashboard"),
            new("Dashboard:Frontend:AuthMode", "OpenIdConnect"),
            new("Dashboard:Frontend:OpenIdConnect:ClaimActions:0:ClaimType", "role"),
            new("Dashboard:Frontend:OpenIdConnect:ClaimActions:0:JsonKey", "role"),
            new("Dashboard:Frontend:OpenIdConnect:RequiredClaimType", "role")
        ]));
        var openIdConnectAuthOptions = app.Services.GetService<IOptionsMonitor<OpenIdConnectOptions>>()?.Get(OpenIdConnectDefaults.AuthenticationScheme);
        Assert.NotNull(openIdConnectAuthOptions);
        Assert.NotEmpty(openIdConnectAuthOptions.ClaimActions);
        var claimAction = openIdConnectAuthOptions.ClaimActions.FirstOrDefault(x => x.ClaimType == "role");
        Assert.NotNull(claimAction);
        Assert.Equal("role", claimAction.ClaimType);
        var jsonElement = JsonDocument.Parse("""
                           {
                             "role": ["admin", "test"]
                           }
                           """).RootElement.Clone();
        var claimIdentity = new ClaimsIdentity();
        claimAction.Run(jsonElement, claimIdentity, "test");
        Assert.Equal(2, claimIdentity.Claims.Count());
        Assert.True(claimIdentity.HasClaim("role", "admin"));
        Assert.True(claimIdentity.HasClaim("role", "test"));
    }

    [Fact]
    public void GetOidcClaimActionConfigure_MapJsonKeyTest()
    {
        var claimAction = new ClaimAction
        {
            ClaimType = "role",
            JsonKey = "role"
        };
        var oidcOption = new OpenIdConnectOptions();
        oidcOption.ClaimActions.Clear();
        var configure = DashboardWebApplication.GetOidcClaimActionConfigure(claimAction);
        configure(oidcOption);
        Assert.Single(oidcOption.ClaimActions);
        Assert.Contains(oidcOption.ClaimActions, x => x.ClaimType == claimAction.ClaimType && x.ValueType == ClaimValueTypes.String);
        var action = oidcOption.ClaimActions.FirstOrDefault(x => x.ClaimType == claimAction.ClaimType);
        Assert.NotNull(action);
        var jsonElement = JsonDocument.Parse("""
                                             {
                                               "role": ["admin", "test"]
                                             }
                                             """).RootElement.Clone();
        var claimIdentity = new ClaimsIdentity();
        action.Run(jsonElement, claimIdentity, "test");
        Assert.Equal(2, claimIdentity.Claims.Count());
        Assert.True(claimIdentity.HasClaim("role", "admin"));
        Assert.True(claimIdentity.HasClaim("role", "test"));
    }

    [Fact]
    public void GetOidcClaimActionConfigure_MapUniqueJsonKeyTest()
    {
        var claimAction = new ClaimAction
        {
            ClaimType = "name",
            JsonKey = "name",
            IsUnique = true
        };
        var oidcOption = new OpenIdConnectOptions();
        oidcOption.ClaimActions.Clear();
        var configure = DashboardWebApplication.GetOidcClaimActionConfigure(claimAction);
        configure(oidcOption);
        Assert.Single(oidcOption.ClaimActions);
        Assert.Contains(oidcOption.ClaimActions, x => x.ClaimType == claimAction.ClaimType && x.ValueType == ClaimValueTypes.String);
        var action = oidcOption.ClaimActions.FirstOrDefault(x => x.ClaimType == claimAction.ClaimType);
        Assert.NotNull(action);
        var jsonElement = JsonDocument.Parse("""
                                             {
                                               "name": "test"
                                             }
                                             """).RootElement.Clone();
        var claimIdentity = new ClaimsIdentity(
        [
            new Claim("name", "test")
        ]);
        action.Run(jsonElement, claimIdentity, "test");
        Assert.Single(claimIdentity.Claims);
        Assert.True(claimIdentity.HasClaim("name", "test"));

        var emptyClaimIdentity = new ClaimsIdentity();
        action.Run(jsonElement, emptyClaimIdentity, "test");
        Assert.Single(emptyClaimIdentity.Claims);
        Assert.True(emptyClaimIdentity.HasClaim("name", "test"));
    }

    [Fact]
    public void GetOidcClaimActionConfigure_MapJsonSubKeyTest()
    {
        var claimAction = new ClaimAction
        {
            ClaimType = "name",
            JsonKey = "profile",
            SubKey = "name"
        };
        var oidcOption = new OpenIdConnectOptions();
        oidcOption.ClaimActions.Clear();
        var configure = DashboardWebApplication.GetOidcClaimActionConfigure(claimAction);
        configure(oidcOption);
        Assert.Single(oidcOption.ClaimActions);
        Assert.Contains(oidcOption.ClaimActions, x => x.ClaimType == claimAction.ClaimType && x.ValueType == ClaimValueTypes.String);
        var action = oidcOption.ClaimActions.FirstOrDefault(x => x.ClaimType == claimAction.ClaimType);
        Assert.NotNull(action);
        var jsonElement = JsonDocument.Parse("""
                                             {
                                               "profile": {
                                                 "name": "test"
                                               }
                                             }
                                             """).RootElement.Clone();
        var claimIdentity = new ClaimsIdentity(
        [
            new Claim("name", "test")
        ]);
        action.Run(jsonElement, claimIdentity, "test");
        Assert.Equal(2, claimIdentity.Claims.Count());
        Assert.True(claimIdentity.HasClaim("name", "test"));

        var emptyClaimIdentity = new ClaimsIdentity();
        action.Run(jsonElement, emptyClaimIdentity, "test");
        Assert.Single(emptyClaimIdentity.Claims);
        Assert.True(emptyClaimIdentity.HasClaim("name", "test"));
    }

    [Fact]
    public void GetOidcClaimActionConfigure_MapJsonKey_ValueTypeTest()
    {
        var claimAction = new ClaimAction
        {
            ClaimType = "sub",
            JsonKey = "userId",
            ValueType = ClaimValueTypes.Integer,
            IsUnique = true
        };
        var oidcOption = new OpenIdConnectOptions();
        oidcOption.ClaimActions.Clear();
        var configure = DashboardWebApplication.GetOidcClaimActionConfigure(claimAction);
        configure(oidcOption);
        Assert.Single(oidcOption.ClaimActions);
        Assert.Contains(oidcOption.ClaimActions, x => x.ClaimType == claimAction.ClaimType && x.ValueType == claimAction.ValueType);
        var action = oidcOption.ClaimActions.FirstOrDefault(x => x.ClaimType == claimAction.ClaimType);
        Assert.NotNull(action);
        var jsonElement = JsonDocument.Parse("""
                                             {
                                               "userId": "1"
                                             }
                                             """).RootElement.Clone();
        var claimIdentity = new ClaimsIdentity();
        action.Run(jsonElement, claimIdentity, "test");
        Assert.NotEmpty(claimIdentity.Claims);
        Assert.True(claimIdentity.HasClaim("sub", "1"));
    }

    #endregion
}
