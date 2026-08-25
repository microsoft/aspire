// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Experimental: the secret-store APIs are exercised directly.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Radius.Publishing.Constructs;
using Aspire.Hosting.Radius.ResourceMapping;
using Aspire.Hosting.Utils;
using Azure.Provisioning;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

/// <summary>
/// Covers the passes that run <em>after</em> <c>ConfigureRadiusInfrastructure</c> callbacks, where a
/// callback has already had the last word and the publisher can only inspect the final state.
/// </summary>
public class PostCallbackSecretValidationTests : IDisposable
{
    private readonly string _manifestDirectory = Directory.CreateTempSubdirectory("radius-sealed-collision").FullName;

    public void Dispose() => Directory.Delete(_manifestDirectory, recursive: true);

    private string WriteSealedManifest(string name, string ns)
    {
        var path = Path.Combine(_manifestDirectory, $"{name}.sealed.yaml");
        File.WriteAllText(path,
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            $"  name: {name}\n" +
            $"  namespace: {ns}\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    username: AgByCIPHERTEXTONLYxx\n");
        return path;
    }

    private static string GenerateBicep(
        Action<IDistributedApplicationBuilder> configure,
        Action<RadiusInfrastructureOptions>? configureInfrastructure = null,
        Action<IResourceBuilder<RadiusEnvironmentResource>>? configureEnvironment = null)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var radius = builder.AddRadiusEnvironment("myenv");
        configureEnvironment?.Invoke(radius);
        if (configureInfrastructure is not null)
        {
            radius.ConfigureRadiusInfrastructure(configureInfrastructure);
        }

