// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.CloudProviders;

public class ConflictingCredentialsTests
{
    private const string SubA = "11111111-1111-1111-1111-111111111111";
    private const string SubB = "22222222-2222-2222-2222-222222222222";
    private const string TenantId = "33333333-3333-3333-3333-333333333333";
    private const string ClientId = "44444444-4444-4444-4444-444444444444";
    private const string Account = "123456789012";
    private const string Region = "us-east-1";

    [Fact]
    public void DifferentAzureCredentialsAcrossEnvironments_Throws()
    {
        using var builder = Aspire.Hosting.Utils.TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var secret = builder.AddParameter("s", "v", secret: true);

        builder.AddRadiusEnvironment("dev")
            .WithAzureProvider(SubA, "rg-dev", azure => azure.WithServicePrincipal(TenantId, ClientId, secret));
        builder.AddRadiusEnvironment("prod")
            .WithAzureProvider(SubB, "rg-prod", azure => azure.WithWorkloadIdentity(TenantId, ClientId));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var ex = Assert.Throws<InvalidOperationException>(
            () => RadCredentialRegisterStep.ValidateNoConflictingInstallationCredentials(model));
        Assert.Contains("ASPIRERADIUS011", ex.Message);
        Assert.Contains("Azure", ex.Message);
    }

    [Fact]
    public void SharedAzureCredentialAcrossEnvironments_DifferentScopes_DoesNotThrow()
    {
        using var builder = Aspire.Hosting.Utils.TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var secret = builder.AddParameter("s", "v", secret: true);

        builder.AddRadiusEnvironment("dev")
            .WithAzureProvider(SubA, "rg-dev", azure => azure.WithServicePrincipal(TenantId, ClientId, secret));
        builder.AddRadiusEnvironment("prod")
            .WithAzureProvider(SubB, "rg-prod", azure => azure.WithServicePrincipal(TenantId, ClientId, secret));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        RadCredentialRegisterStep.ValidateNoConflictingInstallationCredentials(model);
    }

    [Fact]
    public void DifferentAwsCredentialsAcrossEnvironments_Throws()
    {
        using var builder = Aspire.Hosting.Utils.TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddRadiusEnvironment("dev")
            .WithAwsProvider(Account, Region, aws => aws.WithIrsa($"arn:aws:iam::{Account}:role/dev-role"));
        builder.AddRadiusEnvironment("prod")
            .WithAwsProvider(Account, Region, aws => aws.WithIrsa($"arn:aws:iam::{Account}:role/prod-role"));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var ex = Assert.Throws<InvalidOperationException>(
            () => RadCredentialRegisterStep.ValidateNoConflictingInstallationCredentials(model));
        Assert.Contains("ASPIRERADIUS011", ex.Message);
        Assert.Contains("AWS", ex.Message);
    }

    [Fact]
    public void SameAzurePrincipalDifferentGuidCasing_DoesNotThrow()
    {
        using var builder = Aspire.Hosting.Utils.TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var secret = builder.AddParameter("s", "v", secret: true);

        builder.AddRadiusEnvironment("dev")
            .WithAzureProvider(SubA, "rg-dev", azure => azure.WithServicePrincipal(TenantId, ClientId, secret));
        builder.AddRadiusEnvironment("prod")
            .WithAzureProvider(SubB, "rg-prod", azure => azure.WithServicePrincipal(TenantId.ToUpperInvariant(), ClientId.ToUpperInvariant(), secret));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        RadCredentialRegisterStep.ValidateNoConflictingInstallationCredentials(model);
    }

    [Fact]
    public void SingleEnvironmentWithBothProviders_DoesNotThrow()
    {
        using var builder = Aspire.Hosting.Utils.TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var secret = builder.AddParameter("s", "v", secret: true);

        builder.AddRadiusEnvironment("prod")
            .WithAzureProvider(SubA, "rg", azure => azure.WithServicePrincipal(TenantId, ClientId, secret))
            .WithAwsProvider(Account, Region, aws => aws.WithIrsa($"arn:aws:iam::{Account}:role/r"));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        RadCredentialRegisterStep.ValidateNoConflictingInstallationCredentials(model);
    }
}
