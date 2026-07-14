// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Experimental: the secret-store APIs are under test.
#pragma warning disable ASPIREPIPELINES001

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Secrets;

public class AddRadiusSecretStoreTests
{
    [Fact]
    public void AddRadiusSecretStore_AddsApplicationScopedResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("radius");
        var store = builder.AddRadiusSecretStore("db-creds", RadiusSecretStoreType.BasicAuthentication);

        Assert.Equal("db-creds", store.Resource.Name);
        Assert.Equal(RadiusSecretStoreType.BasicAuthentication, store.Resource.Type);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.Single(model.Resources.OfType<RadiusSecretStoreResource>());
    }

    [Fact]
    public void WithSecretStore_AddsEnvironmentScopedResource_OwnedByEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var pass = builder.AddParameter("db-pass", secret: true);
        var env = builder.AddRadiusEnvironment("radius");

        var returned = env.WithSecretStore("api-key", RadiusSecretStoreType.Generic, s =>
            s.WithData(d => d.Add("api-key", pass)));

        Assert.Same(env, returned);
        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var store = Assert.Single(model.Resources.OfType<RadiusSecretStoreResource>());
        Assert.Equal("api-key", store.Name);
        Assert.Equal(RadiusSecretStoreScope.Environment, store.Scope);
        Assert.Same(env.Resource, store.OwningEnvironment);
    }

    [Fact]
    public void EnvironmentScopedStore_EmitsEnvironmentReference_NotApplication()
    {
        var bicep = GenerateStoreBicep(env =>
        {
            var user = env.ApplicationBuilder.AddParameter("db-user", secret: true);
            var pass = env.ApplicationBuilder.AddParameter("db-pass", secret: true);
            env.WithSecretStore("db-creds", RadiusSecretStoreType.BasicAuthentication, s =>
                s.WithData(d => { d.Add("username", user); d.Add("password", pass); }));
        });

        Assert.Contains("Applications.Core/secretStores@2023-10-01-preview", bicep);
        Assert.Contains("type: 'basicAuthentication'", bicep);
        // Environment-scoped stores reference the environment, never `application:`.
        Assert.DoesNotContain("application:", bicep);
    }

    [Fact]
    public void ApplicationScopedStore_EmitsApplicationReference()
    {
        var bicep = GenerateStoreBicep(env =>
        {
            var user = env.ApplicationBuilder.AddParameter("db-user", secret: true);
            var pass = env.ApplicationBuilder.AddParameter("db-pass", secret: true);
            env.ApplicationBuilder
                .AddRadiusSecretStore("db-creds", RadiusSecretStoreType.BasicAuthentication)
                .WithData(d => { d.Add("username", user); d.Add("password", pass); });
        });

        Assert.Contains("Applications.Core/secretStores@2023-10-01-preview", bicep);
        Assert.Contains("application:", bicep);
    }

    [Fact]
    public void InvalidStoreName_ThrowsAtCallSite()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("radius");

        Assert.Throws<ArgumentException>(() =>
            builder.AddRadiusSecretStore("bad/name", RadiusSecretStoreType.Generic));
        Assert.Throws<ArgumentException>(() =>
            builder.AddRadiusSecretStore("  ", RadiusSecretStoreType.Generic));
    }

    [Fact]
    public void EmptyDataKey_ThrowsAtCallSite()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var pass = builder.AddParameter("db-pass", secret: true);
        var store = builder.AddRadiusSecretStore("db-creds", RadiusSecretStoreType.Generic);

        Assert.Throws<ArgumentException>(() => store.WithData(d => d.Add("  ", pass)));
    }

    [Fact]
    public void EmptyExistingSecretKey_ThrowsAtCallSite()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var store = builder.AddRadiusSecretStore("tls", RadiusSecretStoreType.Generic);

        Assert.Throws<ArgumentException>(() => store.WithExistingSecret("app/tls", "ok", "  "));
    }

    [Fact]
    public void AddRadiusSecretStore_IsGatedByExperimentalDiagnostic()
    {
        var method = typeof(RadiusSecretStoreExtensions)
            .GetMethod(nameof(RadiusSecretStoreExtensions.AddRadiusSecretStore));
        var attribute = method!.GetCustomAttributes(typeof(ExperimentalAttribute), inherit: false)
            .Cast<ExperimentalAttribute>()
            .Single();

        Assert.Equal("ASPIRERADIUS006", attribute.DiagnosticId);
    }

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
}
