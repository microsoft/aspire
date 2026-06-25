// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.SqlServer.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Microsoft.EntityFrameworkCore.SqlServer.Tests;

public class AspireSqlServerEFCoreSqlClientExtensionsTests
{
    private const string ConnectionString = "Data Source=fake;Initial Catalog=master;Encrypt=True";

    [Fact]
    public void ReadsFromConnectionStringsCorrectly()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection", ConnectionString)
        ]);

        builder.AddSqlServerDbContext<TestDbContext>("sqlconnection");

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

        AssertCorrectConnectionString(ConnectionString, context.Database.GetDbConnection().ConnectionString);
    }

    [Fact]
    public void ConnectionStringCanBeSetInCode()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection", "unused")
        ]);

        builder.AddSqlServerDbContext<TestDbContext>("sqlconnection", settings => settings.ConnectionString = ConnectionString);

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

        var actualConnectionString = context.Database.GetDbConnection().ConnectionString;
        AssertCorrectConnectionString(ConnectionString, actualConnectionString);
        // the connection string from config should not be used since code set it explicitly
        Assert.DoesNotContain("unused", actualConnectionString);
    }

    [Fact]
    public void ConnectionNameWinsOverConfigSection()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("Aspire:Microsoft:EntityFrameworkCore:SqlServer:ConnectionString", "unused"),
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection", ConnectionString)
        ]);

        builder.AddSqlServerDbContext<TestDbContext>("sqlconnection");

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

        var actualConnectionString = context.Database.GetDbConnection().ConnectionString;
        AssertCorrectConnectionString(ConnectionString, actualConnectionString);
        // the connection string from config should not be used since it was found in ConnectionStrings
        Assert.DoesNotContain("unused", actualConnectionString);
    }

    [Fact]
    public void CanConfigureDbContextOptions()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection", ConnectionString),
            new KeyValuePair<string, string?>("Aspire:Microsoft:EntityFrameworkCore:SqlServer:DisableRetry", "false"),
            new KeyValuePair<string, string?>("Aspire:Microsoft:EntityFrameworkCore:SqlServer:CommandTimeout", "608")
        ]);

        builder.AddSqlServerDbContext<TestDbContext>("sqlconnection", configureDbContextOptions: optionsBuilder =>
        {
            optionsBuilder.UseSqlServer(sqlBuilder =>
            {
                sqlBuilder.MinBatchSize(123);
            });
        });

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

#pragma warning disable EF1001 // Internal EF Core API usage.

        var extension = context.Options.FindExtension<SqlServerOptionsExtension>();
        Assert.NotNull(extension);

        // ensure the min batch size was respected
        Assert.Equal(123, extension.MinBatchSize);

        // ensure the connection string from config was respected
        var actualConnectionString = context.Database.GetDbConnection().ConnectionString;
        AssertCorrectConnectionString(ConnectionString, actualConnectionString);

        // ensure the retry strategy is enabled and set to its default value
        Assert.NotNull(extension.ExecutionStrategyFactory);
        var executionStrategy = extension.ExecutionStrategyFactory(new ExecutionStrategyDependencies(new CurrentDbContext(context), context.Options, null!));
        var retryStrategy = Assert.IsType<SqlServerRetryingExecutionStrategy>(executionStrategy);
        Assert.Equal(new WorkaroundToReadProtectedField(context).MaxRetryCount, retryStrategy.MaxRetryCount);

        // ensure the command timeout from config was respected
        Assert.Equal(608, extension.CommandTimeout);

#pragma warning restore EF1001 // Internal EF Core API usage.
    }

    [Fact]
    public void CanConfigureDbContextOptionsWithoutRetry()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection", ConnectionString),
            new KeyValuePair<string, string?>("Aspire:Microsoft:EntityFrameworkCore:SqlServer:DisableRetry", "true"),
        ]);

        builder.AddSqlServerDbContext<TestDbContext>("sqlconnection", configureDbContextOptions: optionsBuilder =>
        {
            optionsBuilder.UseSqlServer(sqlBuilder =>
            {
                sqlBuilder.CommandTimeout(123);
            });
        });

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

