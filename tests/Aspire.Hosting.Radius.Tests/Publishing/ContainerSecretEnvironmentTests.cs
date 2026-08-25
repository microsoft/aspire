// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Radius.Publishing.Constructs;
using Aspire.Hosting.Radius.ResourceMapping;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

/// <summary>
/// Covers how credential-bearing container environment variables are published.
/// </summary>
/// <remarks>
/// Routing a credential to an <c>@secure()</c> Bicep <c>param</c> only keeps it out of the
/// published artifact; the resolved string still lands in the Kubernetes <c>Deployment</c> spec and
/// its rollout history. These tests pin that such values are emitted as
/// <c>valueFrom.secretKeyRef</c> instead, and that values carrying nothing sensitive are left as
/// plain <c>value</c> entries so the artifact stays readable.
/// </remarks>
public class ContainerSecretEnvironmentTests
{
    private static string GenerateBicep(
        Action<IDistributedApplicationBuilder> configure,
        Action<RadiusInfrastructureOptions>? configureInfrastructure = null)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var radius = builder.AddRadiusEnvironment("myenv");
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

    [Fact]
    public void SecretParameter_IsReferencedFromSecret_NotWrittenIntoTheContainerSpec()
    {
        var secret = default(IResourceBuilder<ParameterResource>);
        var bicep = GenerateBicep(builder =>
        {
            secret = builder.AddParameter("apikey", secret: true);
            builder.AddContainer("api", "myapp/api:latest")
                .WithEnvironment("API_KEY", secret!);
        });

        Assert.Contains(
            """
                      API_KEY: {
                        valueFrom: {
                          secretKeyRef: {
                            secretName: 'api-env-secret'
                            key: 'API_KEY'
                          }
                        }
                      }
            """.ReplaceLineEndings(),
            bicep.ReplaceLineEndings());

        // The composed value moves to the secret rather than disappearing.
        Assert.Contains(
            """
                  API_KEY: {
                    value: apikey
                    encoding: 'string'
                  }
            """.ReplaceLineEndings(),
            bicep.ReplaceLineEndings());
    }

