// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Experimental: the secret-store APIs are under test.
#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

public class ExistingSecretStoreBicepTests
{
    private static string GenerateStoreBicep(
        string environmentNamespace,
        Action<IResourceBuilder<RadiusEnvironmentResource>> configure)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env = builder.AddRadiusEnvironment("radius").WithNamespace(environmentNamespace);
        configure(env);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        return new RadiusBicepPublishingContext(radiusEnv).GenerateBicep(model);
    }

    [Fact]
    public void FromExistingSecret_QualifiedName_EmitsResourceAndEmptyKeys()
    {
        var bicep = GenerateStoreBicep("default", env =>
            env.WithSecretStore("tls-cert", RadiusSecretStoreType.Certificate, s =>
                s.FromExistingSecret("app/tls-cert", "tls.crt", "tls.key")));

        Assert.Contains("Applications.Core/secretStores@2023-10-01-preview", bicep);
        Assert.Contains("type: 'certificate'", bicep);
        Assert.Contains("resource: 'app/tls-cert'", bicep);
        Assert.Contains("tls.crt", bicep);
        Assert.Contains("tls.key", bicep);
        // No secret value / @secure() param flows through Aspire for a referenced secret.
        Assert.DoesNotContain("@secure()", bicep);
    }

    [Fact]
    public void FromExistingSecret_BareName_DefaultsNamespaceFromEnvironment()
    {
        var bicep = GenerateStoreBicep("team-a", env =>
            env.WithSecretStore("tls-cert", RadiusSecretStoreType.Certificate, s =>
                s.FromExistingSecret("tls-cert", "tls.crt", "tls.key")));

        // A bare name is prefixed with the owning environment's namespace.
        Assert.Contains("resource: 'team-a/tls-cert'", bicep);
    }
}
