// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Experimental: the secret-store APIs are under test.
#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Secrets;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Secrets;

public class SecretStoreValidationTests
{
    private static void WithModel(
        Action<IDistributedApplicationBuilder> configure,
        Action<DistributedApplicationModel> assert)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        configure(builder);
        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        assert(model);
    }

    private static string Validate(DistributedApplicationModel model)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RadiusSecretStoreValidation.Validate(model));
        return ex.Message;
    }

    [Fact]
    public void NoStores_IsNoOp()
    {
        WithModel(
            b =>
            {
                b.AddRadiusEnvironment("radius");
                b.AddContainer("api", "img", "latest");
            },
            RadiusSecretStoreValidation.Validate); // no throw
    }

    [Fact]
    public void MissingRequiredKey_Throws_ASPIRERADIUS040()
    {
        WithModel(
            b =>
            {
                b.AddRadiusEnvironment("radius");
                // certificate requires tls.crt and tls.key; only tls.crt is declared.
                b.AddRadiusSecretStore("tls", RadiusSecretStoreType.Certificate)
                    .FromExistingSecret("app/tls", "tls.crt");
            },
            m => Assert.Contains("ASPIRERADIUS040", Validate(m)));
    }

    [Fact]
    public void NoPopulationMode_Throws_ASPIRERADIUS041()
    {
        WithModel(
            b =>
            {
                b.AddRadiusEnvironment("radius");
                b.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic);
            },
            m => Assert.Contains("ASPIRERADIUS041", Validate(m)));
    }

    [Fact]
    public void BothPopulationModes_Throw_ASPIRERADIUS041()
    {
        WithModel(
            b =>
            {
                var p = b.AddParameter("p", secret: true);
                b.AddRadiusEnvironment("radius");
                b.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
                    .WithData(d => d.Add("k", p))
                    .FromExistingSecret("app/s", "k");
            },
            m => Assert.Contains("ASPIRERADIUS041", Validate(m)));
    }

    [Fact]
    public void NonSecretInlineBinding_Throws_ASPIRERADIUS042()
    {
        WithModel(
            b =>
            {
                var notSecret = b.AddParameter("plain");
                b.AddRadiusEnvironment("radius");
                b.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
                    .WithData(d => d.Add("k", notSecret));
            },
            m => Assert.Contains("ASPIRERADIUS042", Validate(m)));
    }

    [Fact]
    public void DuplicateKey_Throws_ASPIRERADIUS043()
    {
        WithModel(
            b =>
            {
                b.AddRadiusEnvironment("radius");
                b.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
                    .FromExistingSecret("app/s", "dup", "dup");
            },
            m => Assert.Contains("ASPIRERADIUS043", Validate(m)));
    }

    [Fact]
    public void RawEncodingOnCertificate_Throws_ASPIRERADIUS047()
    {
        WithModel(
            b =>
            {
                var crt = b.AddParameter("crt", secret: true);
                var key = b.AddParameter("key", secret: true);
                b.AddRadiusEnvironment("radius");
                b.AddRadiusSecretStore("tls", RadiusSecretStoreType.Certificate)
                    .WithData(d =>
                    {
                        d.Add("tls.crt", crt, encoding: RadiusSecretStoreEncoding.Raw);
                        d.Add("tls.key", key);
                    });
            },
            m => Assert.Contains("ASPIRERADIUS047", Validate(m)));
    }

    [Fact]
    public void DuplicateStoreName_IsRejectedByModel()
    {
        // Aspire enforces unique, grammar-restricted resource names, so a duplicate store name
        // can never reach the ASPIRERADIUS048 gate — the model rejects it at Add time. The gate
        // remains as a defensive check for any future routing path that bypasses the model.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var p = builder.AddParameter("p", secret: true);
        builder.AddRadiusEnvironment("radius");
        builder.AddRadiusSecretStore("db-creds", RadiusSecretStoreType.Generic).WithData(d => d.Add("k", p));

        Assert.ThrowsAny<Exception>(() =>
            builder.AddRadiusSecretStore("db-creds", RadiusSecretStoreType.Generic));
    }

    [Fact]
    public void ConsumerKindIncompatibleWithStoreType_Throws_ASPIRERADIUS051()
    {
        WithModel(
            b =>
            {
                var env = b.AddRadiusEnvironment("radius");
                // Bicep-registry auth requires a basicAuthentication store; a Generic store is incompatible.
                var store = b.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
                    .FromExistingSecret("app/s", "k");
                env.WithBicepRegistryAuthentication("myregistry.azurecr.io", store);
            },
            m => Assert.Contains("ASPIRERADIUS051", Validate(m)));
    }

    [Fact]
    public void EnvSecretReferencesUndeclaredKey_Throws_ASPIRERADIUS052()
    {
        WithModel(
            b =>
            {
                var pass = b.AddParameter("p", secret: true);
                var env = b.AddRadiusEnvironment("radius");
                var store = b.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
                    .WithData(d => d.Add("password", pass));
                // 'missing' is not a declared key on the store.
                env.WithRecipeEnvironmentSecret("DB_PASSWORD", store, "missing");
            },
            m => Assert.Contains("ASPIRERADIUS052", Validate(m)));
    }

    [Fact]
    public void TerraformProviderSecretConsumer_Throws_ASPIRERADIUS053()
    {
        WithModel(
            b =>
            {
                var env = b.AddRadiusEnvironment("radius");
                var store = b.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
                    .FromExistingSecret("app/s", "k");
                env.WithTerraformProviderSecret("azurerm", store);
            },
            m => Assert.Contains("ASPIRERADIUS053", Validate(m)));
    }

    [Fact]
    public void ApplicationScopedBareExistingSecretReference_Throws_ASPIRERADIUS055()
    {
        WithModel(
            b =>
            {
                b.AddRadiusEnvironment("radius");
                // Application-scoped store with a bare '<name>' reference has no owning environment
                // to default the namespace from.
                b.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
                    .FromExistingSecret("bare-name", "k");
            },
            m => Assert.Contains("ASPIRERADIUS055", Validate(m)));
    }

    [Fact]
    public void ApplicationScopedQualifiedExistingSecretReference_IsAllowed()
    {
        WithModel(
            b =>
            {
                b.AddRadiusEnvironment("radius");
                b.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
                    .FromExistingSecret("prod/my-secret", "k");
            },
            RadiusSecretStoreValidation.Validate); // no throw
    }
}