        configure(builder);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);

        return new RadiusBicepPublishingContext(radiusEnv).GenerateBicep(model, new RecordingLogger());
    }

    private static void AddContainerWithSecretEnvironment(IDistributedApplicationBuilder builder)
    {
        var password = builder.AddParameter("pw", secret: true);
        builder.AddContainer("api", "myapp/api:latest")
            .WithEnvironment("PW", password);
    }

    // ASPIRERADIUS088 — the final Kubernetes object name and data keys.

    /// <summary>
    /// The secrets recipe copies <c>SecretName</c> straight into <c>metadata.name</c>, and Radius
    /// does not validate it, so an invalid name compiles as Bicep and is only rejected by the API
    /// server at deploy.
    /// </summary>
    [Fact]
    public void CallbackSettingAnInvalidSecretName_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(
            AddContainerWithSecretEnvironment,
            opts => opts.SecuritySecrets[0].SecretName = "Not_A_Valid_Name"));

        Assert.Contains("ASPIRERADIUS088", ex.Message);
        Assert.Contains("Not_A_Valid_Name", ex.Message);
    }

    /// <summary>
    /// A name that is only knowable at deploy time cannot be checked, and rejecting it would
    /// contradict last-write-wins — the reference stays coherent because the consuming variable is
    /// re-synced to the same expression.
    /// </summary>
    [Fact]
    public void CallbackSettingASecretNameExpression_StillPublishes()
    {
        var bicep = GenerateBicep(
            AddContainerWithSecretEnvironment,
            opts =>
            {
                var parameter = new ProvisioningParameter("secretName", typeof(string));
                opts.Parameters.Add(parameter);
                opts.SecuritySecrets[0].SecretName = parameter;
            });

        Assert.Contains("name: secretName", bicep);
    }

    /// <summary>
    /// Data keys are copied verbatim into the Kubernetes <c>Secret</c>'s <c>data</c> map, which
    /// permits a much narrower alphabet than Bicep does.
    /// </summary>
    [Fact]
    public void CallbackAddingAnInvalidDataKeyToASecurititySecret_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(
            AddContainerWithSecretEnvironment,
            opts => opts.SecuritySecrets[0].Data["not a key"] = new RadiusSecuritySecretDataEntryConstruct
            {
                Encoding = "string",
                Value = "value",
            }));

        Assert.Contains("ASPIRERADIUS088", ex.Message);
        Assert.Contains("not a key", ex.Message);
    }

    /// <summary>
    /// Secret stores populate <c>Data</c> identically in the inline and existing-secret modes — one
    /// carries a value, the other names a key to expose — so the key alphabet applies to both.
    /// </summary>
    [Fact]
    public void CallbackAddingAnInvalidDataKeyToASecretStore_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(
            builder =>
            {
                builder.AddRadiusSecretStore("store", RadiusSecretStoreType.Generic)
                    .WithData("good", builder.AddParameter("pw", secret: true));
                builder.AddContainer("api", "myapp/api:latest");
            },
            opts => opts.SecretStores[0].Data["not a key"] = new RadiusSecretStoreDataEntryConstruct()));

        Assert.Contains("ASPIRERADIUS088", ex.Message);
        Assert.Contains("not a key", ex.Message);
    }

    // ASPIRERADIUS090 — two Radius resources, one Kubernetes object.

    /// <summary>
    /// Radius scopes uniqueness by resource type, so a generated container env secret and a
    /// user-declared secret store can be distinct Radius resources that deploy as the same cluster
    /// object. The second one applied overwrites the first, and the consumer of the overwritten
    /// object reads a key that no longer exists.
    /// </summary>
    [Fact]
    public void SecretStoreNamedLikeTheGeneratedContainerSecret_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(builder =>
        {
            var password = builder.AddParameter("pw", secret: true);
            builder.AddRadiusSecretStore("api-env-secret", RadiusSecretStoreType.Generic)
                .WithData("other", password);
            // `Api` sanitizes to the Bicep identifier `Api_env_secret`, which is distinct from the
            // store's `api_env_secret`, so the existing identifier check (ASPIRERADIUS056) passes —
            // yet both name the same lowercase Kubernetes object.
            builder.AddContainer("Api", "myapp/api:latest")
                .WithEnvironment("PW", password);
        }));

        Assert.Contains("ASPIRERADIUS090", ex.Message);
        Assert.Contains("api-env-secret", ex.Message);
    }

    /// <summary>
    /// Renaming one side resolves the collision, so the check must be on the final physical name and
    /// not on the identifiers the publisher started from.
    /// </summary>
    [Fact]
    public void CallbackRenamingOneOfTheCollidingSecrets_ResolvesTheCollision()
    {
        var bicep = GenerateBicep(
            builder =>
            {
                var password = builder.AddParameter("pw", secret: true);
                builder.AddRadiusSecretStore("api-env-secret", RadiusSecretStoreType.Generic)
                    .WithData("other", password);
                // `Api` sanitizes to the Bicep identifier `Api_env_secret`, which is distinct from
                // the store's `api_env_secret`, so the existing identifier check (ASPIRERADIUS056)
                // passes — yet both name the same lowercase Kubernetes object.
                builder.AddContainer("Api", "myapp/api:latest")
                    .WithEnvironment("PW", password);
            },
            opts => opts.SecretStores.Single(s => s.StoreName.Value?.ToString() == "api-env-secret").StoreName = "renamed-store");

        Assert.Contains("name: 'renamed-store'", bicep);
        Assert.Contains("name: 'api-env-secret'", bicep);
    }

    /// <summary>
    /// Two secrets sharing a name is only a collision when they also share a namespace, and the
    /// namespace has to be read from the final environment construct because a callback can change
    /// it.
    /// </summary>
    [Fact]
    public void SecretsWithTheSameNameInDifferentNamespaces_StillPublish()
    {
        var bicep = GenerateBicep(
            builder =>
            {
                var password = builder.AddParameter("pw", secret: true);
                builder.AddRadiusSecretStore("api-env-secret", RadiusSecretStoreType.Generic)
                    .WithData("other", password);
                // `Api` sanitizes to the Bicep identifier `Api_env_secret`, which is distinct from
                // the store's `api_env_secret`, so the existing identifier check (ASPIRERADIUS056)
                // passes — yet both name the same lowercase Kubernetes object.
                builder.AddContainer("Api", "myapp/api:latest")
                    .WithEnvironment("PW", password);
            },
            opts => opts.LegacyEnvironments[0].ComputeNamespace = "other-namespace");

        Assert.Contains("namespace: 'other-namespace'", bicep);
    }

    // ASPIRERADIUS087 — an environment variable that sets none of its three forms.

    /// <summary>
    /// All three properties are public and independently clearable, so a callback can leave a
    /// variable that emits as an empty object. Kubernetes rejects it, but only at deploy.
    /// </summary>
    [Fact]
    public void CallbackClearingEveryFormOfAnEnvironmentVariable_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(
            builder => builder.AddContainer("api", "myapp/api:latest")
                .WithEnvironment("PLAIN", "value"),
            opts => opts.Containers[0].Env["PLAIN"] = new ContainerEnvVarConstruct()));

        Assert.Contains("ASPIRERADIUS087", ex.Message);
        Assert.Contains("PLAIN", ex.Message);
    }
    private static void AddRabbitMqWithGeneratedPassword(IDistributedApplicationBuilder builder)
    {
        // An explicit user name is required: the default `guest` is loopback-only on a real broker,
        // and the publisher rejects it (ASPIRERADIUS082) before any of the passes under test run.
        var rabbit = builder.AddRabbitMQ("rabbit", userName: builder.AddParameter("rabbituser"));
        builder.AddContainer("api", "myapp/api:latest")
            .WithReference(rabbit);
    }

    // ASPIRERADIUS089 — the credential a UDT resource consumes by resource ID.

    /// <summary>
    /// Unlike a container env secret — whose only reader is the variable pointing at it — this value
    /// is handed to the recipe that provisions the broker, while the matching credential was already
    /// composed into every consumer's connection string. Removing the key leaves the recipe unable
    /// to provision at all.
    /// </summary>
    [Fact]
    public void CallbackRemovingTheCredentialKey_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(
            AddRabbitMqWithGeneratedPassword,
            opts => opts.SecuritySecrets[0].Data.Clear()));

        Assert.Contains("ASPIRERADIUS089", ex.Message);
    }

    /// <summary>
    /// A swapped credential is worse than a missing one: the deploy succeeds and the broker comes up
    /// with a password no consumer was told about, so it surfaces only as an authentication failure
    /// at runtime.
    /// </summary>
    [Fact]
    public void CallbackReplacingTheCredentialEntry_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(
            AddRabbitMqWithGeneratedPassword,
            opts =>
            {
                var secret = opts.SecuritySecrets[0];
                var key = secret.Data.Keys.First();
                secret.Data[key] = new RadiusSecuritySecretDataEntryConstruct
                {
                    Encoding = "string",
                    Value = "hunter2",
                };
            }));

        Assert.Contains("ASPIRERADIUS089", ex.Message);
    }

    /// <summary>
    /// Mutating the existing entry in place desynchronizes broker and clients exactly as replacing
    /// it does, and leaves the entry object identical, so identity alone cannot catch it.
    /// </summary>
    [Fact]
    public void CallbackMutatingTheCredentialValueInPlace_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(
            AddRabbitMqWithGeneratedPassword,
            opts => opts.SecuritySecrets[0].Data.Values.First().Value!.Value = "hunter2"));

        Assert.Contains("ASPIRERADIUS089", ex.Message);
    }

    /// <summary>
    /// Assigning the consumer's credential property hands that relationship to the callback, so the
    /// publisher no longer owns what the generated secret holds and a changed value is legitimate.
    /// The ownership check therefore has to run <em>before</em> the ASPIRERADIUS089 checks, which
    /// would otherwise reject a valid last-write-wins configuration.
    /// </summary>
    [Fact]
    public void CallbackTakingOverTheCredentialProperty_MayThenChangeTheValue()
    {
        var bicep = GenerateBicep(
            AddRabbitMqWithGeneratedPassword,
            opts =>
            {
                foreach (var instance in opts.ResourceTypeInstances)
                {
                    if (instance.GetSchemaProperty("password") is not null)
                    {
                        instance.SetSchemaProperty("password", new BicepValue<object>("callback-owned"));
                    }
                }

                opts.SecuritySecrets[0].Data.Values.First().Value!.Value = "hunter2";
            });

        Assert.Contains("password: 'callback-owned'", bicep);
        Assert.Contains("hunter2", bicep);
    }

    // The secrets recipe registration.

    /// <summary>
    /// <c>Radius.Security/secrets</c> is recipe-backed, so a secret with no registered recipe fails
    /// the deploy. A callback can remove the entry the first pass registered while leaving the
    /// secret that needs it, and that state has no valid deployment, so it is repaired.
    /// </summary>
    [Fact]
    public void CallbackRemovingTheSecretsRecipeEntry_IsRepairedWithItsRecipeParameters()
    {
        var bicep = GenerateBicep(
            AddContainerWithSecretEnvironment,
            opts => opts.RecipePacks[0].Recipes.Remove(RadiusResourceTypes.SecuritySecrets),
            radius => radius
                .WithRecipeParameters(p => p["envWide"] = "from-environment")
                .WithRecipeParameters(RadiusResourceTypes.SecuritySecrets, p => p["typeScoped"] = "from-type"));

        Assert.Contains($"'{RadiusResourceTypes.SecuritySecrets}': {{", bicep);

        // The repair has to go through the same construction path as the first pass. Re-registering
        // a bare entry would drop both parameters and deploy a recipe configured differently from
        // every other recipe in the pack.
        Assert.Contains("envWide: 'from-environment'", bicep);
        Assert.Contains("typeScoped: 'from-type'", bicep);
    }

    /// <summary>
    /// The existing mode references an object the cluster already has rather than creating it, so
    /// two of those stores naming the same object cannot overwrite each other — exposing different
    /// keys from one <c>Secret</c> is the point of the mode.
    /// </summary>
    [Fact]
    public void TwoExistingSecretStoresReferencingTheSameObject_StillPublish()
    {
        var bicep = GenerateBicep(builder =>
        {
            builder.AddRadiusSecretStore("creds-user", RadiusSecretStoreType.Generic)
                .WithExistingSecret("shared-ns/shared-secret", "username");
            builder.AddRadiusSecretStore("creds-password", RadiusSecretStoreType.Generic)
                .WithExistingSecret("shared-ns/shared-secret", "password");
            builder.AddContainer("api", "myapp/api:latest");
        });

        Assert.Contains("resource: 'shared-ns/shared-secret'", bicep);
    }

    /// <summary>
    /// A generated secret does create the object, so it would overwrite what an existing store
    /// references — the reference then exposes keys the surviving object does not have.
    /// </summary>
    [Fact]
    public void ExistingSecretStoreReferencingAGeneratedSecretsObject_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(builder =>
        {
            builder.AddRadiusSecretStore("creds", RadiusSecretStoreType.Generic)
                // The environment's Kubernetes namespace, which is what the generated secret
                // deploys into — not the environment's Radius resource name.
                .WithExistingSecret("default/api-env-secret", "username");
            builder.AddContainer("Api", "myapp/api:latest")
                .WithEnvironment("PW", builder.AddParameter("pw", secret: true));
        }));

        Assert.Contains("ASPIRERADIUS090", ex.Message);
        Assert.Contains("api-env-secret", ex.Message);
    }

    /// <summary>
    /// A sealed store also carries a <c>resource</c> reference, but deploy applies its manifest with
    /// <c>kubectl apply</c>, so it creates or replaces the object rather than only reading it —
    /// classifying it with the existing mode would let it silently clobber a generated secret.
    /// </summary>
    [Fact]
    public void SealedSecretStoreNamedLikeTheGeneratedContainerSecret_FailsThePublish()
    {
        var manifest = WriteSealedManifest("api-env-secret", "default");

        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(
            builder => builder.AddContainer("Api", "myapp/api:latest")
                .WithEnvironment("PW", builder.AddParameter("pw", secret: true)),
            configureEnvironment: radius => radius.WithSecretStore(
                "creds",
                RadiusSecretStoreType.Generic,
                s => s.WithSealedSecret(manifest, "username"))));

        Assert.Contains("ASPIRERADIUS090", ex.Message);
        Assert.Contains("api-env-secret", ex.Message);
    }

    /// <summary>
    /// Two sealed stores are two <c>kubectl apply</c> calls against one object, so the second
    /// manifest replaces the first — unlike two existing-mode references, this is a real collision.
    /// </summary>
    [Fact]
    public void TwoSealedSecretStoresApplyingTheSameObject_FailThePublish()
    {
        var manifest = WriteSealedManifest("shared-secret", "default");

        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(
            builder => builder.AddContainer("api", "myapp/api:latest"),
            configureEnvironment: radius => radius
                .WithSecretStore("creds-user", RadiusSecretStoreType.Generic, s => s.WithSealedSecret(manifest, "username"))
                .WithSecretStore("creds-password", RadiusSecretStoreType.Generic, s => s.WithSealedSecret(manifest, "password"))));

        Assert.Contains("ASPIRERADIUS090", ex.Message);
        Assert.Contains("shared-secret", ex.Message);
    }

    /// <summary>
    /// Removing a sealed store's construct does not stop its manifest from being applied — the apply
    /// step selects sealed stores from the application model, not from the emitted constructs — so
    /// the collision has to survive the removal.
    /// </summary>
    [Fact]
    public void CallbackRemovingASealedStoreConstruct_StillDetectsTheCollision()
    {
        var manifest = WriteSealedManifest("api-env-secret", "default");

        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(
            builder => builder.AddContainer("Api", "myapp/api:latest")
                .WithEnvironment("PW", builder.AddParameter("pw", secret: true)),
            opts => opts.SecretStores.Clear(),
            radius => radius.WithSecretStore(
                "creds",
                RadiusSecretStoreType.Generic,
                s => s.WithSealedSecret(manifest, "username"))));

        Assert.Contains("ASPIRERADIUS090", ex.Message);
        Assert.Contains("api-env-secret", ex.Message);
    }

    /// <summary>
    /// An environment-scoped <c>Radius.Security/secrets</c> resource leaves <c>ApplicationId</c>
    /// unset, but the property is non-nullable and always returns a construct — so its scope has to
    /// be decided by what it renders to. Testing the reference for null skipped every
    /// environment-scoped secret, leaving the collision undetected.
    /// </summary>
    [Fact]
    public void EnvironmentScopedSecuritySecretCollidingWithAStore_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(
            builder =>
            {
                builder.AddRadiusSecretStore("shared", RadiusSecretStoreType.Generic)
                    .WithData("other", builder.AddParameter("store-pw", secret: true));
                AddContainerWithSecretEnvironment(builder);
            },
            opts =>
            {
                // Copy the environment reference from the generated (application-scoped) secret so
                // the new one lands in the same namespace as the colliding store.
                var scoped = opts.SecuritySecrets[0];
                var envSecret = new RadiusSecuritySecretConstruct("env_scoped_secret")
                {
                    SecretName = "shared",
                    EnvironmentId = scoped.EnvironmentId,
                };

                opts.SecuritySecrets.Add(envSecret);
            }));

        Assert.Contains("ASPIRERADIUS090", ex.Message);
        Assert.Contains("shared", ex.Message);
    }
}
