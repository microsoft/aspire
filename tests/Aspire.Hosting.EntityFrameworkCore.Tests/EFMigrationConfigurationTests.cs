// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.EntityFrameworkCore.Tests;

public class EFMigrationConfigurationTests
{
    [Fact]
    public void RunDatabaseUpdateOnStartRegistersEventSubscription()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .RunDatabaseUpdateOnStart();

        // The resource should have the migrations applied at startup
        // Event subscription is internal, so we just verify the call doesn't throw
        Assert.NotNull(migrations);
    }

    [Fact]
    public void PublishAsMigrationScriptSetsOption()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript();

        Assert.True(migrations.Resource.PublishAsMigrationScript);
    }

    [Fact]
    public void PublishAsMigrationBundleSetsOption()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        Assert.True(migrations.Resource.PublishAsMigrationBundle);
    }

    [Fact]
    public void WithMigrationOutputDirectorySetsOption()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WithMigrationOutputDirectory("Data/Migrations");

        Assert.Equal("Data/Migrations", migrations.Resource.MigrationOutputDirectory);
    }

    [Fact]
    public void WithMigrationNamespaceSetsOption()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WithMigrationNamespace("MyApp.Data.Migrations");

        Assert.Equal("MyApp.Data.Migrations", migrations.Resource.MigrationNamespace);
    }

    [Fact]
    public void WithMigrationOutputDirectoryThrowsForEmptyString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        Assert.Throws<ArgumentException>(() => migrations.WithMigrationOutputDirectory(""));
    }

    [Fact]
    public void WithMigrationNamespaceThrowsForEmptyString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        Assert.Throws<ArgumentException>(() => migrations.WithMigrationNamespace(""));
    }

    [Fact]
    public void MultipleConfigurationOptionsCanBeChained()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .RunDatabaseUpdateOnStart()
            .PublishAsMigrationScript();

        Assert.True(migrations.Resource.PublishAsMigrationScript);
    }

    [Fact]
    public void AllConfigurationOptionsCanBeChained()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .RunDatabaseUpdateOnStart()
            .PublishAsMigrationScript()
            .PublishAsMigrationBundle()
            .WithMigrationOutputDirectory("CustomDir")
            .WithMigrationNamespace("MyApp.Migrations");

        Assert.True(migrations.Resource.PublishAsMigrationScript);
        Assert.True(migrations.Resource.PublishAsMigrationBundle);
        Assert.Equal("CustomDir", migrations.Resource.MigrationOutputDirectory);
        Assert.Equal("MyApp.Migrations", migrations.Resource.MigrationNamespace);
    }

    [Fact]
    public void ConfigurationMethodsPreserveContextTypeName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .RunDatabaseUpdateOnStart()
            .PublishAsMigrationScript()
            .PublishAsMigrationBundle();

        // The context type name should be preserved through chaining
        Assert.Equal(typeof(TestDbContext).FullName, migrations.Resource.DbContextTypeName);
    }

    [Fact]
    public void ConfigurationMethodsReturnSameBuilderType()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);
        
        var afterUpdate = migrations.RunDatabaseUpdateOnStart();
        var afterScript = afterUpdate.PublishAsMigrationScript();
        var afterBundle = afterScript.PublishAsMigrationBundle();
        var afterOutputDir = afterBundle.WithMigrationOutputDirectory("Migrations");
        var afterNamespace = afterOutputDir.WithMigrationNamespace("MyApp.Migrations");

        // All methods should return IResourceBuilder<EFMigrationResource> for proper chaining
        Assert.IsAssignableFrom<IResourceBuilder<EFMigrationResource>>(afterUpdate);
        Assert.IsAssignableFrom<IResourceBuilder<EFMigrationResource>>(afterScript);
        Assert.IsAssignableFrom<IResourceBuilder<EFMigrationResource>>(afterBundle);
        Assert.IsAssignableFrom<IResourceBuilder<EFMigrationResource>>(afterOutputDir);
        Assert.IsAssignableFrom<IResourceBuilder<EFMigrationResource>>(afterNamespace);
    }

    [Fact]
    public void OptionsInitiallyFalseOrNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        // Options should all be false/null initially
        Assert.False(migrations.Resource.PublishAsMigrationScript);
        Assert.False(migrations.Resource.PublishAsMigrationBundle);
        Assert.Null(migrations.Resource.MigrationOutputDirectory);
        Assert.Null(migrations.Resource.MigrationNamespace);
        Assert.Null(migrations.Resource.MigrationsProjectPath);
    }

    [Fact]
    public void WithMigrationsProjectWithPathSetsOption()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var startupProject = builder.AddProject<Projects.ServiceA>("startup");
        var migrations = startupProject.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WithMigrationsProject("path/to/Target.csproj");

        Assert.NotNull(migrations.Resource.MigrationsProjectPath);
        // Path gets combined with AppHostDirectory and normalized
        Assert.EndsWith("Target.csproj", migrations.Resource.MigrationsProjectPath);
    }

    [Fact]
    public void WithMigrationsProjectThrowsForNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        Assert.Throws<ArgumentNullException>(() => migrations.WithMigrationsProject(null!));
    }

    [Fact]
    public void WithMigrationsProjectThrowsForEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        Assert.Throws<ArgumentException>(() => migrations.WithMigrationsProject(""));
    }

    [Fact]
    public void PublishAsMigrationScriptDefaultsIdempotentToTrue()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript();

        Assert.True(migrations.Resource.PublishAsMigrationScript);
        Assert.True(migrations.Resource.ScriptIdempotent);
    }

    [Fact]
    public void PublishAsMigrationScriptCanOptOutOfIdempotentDefault()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript(idempotent: false);

        Assert.True(migrations.Resource.PublishAsMigrationScript);
        Assert.False(migrations.Resource.ScriptIdempotent);
    }

    [Fact]
    public void PublishAsMigrationScriptSetsNoTransactionsProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript(noTransactions: true);

        Assert.True(migrations.Resource.PublishAsMigrationScript);
        Assert.True(migrations.Resource.ScriptNoTransactions);
    }

    [Fact]
    public void PublishAsMigrationBundleSetsTargetRuntimeProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle(targetRuntime: "linux-x64");

        Assert.True(migrations.Resource.PublishAsMigrationBundle);
        Assert.Equal("linux-x64", migrations.Resource.BundleTargetRuntime);
    }

    [Fact]
    public void PublishAsMigrationBundleSetsSelfContainedProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle(selfContained: true);

        Assert.True(migrations.Resource.PublishAsMigrationBundle);
        Assert.True(migrations.Resource.BundleSelfContained);
    }

    [Fact]
    public void PublishAsMigrationScriptAndBundleWithAllOptions()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript(idempotent: true, noTransactions: true)
            .PublishAsMigrationBundle(targetRuntime: "win-x64", selfContained: true);

        Assert.True(migrations.Resource.PublishAsMigrationScript);
        Assert.True(migrations.Resource.ScriptIdempotent);
        Assert.True(migrations.Resource.ScriptNoTransactions);
        Assert.True(migrations.Resource.PublishAsMigrationBundle);
        Assert.Equal("win-x64", migrations.Resource.BundleTargetRuntime);
        Assert.True(migrations.Resource.BundleSelfContained);
    }

    // Test classes for DbContext types
    private sealed class TestDbContext { }
}
