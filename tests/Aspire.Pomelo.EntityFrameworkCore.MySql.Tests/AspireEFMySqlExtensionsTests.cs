// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.MySql;
using Aspire.MySqlConnector.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure.Internal;
using Xunit;

namespace Aspire.Pomelo.EntityFrameworkCore.MySql.Tests;

public class AspireEFMySqlExtensionsTests : IClassFixture<MySqlContainerFixture>
{
    private const string ConnectionStringSuffixAddedByPomelo = ";Allow User Variables=True;Use Affected Rows=False";
    private static readonly MySqlServerVersion s_serverVersion = new(new Version(MySqlContainerImageTags.Tag));
    private static readonly string s_serverVersionString = s_serverVersion.ToString();
    private readonly MySqlContainerFixture _containerFixture;
    private string ConnectionString => RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker)
                                            ? _containerFixture.GetConnectionString()
                                            : "Server=localhost;User ID=root;Password=pass;Database=test";

    public AspireEFMySqlExtensionsTests(MySqlContainerFixture containerFixture)
        => _containerFixture = containerFixture;

    [Fact]
    public void ReadsFromConnectionStringsCorrectly()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("Aspire:Pomelo:EntityFrameworkCore:MySql:ServerVersion", s_serverVersionString),
            new KeyValuePair<string, string?>("ConnectionStrings:mysql", ConnectionString)
        ]);

        builder.AddMySqlDbContext<TestDbContext>("mysql");

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

        var actualConnectionString = context.Database.GetDbConnection().ConnectionString;
        string expectedConnectionString = new MySqlConnectionStringBuilder(ConnectionString + ConnectionStringSuffixAddedByPomelo).ConnectionString;
        Assert.Equal(expectedConnectionString, actualConnectionString);
    }

    [Fact]
    public void ConnectionStringCanBeSetInCode()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("Aspire:Pomelo:EntityFrameworkCore:MySql:ServerVersion", s_serverVersionString),
            new KeyValuePair<string, string?>("ConnectionStrings:mysql", "unused")
        ]);

        builder.AddMySqlDbContext<TestDbContext>("mysql", settings => settings.ConnectionString = ConnectionString);

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

        var actualConnectionString = context.Database.GetDbConnection().ConnectionString;
        string expectedConnectionString = new MySqlConnectionStringBuilder(ConnectionString + ConnectionStringSuffixAddedByPomelo).ConnectionString;
        Assert.Equal(expectedConnectionString, actualConnectionString);
        // the connection string from config should not be used since code set it explicitly
        Assert.DoesNotContain("unused", actualConnectionString);
    }

    [Fact]
    public void ConnectionNameWinsOverConfigSection()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("Aspire:Pomelo:EntityFrameworkCore:MySql:ServerVersion", s_serverVersionString),
            new KeyValuePair<string, string?>("Aspire:Pomelo:EntityFrameworkCore:MySql:ConnectionString", "unused"),
            new KeyValuePair<string, string?>("ConnectionStrings:mysql", ConnectionString)
        ]);

        builder.AddMySqlDbContext<TestDbContext>("mysql");

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

        var actualConnectionString = context.Database.GetDbConnection().ConnectionString;
        string expectedConnectionString = new MySqlConnectionStringBuilder(ConnectionString + ConnectionStringSuffixAddedByPomelo).ConnectionString;
        Assert.Equal(expectedConnectionString, actualConnectionString);
        // the connection string from config should not be used since it was found in ConnectionStrings
        Assert.DoesNotContain("unused", actualConnectionString);
    }

    [Fact]
    public void CanConfigureDbContextOptions()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("Aspire:Pomelo:EntityFrameworkCore:MySql:ServerVersion", s_serverVersionString),
            new KeyValuePair<string, string?>("ConnectionStrings:mysql", ConnectionString),
            new KeyValuePair<string, string?>("Aspire:Pomelo:EntityFrameworkCore:MySql:DisableRetry", "false")
        ]);

        builder.AddMySqlDbContext<TestDbContext>("mysql", configureDbContextOptions: optionsBuilder =>
        {
            optionsBuilder.UseMySql(s_serverVersion, mySqlBuilder =>
            {
                mySqlBuilder.CommandTimeout(123);
            });
        });

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

