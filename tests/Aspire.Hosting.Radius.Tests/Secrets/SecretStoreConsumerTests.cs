// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Experimental: the secret-store APIs are under test.

using Aspire.Hosting.Radius.Annotations;
using Aspire.Hosting.Radius.Secrets;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Radius.Tests.Secrets;

public class SecretStoreConsumerTests
{
    [Fact]
    public void ConsumerMethods_RecordConsumersOnEnvironmentAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var user = builder.AddParameter("u", secret: true);
        var pass = builder.AddParameter("p", secret: true);
        var env = builder.AddRadiusEnvironment("radius");
        var store = builder.AddRadiusSecretStore("registry-creds", RadiusSecretStoreType.BasicAuthentication)
            .WithData(d => { d.Add("username", user); d.Add("password", pass); });

        env.WithBicepRegistryAuthentication("myregistry.azurecr.io", store)
           .WithTerraformGitAuthentication("github.com", store)
           .WithRecipeEnvironmentSecret("DB_PASSWORD", store, "password");

        var annotation = env.Resource.Annotations.OfType<RadiusSecretStoresAnnotation>().Single();
        Assert.Equal(3, annotation.Consumers.Count);

        var registry = annotation.Consumers.Single(c => c.Kind == RadiusSecretStoreConsumerKind.BicepRegistryAuth);
        Assert.Equal("myregistry.azurecr.io", registry.Selector);
        Assert.Same(store.Resource, registry.Store);

        var git = annotation.Consumers.Single(c => c.Kind == RadiusSecretStoreConsumerKind.TerraformGitPat);
        Assert.Equal("github.com", git.Selector);

        var envSecret = annotation.Consumers.Single(c => c.Kind == RadiusSecretStoreConsumerKind.EnvSecret);
        Assert.Equal("DB_PASSWORD", envSecret.Selector);
        Assert.Equal("password", envSecret.Key);
    }

    [Fact]
    public void WithRecipeEnvironmentSecret_EmptyKey_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env = builder.AddRadiusEnvironment("radius");
        var store = builder.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic);

        Assert.Throws<ArgumentException>(() => env.WithRecipeEnvironmentSecret("VAR", store, "  "));
    }

    [Fact]
    public void WithTlsCertificate_ApplicationScopedStore_Throws_ASPIRERADIUS054()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("radius");
        // Application-scoped (AddRadiusSecretStore) store has no owning environment, so gateway TLS
        // has nowhere deterministic to record the reference.
        var store = builder.AddRadiusSecretStore("tls", RadiusSecretStoreType.Certificate);
        var gateway = builder.AddContainer("gw", "img", "latest");

        var ex = Assert.Throws<InvalidOperationException>(() => gateway.WithTlsCertificate(store));
        Assert.Contains("ASPIRERADIUS054", ex.Message);
    }

    [Fact]
    public void WithTlsCertificate_EnvironmentScopedStore_RecordsConsumerOnOwningEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var crt = builder.AddParameter("crt", secret: true);
        var key = builder.AddParameter("key", secret: true);
        var env = builder.AddRadiusEnvironment("radius");
        var gateway = builder.AddContainer("gw", "img", "latest");

        env.WithSecretStore("tls", RadiusSecretStoreType.Certificate, store =>
        {
            store.WithData(d => { d.Add("tls.crt", crt); d.Add("tls.key", key); });
            gateway.WithTlsCertificate(store);
        });

        var annotation = env.Resource.Annotations.OfType<RadiusSecretStoresAnnotation>().Single();
        var tls = annotation.Consumers.Single(c => c.Kind == RadiusSecretStoreConsumerKind.GatewayTls);
        Assert.Equal("gw", tls.Selector);
    }
}
