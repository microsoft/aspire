// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Postgres;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.PostgreSQL.Tests;

public class AddPostgresTests
{
    [Fact]
    public void AddPostgresAddsHealthCheckAnnotationToResource()
    {
        var builder = DistributedApplication.CreateBuilder();
        var redis = builder.AddPostgres("postgres");
        Assert.Single(redis.Resource.Annotations, a => a is HealthCheckAnnotation hca && hca.Key == "postgres_check");
    }

    [Fact]
    public void AddPostgresAddsGeneratedPasswordParameterWithUserSecretsParameterDefaultInRunMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var pg = appBuilder.AddPostgres("pg");

        Assert.Equal("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", pg.Resource.PasswordParameter.Default?.GetType().FullName);
    }

    [Fact]
    public void AddPostgresDoesNotAddGeneratedPasswordParameterWithUserSecretsParameterDefaultInPublishMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var pg = appBuilder.AddPostgres("pg");

        Assert.NotEqual("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", pg.Resource.PasswordParameter.Default?.GetType().FullName);
    }

    [Fact]
    public async Task AddPostgresWithDefaultsAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddPostgres("myPostgres");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.GetContainerResources());
        Assert.Equal("myPostgres", containerResource.Name);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(PostgresContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(PostgresContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(PostgresContainerImageTags.Registry, containerAnnotation.Registry);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(5432, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(containerResource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Collection(config,
            env =>
            {
                Assert.Equal("POSTGRES_HOST_AUTH_METHOD", env.Key);
                Assert.Equal("scram-sha-256", env.Value);
            },
            env =>
            {
                Assert.Equal("POSTGRES_INITDB_ARGS", env.Key);
                Assert.Equal("--auth-host=scram-sha-256 --auth-local=scram-sha-256", env.Value);
            },
            env =>
            {
                Assert.Equal("POSTGRES_USER", env.Key);
                Assert.Equal("postgres", env.Value);
            },
            env =>
            {
                Assert.Equal("POSTGRES_PASSWORD", env.Key);
                Assert.False(string.IsNullOrEmpty(env.Value));
            });
    }

    [Fact]
    public async Task AddPostgresAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pass = appBuilder.AddParameter("pass", "pass");
        appBuilder.AddPostgres("myPostgres", password: pass, port: 1234);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.GetContainerResources());
        Assert.Equal("myPostgres", containerResource.Name);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(PostgresContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(PostgresContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(PostgresContainerImageTags.Registry, containerAnnotation.Registry);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(5432, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Equal(1234, endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(containerResource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Collection(config,
            env =>
            {
                Assert.Equal("POSTGRES_HOST_AUTH_METHOD", env.Key);
                Assert.Equal("scram-sha-256", env.Value);
            },
            env =>
            {
                Assert.Equal("POSTGRES_INITDB_ARGS", env.Key);
                Assert.Equal("--auth-host=scram-sha-256 --auth-local=scram-sha-256", env.Value);
            },
            env =>
            {
                Assert.Equal("POSTGRES_USER", env.Key);
                Assert.Equal("postgres", env.Value);
            },
            env =>
            {
                Assert.Equal("POSTGRES_PASSWORD", env.Key);
                Assert.Equal("pass", env.Value);
            });
    }

    [Fact]
    public async Task PostgresCreatesConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var postgres = appBuilder.AddPostgres("postgres")
                                 .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        var connectionStringResource = postgres.Resource as IResourceWithConnectionString;

        var connectionString = await connectionStringResource.GetConnectionStringAsync();
        Assert.Equal("Host={postgres.bindings.tcp.host};Port={postgres.bindings.tcp.port};Username=postgres;Password={postgres-password.value}", connectionStringResource.ConnectionStringExpression.ValueExpression);
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"Host=localhost;Port=2000;Username=postgres;Password={postgres.Resource.PasswordParameter.Value}", connectionString);
#pragma warning restore CS0618 // Type or member is obsolete
    }

    [Fact]
    public async Task PostgresCreatesConnectionStringWithDatabase()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddPostgres("postgres")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000))
            .AddDatabase("db");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var postgresResource = Assert.Single(appModel.Resources.OfType<PostgresServerResource>());
        var postgresConnectionString = await postgresResource.GetConnectionStringAsync();
        var postgresDatabaseResource = Assert.Single(appModel.Resources.OfType<PostgresDatabaseResource>());
        var postgresDatabaseConnectionStringResource = (IResourceWithConnectionString)postgresDatabaseResource;
        var dbConnectionString = await postgresDatabaseConnectionStringResource.GetConnectionStringAsync();

        Assert.Equal("{postgres.connectionString};Database=db", postgresDatabaseResource.ConnectionStringExpression.ValueExpression);
        Assert.Equal(postgresConnectionString + ";Database=db", dbConnectionString);
    }

    [Fact]
    public async Task WithReferenceDispatchesPostgresDatabaseReference()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var postgres = appBuilder.AddPostgres("postgres")
            .WithEndpoint("tcp", e =>
            {
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000);
                e.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork, new AllocatedEndpoint(e, "postgres.dev.internal", 2000, EndpointBindingMode.SingleAddress, targetPortExpression: null, networkID: KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
            });
        var database = postgres.AddDatabase("db");
        var consumer = appBuilder.AddContainer("consumer", "fake");

        InvokeWithReference(consumer, database);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"Host=postgres.dev.internal;Port=2000;Username=postgres;Password={postgres.Resource.PasswordParameter.Value};Database=db", config["ConnectionStrings__db"]);
