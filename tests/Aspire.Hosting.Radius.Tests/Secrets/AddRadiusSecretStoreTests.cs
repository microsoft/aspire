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

        // The store name must match Aspire's resource-name grammar (letters/digits/hyphens, start with
        // a letter, no consecutive/trailing hyphen, <= 64 chars) so that publish-mode AddResource does
        // not reject a name that run mode accepted. Underscores, dots, digit-start, and over-length were
        // previously accepted here but rejected by AddResource — a mode-dependent contract.
        foreach (var invalid in new[] { "bad/name", "  ", "under_score", "dot.name", "1leading", "-leading", "trailing-", "a--b", new string('a', 65), "CON", "COM1" })
        {
            Assert.Throws<ArgumentException>(() =>
                builder.AddRadiusSecretStore(invalid, RadiusSecretStoreType.Generic));
        }
    }

    [Fact]
    public void ValidStoreName_IsAccepted()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("radius");

        foreach (var valid in new[] { "s", "db-creds", "a1", "a-b-c", new string('a', 64) })
        {
            var store = builder.AddRadiusSecretStore(valid, RadiusSecretStoreType.Generic);
            Assert.Equal(valid, store.Resource.Name);
        }
    }

    [Fact]
    public void WithDataKeyParameterOverload_BindsSingleKey()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var pass = builder.AddParameter("db-pass", secret: true);
        var store = builder.AddRadiusSecretStore("db-creds", RadiusSecretStoreType.Generic)
            .WithData("password", pass);

        Assert.True(store.Resource.Population.HasInlineData);
        Assert.True(store.Resource.Population.Data.ContainsKey("password"));
    }

    [Fact]
    public void WithDataKeyParameterOverload_SecondCall_ThrowsAtCallSite_ASPIRERADIUS065()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var pass = builder.AddParameter("db-pass", secret: true);
        var store = builder.AddRadiusSecretStore("db-creds", RadiusSecretStoreType.Generic)
            .WithData("username", pass);

        var ex = Assert.Throws<InvalidOperationException>(() => store.WithData("password", pass));
        Assert.Contains("ASPIRERADIUS065", ex.Message);
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
    public void DuplicateInlineDataKey_ThrowsAtCallSite_ASPIRERADIUS043()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var pass = builder.AddParameter("db-pass", secret: true);
        var store = builder.AddRadiusSecretStore("db-creds", RadiusSecretStoreType.Generic);

        // A duplicate inline key must be rejected rather than silently overwriting the earlier binding.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            store.WithData(d => { d.Add("password", pass); d.Add("password", pass); }));
        Assert.Contains("ASPIRERADIUS043", ex.Message);
    }

    [Theory]
    [InlineData("/secret")]
    [InlineData("app/")]
    [InlineData("a/b/c")]
    [InlineData("App_Creds")]
    [InlineData("UPPER")]
    [InlineData("APP/db-creds")]
    public void InvalidExistingSecretReference_ThrowsAtCallSite(string reference)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var store = builder.AddRadiusSecretStore("tls", RadiusSecretStoreType.Generic);

        Assert.Throws<ArgumentException>(() => store.WithExistingSecret(reference, "k"));
    }

    [Theory]
    [InlineData("db-creds")]
    [InlineData("app/db-creds")]
    [InlineData("app/db.creds")]
    public void ValidExistingSecretReference_IsAccepted(string reference)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var store = builder.AddRadiusSecretStore("tls", RadiusSecretStoreType.Generic)
            .WithExistingSecret(reference, "k");

        Assert.Equal(reference, store.Resource.Population.ResourceReference);
    }

    [Fact]
    public void EmptyExistingSecretKey_ThrowsAtCallSite()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var store = builder.AddRadiusSecretStore("tls", RadiusSecretStoreType.Generic);

        Assert.Throws<ArgumentException>(() => store.WithExistingSecret("app/tls", "ok", "  "));
    }

    [Fact]
    public void RepeatedSameModePopulation_ThrowsAtCallSite_ASPIRERADIUS065()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var store = builder.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
            .WithExistingSecret("app/s", "k1");

        // Calling the same population mode a second time previously silently appended keys; it now
        // fails fast because a store must declare exactly one population mode, once.
        var ex = Assert.Throws<InvalidOperationException>(() => store.WithExistingSecret("app/s", "k2"));
        Assert.Contains("ASPIRERADIUS065", ex.Message);
    }

    [Fact]
    public void CrossModePopulation_ThrowsAtCallSite_ASPIRERADIUS065()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var pass = builder.AddParameter("p", secret: true);
        var store = builder.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
            .WithData(d => d.Add("k", pass));

        var ex = Assert.Throws<InvalidOperationException>(() => store.WithExistingSecret("app/s", "k"));
        Assert.Contains("ASPIRERADIUS065", ex.Message);
    }

    [Fact]
    public void MultipleKeysInSingleCall_IsAllowed()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var store = builder.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
            .WithExistingSecret("app/s", "k1", "k2", "k3");

        Assert.Equal(["k1", "k2", "k3"], store.Resource.Population.Keys);
    }

    [Fact]
    public void InvalidKeyInFirstCall_DoesNotPoisonCorrectedRetry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var store = builder.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic);

        // A first call that throws on an invalid key must not leave the population partially
        // assigned; otherwise the corrected retry would wrongly trip the single-population guard.
        Assert.Throws<ArgumentException>(() => store.WithExistingSecret("app/s", "ok", "  "));

        store.WithExistingSecret("app/s", "ok");
        Assert.Equal(["ok"], store.Resource.Population.Keys);
    }

    [Fact]
    public void InvalidReferenceInFirstCall_DoesNotPoisonCorrectedRetry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var store = builder.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic);

        // A first call that throws on an invalid reference must not leave HasExistingSecret set;
        // otherwise the corrected retry would wrongly trip the ASPIRERADIUS065 single-population guard.
        Assert.Throws<ArgumentException>(() => store.WithExistingSecret("BAD/REF", "ok"));

        store.WithExistingSecret("app/s", "ok");
        Assert.Equal("app/s", store.Resource.Population.ResourceReference);
        Assert.Equal(["ok"], store.Resource.Population.Keys);
    }

    [Theory]
    [InlineData("bad/key")]
    [InlineData("bad key")]
    [InlineData("bad:key")]
    public void InvalidSecretDataKey_OnExistingSecret_ThrowsAtCallSite_ASPIRERADIUS067(string key)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var store = builder.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic);

        var ex = Assert.Throws<ArgumentException>(() => store.WithExistingSecret("app/s", key));
        Assert.Contains("ASPIRERADIUS067", ex.Message);
    }

    [Theory]
    [InlineData("bad/key")]
    [InlineData("bad key")]
    [InlineData("bad:key")]
    public void InvalidSecretDataKey_OnInlineData_ThrowsAtCallSite_ASPIRERADIUS067(string key)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var pass = builder.AddParameter("p", secret: true);
        var store = builder.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic);

        var ex = Assert.Throws<ArgumentException>(() => store.WithData(d => d.Add(key, pass)));
        Assert.Contains("ASPIRERADIUS067", ex.Message);
    }

    [Theory]
    [InlineData("valid-key")]
    [InlineData("valid.key")]
    [InlineData("valid_key")]
    [InlineData("Valid0")]
    public void ValidSecretDataKey_IsAccepted(string key)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var store = builder.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
            .WithExistingSecret("app/s", key);

        Assert.Equal([key], store.Resource.Population.Keys);
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WithMaterializationTimeout_RejectsNonPositive(int seconds)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var store = builder.AddRadiusSecretStore("tls", RadiusSecretStoreType.Generic);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.WithMaterializationTimeout(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void WithMaterializationTimeout_RejectsAboveTimerRange()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var store = builder.AddRadiusSecretStore("tls", RadiusSecretStoreType.Generic);

        // Values above int.MaxValue milliseconds cannot be represented by CancellationTokenSource.CancelAfter
        // and would otherwise pass config-time validation only to throw mid-deploy.
        Assert.Throws<ArgumentOutOfRangeException>(() => store.WithMaterializationTimeout(TimeSpan.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.WithMaterializationTimeout(TimeSpan.FromMilliseconds((double)int.MaxValue + 1)));
    }

    [Fact]
    public void WithMaterializationTimeout_AcceptsMaxSupported()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var store = builder.AddRadiusSecretStore("tls", RadiusSecretStoreType.Generic);
        var max = TimeSpan.FromMilliseconds(int.MaxValue);

        store.WithMaterializationTimeout(max);

        Assert.Equal(max, store.Resource.MaterializationTimeout);
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