    /// <summary>
    /// A mixed-case Aspire resource name still produces a Kubernetes-legal secret name.
    /// </summary>
    /// <remarks>
    /// The Kubernetes secrets recipe uses the Radius resource name verbatim as the
    /// <c>core/Secret</c> <c>metadata.name</c>, which must be an RFC 1123 subdomain, so
    /// <c>MyApi-env-secret</c> would be rejected at apply time. The container recipe lowercases for
    /// itself, so nothing else in the emitted document catches this.
    /// </remarks>
    [Fact]
    public void UppercaseResourceName_ProducesALowercaseSecretName()
    {
        var bicep = GenerateBicep(builder =>
        {
            var secret = builder.AddParameter("apikey", secret: true);
            builder.AddContainer("MyApi", "myapp/api:latest")
                .WithEnvironment("API_KEY", secret);
        });

        Assert.Contains("name: 'myapi-env-secret'", bicep, StringComparison.Ordinal);
        Assert.Contains("secretName: 'myapi-env-secret'", bicep, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same normalization applies to the credential secret emitted for a backing resource,
    /// which is named after that resource rather than a container.
    /// </summary>
    [Fact]
    public void UppercaseBackingResourceName_ProducesALowercaseSecretName()
    {
        var bicep = GenerateBicep(builder =>
        {
            // An explicit user name is required for RabbitMQ (ASPIRERADIUS082), so supply one
            // rather than tripping that diagnostic before the secret is ever emitted.
            var user = builder.AddParameter("queueuser", "app");
            builder.AddRabbitMQ("MyQueue", userName: user);
        });

        Assert.Contains("name: 'myqueue-password-secret'", bicep, StringComparison.Ordinal);
    }

    /// <summary>
    /// A value with nothing sensitive in it stays a plain <c>value</c>. Routing everything through a
    /// secret would make the deployed spec unreadable and bloat the emitted secret for no benefit.
    /// </summary>
    [Fact]
    public void NonSecretValue_StaysAPlainValue()
    {
        var bicep = GenerateBicep(builder =>
        {
            var plain = builder.AddParameter("region", "westus");
            builder.AddContainer("api", "myapp/api:latest")
                .WithEnvironment("REGION", plain)
                .WithEnvironment("MODE", "production");
        });

        Assert.Contains("MODE: {", bicep);
        Assert.Contains("value: 'production'", bicep);
        Assert.Contains("value: region", bicep);
        Assert.DoesNotContain("secretKeyRef", bicep);
        Assert.DoesNotContain(RadiusResourceTypes.SecuritySecrets, bicep);
    }

    /// <summary>
    /// The per-fragment <c>uriComponent()</c> escaping has to survive the move into the secret.
    /// This is the reason the whole composed expression is written into the secret rather than the
    /// container referencing a secret-backed fragment through kubelet's <c>$(VAR)</c> expansion:
    /// <c>$(VAR)</c> substitutes at pod start, long after Bicep could have escaped the fragment, so
    /// a password containing <c>@</c>, <c>:</c> or <c>/</c> would corrupt the URI.
    /// </summary>
    [Fact]
    public void ComposedUriValue_KeepsPerFragmentEscaping()
    {
        var bicep = GenerateBicep(builder =>
        {
            var password = builder.AddParameter("pw", secret: true);
            var rabbit = builder.AddRabbitMQ("rabbit", userName: builder.AddParameter("rabbituser", "app"), password: password);
            builder.AddContainer("api", "myapp/api:latest").WithReference(rabbit);
        });

        Assert.Contains("uriComponent(pw)", bicep);
    }

    /// <summary>
    /// <c>Radius.Security/secrets</c> is recipe-backed, so a secret emitted for a container's
    /// environment has to pull its recipe into the pack. The pack is built before container
    /// environments are resolved, so this is the case that regresses if the top-up is dropped: an
    /// app with no backing resource at all still emits a secret.
    /// </summary>
    [Fact]
    public void ContainerSecret_RegistersTheSecretsRecipe_EvenWithNoBackingResources()
    {
        var bicep = GenerateBicep(builder =>
        {
            var password = builder.AddParameter("pw", secret: true);
            builder.AddContainer("api", "myapp/api:latest")
                .WithEnvironment("PW", password);
        });

        Assert.Contains($"'{RadiusResourceTypes.SecuritySecrets}': {{", bicep);
        Assert.Contains("kube-recipes/secrets", bicep);
    }

    /// <summary>
    /// The secrets recipe must be registered exactly once when a backing resource already pulled it
    /// in for its own credential.
    /// </summary>
    [Fact]
    public void SecretsRecipe_IsRegisteredOnce_WhenABackingResourceAlsoNeedsIt()
    {
        var bicep = GenerateBicep(builder =>
        {
            var password = builder.AddParameter("pw", secret: true);
            var rabbit = builder.AddRabbitMQ("rabbit", userName: builder.AddParameter("rabbituser", "app"), password: password);
            builder.AddContainer("api", "myapp/api:latest").WithReference(rabbit);
        });

        var occurrences = bicep.Split($"'{RadiusResourceTypes.SecuritySecrets}': {{").Length - 1;
        Assert.Equal(1, occurrences);
    }

    /// <summary>
    /// Kubernetes restricts secret data keys to <c>[-._a-zA-Z0-9]+</c>. An environment variable name
    /// outside that set would be rejected by the API server at deploy time, with no indication of
    /// which variable caused it, so the publisher rejects it while the name is still attributable.
    /// </summary>
    [Fact]
    public void CredentialInAVariableWhoseNameIsNotAValidSecretKey_FailsWithADiagnostic()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(builder =>
        {
            var password = builder.AddParameter("pw", secret: true);
            builder.AddContainer("api", "myapp/api:latest")
                .WithEnvironment("BAD KEY", password);
        }));

        Assert.Contains("ASPIRERADIUS083", ex.Message);
        Assert.Contains("BAD KEY", ex.Message);
    }

    /// <summary>
    /// The container's secret is consumed only by that container, so it can safely read a backing
    /// resource's outputs. The credential secret a backing resource consumes by resource ID must
    /// stay a separate resource, or the graph becomes <c>secret → resource → secret</c>.
    /// </summary>
    [Fact]
    public void ContainerSecret_IsDistinctFromTheBackingResourceCredentialSecret()
    {
        var bicep = GenerateBicep(builder =>
        {
            var password = builder.AddParameter("pw", secret: true);
            var rabbit = builder.AddRabbitMQ("rabbit", userName: builder.AddParameter("rabbituser", "app"), password: password);
            builder.AddContainer("api", "myapp/api:latest").WithReference(rabbit);
        });

        Assert.Contains("resource api_env_secret 'Radius.Security/secrets@", bicep);
        Assert.Contains("resource rabbit_password_secret 'Radius.Security/secrets@", bicep);

        // The container's secret is never referenced by the backing resource.
        var rabbitResource = bicep[bicep.IndexOf("resource rabbit 'Radius.Messaging/rabbitMQ@", StringComparison.Ordinal)..];
        rabbitResource = rabbitResource[..rabbitResource.IndexOf("\n}", StringComparison.Ordinal)];
        Assert.DoesNotContain("api_env_secret", rabbitResource);
    }