#pragma warning disable EF1001 // Internal EF Core API usage.

        var extension = context.Options.FindExtension<SqlServerOptionsExtension>();
        Assert.NotNull(extension);

        // ensure the command timeout was respected
        Assert.Equal(123, extension.CommandTimeout);

        // ensure the connection string from config was respected
        var actualConnectionString = context.Database.GetDbConnection().ConnectionString;
        AssertCorrectConnectionString(ConnectionString, actualConnectionString);

        // ensure no retry strategy was registered
        Assert.Null(extension.ExecutionStrategyFactory);

#pragma warning restore EF1001 // Internal EF Core API usage.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanConfigureCommandTimeout(bool useSettings)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection", ConnectionString)
        ]);
        if (!useSettings)
        {
            builder.Configuration.AddInMemoryCollection([
                new KeyValuePair<string, string?>("Aspire:Microsoft:EntityFrameworkCore:SqlServer:CommandTimeout", "608")
            ]);
        }

        builder.AddSqlServerDbContext<TestDbContext>("sqlconnection",
                configureDbContextOptions: optionsBuilder => optionsBuilder.UseSqlServer(),
                configureSettings: useSettings ? settings => settings.CommandTimeout = 608 : null);

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

#pragma warning disable EF1001 // Internal EF Core API usage.

        var extension = context.Options.FindExtension<SqlServerOptionsExtension>();
        Assert.NotNull(extension);

        // ensure the command timeout was respected
        Assert.Equal(608, extension.CommandTimeout);

#pragma warning restore EF1001 // Internal EF Core API usage.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CommandTimeoutFromBuilderWinsOverOthers(bool useSettings)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection", ConnectionString)
        ]);
        if (!useSettings)
        {
            builder.Configuration.AddInMemoryCollection([
                new KeyValuePair<string, string?>("Aspire:Microsoft:EntityFrameworkCore:SqlServer:CommandTimeout", "400")
            ]);
        }

        builder.AddSqlServerDbContext<TestDbContext>("sqlconnection",
                configureDbContextOptions: optionsBuilder =>
                    optionsBuilder.UseSqlServer(builder => builder.CommandTimeout(123)),
                configureSettings: useSettings ? settings => settings.CommandTimeout = 300 : null);

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

#pragma warning disable EF1001 // Internal EF Core API usage.

        var extension = context.Options.FindExtension<SqlServerOptionsExtension>();
        Assert.NotNull(extension);

        // ensure the command timeout from builder was respected
        Assert.Equal(123, extension.CommandTimeout);

