// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Azure.EntraId.Tests.TestServices;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Azure.EntraId.Tests;

public class LocalDeveloperCertificateProvisioningTests
{
    [Fact]
    public async Task Checksum_ReusesCachedCertificateAndChangesWhenANewRunRenewsIt()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var clock = new CertificateTestTimeProvider();
        builder.Services.AddSingleton<TimeProvider>(clock);
        var entra = builder.AddEntraIdApplication("entra-web").RunWithLocalDeveloperCertificateCredential();
        entra.Resource.Parameters[AzureBicepResource.KnownParameters.TenantResourceSuffix] = "dev";
        using var app = builder.Build();
        var credential = Assert.IsType<EntraIdLocalDeveloperCertificateCredential>(Assert.Single(entra.Resource.ClientCredentials));
        var original = await credential.GetCertificateAsync(app.Services, entra.Resource, TestContext.Current.CancellationToken);
        _ = entra.Resource.GetBicepTemplateString();
        var parameters = new JsonObject();
        await BicepUtilities.SetParametersAsync(parameters, entra.Resource, builder.ExecutionContext, cancellationToken: TestContext.Current.CancellationToken);
        var initialChecksum = BicepUtilities.GetChecksum(entra.Resource, parameters, scope: null);
        var section = new DeploymentStateSection("entra-web", new JsonObject { ["Parameters"] = parameters.ToJsonString() }, 0);

        Assert.Equal(initialChecksum, await BicepUtilities.GetCurrentChecksumAsync(entra.Resource, section, builder.ExecutionContext, NullLogger.Instance, TestContext.Current.CancellationToken));
        entra.Resource.ClientCredentials.Clear();
        var reusedCredential = new EntraIdLocalDeveloperCertificateCredential(entra.Resource, builder.ExecutionContext);
        entra.Resource.ClientCredentials.Add(reusedCredential);
        Assert.Equal(initialChecksum, await BicepUtilities.GetCurrentChecksumAsync(entra.Resource, section, builder.ExecutionContext, NullLogger.Instance, TestContext.Current.CancellationToken));
        var reused = await reusedCredential.GetCertificateAsync(app.Services, entra.Resource, TestContext.Current.CancellationToken);
        Assert.Equal(original.PublicCertificate, reused.PublicCertificate);
        Assert.Equal(original.Base64EncodedValue, reused.Base64EncodedValue);
        clock.UtcNow = clock.UtcNow.AddDays(1);
        // A new credential instance represents selection at the next AppHost startup.
        entra.Resource.ClientCredentials.Clear();
        var nextRun = new EntraIdLocalDeveloperCertificateCredential(entra.Resource, builder.ExecutionContext);
        entra.Resource.ClientCredentials.Add(nextRun);

        var renewedChecksum = await BicepUtilities.GetCurrentChecksumAsync(entra.Resource, section, builder.ExecutionContext, NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.NotNull(renewedChecksum);
        Assert.NotEqual(initialChecksum, renewedChecksum);
        var renewed = await nextRun.GetCertificateAsync(app.Services, entra.Resource, TestContext.Current.CancellationToken);
        Assert.NotEqual(original.Thumbprint, renewed.Thumbprint);
        await Verify(parameters.ToJsonString(), "json");
    }
}
