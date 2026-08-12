// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Radius.Tests.Publishing;

/// <summary>
/// Covers how the publisher resolves container environment values: which failures it is allowed to
/// skip, which credentials it replaces, and how it escapes values destined for a URI.
/// </summary>
public class BackingResourceValueResolutionTests
{
    private static (string Bicep, RecordingLogger Logger) GenerateBicep(Action<IDistributedApplicationBuilder> configure)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        configure(builder);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);

        var logger = new RecordingLogger();
        return (new RadiusBicepPublishingContext(radiusEnv).GenerateBicep(model, logger), logger);
    }

    /// <summary>
    /// A value whose provider only knows its answer after another deployment — an Azure Bicep output
    /// is the real-world case — cannot be produced while publishing, so the variable is dropped.
    /// </summary>
    /// <remarks>
    /// This pins the behaviour the publisher's narrow skip preserves. It is deliberately covered
    /// with a stand-in provider rather than a real Azure resource: the condition under test is
    /// "the value declares deployment-substituted semantics (<c>IManifestExpressionProvider</c>) and
    /// cannot produce a value now", and reproducing it through <c>Aspire.Hosting.Azure</c> would add
    /// a package reference without testing anything more. The warning matters as much as the skip —
    /// before, this was logged at Debug and so never appeared in a normal publish.
    /// </remarks>
    [Fact]
    public void ValueOnlyKnownAfterAnotherDeployment_SkipsJustThatVariable()
    {
        var (bicep, logger) = GenerateBicep(b =>
        {
            b.AddContainer("api", "myapp/api", "latest")
                .WithEnvironment(context =>
                {
                    context.EnvironmentVariables["UNRESOLVABLE"] = new ThrowingDeploymentOutput(
                        new InvalidOperationException("The output 'x' does not have a value."));
                    context.EnvironmentVariables["RESOLVABLE"] = "kept";
                });
        });

        Assert.DoesNotContain("UNRESOLVABLE", bicep, StringComparison.Ordinal);
        Assert.Contains("'kept'", bicep, StringComparison.Ordinal);

        var warnings = logger.Matching(LogLevel.Warning, "UNRESOLVABLE", "omitted from the Radius output");
        Assert.Single(warnings);
    }

    /// <summary>
    /// A plain value provider may use <see cref="InvalidOperationException"/> for a genuine invalid
    /// state, so it must not be mistaken for a deferred deployment output and silently dropped. Only
    /// a value that positively declares deployment-substituted semantics is eligible for the skip.
    /// </summary>
    [Fact]
    public void InvalidOperationFromAPlainValueProvider_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(b =>
        {
            b.AddContainer("api", "myapp/api", "latest")
                .WithEnvironment(context =>
                {
                    context.EnvironmentVariables["BROKEN"] = new ThrowingValueProvider(
                        new InvalidOperationException("the provider is in a genuinely invalid state"));
                });
        }));

        Assert.Equal("the provider is in a genuinely invalid state", ex.Message);
    }

    /// <summary>
    /// Any other failure is a real error and must fail the publish.
    /// </summary>
    /// <remarks>
    /// The publisher used to wrap the whole value resolution in
    /// <c>catch (InvalidOperationException)</c>, so whether a bug surfaced depended on the
    /// exception's type rather than on the publisher having judged the value unavailable. This test
    /// is the reason the skip now has a dedicated type.
    /// </remarks>
    [Fact]
    public void UnexpectedResolutionFailure_FailsThePublish()
    {
        var ex = Assert.Throws<NotSupportedException>(() => GenerateBicep(b =>
        {
            b.AddContainer("api", "myapp/api", "latest")
                .WithEnvironment(context =>
                {
                    context.EnvironmentVariables["BROKEN"] = new ThrowingValueProvider(
                        new NotSupportedException("something genuinely wrong"));
                });
        }));

        Assert.Equal("something genuinely wrong", ex.Message);
    }

    /// <summary>
    /// A reference to an endpoint the target resource does not declare is detected before anything
    /// reads the missing annotation, so it is skipped as an unavailable value rather than surfacing
    /// as an indistinguishable <see cref="InvalidOperationException"/>.
    /// </summary>
    [Fact]
    public void ReferenceToUndeclaredEndpoint_SkipsJustThatVariable()
    {
        var (bicep, logger) = GenerateBicep(b =>
        {
            var backend = b.AddContainer("backend", "myapp/backend", "latest").WithHttpEndpoint(targetPort: 8080);

            b.AddContainer("api", "myapp/api", "latest")
                .WithEnvironment("MISSING", backend.GetEndpoint("does-not-exist").Property(EndpointProperty.Host))
                .WithEnvironment("PRESENT", backend.GetEndpoint("http").Property(EndpointProperty.Host));
        });

        Assert.DoesNotContain("MISSING", bicep, StringComparison.Ordinal);
        Assert.Contains("backend-backend.default.svc.cluster.local", bicep, StringComparison.Ordinal);
        Assert.Single(logger.Matching(LogLevel.Warning, "MISSING", "is not defined on resource 'backend'"));
    }

    /// <summary>
    /// A user name supplied as a parameter is replaced by the one the recipe created, exactly as the
    /// password is. Without this the connection string names a user the recipe never provisioned.
    /// </summary>
    [Fact]
    public Task UserNameParameter_IsProjectedFromRecipeOutputs()
    {
        var (bicep, _) = GenerateBicep(b =>
        {
            var mongoUser = b.AddParameter("mongouser");
            var rabbitUser = b.AddParameter("rabbituser");
            var mongo = b.AddMongoDB("mongo", userName: mongoUser);
            var rabbit = b.AddRabbitMQ("rabbit", userName: rabbitUser);

            b.AddContainer("api", "myapp/api", "latest")
                .WithReference(mongo)
                .WithReference(rabbit);
        });

        return Verify(bicep, extension: "bicep");
    }

    /// <summary>
    /// Known gap: the default user names ("admin" for MongoDB, "guest" for RabbitMQ) are appended
    /// through <c>ReferenceExpressionBuilder.AppendFormatted(string?, string?)</c>, which formats
    /// immediately and writes the result into the format string. They therefore arrive at the
    /// publisher as opaque literal text with no value provider to substitute, and keep their default
    /// value in the emitted connection string. Pinned so the gap is visible rather than forgotten.
    /// </summary>
    [Fact]
    public void DefaultUserName_RemainsALiteral()
    {
        var (bicep, _) = GenerateBicep(b =>
        {
            var mongo = b.AddMongoDB("mongo");
            b.AddContainer("api", "myapp/api", "latest").WithReference(mongo);
        });

        Assert.Contains("mongodb://admin:", bicep, StringComparison.Ordinal);
    }

    /// <summary>
    /// An extra database nobody references cannot produce a wrong connection string, so it must not
    /// break a model that published before.
    /// </summary>
    [Fact]
    public void UnreferencedSecondDatabase_DoesNotFailThePublish()
    {
        var (bicep, logger) = GenerateBicep(b =>
        {
            var pg = b.AddPostgres("pg");
            var used = pg.AddDatabase("used");
            pg.AddDatabase("unused");

            b.AddContainer("api", "myapp/api", "latest").WithReference(used);
        });

        Assert.Contains("'used'", bicep, StringComparison.Ordinal);
        Assert.Single(logger.Matching(LogLevel.Warning, "declares 2 databases", "'used' was passed"));
    }

    /// <summary>
    /// A server with no <c>AddDatabase(...)</c> child is a valid, common model, so it warns rather
    /// than failing — and the <c>database</c> property is set to the user name, because that is
    /// what a client derives from a connection string that carries no database name.
    /// </summary>
    [Fact]
    public void ServerWithNoDatabase_WarnsAndUsesTheUserNameAsTheDatabase()
    {
        var (bicep, logger) = GenerateBicep(b =>
        {
            var pg = b.AddPostgres("pg");
            b.AddContainer("api", "myapp/api", "latest").WithReference(pg);
        });

        Assert.Contains("pg.properties.host", bicep, StringComparison.Ordinal);
        Assert.Contains("database: 'postgres'", bicep, StringComparison.Ordinal);
        Assert.Single(logger.Matching(LogLevel.Warning, "pg", "named after the user"));
    }

    /// <summary>
    /// The user name a childless server is given must be the one the recipe creates the database
    /// from, so a custom user name has to reach both properties. Pinned because a mismatch here is
    /// the exact failure this fallback exists to prevent: the recipe would create <c>postgres_db</c>
    /// while the consumer opened a database named after its user.
    /// </summary>
    [Fact]
    public void ServerWithNoDatabase_UsesTheCustomUserNameAsTheDatabase()
    {
        var (bicep, _) = GenerateBicep(b =>
        {
            var userName = b.AddParameter("pguser", "appuser");
            var pg = b.AddPostgres("pg", userName: userName);
            b.AddContainer("api", "myapp/api", "latest").WithReference(pg);
        });

        Assert.Contains("username: pguser", bicep, StringComparison.Ordinal);
        Assert.Contains("database: pguser", bicep, StringComparison.Ordinal);
    }

    /// <summary>
    /// Annotations are the only reference signal available when the <c>database</c> property is
    /// chosen, and a <c>WithEnvironment</c> callback that composes a database's value inline records
    /// none. Picking the first child in that case creates a database the consumer does not use, so
    /// the publish fails instead.
    /// </summary>
    [Fact]
    public void MultipleUnreferencedDatabases_FailThePublishRatherThanGuessing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(b =>
        {
            var pg = b.AddPostgres("pg");
            pg.AddDatabase("first");
            var second = pg.AddDatabase("second");

            b.AddContainer("api", "myapp/api", "latest")
                .WithEnvironment(context =>
                {
                    context.EnvironmentVariables["DB"] = second.Resource.ConnectionStringExpression;
                });
        }));

        Assert.Contains("ASPIRERADIUS072", ex.Message, StringComparison.Ordinal);
        Assert.Contains("none of them is referenced", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Substitutions are keyed by parameter identity, so a parameter given as both the user name and
    /// the password of a recipe-provisioned resource can only be rewritten to one of the two
    /// recipe-generated values. It used to be silently rewritten to the user name, handing consumers
    /// <c>properties.username</c> wherever they asked for the password.
    /// </summary>
    [Fact]
    public void ParameterSharedBetweenUserNameAndPassword_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(b =>
        {
            var shared = b.AddParameter("shared", "value", secret: true);
            var mongo = b.AddMongoDB("mongo", userName: shared, password: shared);

            b.AddContainer("api", "myapp/api", "latest").WithReference(mongo);
        }));

        Assert.Contains("ASPIRERADIUS070", ex.Message, StringComparison.Ordinal);
        Assert.Contains("both the user name and the password", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A user-name parameter shared with a resource whose credential is substituted from
    /// <c>listSecrets()</c> is just as unsafe as a shared password: this resource keeps the
    /// parameter's own value while every consumer reference is rewritten to the other resource's
    /// recipe secret. It is only caught if user names are registered as recipe credentials too.
    /// </summary>
    [Fact]
    public void UserNameParameterSharedWithASubstitutedCredential_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(b =>
        {
            var shared = b.AddParameter("shared", "value", secret: true);
            var pg = b.AddPostgres("pg", userName: shared);
            var cache = b.AddRedis("cache", password: shared);

            b.AddContainer("api", "myapp/api", "latest")
                .WithReference(pg)
                .WithReference(cache);
        }));

        Assert.Contains("ASPIRERADIUS070", ex.Message, StringComparison.Ordinal);
        Assert.Contains("shared", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Replacing a password Aspire generated for run mode is invisible and correct. Replacing one
    /// the AppHost author chose is a decision being overridden, so it has to be reported — otherwise
    /// they debug a credential mismatch against a value that never reached the cluster.
    /// </summary>
    [Fact]
    public void UserSuppliedPassword_IsReportedWhenReplacedByTheRecipeSecret()
    {
        var (_, logger) = GenerateBicep(b =>
        {
            var password = b.AddParameter("cachepassword", "hunter2", secret: true);
            var cache = b.AddRedis("cache", password: password);
            b.AddContainer("api", "myapp/api", "latest").WithReference(cache);
        });

        Assert.Single(logger.Matching(LogLevel.Warning, "cachepassword", "is not used when deploying"));
    }

    /// <summary>
    /// A password Aspire generated has no deploy-time meaning, so replacing it is not worth a
    /// warning. Pinned so the warning above cannot become noise on every model.
    /// </summary>
    [Fact]
    public void GeneratedPassword_IsReplacedSilently()
    {
        var (_, logger) = GenerateBicep(b =>
        {
            var cache = b.AddRedis("cache");
            b.AddContainer("api", "myapp/api", "latest").WithReference(cache);
        });

        Assert.Empty(logger.Matching(LogLevel.Warning, "is not used when deploying"));
    }

    /// <summary>
    /// A parameter that was substituted for a recipe-generated secret is rewritten everywhere it
    /// appears, so an unrelated consumer of the same parameter silently receives another resource's
    /// credential. The intent is genuinely ambiguous there, so it is reported rather than rejected.
    /// </summary>
    [Fact]
    public void SubstitutedParameterUsedElsewhere_IsReported()
    {
        var (_, logger) = GenerateBicep(b =>
        {
            var shared = b.AddParameter("shared", "hunter2", secret: true);
            var cache = b.AddRedis("cache", password: shared);

            b.AddContainer("api", "myapp/api", "latest")
                .WithReference(cache)
                .WithEnvironment("ADMIN_PASSWORD", shared);
        });

        Assert.Single(logger.Matching(LogLevel.Warning, "references parameter 'shared'", "credential of 'cache'"));
    }

    /// <summary>
    /// The same ambiguity, expressed through a <c>WithEnvironment</c> callback. This form records no
    /// <c>ResourceRelationshipAnnotation</c> at all, so the detection has to run where the
    /// substitution is actually applied rather than over the annotation graph.
    /// </summary>
    [Fact]
    public void SubstitutedParameterUsedInsideAnEnvironmentCallback_IsReported()
    {
        var (_, logger) = GenerateBicep(b =>
        {
            var shared = b.AddParameter("shared", "hunter2", secret: true);
            var cache = b.AddRedis("cache", password: shared);

            b.AddContainer("api", "myapp/api", "latest")
                .WithReference(cache)
                .WithEnvironment(context =>
                {
                    context.EnvironmentVariables["ADMIN_PASSWORD"] = shared;
                });
        });

        var warnings = logger.Matching(LogLevel.Warning, "references parameter 'shared'", "credential of 'cache'");
        Assert.Single(warnings);

        // The README documents this warning under ASPIRERADIUS070, so the message has to name the
        // code — otherwise the reader cannot map it back to the diagnostics table.
        Assert.Contains("ASPIRERADIUS070", warnings[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A consumer that only takes the backing resource's connection string reaches the very same
    /// substituted parameter, but through the owner's own connection string — which is exactly what
    /// the substitution is for. Pinned so the detection cannot regress into warning on every
    /// <c>WithReference</c>.
    /// </summary>
    [Fact]
    public void ConsumerOfTheOwningResource_IsNotReportedAsAnUnrelatedUse()
    {
        var (_, logger) = GenerateBicep(b =>
        {
            var password = b.AddParameter("cachepassword", "hunter2", secret: true);
            var cache = b.AddRedis("cache", password: password);

            b.AddContainer("api", "myapp/api", "latest").WithReference(cache);
        });

        Assert.Empty(logger.Matching(LogLevel.Warning, "references parameter"));
    }

    /// <summary>
    /// The <c>RecipeInputProperties</c> types write both credentials straight onto the resource, so
    /// neither registration is a projection substitution and
    /// <c>RegisterRecipeCredential</c>'s cross-owner check never fires. One parameter published as
    /// both the user name and the password is still never what the AppHost meant, and the README
    /// documents the restriction without qualifying it by type, so it has to fail here too.
    /// </summary>
    [Fact]
    public void ParameterSharedBetweenUserNameAndPasswordOfARecipeInputResource_FailsThePublish()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(b =>
        {
            var shared = b.AddParameter("shared", "value", secret: true);
            var pg = b.AddPostgres("pg", userName: shared, password: shared);
            pg.AddDatabase("pgdb");

            b.AddContainer("api", "myapp/api", "latest").WithReference(pg);
        }));

        Assert.Contains("ASPIRERADIUS070", ex.Message, StringComparison.Ordinal);
        Assert.Contains("both the user name and the password", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Values a <c>ReferenceExpression</c> declared with the <c>uri</c> format must be
    /// percent-encoded in the emitted Bicep. Aspire escapes them with <c>Uri.EscapeDataString</c>
    /// when it resolves such a value itself, but the publisher emits an expression whose value is
    /// only known at deploy time, so the escaping has to be emitted as a <c>uriComponent(...)</c>
    /// call. It matters more since the fix: the password now comes from an alphabet the recipe
    /// chooses rather than Aspire's URL-safe generated one, so an unescaped <c>@</c> or <c>/</c>
    /// would corrupt the URI.
    /// </summary>
    [Fact]
    public void UriFormattedValues_AreEscapedInTheEmittedBicep()
    {
        var (bicep, _) = GenerateBicep(b =>
        {
            var cache = b.AddRedis("cache");
            var pgdb = b.AddPostgres("pg").AddDatabase("pgdb");

            b.AddContainer("api", "myapp/api", "latest")
                .WithReference(cache)
                .WithReference(pgdb);
        });

        // The recipe-generated secret (listSecrets) and the recipe-input parameter both need it.
        Assert.Contains("uriComponent(cache.listSecrets().password)", bicep, StringComparison.Ordinal);
        Assert.Contains("uriComponent(pg_password)", bicep, StringComparison.Ordinal);

        // Non-URI values are untouched: escaping a connection-string password would corrupt it.
        Assert.Contains("value: cache.listSecrets().password", bicep, StringComparison.Ordinal);
    }

    /// <summary>
    /// Radius types the port output as an int, and Bicep's <c>uriComponent()</c> type-checks its
    /// argument eagerly rather than coercing it the way string interpolation does. A
    /// <c>uri</c>-formatted port inside a composite expression therefore has to be wrapped in
    /// <c>string(...)</c> first, exactly as the lone-projection path already does.
    /// </summary>
    [Fact]
    public void UriFormattedNumericProjection_IsConvertedToAStringFirst()
    {
        var (bicep, _) = GenerateBicep(b =>
        {
            var cache = b.AddRedis("cache");
            var port = cache.GetEndpoint("tcp").Property(EndpointProperty.Port);

            b.AddContainer("api", "myapp/api", "latest")
                .WithReference(cache)
                .WithEnvironment("PORT_URL", ReferenceExpression.Create($"tcp://cache:{port:uri}/"));
        });

        Assert.Contains("uriComponent(string(cache.properties.port))", bicep, StringComparison.Ordinal);
    }

    /// <summary>
    /// A conditional <c>ReferenceExpression</c> deliberately carries an empty <c>Format</c> and
    /// exposes the union of both branches' value providers, so the ordinary splice would resolve
    /// both branches and then emit nothing at all. The branch has to be selected first, as
    /// <c>ReferenceExpression.GetValueAsync</c> and <c>ExpressionResolver</c> both do.
    /// </summary>
    [Fact]
    public void ConditionalExpression_EmitsOnlyTheSelectedBranch()
    {
        var (bicep, _) = GenerateBicep(b =>
        {
            var mode = b.AddParameter("mode", "primary");
            var cache = b.AddRedis("cache");

            var conditional = ReferenceExpression.CreateConditional(
                mode.Resource,
                "primary",
                ReferenceExpression.Create($"primary-{cache.GetEndpoint("tcp").Property(EndpointProperty.Host)}"),
                ReferenceExpression.Create($"secondary-fallback"));

            b.AddContainer("api", "myapp/api", "latest")
                .WithReference(cache)
                .WithEnvironment("MODE_URL", conditional);
        });

        Assert.Contains("'primary-${cache.properties.host}'", bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("secondary-fallback", bicep, StringComparison.Ordinal);
    }

    /// <summary>
    /// A non-HTTP endpoint declared without a port is given an allocated one, so a consumer that
    /// references it still receives a complete address and no environment variable is dropped.
    /// </summary>
    /// <remarks>
    /// Guards the narrowed <c>catch (RadiusUnresolvableValueException)</c> in the container
    /// environment loop. The concern is that a portless <c>tcp</c> endpoint would reach
    /// <c>GetDefaultPort</c>, which throws a plain <see cref="InvalidOperationException"/> for a
    /// non-HTTP scheme and would now abort the publish. It cannot: the Radius override consults
    /// <c>RadiusServiceDiscovery.ResolveServicePort</c> first, which delegates to
    /// <c>ResourceExtensions.ResolveEndpoints</c>, whose fallback arm allocates a port
    /// (<c>ResolvedPort.Allocated(portAllocator.AllocatePort())</c>). <c>ResolveServicePort</c>
    /// returns <see langword="null"/> only for a project's synthetic default HTTPS endpoint, whose
    /// scheme <c>GetDefaultPort</c> answers with 443. This test pins that so the narrowing cannot
    /// regress into a publish failure.
    /// </remarks>
    [Fact]
    public void PortlessNonHttpEndpoint_IsResolvedRatherThanOmitted()
    {
        var (bicep, logger) = GenerateBicep(b =>
        {
            var backend = b.AddContainer("backend", "myapp/backend", "latest")
                .WithEndpoint(scheme: "tcp", name: "custom");

            b.AddContainer("api", "myapp/api", "latest")
                .WithEnvironment("BACKEND_URL", backend.GetEndpoint("custom"));
        });

        Assert.Contains("BACKEND_URL", bicep, StringComparison.Ordinal);
        Assert.Empty(logger.Matching(LogLevel.Warning, "omitted from the Radius output"));
    }

    /// <summary>
    /// The legacy SQL recipe starts a SQL Server but never creates a database, while
    /// <c>AddDatabase(...)</c> is composed into the consumer's connection string. That mismatch is
    /// reported so it is not discovered as a connection failure after deployment.
    /// </summary>
    [Fact]
    public void ReferencedSqlDatabase_IsReportedAsNotCreatedByTheRecipe()
    {
        var (_, logger) = GenerateBicep(b =>
        {
            var db = b.AddSqlServer("sql").AddDatabase("appdb");
            b.AddContainer("api", "myapp/api", "latest").WithReference(db);
        });

        Assert.Single(logger.Matching(LogLevel.Warning, "sql", "does not create databases", "appdb"));
    }

    /// <summary>
    /// An unreferenced <c>AddDatabase(...)</c> produces no consumer connection string, so it cannot
    /// mislead anyone. Pinned so the warning above cannot become noise on every model that declares
    /// a database it does not use.
    /// </summary>
    [Fact]
    public void UnreferencedSqlDatabase_IsNotReported()
    {
        var (_, logger) = GenerateBicep(b =>
        {
            var sql = b.AddSqlServer("sql");
            sql.AddDatabase("appdb");
            b.AddContainer("api", "myapp/api", "latest").WithReference(sql);
        });

        Assert.Empty(logger.Matching(LogLevel.Warning, "does not create databases"));
    }

    /// <summary>
    /// A value provider whose resolution fails and which claims no deployment-substituted semantics.
    /// </summary>
    private sealed class ThrowingValueProvider(Exception exception) : IValueProvider
    {
        public ValueTask<string?> GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken = default)
            => throw exception;

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
            => throw exception;
    }

    /// <summary>
    /// Stands in for a resource output that another deployment substitutes — an Azure
    /// <c>BicepOutputReference</c> is the real-world shape: an <see cref="IValueProvider"/> that also
    /// declares a manifest expression and cannot produce a value until its own deployment has run.
    /// </summary>
    private sealed class ThrowingDeploymentOutput(Exception exception) : IValueProvider, IManifestExpressionProvider
    {
        public string ValueExpression => "{other.outputs.x}";

        public ValueTask<string?> GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken = default)
            => throw exception;

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
            => throw exception;
    }
}
