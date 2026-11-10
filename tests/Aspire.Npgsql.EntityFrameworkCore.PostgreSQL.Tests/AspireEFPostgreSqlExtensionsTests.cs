// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Aspire.Npgsql.Tests;
using Aspire.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Xunit;

namespace Aspire.Npgsql.EntityFrameworkCore.PostgreSQL.Tests;

public class AspireEFPostgreSqlExtensionsTests
{
    private const string ConnectionString = "Host=localhost;Database=test;Username=postgres";

    internal static void ConfigureDbContextOptionsBuilderForTesting(DbContextOptionsBuilder builder)
    {
        // Don't cache the service provider in testing.
        // Works around https://github.com/npgsql/efcore.pg/issues/2891, which is errantly caches connection strings across DI containers.
        builder.EnableServiceProviderCaching(false);
    }

    [Fact]
    public void ReadsFromConnectionStringsCorrectly()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:npgsql", ConnectionString)
        ]);

        builder.AddNpgsqlDbContext<TestDbContext>("npgsql", configureDbContextOptions: ConfigureDbContextOptionsBuilderForTesting);

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

        Assert.Equal(ConnectionString, context.Database.GetDbConnection().ConnectionString);
    }

    [Fact]
    public void ConnectionStringCanBeSetInCode()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:npgsql", "unused")
        ]);

        builder.AddNpgsqlDbContext<TestDbContext>("npgsql",
            settings => settings.ConnectionString = ConnectionString,
            configureDbContextOptions: ConfigureDbContextOptionsBuilderForTesting);

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

        var actualConnectionString = context.Database.GetDbConnection().ConnectionString;
        Assert.Equal(ConnectionString, actualConnectionString);
        // the connection string from config should not be used since code set it explicitly
        Assert.DoesNotContain("unused", actualConnectionString);
    }

    [Fact]
    public void ConnectionNameWinsOverConfigSection()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("Aspire:Npgsql:EntityFrameworkCore:PostgreSQL:ConnectionString", "unused"),
            new KeyValuePair<string, string?>("ConnectionStrings:npgsql", ConnectionString)
        ]);

        builder.AddNpgsqlDbContext<TestDbContext>("npgsql", configureDbContextOptions: ConfigureDbContextOptionsBuilderForTesting);

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

        var actualConnectionString = context.Database.GetDbConnection().ConnectionString;
        Assert.Equal(ConnectionString, actualConnectionString);
        // the connection string from config should not be used since it was found in ConnectionStrings
        Assert.DoesNotContain("unused", actualConnectionString);
    }

    [Fact]
    public void AddNpgsqlCanConfigureDbContextOptions()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:npgsql", ConnectionString),
            new KeyValuePair<string, string?>("Aspire:Npgsql:EntityFrameworkCore:PostgreSQL:Retry", "true")
        ]);

        builder.AddNpgsqlDbContext<TestDbContext>("npgsql", configureDbContextOptions: optionsBuilder =>
        {
            ConfigureDbContextOptionsBuilderForTesting(optionsBuilder);
            optionsBuilder.UseNpgsql(npgsqlBuilder =>
            {
                npgsqlBuilder.CommandTimeout(123);
            });
        });

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

#pragma warning disable EF1001 // Internal EF Core API usage.

        var extension = context.Options.FindExtension<NpgsqlOptionsExtension>();
        Assert.NotNull(extension);

        // ensure the command timeout was respected
        Assert.Equal(123, extension.CommandTimeout);

        // ensure the connection string from config was respected
        var actualConnectionString = context.Database.GetDbConnection().ConnectionString;
        Assert.Equal(ConnectionString, actualConnectionString);

        // ensure the retry strategy is enabled and set to its default value
        Assert.NotNull(extension.ExecutionStrategyFactory);
        var executionStrategy = extension.ExecutionStrategyFactory(new ExecutionStrategyDependencies(new CurrentDbContext(context), context.Options, null!));
        var retryStrategy = Assert.IsType<NpgsqlRetryingExecutionStrategy>(executionStrategy);
        Assert.Equal(new WorkaroundToReadProtectedField(context).MaxRetryCount, retryStrategy.MaxRetryCount);