#pragma warning disable EF1001 // Internal EF Core API usage.

        var extension = context.Options.FindExtension<MySqlOptionsExtension>();
        Assert.NotNull(extension);

        // ensure the command timeout was respected
        Assert.Equal(123, extension.CommandTimeout);

        // ensure the connection string from config was respected
        var actualConnectionString = context.Database.GetDbConnection().ConnectionString;
        var expectedConnectionString = new MySqlConnectionStringBuilder(ConnectionString + ";Allow User Variables=True;Default Command Timeout=123;Use Affected Rows=False").ConnectionString;
        Assert.Equal(expectedConnectionString, actualConnectionString);

        // ensure the retry strategy is enabled and set to its default value
        Assert.NotNull(extension.ExecutionStrategyFactory);
        var executionStrategy = extension.ExecutionStrategyFactory(new ExecutionStrategyDependencies(new CurrentDbContext(context), context.Options, null!));
        var retryStrategy = Assert.IsType<MySqlRetryingExecutionStrategy>(executionStrategy);
        Assert.Equal(new WorkaroundToReadProtectedField(context).MaxRetryCount, retryStrategy.MaxRetryCount);

#pragma warning restore EF1001 // Internal EF Core API usage.
    }

    [Fact]
    public void CanConfigureDbContextOptionsWithoutRetry()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("Aspire:Pomelo:EntityFrameworkCore:MySql:ServerVersion", s_serverVersionString),
            new KeyValuePair<string, string?>("ConnectionStrings:mysql", ConnectionString),
            new KeyValuePair<string, string?>("Aspire:Pomelo:EntityFrameworkCore:MySql:DisableRetry", "true")
        ]);

        builder.AddMySqlDbContext<TestDbContext>("mysql", configureDbContextOptions: optionsBuilder =>
        {
            optionsBuilder.UseMySql(s_serverVersion, mySqlBuilder =>
            {
                mySqlBuilder.CommandTimeout(123);
            });
        });

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

#pragma warning disable EF1001 // Internal EF Core API usage.

        var extension = context.Options.FindExtension<MySqlOptionsExtension>();
        Assert.NotNull(extension);

        // ensure the command timeout was respected
        Assert.Equal(123, extension.CommandTimeout);

        // ensure the connection string from config was respected
        var actualConnectionString = context.Database.GetDbConnection().ConnectionString;
        var expectedConnectionString = new MySqlConnectionStringBuilder(ConnectionString + ";Allow User Variables=True;Default Command Timeout=123;Use Affected Rows=False").ConnectionString;
        Assert.Equal(expectedConnectionString, actualConnectionString);

        // ensure no retry strategy was registered
        Assert.Null(extension.ExecutionStrategyFactory);

#pragma warning restore EF1001 // Internal EF Core API usage.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/11820", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public void CanConfigureCommandTimeout(bool useSettings)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("Aspire:Pomelo:EntityFrameworkCore:MySql:ServerVersion", "8.2.0-mysql"),
            new KeyValuePair<string, string?>("ConnectionStrings:mysql", ConnectionString),
        ]);
        if (!useSettings)
        {
            builder.Configuration.AddInMemoryCollection([
                new KeyValuePair<string, string?>("Aspire:Pomelo:EntityFrameworkCore:MySql:CommandTimeout", "123"),
            ]);
        }

        builder.AddMySqlDbContext<TestDbContext>("mysql", configureDbContextOptions: optionsBuilder =>
            optionsBuilder.UseMySql(new MySqlServerVersion(new Version(8, 2, 0))),
            configureSettings: useSettings ? settings => settings.CommandTimeout = 123 : null);

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

#pragma warning disable EF1001 // Internal EF Core API usage.

        var extension = context.Options.FindExtension<MySqlOptionsExtension>();
        Assert.NotNull(extension);

        // ensure the command timeout was respected
        Assert.Equal(123, extension.CommandTimeout);