#pragma warning restore EF1001 // Internal EF Core API usage.
    }

    /// <summary>
    /// Verifies that two different DbContexts can be registered with different connection strings.
    /// </summary>
    [Fact]
    public void CanHave2DbContexts()
    {
        const string connectionString2 = "Data Source=fake2;Initial Catalog=master2;Encrypt=True";

        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection", ConnectionString),
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection2", connectionString2),
        ]);

        builder.AddSqlServerDbContext<TestDbContext>("sqlconnection");
        builder.AddSqlServerDbContext<TestDbContext2>("sqlconnection2");

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();
        var context2 = host.Services.GetRequiredService<TestDbContext2>();

        var actualConnectionString = context.Database.GetDbConnection().ConnectionString;
        AssertCorrectConnectionString(ConnectionString, actualConnectionString);

        actualConnectionString = context2.Database.GetDbConnection().ConnectionString;
        AssertCorrectConnectionString(connectionString2, actualConnectionString);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThrowsWhenDbContextIsRegisteredBeforeAspireComponent(bool useServiceType)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection", ConnectionString)
        ]);

        if (useServiceType)
        {
            builder.Services.AddDbContextPool<ITestDbContext, TestDbContext>(options => options.UseSqlServer(ConnectionString));
        }
        else
        {
            builder.Services.AddDbContextPool<TestDbContext>(options => options.UseSqlServer(ConnectionString));
        }

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddSqlServerDbContext<TestDbContext>("sqlconnection"));
        Assert.Equal("DbContext<TestDbContext> is already registered. Please ensure 'services.AddDbContext<TestDbContext>()' is not used when calling 'AddSqlServerDbContext()' or use the corresponding 'Enrich' method.", exception.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DoesntThrowWhenDbContextIsRegisteredBeforeAspireComponentProduction(bool useServiceType)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection", ConnectionString)
        ]);

        if (useServiceType)
        {
            builder.Services.AddDbContextPool<ITestDbContext, TestDbContext>(options => options.UseSqlServer(ConnectionString));
        }
        else
        {
            builder.Services.AddDbContextPool<TestDbContext>(options => options.UseSqlServer(ConnectionString));
        }

        var exception = Record.Exception(() => builder.AddSqlServerDbContext<TestDbContext>("sqlconnection"));

        Assert.Null(exception);
    }

    [Fact]
    public void AddSqlServerDbContext_WithConnectionNameAndSettings_AppliesConnectionSpecificSettings()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var connectionName = "testdb";

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{connectionName}"] = ConnectionString,
            [$"Aspire:Microsoft:EntityFrameworkCore:SqlServer:{connectionName}:CommandTimeout"] = "60",
            [$"Aspire:Microsoft:EntityFrameworkCore:SqlServer:{connectionName}:DisableTracing"] = "true"
        });

        MicrosoftEntityFrameworkCoreSqlServerSettings? capturedSettings = null;
        builder.AddSqlServerDbContext<TestDbContext>(connectionName, settings =>
        {
            capturedSettings = settings;
        });

        Assert.NotNull(capturedSettings);
        Assert.Equal(60, capturedSettings.CommandTimeout);
        Assert.True(capturedSettings.DisableTracing);
    }

    [Fact]
    public void AddSqlServerDbContext_WithConnectionSpecificAndContextSpecificSettings_PrefersContextSpecific()
    {
        // Arrange
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var connectionName = "testdb";

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{connectionName}"] = ConnectionString,
            // Connection-specific settings
            [$"Aspire:Microsoft:EntityFrameworkCore:SqlServer:{connectionName}:CommandTimeout"] = "60",
            // Context-specific settings wins
            [$"Aspire:Microsoft:EntityFrameworkCore:SqlServer:TestDbContext:CommandTimeout"] = "120"
        });

        MicrosoftEntityFrameworkCoreSqlServerSettings? capturedSettings = null;
        builder.AddSqlServerDbContext<TestDbContext>(connectionName, settings =>
        {
            capturedSettings = settings;
        });

        Assert.NotNull(capturedSettings);
        Assert.Equal(120, capturedSettings.CommandTimeout);
    }

    private static void AssertCorrectConnectionString(string expectedConnectionString, string actualConnectionString)
    {
#if NET10_0_OR_GREATER
        // In .NET 10, the connection string may have additional parameters appended, so we check the start only.
        Assert.StartsWith(expectedConnectionString, actualConnectionString);
#else
        Assert.Equal(expectedConnectionString, actualConnectionString);
#endif
    }

    // Regression guard: EF Core's SqlServer provider pulls in SqlClient 7.0, which moved the Entra ID (Active
    // Directory) auth providers into Microsoft.Data.SqlClient.Extensions.Azure. This integration references that
    // package, so they must resolve here; otherwise the Authentication="Active Directory Default" strings Aspire emits fail.
    [Theory]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryDefault)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity)]
    public void EntraIdAuthenticationProviderIsRegistered(SqlAuthenticationMethod method)
    {
        var provider = SqlAuthenticationProvider.GetProvider(method);

        Assert.NotNull(provider);
    }

    public class TestDbContext2 : DbContext
    {
        public TestDbContext2(DbContextOptions<TestDbContext2> options) : base(options)
        {
        }

        public DbSet<Product> Products => Set<Product>();

        public class Product
        {
            public int Id { get; set; }
            public string Name { get; set; } = default!;
        }
    }
}
