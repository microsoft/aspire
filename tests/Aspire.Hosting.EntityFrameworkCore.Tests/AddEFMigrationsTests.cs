// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.EntityFrameworkCore.Tests;

public class AddEFMigrationsTests
{
    [Fact]
    public void AddEFMigrationsCreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        Assert.NotNull(migrations);
        Assert.IsAssignableFrom<IResourceBuilder<EFMigrationResource>>(migrations);
        Assert.Equal("mymigrations", migrations.Resource.Name);
        Assert.Equal(project.Resource, migrations.Resource.ProjectResource);
        Assert.Equal(typeof(TestDbContext).FullName, migrations.Resource.DbContextTypeName);
    }

    [Fact]
    public void AddEFMigrationsWithoutContextTypeCreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations");

        Assert.NotNull(migrations);
        Assert.IsAssignableFrom<IResourceBuilder<EFMigrationResource>>(migrations);
        Assert.Equal("mymigrations", migrations.Resource.Name);
        Assert.Equal(project.Resource, migrations.Resource.ProjectResource);
        Assert.Null(migrations.Resource.DbContextTypeName);
    }

    [Fact]
    public void AddEFMigrationsWithExplicitContextTypeNameCreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        Assert.NotNull(migrations);
        Assert.IsAssignableFrom<IResourceBuilder<EFMigrationResource>>(migrations);
        Assert.Equal("mymigrations", migrations.Resource.Name);
        Assert.Equal(project.Resource, migrations.Resource.ProjectResource);
        Assert.Equal(typeof(TestDbContext).FullName, migrations.Resource.DbContextTypeName);
    }

    [Fact]
    public void AddEFMigrationsWithContextTypeNameStringCreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var contextTypeName = "MyApp.Data.ApplicationDbContext";
        var migrations = project.AddEFMigrations("mymigrations", contextTypeName);

        Assert.NotNull(migrations);
        Assert.IsAssignableFrom<IResourceBuilder<EFMigrationResource>>(migrations);
        Assert.Equal("mymigrations", migrations.Resource.Name);
        Assert.Equal(project.Resource, migrations.Resource.ProjectResource);
        Assert.Equal(contextTypeName, migrations.Resource.DbContextTypeName);
    }

    [Fact]
    public void AddEFMigrationsAddsResourceToAppModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var migrationResource = Assert.Single(appModel.Resources.OfType<EFMigrationResource>());
        Assert.Equal("mymigrations", migrationResource.Name);
    }

    [Fact]
    public void AddEFMigrationsForMultipleContextsSucceeds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");

        var migrations1 = project.AddEFMigrations("migrations1", typeof(TestDbContext).FullName!);
        var migrations2 = project.AddEFMigrations("migrations2", typeof(AnotherDbContext).FullName!);

        Assert.NotEqual(migrations1.Resource, migrations2.Resource);
        Assert.Equal(typeof(TestDbContext).FullName, migrations1.Resource.DbContextTypeName);
        Assert.Equal(typeof(AnotherDbContext).FullName, migrations2.Resource.DbContextTypeName);
    }

    [Fact]
    public void AddEFMigrationsForMultipleContextsWithStringNamesSucceeds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");

        var migrations1 = project.AddEFMigrations("migrations1", "MyApp.Data.AppDbContext");
        var migrations2 = project.AddEFMigrations("migrations2", "MyApp.Data.LoggingDbContext");

        Assert.NotEqual(migrations1.Resource, migrations2.Resource);
        Assert.Equal("MyApp.Data.AppDbContext", migrations1.Resource.DbContextTypeName);
        Assert.Equal("MyApp.Data.LoggingDbContext", migrations2.Resource.DbContextTypeName);
    }

    [Fact]
    public void AddEFMigrationsDuplicateContextTypeThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");

        project.AddEFMigrations("migrations1", typeof(TestDbContext).FullName!);

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            project.AddEFMigrations("migrations2", typeof(TestDbContext).FullName!);
        });

        Assert.Contains("TestDbContext", exception.Message);
        Assert.Contains("already been registered", exception.Message);
    }

    [Fact]
    public void AddEFMigrationsDuplicateContextTypeNameStringThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");

        project.AddEFMigrations("migrations1", "MyApp.Data.AppDbContext");

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            project.AddEFMigrations("migrations2", "MyApp.Data.AppDbContext");
        });

        Assert.Contains("AppDbContext", exception.Message);
        Assert.Contains("already been registered", exception.Message);
    }

    [Fact]
    public void AddEFMigrationsWithNullNameThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");

        Assert.Throws<ArgumentNullException>(() =>
        {
            project.AddEFMigrations(null!, typeof(TestDbContext).FullName!);
        });
    }

    [Fact]
    public void AddEFMigrationsWithEmptyNameThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");

        Assert.Throws<ArgumentException>(() =>
        {
            project.AddEFMigrations("", typeof(TestDbContext).FullName!);
        });
    }

    [Fact]
    public void AddEFMigrationsWithEmptyContextTypeNameThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");

        Assert.Throws<ArgumentException>(() =>
        {
            project.AddEFMigrations("mymigrations", "");
        });
    }

    [Fact]
    public void AddEFMigrationsHasResourceSnapshotAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var snapshotAnnotation = migrations.Resource.Annotations.OfType<ResourceSnapshotAnnotation>().FirstOrDefault();
        Assert.NotNull(snapshotAnnotation);
        Assert.Equal("EFMigration", snapshotAnnotation.InitialSnapshot.ResourceType);
        Assert.Equal("NotStarted", snapshotAnnotation.InitialSnapshot.State?.Text);
    }

    [Fact]
    public void AddEFMigrationsHasDatabaseIcon()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var iconAnnotation = migrations.Resource.Annotations.OfType<ResourceIconAnnotation>().FirstOrDefault();
        Assert.NotNull(iconAnnotation);
        Assert.Equal("Database", iconAnnotation.IconName);
    }

    [Fact]
    public void EFMigrationResourceImplementsIResourceWithWaitSupport()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        Assert.IsAssignableFrom<IResourceWithWaitSupport>(migrations.Resource);
    }

    [Fact]
    public void EFMigrationResourceHasOptionsProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        Assert.False(migrations.Resource.PublishAsMigrationScript);
        Assert.False(migrations.Resource.PublishAsMigrationBundle);
    }

    // Test classes for DbContext types
    private sealed class TestDbContext { }
    private sealed class AnotherDbContext { }
}