    /// <summary>
    /// The secret's scope references have to follow a callback that renames the environment or
    /// application construct, exactly as a backing resource's credential secret does. Without this
    /// the secret emits <c>environment: &lt;old&gt;.id</c>, which is not a symbol that exists.
    /// </summary>
    [Fact]
    public void RenamingTheEnvironmentAndApplication_RepairsTheContainerSecretScope()
    {
        var bicep = GenerateBicep(builder =>
        {
            var password = builder.AddParameter("pw", secret: true);
            builder.AddContainer("api", "myapp/api:latest")
                .WithEnvironment("PW", password);

        }, opts =>
        {
            opts.Environments[0].BicepIdentifier = "renamedEnv";
            opts.Applications[0].BicepIdentifier = "renamedApp";
        });

        Assert.Contains("environment: renamedEnv.id", bicep);
        Assert.Contains("application: renamedApp.id", bicep);
        Assert.DoesNotContain("environment: myenv.id", bicep);
    }

    /// <summary>
    /// A value composed purely from a secret parameter has no backing-resource projection, so it is
    /// not tracked by the projection repair path. Its reference to the secret is just as breakable,
    /// so removing the secret has to be rejected rather than emitted as a dangling reference — the
    /// API server accepts the artifact and the pod then fails to start.
    /// </summary>
    [Fact]
    public void RemovingTheContainerSecret_FailsEvenWithNoBackingResourceProjection()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(builder =>
        {
            var password = builder.AddParameter("pw", secret: true);
            builder.AddContainer("api", "myapp/api:latest")
                .WithEnvironment("PW", password);
        }, opts => opts.SecuritySecrets.Clear()));

        Assert.Contains("ASPIRERADIUS084", ex.Message);
        Assert.Contains("PW", ex.Message);
    }

    /// <summary>
    /// Removing just the key leaves the secret resource in place, so the resource-level check does
    /// not catch it; the pod still fails to start on a missing key.
    /// </summary>
    [Fact]
    public void RemovingTheSecretDataKey_FailsWithADiagnostic()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(builder =>
        {
            var password = builder.AddParameter("pw", secret: true);
            builder.AddContainer("api", "myapp/api:latest")
                .WithEnvironment("PW", password);
        }, opts => opts.SecuritySecrets[0].Data.Clear()));

        Assert.Contains("ASPIRERADIUS084", ex.Message);
        Assert.Contains("PW", ex.Message);
    }

    /// <summary>
    /// The env var references the secret by resource <em>name</em>, so renaming the resource has to
    /// re-sync the reference.
    /// </summary>
    [Fact]
    public void RenamingTheSecretResource_ResyncsTheReference()
    {
        var bicep = GenerateBicep(builder =>
        {
            var password = builder.AddParameter("pw", secret: true);
            builder.AddContainer("api", "myapp/api:latest")
                .WithEnvironment("PW", password);
        }, opts => opts.SecuritySecrets[0].SecretName = "custom-secret");

        Assert.Contains("secretName: 'custom-secret'", bicep);
        Assert.DoesNotContain("secretName: 'api-env-secret'", bicep);
    }

    /// <summary>
    /// A callback that re-points the reference itself owns the result — last-write-wins, as
    /// everywhere else on the escape-hatch surface.
    /// </summary>
    [Fact]
    public void CallbackThatRepointsTheReference_IsPreserved()
    {
        var bicep = GenerateBicep(builder =>
        {
            var password = builder.AddParameter("pw", secret: true);
            builder.AddContainer("api", "myapp/api:latest")
                .WithEnvironment("PW", password);
        }, opts =>
        {
            opts.Containers[0].Env["PW"].Value!.SecretName = "my-own-secret";
            opts.SecuritySecrets[0].SecretName = "renamed";
        });

        Assert.Contains("secretName: 'my-own-secret'", bicep);
    }

    /// <summary>
    /// The <c>value</c> and <c>valueFrom.secretKeyRef</c> forms are mutually exclusive, and
    /// Kubernetes rejects an environment variable that sets both. All three properties are public,
    /// so only a post-callback check can enforce it.
    /// </summary>
    [Fact]
    public void CallbackThatSetsBothValueAndSecretReference_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(builder =>
        {
            var password = builder.AddParameter("pw", secret: true);
            builder.AddContainer("api", "myapp/api:latest")
                .WithEnvironment("PW", password);
        }, opts => opts.Containers[0].Env["PW"].Value!.Value = "literal"));

        Assert.Contains("ASPIRERADIUS087", ex.Message);
        Assert.Contains("'PW'", ex.Message);
    }

    /// <summary>
    /// A <c>secretKeyRef</c> needs both halves: <c>secretName</c> alone names no data entry. Added
    /// as a fresh entry, which is how a callback introduces an environment variable of its own.
    /// </summary>
    [Fact]
    public void CallbackThatAddsAnEntryWithOnlyASecretName_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(builder =>
        {
            builder.AddContainer("api", "myapp/api:latest")
                .WithEnvironment("PLAIN", "value");
        }, opts => opts.Containers[0].Env["ADDED"] = new ContainerEnvVarConstruct { SecretName = "some-secret" }));

        Assert.Contains("ASPIRERADIUS087", ex.Message);
        Assert.Contains("SecretName is set but SecretKey is not", ex.Message);
    }

    /// <summary>
    /// Setting a secret reference on a variable the publisher already emitted as a plain
    /// <c>value</c> leaves both forms on the entry, which Kubernetes rejects.
    /// </summary>
    [Fact]
    public void CallbackThatAddsASecretReferenceToAPlainValue_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(builder =>
        {
            builder.AddContainer("api", "myapp/api:latest")
                .WithEnvironment("PLAIN", "value");
        }, opts => opts.Containers[0].Env["PLAIN"].Value!.SecretName = "some-secret"));

        Assert.Contains("ASPIRERADIUS087", ex.Message);
        Assert.Contains("both a 'value' and a 'valueFrom.secretKeyRef'", ex.Message);
    }

    /// <summary>
    /// The mirror of <see cref="CallbackThatAddsAnEntryWithOnlyASecretName_FailsThePublish"/>: a key
    /// with no secret to read it from. Added as a fresh entry, which is how a callback introduces an
    /// environment variable of its own.
    /// </summary>
    [Fact]
    public void CallbackThatAddsAnEntryWithOnlyASecretKey_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(builder =>
        {
            builder.AddContainer("api", "myapp/api:latest")
                .WithEnvironment("PLAIN", "value");
        }, opts => opts.Containers[0].Env["ADDED"] = new ContainerEnvVarConstruct { SecretKey = "some-key" }));

        Assert.Contains("ASPIRERADIUS087", ex.Message);
        Assert.Contains("SecretKey is set but SecretName is not", ex.Message);
    }

    /// <summary>
    /// A complete reference can still be unrepresentable. Both halves are copied verbatim into the
    /// pod's <c>secretKeyRef</c>, so an invalid literal passes publish and Radius deploy and is
    /// rejected only when the API server creates the pod.
    /// </summary>
    [Fact]
    public void CallbackThatAddsAnEntryWithAnInvalidLiteralSecretName_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(builder =>
        {
            builder.AddContainer("api", "myapp/api:latest")
                .WithEnvironment("PLAIN", "value");
        }, opts => opts.Containers[0].Env["ADDED"] = new ContainerEnvVarConstruct
        {
            SecretName = "Bad_Name",
            SecretKey = "some-key",
        }));

        Assert.Contains("ASPIRERADIUS087", ex.Message);
        Assert.Contains("Bad_Name", ex.Message);
        Assert.Contains("DNS-1123 subdomain", ex.Message);
    }

    /// <summary>
    /// The key half has its own, different alphabet: a Kubernetes <c>Secret</c> data key permits
    /// letters, digits, <c>-</c>, <c>_</c> and <c>.</c>, so a path-like key is rejected.
    /// </summary>
    [Fact]
    public void CallbackThatAddsAnEntryWithAnInvalidLiteralSecretKey_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(builder =>
        {
            builder.AddContainer("api", "myapp/api:latest")
                .WithEnvironment("PLAIN", "value");
        }, opts => opts.Containers[0].Env["ADDED"] = new ContainerEnvVarConstruct
        {
            SecretName = "some-secret",
            SecretKey = "bad/key",
        }));

        Assert.Contains("ASPIRERADIUS087", ex.Message);
        Assert.Contains("bad/key", ex.Message);
    }
}