#pragma warning restore EF1001 // Internal EF Core API usage.
    }

    [Fact]
    public void AddNpgsqlCanConfigureDbContextOptionsWithoutRetry()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:npgsql", ConnectionString),
            new KeyValuePair<string, string?>("Aspire:Npgsql:EntityFrameworkCore:PostgreSQL:DisableRetry", "true")
        ]);

        builder.AddNpgsqlDbContext<TestDbContext>("npgsql", configureDbContextOptions: optionsBuilder =>
        {
            ConfigureDbContextOptionsBuilderForTesting(optionsBuilder);
            optionsBuilder.UseNpgsql(npgsqlBuilder =>
            {
                npgsqlBuilder.CommandTimeout(123);
            });
        });

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

#pragma warning disable EF1001 // Internal EF Core API usage.

        var extension = context.Options.FindExtension<NpgsqlOptionsExtension>();
        Assert.NotNull(extension);

        // ensure the command timeout was respected
        Assert.Equal(123, extension.CommandTimeout);

        // ensure the connection string from config was respected
        var actualConnectionString = context.Database.GetDbConnection().ConnectionString;
        Assert.Equal(ConnectionString, actualConnectionString);

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
            new KeyValuePair<string, string?>("ConnectionStrings:npgsql", ConnectionString)
        ]);
        if (!useSettings)
        {
            builder.Configuration.AddInMemoryCollection([
                new KeyValuePair<string, string?>("Aspire:Npgsql:EntityFrameworkCore:PostgreSQL:CommandTimeout", "123")
            ]);
        }

        builder.AddNpgsqlDbContext<TestDbContext>("npgsql",
            configureDbContextOptions: optionsBuilder => optionsBuilder.UseNpgsql(),
            configureSettings: useSettings ? settings => settings.CommandTimeout = 123 : null);

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

#pragma warning disable EF1001 // Internal EF Core API usage.

        var extension = context.Options.FindExtension<NpgsqlOptionsExtension>();
        Assert.NotNull(extension);

        // ensure the command timeout was respected
        Assert.Equal(123, extension.CommandTimeout);

#pragma warning restore EF1001 // Internal EF Core API usage.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CommandTimeoutFromBuilderWinsOverOthers(bool useSettings)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:npgsql", ConnectionString)
        ]);
        if (!useSettings)
        {
            builder.Configuration.AddInMemoryCollection([
                new KeyValuePair<string, string?>("Aspire:Npgsql:EntityFrameworkCore:PostgreSQL:CommandTimeout", "400")
            ]);
        }

        builder.AddNpgsqlDbContext<TestDbContext>("npgsql",
            configureDbContextOptions: optionsBuilder => optionsBuilder.UseNpgsql(builder => builder.CommandTimeout(123)),
            configureSettings: useSettings ? settings => settings.CommandTimeout = 300 : null);

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();

#pragma warning disable EF1001 // Internal EF Core API usage.

        var extension = context.Options.FindExtension<NpgsqlOptionsExtension>();
        Assert.NotNull(extension);

        // ensure the command timeout was respected
        Assert.Equal(123, extension.CommandTimeout);

