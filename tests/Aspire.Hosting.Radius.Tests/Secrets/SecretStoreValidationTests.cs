// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Experimental: the secret-store APIs are under test.
#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Radius.Secrets;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Secrets;

public class SecretStoreValidationTests : IDisposable
{
    private readonly string _sealedManifestDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "radius-secret-store-validation-tests",
        Guid.NewGuid().ToString("N"));

    public SecretStoreValidationTests() => Directory.CreateDirectory(_sealedManifestDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_sealedManifestDirectory))
        {
            Directory.Delete(_sealedManifestDirectory, recursive: true);
        }
    }

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

    private static void BuildOptions(DistributedApplicationModel model)
    {
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        _ = new RadiusBicepPublishingContext(radiusEnv).BuildOptions(model);
    }

    private string WriteSealedSecretManifest(string? namespaceName)
    {
        var path = Path.Combine(_sealedManifestDirectory, $"{Guid.NewGuid():N}.sealed.yaml");
        var namespaceYaml = namespaceName is null
            ? string.Empty
            : $"  namespace: {namespaceName}\n";

        File.WriteAllText(
            path,
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            namespaceYaml +
            "spec:\n" +
            "  encryptedData:\n" +
            "    password: AgBcipher\n");

        return path;
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
    public void AppScopedStore_WithoutEnvironment_Throws_ASPIRERADIUS068()
    {
        WithModel(
            b => b.AddRadiusSecretStore("appsecrets", RadiusSecretStoreType.Generic)
                  .WithExistingSecret("app/appsecrets", "k"),
            model =>
            {
                var ex = Assert.Throws<InvalidOperationException>(
                    () => RadiusSecretStoreValidation.ValidateHasEnvironment(model));
                Assert.Contains("ASPIRERADIUS068", ex.Message);
                Assert.Contains("appsecrets", ex.Message);
            });
    }

    [Fact]
    public void AppScopedStore_WithEnvironment_HasEnvironmentIsNoOp()
    {
        WithModel(
            b =>
            {
                b.AddRadiusEnvironment("radius");
                b.AddRadiusSecretStore("appsecrets", RadiusSecretStoreType.Generic)
                 .WithExistingSecret("app/appsecrets", "k");
            },
            RadiusSecretStoreValidation.ValidateHasEnvironment); // no throw
    }

    [Fact]
    public async Task AppScopedStore_WithoutEnvironment_RegistersValidationGate()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var store = builder.AddRadiusSecretStore("appsecrets", RadiusSecretStoreType.Generic)
            .WithExistingSecret("app/appsecrets", "k");

        // The gate is registered on the store resource itself (not an environment) so it runs even
        // when the model has no Radius environment; verify the pipeline step is present and wired up.
        var annotation = Assert.Single(store.Resource.Annotations.OfType<PipelineStepAnnotation>());
        var steps = (await annotation.CreateStepsAsync(new PipelineStepFactoryContext
        {
            PipelineContext = null!,
            Resource = store.Resource,
        })).ToList();
        var gate = Assert.Single(steps);
        Assert.Equal("validate-radius-app-scoped-store-appsecrets", gate.Name);
        Assert.Contains("publish", gate.RequiredBySteps);
        Assert.Contains("deploy", gate.RequiredBySteps);
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
                    .WithExistingSecret("app/tls", "tls.crt");
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
    public void BothPopulationModes_Throw_ASPIRERADIUS065()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var p = builder.AddParameter("p", secret: true);
        builder.AddRadiusEnvironment("radius");
        var store = builder.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
            .WithData(d => d.Add("k", p));

        // A second population call (cross-mode here) is now rejected at the call site with 065,
        // before the validation gate runs, so misuse fails immediately with a clear stack trace.
        var ex = Assert.Throws<InvalidOperationException>(() => store.WithExistingSecret("app/s", "k"));
        Assert.Contains("ASPIRERADIUS065", ex.Message);
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
                    .WithExistingSecret("app/s", "dup", "dup");
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
                    .WithExistingSecret("app/s", "k");
                env.WithBicepRegistryAuthentication("myregistry.azurecr.io", store);
            },
            m => Assert.Contains("ASPIRERADIUS051", Validate(m)));
    }

    [Fact]
    public void TerraformGitPatStoreMissingPatKey_Throws_ASPIRERADIUS051()
    {
        WithModel(
            b =>
            {
                var env = b.AddRadiusEnvironment("radius");
                var user = b.AddParameter("u", secret: true);
                var pass = b.AddParameter("p", secret: true);
                // A basicAuthentication (username/password) store has no 'pat' key, so Radius cannot
                // obtain a PAT for Terraform Git auth.
                var store = b.AddRadiusSecretStore("git-creds", RadiusSecretStoreType.BasicAuthentication)
                    .WithData(d => { d.Add("username", user); d.Add("password", pass); });
                env.WithTerraformGitAuthentication("github.com", store);
            },
            m => Assert.Contains("ASPIRERADIUS051", Validate(m)));
    }

    [Fact]
    public void TerraformGitPatStoreWithPatKey_IsValid()
    {
        WithModel(
            b =>
            {
                var env = b.AddRadiusEnvironment("radius");
                var user = b.AddParameter("u", secret: true);
                var pat = b.AddParameter("pat", secret: true);
                var store = b.AddRadiusSecretStore("git-creds", RadiusSecretStoreType.Generic)
                    .WithData(d => { d.Add("username", user); d.Add("pat", pat); });
                env.WithTerraformGitAuthentication("github.com", store);
            },
                RadiusSecretStoreValidation.Validate);
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
    public void MaterializationTimeoutOnNonSealedStore_Throws_ASPIRERADIUS062()
    {
        WithModel(
            b =>
            {
                var p = b.AddParameter("p", secret: true);
                b.AddRadiusEnvironment("radius");
                b.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
                    .WithData(d => d.Add("k", p))
                    .WithMaterializationTimeout(TimeSpan.FromSeconds(30));
            },
            m => Assert.Contains("ASPIRERADIUS062", Validate(m)));
    }

    [Fact]
    public void KeylessStoreWithKeySpecificEnvSecret_Throws_ASPIRERADIUS064()
    {
        WithModel(
            b =>
            {
                var env = b.AddRadiusEnvironment("radius");
                // Keyless existing store: WithExistingSecret with no key list declares no keys, so a
                // key-specific envSecrets consumer against it would emit a dangling reference.
                var store = b.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
                    .WithExistingSecret("prod/my-secret");
                env.WithRecipeEnvironmentSecret("DB_PASSWORD", store, "password");
            },
            m => Assert.Contains("ASPIRERADIUS064", Validate(m)));
    }

    [Fact]
    public void KeylessStoreWithoutKeySpecificConsumer_IsAllowed()
    {
        WithModel(
            b =>
            {
                b.AddRadiusEnvironment("radius");
                // A keyless existing store that no key-specific envSecrets consumer references
                // materializes its keys out-of-band and is intentionally left unchecked.
                b.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
                    .WithExistingSecret("prod/my-secret");
            },
            RadiusSecretStoreValidation.Validate); // no throw
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
                    .WithExistingSecret("bare-name", "k");
            },
            m => Assert.Contains("ASPIRERADIUS055", Validate(m)));
    }

    [Fact]
    public void ApplicationScopedSealedSecretWithoutExplicitNamespace_Throws_ASPIRERADIUS055()
    {
        var manifestPath = WriteSealedSecretManifest(namespaceName: null);

        WithModel(
            b =>
            {
                b.AddRadiusEnvironment("radius");
                b.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
                    .WithSealedSecret(manifestPath, "password");
            },
            m =>
            {
                var ex = Assert.Throws<InvalidOperationException>(() => BuildOptions(m));
                Assert.Contains("ASPIRERADIUS055", ex.Message);
                Assert.Contains("metadata.namespace", ex.Message);
            });
    }

    [Fact]
    public void ApplicationScopedSealedSecretWithExplicitNamespace_IsAllowed()
    {
        var manifestPath = WriteSealedSecretManifest("prod");

        WithModel(
            b =>
            {
                b.AddRadiusEnvironment("radius");
                b.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
                    .WithSealedSecret(manifestPath, "password");
            },
            BuildOptions);
    }

    [Fact]
    public void EnvironmentScopedSealedSecretWithoutExplicitNamespace_IsAllowed()
    {
        var manifestPath = WriteSealedSecretManifest(namespaceName: null);

        WithModel(
            b =>
            {
                var env = b.AddRadiusEnvironment("radius");
                env.WithSecretStore("s", RadiusSecretStoreType.Generic, s =>
                    s.WithSealedSecret(manifestPath, "password"));
            },
            BuildOptions);
    }

    [Fact]
    public void ApplicationScopedQualifiedExistingSecretReference_IsAllowed()
    {
        WithModel(
            b =>
            {
                b.AddRadiusEnvironment("radius");
                b.AddRadiusSecretStore("s", RadiusSecretStoreType.Generic)
                    .WithExistingSecret("prod/my-secret", "k");
            },
            RadiusSecretStoreValidation.Validate); // no throw
    }

    [Fact]
    public void ApplicationScopedStoreCollidesWithEnvironmentScopedIdentifier_Throws_ASPIRERADIUS048()
    {
        WithModel(
            b =>
            {
                var env = b.AddRadiusEnvironment("env1");
                env.WithSecretStore("radius", RadiusSecretStoreType.Generic, s =>
                    s.WithExistingSecret("db-creds", "password"));
                b.AddRadiusSecretStore("radiusenv", RadiusSecretStoreType.Generic)
                    .WithExistingSecret("prod/db-creds", "password");
            },
            m =>
            {
                var message = Validate(m);
                Assert.Contains("ASPIRERADIUS048", message);
                Assert.EndsWith("Diagnostic: ASPIRERADIUS048.", message);
            });
    }

    [Fact]
    public void EnvironmentScopedStoresInDifferentEnvironmentsMayShareIdentifier()
    {
        WithModel(
            b =>
            {
                var dev = b.AddRadiusEnvironment("dev");
                var prod = b.AddRadiusEnvironment("prod");
                dev.WithSecretStore("radius", RadiusSecretStoreType.Generic, s =>
                    s.WithExistingSecret("db-creds", "password"));
                prod.WithSecretStore("radiusenv", RadiusSecretStoreType.Generic, s =>
                    s.WithExistingSecret("db-creds", "password"));
            },
            RadiusSecretStoreValidation.Validate);
    }
}