#pragma warning restore EF1001 // Internal EF Core API usage.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/11820", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public void CommandTimeoutFromSettingsWinsOverOthers(bool useSettings)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("Aspire:Pomelo:EntityFrameworkCore:MySql:ServerVersion", "8.2.0-mysql"),
            new KeyValuePair<string, string?>("ConnectionStrings:mysql", ConnectionString),
        ]);
        if (!useSettings)
        {
            builder.Configuration.AddInMemoryCollection([
                new KeyValuePair<string, string?>("Aspire:Pomelo:EntityFrameworkCore:MySql:CommandTimeout", "400"),
            ]);
        }

        builder.AddMySqlDbContext<TestDbContext>("mysql", configureDbContextOptions: optionsBuilder =>
            optionsBuilder.UseMySql(new MySqlServerVersion(new Version(8, 2, 0)), mySqlBuilder =>
            {
                mySqlBuilder.CommandTimeout(123);
            }),
            configureSettings: useSettings ? settings => settings.CommandTimeout = 300 : null);

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

#pragma warning disable EF1001 // Internal EF Core API usage.

        var extension = context.Options.FindExtension<MySqlOptionsExtension>();
        Assert.NotNull(extension);

        // ensure the command timeout from builder was respected
        Assert.Equal(123, extension.CommandTimeout);

#pragma warning restore EF1001 // Internal EF Core API usage.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/11820", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public void ThrowsWhenDbContextIsRegisteredBeforeAspireComponent(bool useServiceType)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:mysql", ConnectionString)
        ]);

        if (useServiceType)
        {
            builder.Services.AddDbContextPool<ITestDbContext, TestDbContext>(options => options.UseMySql(ConnectionString, s_serverVersion));
        }
        else
        {
            builder.Services.AddDbContextPool<TestDbContext>(options => options.UseMySql(ConnectionString, s_serverVersion));
        }

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddMySqlDbContext<TestDbContext>("mysql"));
        Assert.Equal("DbContext<TestDbContext> is already registered. Please ensure 'services.AddDbContext<TestDbContext>()' is not used when calling 'AddMySqlDbContext()' or use the corresponding 'Enrich' method.", exception.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/11820", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public void DoesntThrowWhenDbContextIsRegisteredBeforeAspireComponentProduction(bool useServiceType)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:mysql", ConnectionString)
        ]);

        if (useServiceType)
        {
            builder.Services.AddDbContextPool<ITestDbContext, TestDbContext>(options => options.UseMySql(ConnectionString, s_serverVersion));
        }
        else
        {
            builder.Services.AddDbContextPool<TestDbContext>(options => options.UseMySql(ConnectionString, s_serverVersion));
        }

        var exception = Record.Exception(() => builder.AddMySqlDbContext<TestDbContext>("mysql"));

        Assert.Null(exception);
    }

    [Fact]
    public void AddMySqlDbContext_WithConnectionNameAndSettings_AppliesConnectionSpecificSettings()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var connectionName = "testdb";

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{connectionName}"] = ConnectionString,
            [$"Aspire:Pomelo:EntityFrameworkCore:MySql:{connectionName}:CommandTimeout"] = "60",
            [$"Aspire:Pomelo:EntityFrameworkCore:MySql:{connectionName}:DisableRetry"] = "true",
            [$"Aspire:Pomelo:EntityFrameworkCore:MySql:{connectionName}:DisableHealthChecks"] = "true"
        });

        PomeloEntityFrameworkCoreMySqlSettings? capturedSettings = null;
        builder.AddMySqlDbContext<TestDbContext>(connectionName, settings =>
        {
            capturedSettings = settings;
        });

        Assert.NotNull(capturedSettings);
        Assert.Equal(60, capturedSettings.CommandTimeout);
        Assert.True(capturedSettings.DisableRetry);
        Assert.True(capturedSettings.DisableHealthChecks);
    }

    [Fact]
    public void AddMySqlDbContext_WithConnectionSpecificAndContextSpecificSettings_PrefersContextSpecific()
    {
        // Arrange
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var connectionName = "testdb";

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{connectionName}"] = ConnectionString,
            // Connection-specific settings
            [$"Aspire:Pomelo:EntityFrameworkCore:MySql:{connectionName}:CommandTimeout"] = "60",
            // Context-specific settings wins
            [$"Aspire:Pomelo:EntityFrameworkCore:MySql:TestDbContext:CommandTimeout"] = "120"
        });

        PomeloEntityFrameworkCoreMySqlSettings? capturedSettings = null;
        builder.AddMySqlDbContext<TestDbContext>(connectionName, settings =>
        {
            capturedSettings = settings;
        });

        Assert.NotNull(capturedSettings);
        Assert.Equal(120, capturedSettings.CommandTimeout);
    }
}