#pragma warning restore EF1001 // Internal EF Core API usage.
    }

    /// <summary>
    /// Verifies that two different DbContexts can be registered with different connection strings.
    /// </summary>
    [Fact]
    public void CanHave2DbContexts()
    {
        const string connectionString2 = "Host=localhost2;Database=test2;Username=postgres2";

        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:npgsql", ConnectionString),
            new KeyValuePair<string, string?>("ConnectionStrings:npgsql2", connectionString2),
        ]);

        builder.AddNpgsqlDbContext<TestDbContext>("npgsql");
        builder.AddNpgsqlDbContext<TestDbContext2>("npgsql2");

        using var host = builder.Build();
        var context = host.Services.GetRequiredService<TestDbContext>();
        var context2 = host.Services.GetRequiredService<TestDbContext2>();

        var actualConnectionString = context.Database.GetDbConnection().ConnectionString;
        Assert.Equal(ConnectionString, actualConnectionString);

        actualConnectionString = context2.Database.GetDbConnection().ConnectionString;
        Assert.Equal(connectionString2, actualConnectionString);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThrowsWhenDbContextIsRegisteredBeforeAspireComponent(bool useServiceType)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:npgsql", ConnectionString)
        ]);

        if (useServiceType)
        {
            builder.Services.AddDbContextPool<ITestDbContext, TestDbContext>(options => options.UseNpgsql(ConnectionString));
        }
        else
        {
            builder.Services.AddDbContextPool<TestDbContext>(options => options.UseNpgsql(ConnectionString));
        }

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddNpgsqlDbContext<TestDbContext>("npgsql"));
        Assert.Equal("DbContext<TestDbContext> is already registered. Please ensure 'services.AddDbContext<TestDbContext>()' is not used when calling 'AddNpgsqlDbContext()' or use the corresponding 'Enrich' method.", exception.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DoesntThrowWhenDbContextIsRegisteredBeforeAspireComponentProduction(bool useServiceType)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:npgsql", ConnectionString)
        ]);

        if (useServiceType)
        {
            builder.Services.AddDbContextPool<ITestDbContext, TestDbContext>(options => options.UseNpgsql(ConnectionString));
        }
        else
        {
            builder.Services.AddDbContextPool<TestDbContext>(options => options.UseNpgsql(ConnectionString));
        }

        var exception = Record.Exception(() => builder.AddNpgsqlDbContext<TestDbContext>("npgsql"));

        Assert.Null(exception);
    }

    [Fact]
    public void AddNpgsqlDbContext_WithConnectionNameAndSettings_AppliesConnectionSpecificSettings()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var connectionName = "testdb";

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{connectionName}"] = ConnectionString,
            [$"Aspire:Npgsql:EntityFrameworkCore:PostgreSQL:{connectionName}:CommandTimeout"] = "60",
            [$"Aspire:Npgsql:EntityFrameworkCore:PostgreSQL:{connectionName}:DisableRetry"] = "true",
            [$"Aspire:Npgsql:EntityFrameworkCore:PostgreSQL:{connectionName}:DisableHealthChecks"] = "true"
        });

        builder.AddNpgsqlDbContext<TestDbContext>(connectionName);

        NpgsqlEntityFrameworkCorePostgreSQLSettings? capturedSettings = null;
        builder.AddNpgsqlDbContext<TestDbContext>(connectionName, settings =>
        {
            capturedSettings = settings;
        });

        Assert.NotNull(capturedSettings);
        Assert.Equal(60, capturedSettings.CommandTimeout);
        Assert.True(capturedSettings.DisableRetry);
        Assert.True(capturedSettings.DisableHealthChecks);
    }

    [Fact]
    public void AddNpgsqlDbContext_WithConnectionSpecificAndContextSpecificSettings_PrefersContextSpecific()
    {
        // Arrange
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var connectionName = "testdb";

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{connectionName}"] = ConnectionString,
            // Connection-specific settings
            [$"Aspire:Npgsql:EntityFrameworkCore:PostgreSQL:{connectionName}:CommandTimeout"] = "60",
            // Context-specific settings wins
            [$"Aspire:Npgsql:EntityFrameworkCore:PostgreSQL:TestDbContext:CommandTimeout"] = "120"
        });

        NpgsqlEntityFrameworkCorePostgreSQLSettings? capturedSettings = null;
        builder.AddNpgsqlDbContext<TestDbContext>(connectionName, settings =>
        {
            capturedSettings = settings;
        });

        Assert.NotNull(capturedSettings);
        Assert.Equal(120, capturedSettings.CommandTimeout);
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

    [Fact]
    public void ComponentDoesNotReferenceRemovedProviderInternalExecutionStrategy()
    {
        // Npgsql.EntityFrameworkCore.PostgreSQL 11 removed its internal NpgsqlExecutionStrategy type. Any reference to it
        // throws TypeLoadException when a DbContext is resolved with newer provider versions.
        // https://github.com/microsoft/aspire/issues/21152
        using var stream = File.OpenRead(typeof(AspireEFPostgreSqlExtensions).Assembly.Location);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();

        var referencedTypes = metadataReader.TypeReferences
            .Select(metadataReader.GetTypeReference)
            .Select(typeReference => $"{metadataReader.GetString(typeReference.Namespace)}.{metadataReader.GetString(typeReference.Name)}");

        Assert.DoesNotContain("Npgsql.EntityFrameworkCore.PostgreSQL.Storage.Internal.NpgsqlExecutionStrategy", referencedTypes);
    }
}
