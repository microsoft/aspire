// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Experimental: the secret-store APIs are under test.
#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

public class InlineSecretStoreBicepTests
{
    private static string GenerateStoreBicep(Action<IResourceBuilder<RadiusEnvironmentResource>> configure)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env = builder.AddRadiusEnvironment("radius");
        configure(env);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        return new RadiusBicepPublishingContext(radiusEnv).GenerateBicep(model);
    }

    [Fact]
    public void InlineBasicAuthentication_EmitsSecureParamReferences_NoLiteral()
    {
        var bicep = GenerateStoreBicep(env =>
        {
            var user = env.ApplicationBuilder.AddParameter("db-user", secret: true);
            var pass = env.ApplicationBuilder.AddParameter("db-pass", secret: true);
            env.WithSecretStore("db-creds", RadiusSecretStoreType.BasicAuthentication, s =>
                s.WithData(d => { d.Add("username", user); d.Add("password", pass); }));
        });

        // The secretStores resource is emitted with the correct type.
        Assert.Contains("Applications.Core/secretStores@2023-10-01-preview", bicep);
        Assert.Contains("type: 'basicAuthentication'", bicep);

        // Values reference valueless @secure() params (never literals).
        Assert.Contains("@secure()", bicep);
        Assert.Contains("param db_user string", bicep);
        Assert.Contains("param db_pass string", bicep);
        Assert.Contains("db_user", bicep);
        Assert.Contains("db_pass", bicep);
    }

    [Fact]
    public void InlineCertificate_EmitsBase64Encoding()
    {
        var bicep = GenerateStoreBicep(env =>
        {
            var crt = env.ApplicationBuilder.AddParameter("crt", secret: true);
            var key = env.ApplicationBuilder.AddParameter("key", secret: true);
            env.WithSecretStore("tls", RadiusSecretStoreType.Certificate, s =>
                s.WithData(d => { d.Add("tls.crt", crt); d.Add("tls.key", key); }));
        });

        Assert.Contains("Applications.Core/secretStores@2023-10-01-preview", bicep);
        Assert.Contains("type: 'certificate'", bicep);
        // certificate keys are auto-encoded base64 (Radius enforces it).
        Assert.Contains("base64", bicep);
    }

    [Fact]
    public void NoSecretStore_DoesNotEmitSecretStores()
    {
        var bicep = GenerateStoreBicep(env => env.ApplicationBuilder.AddContainer("api", "img", "latest"));

        Assert.DoesNotContain("Applications.Core/secretStores", bicep);
    }
}
