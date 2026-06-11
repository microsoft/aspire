// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.MySql.Tests;

public class AddMySqlTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddMySqlAddsGeneratedPasswordParameterWithUserSecretsParameterDefaultInRunMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var mysql = appBuilder.AddMySql("mysql");

        Assert.Equal("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", mysql.Resource.PasswordParameter.Default?.GetType().FullName);
    }

    [Fact]
    public void AddMySqlDoesNotAddGeneratedPasswordParameterWithUserSecretsParameterDefaultInPublishMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var mysql = appBuilder.AddMySql("mysql");

        Assert.NotEqual("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", mysql.Resource.PasswordParameter.Default?.GetType().FullName);
    }

    [Fact]
    public async Task AddMySqlContainerWithDefaultsAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddMySql("mysql");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<MySqlServerResource>());
        Assert.Equal("mysql", containerResource.Name);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(MySqlContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(MySqlContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(MySqlContainerImageTags.Registry, containerAnnotation.Registry);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(3306, endpoint.TargetPort);
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
                Assert.Equal("MYSQL_ROOT_PASSWORD", env.Key);
                Assert.False(string.IsNullOrEmpty(env.Value));
            });
    }

    [Fact]
    public async Task AddMySqlAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pass = appBuilder.AddParameter("pass", "pass");
        appBuilder.AddMySql("mysql", pass, 1234);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.GetContainerResources());
        Assert.Equal("mysql", containerResource.Name);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(MySqlContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(MySqlContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(MySqlContainerImageTags.Registry, containerAnnotation.Registry);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(3306, endpoint.TargetPort);
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
                Assert.Equal("MYSQL_ROOT_PASSWORD", env.Key);
                Assert.Equal("pass", env.Value);
            });
    }

    [Fact]
    public async Task MySqlCreatesConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddMySql("mysql")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var connectionStringResource = Assert.Single(appModel.Resources.OfType<IResourceWithConnectionString>());
        var connectionString = await connectionStringResource.GetConnectionStringAsync();

        Assert.Equal("Server={mysql.bindings.tcp.host};Port={mysql.bindings.tcp.port};User ID=root;Password={mysql-password.value}", connectionStringResource.ConnectionStringExpression.ValueExpression);
        Assert.StartsWith("Server=localhost;Port=2000;User ID=root;Password=", connectionString);
    }

    [Fact]
    public async Task MySqlCreatesConnectionStringWithDatabase()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddMySql("mysql")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000))
            .AddDatabase("db");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var mySqlResource = Assert.Single(appModel.Resources.OfType<MySqlServerResource>());
        var mySqlConnectionStringResource = (IResourceWithConnectionString)mySqlResource;
        var mySqlConnectionString = await mySqlConnectionStringResource.GetConnectionStringAsync();
        var mySqlDatabaseResource = Assert.Single(appModel.Resources.OfType<MySqlDatabaseResource>());
        var mySqlDatabaseConnectionStringResource = (IResourceWithConnectionString)mySqlDatabaseResource;
        var dbConnectionString = await mySqlDatabaseConnectionStringResource.GetConnectionStringAsync();

        Assert.Equal(mySqlConnectionString + ";Database=db", dbConnectionString);
        Assert.Equal("{mysql.connectionString};Database=db", mySqlDatabaseResource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task VerifyManifest()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mysql = appBuilder.AddMySql("mysql");
        var db = mysql.AddDatabase("db");

        var mySqlManifest = await ManifestUtils.GetManifest(mysql.Resource);
        var dbManifest = await ManifestUtils.GetManifest(db.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "Server={mysql.bindings.tcp.host};Port={mysql.bindings.tcp.port};User ID=root;Password={mysql-password.value}",
              "image": "{{MySqlContainerImageTags.Registry}}/{{MySqlContainerImageTags.Image}}:{{MySqlContainerImageTags.Tag}}",
              "env": {
                "MYSQL_ROOT_PASSWORD": "{mysql-password.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 3306
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, mySqlManifest.ToString());

        expectedManifest = """
            {
              "type": "value.v0",
              "connectionString": "{mysql.connectionString};Database=db"
            }
            """;
        Assert.Equal(expectedManifest, dbManifest.ToString());
    }

    [Fact]
    public async Task VerifyManifestWithPasswordParameter()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var pass = appBuilder.AddParameter("pass");

        var mysql = appBuilder.AddMySql("mysql", pass);
        var serverManifest = await ManifestUtils.GetManifest(mysql.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "Server={mysql.bindings.tcp.host};Port={mysql.bindings.tcp.port};User ID=root;Password={pass.value}",
              "image": "{{MySqlContainerImageTags.Registry}}/{{MySqlContainerImageTags.Image}}:{{MySqlContainerImageTags.Tag}}",
              "env": {
                "MYSQL_ROOT_PASSWORD": "{pass.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 3306
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, serverManifest.ToString());
    }

    [Fact]
    public void WithMySqlTwiceEndsUpWithOneAdminContainer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.AddMySql("mySql").WithPhpMyAdmin();
        builder.AddMySql("mySql2").WithPhpMyAdmin();

        Assert.Single(builder.Resources.OfType<ContainerResource>(), resource => resource.Name == "phpmyadmin");
    }

    [Fact]
    public async Task SingleMySqlInstanceProducesCorrectMySqlHostsVariable()
    {
        var builder = DistributedApplication.CreateBuilder();
        var mysql = builder.AddMySql("mySql").WithPhpMyAdmin();
        using var app = builder.Build();

        // Add fake allocated endpoints.
        mysql.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5001));

        var myAdmin = builder.Resources.Single(r => r.Name.Equals("phpmyadmin"));

        await builder.Eventing.PublishAsync<BeforeResourceStartedEvent>(new(myAdmin, app.Services));

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(myAdmin, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        var container = builder.Resources.Single(r => r.Name == "phpmyadmin");
        Assert.Empty(container.Annotations.OfType<ContainerMountAnnotation>());

        Assert.Equal($"{mysql.Resource.Name}:{mysql.Resource.PrimaryEndpoint.TargetPort}", config["PMA_HOST"]);
        Assert.NotNull(config["PMA_USER"]);
        Assert.NotNull(config["PMA_PASSWORD"]);
    }

    [Fact]
    public void WithPhpMyAdminProducesValidServerConfigFile()
    {
        var builder = DistributedApplication.CreateBuilder();

        using var tempStore = new TestTempDirectory();
        builder.Configuration["Aspire:Store:Path"] = tempStore.Path;

        var mysql1 = builder.AddMySql("mysql1").WithPhpMyAdmin(c => c.WithHostPort(8081));
        var mysql2 = builder.AddMySql("mysql2").WithPhpMyAdmin(c => c.WithHostPort(8081));

        // Add fake allocated endpoints.
        mysql1.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5001));
        mysql2.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5002, "host3"));

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var myAdmin = builder.Resources.Single(r => r.Name.Equals("phpmyadmin"));
        builder.Eventing.PublishAsync<BeforeResourceStartedEvent>(new(myAdmin, app.Services));

        var volume = myAdmin.Annotations.OfType<ContainerMountAnnotation>().Single();

        using var stream = File.OpenRead(volume.Source!);
        var fileContents = new StreamReader(stream).ReadToEnd();

        // check to see that the two hosts are in the file
        string pattern1 = $@"\$cfg\['Servers'\]\[\$i\]\['host'\] = '{mysql1.Resource.Name}:{mysql1.Resource.PrimaryEndpoint.TargetPort}';";
        string pattern2 = $@"\$cfg\['Servers'\]\[\$i\]\['host'\] = '{mysql2.Resource.Name}:{mysql2.Resource.PrimaryEndpoint.TargetPort}';";
        Match match1 = Regex.Match(fileContents, pattern1);
        Assert.True(match1.Success);
        Match match2 = Regex.Match(fileContents, pattern2);
        Assert.True(match2.Success);
    }

    [Fact]
    public void ThrowsWithIdenticalChildResourceNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var db = builder.AddMySql("mysql1");
        db.AddDatabase("db");

        Assert.Throws<DistributedApplicationException>(() => db.AddDatabase("db"));
    }

    [Fact]
    public void ThrowsWithIdenticalChildResourceNamesDifferentParents()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        builder.AddMySql("mysql1")
            .AddDatabase("db");

        var db = builder.AddMySql("mysql2");
        Assert.Throws<DistributedApplicationException>(() => db.AddDatabase("db"));
    }

    [Fact]
    public void CanAddDatabasesWithDifferentNamesOnSingleServer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var mysql1 = builder.AddMySql("mysql1");

        var db1 = mysql1.AddDatabase("db1", "customers1");
        var db2 = mysql1.AddDatabase("db2", "customers2");

        Assert.Equal(["db1", "db2"], mysql1.Resource.Databases.Keys);
        Assert.Equal(["customers1", "customers2"], mysql1.Resource.Databases.Values);

        Assert.Equal("customers1", db1.Resource.DatabaseName);
        Assert.Equal("customers2", db2.Resource.DatabaseName);

        Assert.Equal("{mysql1.connectionString};Database=customers1", db1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("{mysql1.connectionString};Database=customers2", db2.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void CanAddDatabasesWithTheSameNameOnMultipleServers()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var db1 = builder.AddMySql("mysql1")
            .AddDatabase("db1", "imports");

        var db2 = builder.AddMySql("mysql2")
            .AddDatabase("db2", "imports");

        Assert.Equal("imports", db1.Resource.DatabaseName);
        Assert.Equal("imports", db2.Resource.DatabaseName);

        Assert.Equal("{mysql1.connectionString};Database=imports", db1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("{mysql2.connectionString};Database=imports", db2.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task VerifyMySqlServerResourceWithPassword()
    {
        var builder = DistributedApplication.CreateBuilder();
        var password = "p@ssw0rd1";
        var pass = builder.AddParameter("pass", password);
        var mysql = builder.AddMySql("mysql")
                               .WithPassword(pass)
                               .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var connectionStringResource = Assert.Single(appModel.Resources.OfType<MySqlServerResource>());
        var connectionString = await connectionStringResource.ConnectionStringExpression.GetValueAsync(default);
        Assert.Equal("Server=localhost;Port=2000;User ID=root;Password=p@ssw0rd1", connectionString);
    }

    [Fact]
    public async Task PhpMyAdminEnvironmentCallbackIsIdempotent()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var mysql = appBuilder.AddMySql("mysql")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 3306))
            .WithPhpMyAdmin();

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var phpMyAdminResource = Assert.Single(appModel.Resources.OfType<PhpMyAdminContainerResource>());

        // Trigger the BeforeResourceStartedEvent to add environment callbacks
        await appBuilder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(phpMyAdminResource, app.Services),
            EventDispatchBehavior.BlockingSequential);

        // Call GetEnvironmentVariablesAsync multiple times to ensure callbacks are idempotent
        var config1 = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(phpMyAdminResource);
        var config2 = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(phpMyAdminResource);

        // Both calls should succeed and return the same values
        Assert.Equal(config1.Count, config2.Count);
        Assert.Contains(config1, kvp => kvp.Key == "PMA_HOST");
        Assert.Contains(config2, kvp => kvp.Key == "PMA_HOST");
        Assert.Equal(
            config1.First(kvp => kvp.Key == "PMA_HOST").Value,
            config2.First(kvp => kvp.Key == "PMA_HOST").Value);
    }
}