#pragma warning restore CS0618 // Type or member is obsolete
    }

    [Fact]
    public async Task AddDatabaseToPostgresAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pass = appBuilder.AddParameter("pass", "pass");
        appBuilder.AddPostgres("postgres", password: pass, port: 1234).AddDatabase("db");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResources = appModel.GetContainerResources();

        var containerResource = Assert.Single(containerResources);
        Assert.Equal("postgres", containerResource.Name);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(PostgresContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(PostgresContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(PostgresContainerImageTags.Registry, containerAnnotation.Registry);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(5432, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Equal(1234, endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(containerResource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Collection(config,
            env =>
            {
                Assert.Equal("POSTGRES_HOST_AUTH_METHOD", env.Key);
                Assert.Equal("scram-sha-256", env.Value);
            },
            env =>
            {
                Assert.Equal("POSTGRES_INITDB_ARGS", env.Key);
                Assert.Equal("--auth-host=scram-sha-256 --auth-local=scram-sha-256", env.Value);
            },
            env =>
            {
                Assert.Equal("POSTGRES_USER", env.Key);
                Assert.Equal("postgres", env.Value);
            },
            env =>
            {
                Assert.Equal("POSTGRES_PASSWORD", env.Key);
                Assert.Equal("pass", env.Value);
            });
    }

    [Fact]
    public async Task VerifyManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var pgServer = builder.AddPostgres("pg");
        var db = pgServer.AddDatabase("db");

        var serverManifest = await ManifestUtils.GetManifest(pgServer.Resource);
        var dbManifest = await ManifestUtils.GetManifest(db.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "Host={pg.bindings.tcp.host};Port={pg.bindings.tcp.port};Username=postgres;Password={pg-password.value}",
              "image": "{{PostgresContainerImageTags.Registry}}/{{PostgresContainerImageTags.Image}}:{{PostgresContainerImageTags.Tag}}",
              "env": {
                "POSTGRES_HOST_AUTH_METHOD": "scram-sha-256",
                "POSTGRES_INITDB_ARGS": "--auth-host=scram-sha-256 --auth-local=scram-sha-256",
                "POSTGRES_USER": "postgres",
                "POSTGRES_PASSWORD": "{pg-password.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 5432
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, serverManifest.ToString());

        expectedManifest = """
            {
              "type": "value.v0",
              "connectionString": "{pg.connectionString};Database=db"
            }
            """;
        Assert.Equal(expectedManifest, dbManifest.ToString());
    }

    [Fact]
    public async Task VerifyManifestWithParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var userNameParameter = builder.AddParameter("user");
        var passwordParameter = builder.AddParameter("pass");

        var pgServer = builder.AddPostgres("pg", userNameParameter, passwordParameter);
        var serverManifest = await ManifestUtils.GetManifest(pgServer.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "Host={pg.bindings.tcp.host};Port={pg.bindings.tcp.port};Username={user.value};Password={pass.value}",
              "image": "{{PostgresContainerImageTags.Registry}}/{{PostgresContainerImageTags.Image}}:{{PostgresContainerImageTags.Tag}}",
              "env": {
                "POSTGRES_HOST_AUTH_METHOD": "scram-sha-256",
                "POSTGRES_INITDB_ARGS": "--auth-host=scram-sha-256 --auth-local=scram-sha-256",
                "POSTGRES_USER": "{user.value}",
                "POSTGRES_PASSWORD": "{pass.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 5432
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, serverManifest.ToString());

        pgServer = builder.AddPostgres("pg2", userNameParameter);
        serverManifest = await ManifestUtils.GetManifest(pgServer.Resource);

        expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "Host={pg2.bindings.tcp.host};Port={pg2.bindings.tcp.port};Username={user.value};Password={pg2-password.value}",
              "image": "{{PostgresContainerImageTags.Registry}}/{{PostgresContainerImageTags.Image}}:{{PostgresContainerImageTags.Tag}}",
              "env": {
                "POSTGRES_HOST_AUTH_METHOD": "scram-sha-256",
                "POSTGRES_INITDB_ARGS": "--auth-host=scram-sha-256 --auth-local=scram-sha-256",
                "POSTGRES_USER": "{user.value}",
                "POSTGRES_PASSWORD": "{pg2-password.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 5432
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, serverManifest.ToString());

        pgServer = builder.AddPostgres("pg3", password: passwordParameter);
        serverManifest = await ManifestUtils.GetManifest(pgServer.Resource);

        expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "Host={pg3.bindings.tcp.host};Port={pg3.bindings.tcp.port};Username=postgres;Password={pass.value}",
              "image": "{{PostgresContainerImageTags.Registry}}/{{PostgresContainerImageTags.Image}}:{{PostgresContainerImageTags.Tag}}",
              "env": {
                "POSTGRES_HOST_AUTH_METHOD": "scram-sha-256",
                "POSTGRES_INITDB_ARGS": "--auth-host=scram-sha-256 --auth-local=scram-sha-256",
                "POSTGRES_USER": "postgres",
                "POSTGRES_PASSWORD": "{pass.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 5432
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, serverManifest.ToString());
    }

    [Fact]
    public void WithPgAdminAddsContainer()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddPostgres("mypostgres").WithPgAdmin(pga => pga.WithHostPort(8081));

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = builder.Resources.Single(r => r.Name == "pgadmin");
        var createFile = container.Annotations.OfType<ContainerFileSystemCallbackAnnotation>().Single();

        Assert.Equal("/pgadmin4", createFile.DestinationPath);
    }

    [Fact]
    public void WithPgWebAddsWithPgWebResource()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddPostgres("mypostgres1").WithPgWeb();
        builder.AddPostgres("mypostgres2").WithPgWeb();

        Assert.Single(builder.Resources.OfType<PgWebContainerResource>());
    }

    [Fact]
    public void WithPgWebSupportsChangingContainerImageValues()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddPostgres("mypostgres").WithPgWeb(c =>
        {
            c.WithImageRegistry("example.mycompany.com");
            c.WithImage("customrediscommander");
            c.WithImageTag("someothertag");
        });

        var resource = Assert.Single(builder.Resources.OfType<PgWebContainerResource>());
        var containerAnnotation = Assert.Single(resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("example.mycompany.com", containerAnnotation.Registry);
        Assert.Equal("customrediscommander", containerAnnotation.Image);
        Assert.Equal("someothertag", containerAnnotation.Tag);
    }

    [Fact]
    public void WithRedisInsightSupportsChangingHostPort()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddPostgres("mypostgres").WithPgWeb(c =>
        {
            c.WithHostPort(1000);
        });

        var resource = Assert.Single(builder.Resources.OfType<PgWebContainerResource>());
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(1000, endpoint.Port);
    }

    [Fact]
    public void WithPgAdminWithCallbackMutatesImage()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddPostgres("mypostgres").WithPgAdmin(pga => pga.WithImageTag("8.3"));

        var container = builder.Resources.Single(r => r.Name == "pgadmin");
        var imageAnnotation = container.Annotations.OfType<ContainerImageAnnotation>().Single();

        Assert.Equal("8.3", imageAnnotation.Tag);
    }

    [Fact]
    public void WithPostgresTwiceEndsUpWithOneContainer()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddPostgres("mypostgres1").WithPgAdmin(pga => pga.WithHostPort(8081));
        builder.AddPostgres("mypostgres2").WithPgAdmin(pga => pga.WithHostPort(8081));

        Assert.Single(builder.Resources, r => r.Name.Equals("pgadmin"));
    }

    [Fact]
    public async Task WithPostgresProducesValidServersJsonFile()
    {
        var builder = DistributedApplication.CreateBuilder();

        using var tempStore = new TestTempDirectory();
        builder.Configuration["Aspire:Store:Path"] = tempStore.Path;

        var username = builder.AddParameter("pg-user", "myuser");
        var pg1 = builder.AddPostgres("mypostgres1").WithPgAdmin(pga => pga.WithHostPort(8081));
        var pg2 = builder.AddPostgres("mypostgres2", username).WithPgAdmin(pga => pga.WithHostPort(8081));

        // Add fake allocated endpoints.
        pg1.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5001));
        pg2.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5002, "host2"));

        using var app = builder.Build();

        var pgadmin = builder.Resources.Single(r => r.Name.Equals("pgadmin"));

        var createServers = pgadmin.Annotations.OfType<ContainerFileSystemCallbackAnnotation>().Single();

        Assert.Equal("/pgadmin4", createServers.DestinationPath);
        Assert.Null(createServers.Umask);
        Assert.Null(createServers.DefaultOwner);
        Assert.Null(createServers.DefaultGroup);

        var entries = await createServers.Callback(new() { Model = pgadmin, ServiceProvider = app.Services }, CancellationToken.None);

        var serversFile = Assert.IsType<ContainerFile>(entries.First());
        Assert.NotNull(serversFile.Contents);
        Assert.Equal(UnixFileMode.None, serversFile.Mode);

        var document = JsonDocument.Parse(serversFile.Contents!);

        var servers = document.RootElement.GetProperty("Servers");

        // Make sure the first server is correct.
        Assert.Equal(pg1.Resource.Name, servers.GetProperty("1").GetProperty("Name").GetString());
        Assert.Equal("Servers", servers.GetProperty("1").GetProperty("Group").GetString());
        Assert.Equal("mypostgres1", servers.GetProperty("1").GetProperty("Host").GetString());
        Assert.Equal(5432, servers.GetProperty("1").GetProperty("Port").GetInt32());
        Assert.Equal("postgres", servers.GetProperty("1").GetProperty("Username").GetString());
        Assert.Equal("prefer", servers.GetProperty("1").GetProperty("SSLMode").GetString());
        Assert.Equal("postgres", servers.GetProperty("1").GetProperty("MaintenanceDB").GetString());
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"echo '{pg1.Resource.PasswordParameter.Value}'", servers.GetProperty("1").GetProperty("PasswordExecCommand").GetString());
#pragma warning restore CS0618 // Type or member is obsolete

        // Make sure the second server is correct.
        Assert.Equal(pg2.Resource.Name, servers.GetProperty("2").GetProperty("Name").GetString());
        Assert.Equal("Servers", servers.GetProperty("2").GetProperty("Group").GetString());
        Assert.Equal("mypostgres2", servers.GetProperty("2").GetProperty("Host").GetString());
        Assert.Equal(5432, servers.GetProperty("2").GetProperty("Port").GetInt32());
        Assert.Equal("myuser", servers.GetProperty("2").GetProperty("Username").GetString());
        Assert.Equal("prefer", servers.GetProperty("2").GetProperty("SSLMode").GetString());
        Assert.Equal("postgres", servers.GetProperty("2").GetProperty("MaintenanceDB").GetString());
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"echo '{pg2.Resource.PasswordParameter.Value}'", servers.GetProperty("2").GetProperty("PasswordExecCommand").GetString());
#pragma warning restore CS0618 // Type or member is obsolete
    }

    [Fact]
    public async Task WithPgwebProducesValidBookmarkFiles()
    {
        var builder = DistributedApplication.CreateBuilder();

        using var tempStore = new TestTempDirectory();
        builder.Configuration["Aspire:Store:Path"] = tempStore.Path;

        var pg1 = builder.AddPostgres("mypostgres1").WithPgWeb(pga => pga.WithHostPort(8081));
        var pg2 = builder.AddPostgres("mypostgres2").WithPgWeb(pga => pga.WithHostPort(8081));

        // Add fake allocated endpoints.
        pg1.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5001));
        pg2.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5002, "host2"));

        var db1 = pg1.AddDatabase("db1");
        var db2 = pg2.AddDatabase("db2");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var pgweb = builder.Resources.Single(r => r.Name.Equals("pgweb"));
        var createBookmarks = pgweb.Annotations.OfType<ContainerFileSystemCallbackAnnotation>().Single();

        Assert.Equal("/", createBookmarks.DestinationPath);
        Assert.Null(createBookmarks.Umask);
        Assert.Null(createBookmarks.DefaultOwner);
        Assert.Null(createBookmarks.DefaultGroup);

        var entries = await createBookmarks.Callback(new() { Model = pgweb, ServiceProvider = app.Services }, CancellationToken.None);

        var pgWebDirectory = Assert.IsType<ContainerDirectory>(entries.First());
        Assert.Equal(".pgweb", pgWebDirectory.Name);
        Assert.Single(pgWebDirectory.Entries);

        var bookmarksDirectory = Assert.IsType<ContainerDirectory>(pgWebDirectory.Entries.First());
        Assert.Equal("bookmarks", bookmarksDirectory.Name);

        Assert.Collection(bookmarksDirectory.Entries,
            entry =>
            {
                var file = Assert.IsType<ContainerFile>(entry);
                Assert.Equal(".toml", Path.GetExtension(file.Name));
                Assert.Equal(UnixFileMode.None, file.Mode);
                Assert.Equal(CreatePgWebBookmarkfileContent(db1.Resource), file.Contents);
            },
            entry =>
            {
                var file = Assert.IsType<ContainerFile>(entry);
                Assert.Equal(".toml", Path.GetExtension(file.Name));
                Assert.Equal(UnixFileMode.None, file.Mode);
                Assert.Equal(CreatePgWebBookmarkfileContent(db2.Resource), file.Contents);
            });
    }

    [Fact]
    public void ThrowsWithIdenticalChildResourceNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var db = builder.AddPostgres("postgres1");
        db.AddDatabase("db");

        Assert.Throws<DistributedApplicationException>(() => db.AddDatabase("db"));
    }

    [Fact]
    public void ThrowsWithIdenticalChildResourceNamesDifferentParents()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddPostgres("postgres1")
            .AddDatabase("db");

        var db = builder.AddPostgres("postgres2");
        Assert.Throws<DistributedApplicationException>(() => db.AddDatabase("db"));
    }

    [Fact]
    public void CanAddDatabasesWithDifferentNamesOnSingleServer()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var postgres1 = builder.AddPostgres("postgres1");

        var db1 = postgres1.AddDatabase("db1", "customers1");
        var db2 = postgres1.AddDatabase("db2", "customers2");

        Assert.Equal("customers1", db1.Resource.DatabaseName);
        Assert.Equal("customers2", db2.Resource.DatabaseName);

        Assert.Equal("{postgres1.connectionString};Database=customers1", db1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("{postgres1.connectionString};Database=customers2", db2.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void CanAddDatabasesWithTheSameNameOnMultipleServers()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var db1 = builder.AddPostgres("postgres1")
            .AddDatabase("db1", "imports");

        var db2 = builder.AddPostgres("postgres2")
            .AddDatabase("db2", "imports");

        Assert.Equal("imports", db1.Resource.DatabaseName);
        Assert.Equal("imports", db2.Resource.DatabaseName);

        Assert.Equal("{postgres1.connectionString};Database=imports", db1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("{postgres2.connectionString};Database=imports", db2.Resource.ConnectionStringExpression.ValueExpression);
    }

    private static string CreatePgWebBookmarkfileContent(PostgresDatabaseResource postgresDatabase)
    {
#pragma warning disable CS0618 // Type or member is obsolete
        var user = postgresDatabase.Parent.UserNameParameter?.Value ?? "postgres";

        // We're hardcoding references to container resources based on a default Aspire network
        // This will need to be refactored once updated service discovery APIs are available
        var fileContent = $"""
                host = "{postgresDatabase.Parent.Name}"
                port = {postgresDatabase.Parent.PrimaryEndpoint.TargetPort}
                user = "{user}"
                password = "{postgresDatabase.Parent.PasswordParameter.Value}"
                database = "{postgresDatabase.DatabaseName}"
                sslmode = "disable"
                """;
#pragma warning restore CS0618 // Type or member is obsolete

        return fileContent;
    }

    [Fact]
    public void VerifyPostgresServerResourceWithHostPort()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddPostgres("postgres")
            .WithHostPort(1000);

        var resource = Assert.Single(builder.Resources.OfType<PostgresServerResource>());
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(1000, endpoint.Port);
    }

    [Fact]
    public async Task VerifyPostgresServerResourceWithPassword()
    {
        var builder = DistributedApplication.CreateBuilder();
        var password = "p@ssw0rd1";
        var pass = builder.AddParameter("pass", password);
        var postgres = builder.AddPostgres("postgres")
                                 .WithPassword(pass)
                                 .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        var connectionString = await postgres.Resource.GetConnectionStringAsync();
        Assert.Equal("Host=localhost;Port=2000;Username=postgres;Password=p@ssw0rd1", connectionString);
    }

    [Fact]
    public async Task VerifyPostgresServerResourceWithUserName()
    {
        var builder = DistributedApplication.CreateBuilder();
        var user = "user1";
        var pass = builder.AddParameter("user", user);
        var postgres = builder.AddPostgres("postgres")
                                 .WithUserName(pass)
                                 .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        var connectionString = await postgres.Resource.GetConnectionStringAsync();
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"Host=localhost;Port=2000;Username=user1;Password={postgres.Resource.PasswordParameter.Value}", connectionString);
#pragma warning restore CS0618 // Type or member is obsolete
    }

    [Fact]
    public async Task PostgresEnvironmentCallbackIsIdempotent()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var postgres = appBuilder.AddPostgres("postgres")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5432));

        // Call GetEnvironmentVariablesAsync multiple times to ensure callbacks are idempotent
        var config1 = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(postgres.Resource);
        var config2 = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(postgres.Resource);

        // Both calls should succeed and return the same values
        Assert.Equal(config1.Count, config2.Count);
        // Verify that environment variables are set consistently across multiple calls
        Assert.All(config1, kvp =>
        {
            Assert.True(config2.ContainsKey(kvp.Key), $"Key {kvp.Key} should exist in second call");
            Assert.Equal(kvp.Value, config2[kvp.Key]);
        });
    }

    private static readonly MethodInfo s_polyglotWithReferenceMethod = typeof(ResourceBuilderExtensions)
        .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(m => m.Name == nameof(ResourceBuilderExtensions.WithReference)
            && m.IsGenericMethodDefinition
            && m.GetParameters() is { Length: 5 } parameters
            && parameters[1].ParameterType == typeof(IResourceBuilder<IResource>));

    private static IResourceBuilder<TDestination> InvokeWithReference<TDestination>(
        IResourceBuilder<TDestination> builder,
        IResourceBuilder<IResource> source,
        string? connectionName = null,
        bool optional = false,
        string? name = null)
        where TDestination : IResourceWithEnvironment
    {
        return (IResourceBuilder<TDestination>)s_polyglotWithReferenceMethod
            .MakeGenericMethod(typeof(TDestination))
            .Invoke(null, [builder, source, connectionName, optional, name])!;
    }

    [Theory]
    [InlineData("17.6", 17)]
    [InlineData("18.1", 18)]
    [InlineData("18", 18)]
    [InlineData("18-alpine", 18)]
    [InlineData("17.6-bookworm", 17)]
    [InlineData("16.0", 16)]
    [InlineData("9.6", 9)]
    public void TryParsePostgresMajorVersionReturnsTrueForValidTags(string tag, int expectedMajorVersion)
    {
        var result = PostgresBuilderExtensions.TryParsePostgresMajorVersion(tag, out var majorVersion);

        Assert.True(result);
        Assert.Equal(expectedMajorVersion, majorVersion);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("alpine")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    public void TryParsePostgresMajorVersionReturnsFalseForInvalidTags(string tag)
    {
        var result = PostgresBuilderExtensions.TryParsePostgresMajorVersion(tag, out var majorVersion);

        Assert.False(result);
        Assert.Equal(0, majorVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void WithDataVolumeUsesLegacyPathForPostgres17(bool? isReadOnly)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var postgres = builder.AddPostgres("myPostgres");

        // Default image is v17.x, so should use legacy path
        if (isReadOnly.HasValue)
        {
            postgres.WithDataVolume(isReadOnly: isReadOnly.Value);
        }
        else
        {
            postgres.WithDataVolume();
        }

        var volumeAnnotation = postgres.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();

        Assert.Equal($"{builder.GetVolumePrefix()}-myPostgres-data", volumeAnnotation.Source);
        Assert.Equal("/var/lib/postgresql/data", volumeAnnotation.Target);
        Assert.Equal(ContainerMountType.Volume, volumeAnnotation.Type);
        Assert.Equal(isReadOnly ?? false, volumeAnnotation.IsReadOnly);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void WithDataVolumeUsesNewPathForPostgres18(bool? isReadOnly)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var postgres = builder.AddPostgres("myPostgres")
            .WithImage("postgres", "18.1");

        if (isReadOnly.HasValue)
        {
            postgres.WithDataVolume(isReadOnly: isReadOnly.Value);
        }
        else
        {
            postgres.WithDataVolume();
        }

        var volumeAnnotation = postgres.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();

        Assert.Equal($"{builder.GetVolumePrefix()}-myPostgres-data", volumeAnnotation.Source);
        Assert.Equal("/var/lib/postgresql", volumeAnnotation.Target);
        Assert.Equal(ContainerMountType.Volume, volumeAnnotation.Type);
        Assert.Equal(isReadOnly ?? false, volumeAnnotation.IsReadOnly);
    }

    [Fact]
    public void WithDataVolumeUsesNewPathForPostgres18Alpine()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var postgres = builder.AddPostgres("myPostgres")
            .WithImage("postgres", "18-alpine")
            .WithDataVolume();

        var volumeAnnotation = postgres.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();

        Assert.Equal("/var/lib/postgresql", volumeAnnotation.Target);
    }

    [Fact]
    public void WithDataVolumeUsesLegacyPathForUnparsableTag()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var postgres = builder.AddPostgres("myPostgres")
            .WithImage("postgres", "latest")
            .WithDataVolume();

        var volumeAnnotation = postgres.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();

        // When tag can't be parsed, fall back to legacy path for safety
        Assert.Equal("/var/lib/postgresql/data", volumeAnnotation.Target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void WithDataBindMountUsesLegacyPathForPostgres17(bool? isReadOnly)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var postgres = builder.AddPostgres("myPostgres");

        // Default image is v17.x, so should use legacy path
        if (isReadOnly.HasValue)
        {
            postgres.WithDataBindMount("mydata", isReadOnly: isReadOnly.Value);
        }
        else
        {
            postgres.WithDataBindMount("mydata");
        }

        var volumeAnnotation = postgres.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();

        Assert.Equal(Path.Combine(builder.AppHostDirectory, "mydata"), volumeAnnotation.Source);
        Assert.Equal("/var/lib/postgresql/data", volumeAnnotation.Target);
        Assert.Equal(ContainerMountType.BindMount, volumeAnnotation.Type);
        Assert.Equal(isReadOnly ?? false, volumeAnnotation.IsReadOnly);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void WithDataBindMountUsesNewPathForPostgres18(bool? isReadOnly)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var postgres = builder.AddPostgres("myPostgres")
            .WithImage("postgres", "18.1");

        if (isReadOnly.HasValue)
        {
            postgres.WithDataBindMount("mydata", isReadOnly: isReadOnly.Value);
        }
        else
        {
            postgres.WithDataBindMount("mydata");
        }

        var volumeAnnotation = postgres.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();

        Assert.Equal(Path.Combine(builder.AppHostDirectory, "mydata"), volumeAnnotation.Source);
        Assert.Equal("/var/lib/postgresql", volumeAnnotation.Target);
        Assert.Equal(ContainerMountType.BindMount, volumeAnnotation.Type);
        Assert.Equal(isReadOnly ?? false, volumeAnnotation.IsReadOnly);
    }
}
