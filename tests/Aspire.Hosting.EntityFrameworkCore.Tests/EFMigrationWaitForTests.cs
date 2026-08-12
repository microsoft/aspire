// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.EntityFrameworkCore.Tests;

public class EFMigrationWaitForTests
{
    [Fact]
    public void ResourceCanWaitForEFMigration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        // Another resource can wait for the migrations
        var anotherProject = builder.AddProject<Projects.ServiceB>("anotherproject")
            .WaitFor(migrations);

        // Verify WaitAnnotation was added
        var waitAnnotation = anotherProject.Resource.Annotations.OfType<WaitAnnotation>().FirstOrDefault();
        Assert.NotNull(waitAnnotation);
        Assert.Equal(migrations.Resource, waitAnnotation.Resource);
    }

    [Fact]
    public void MultipleResourcesCanWaitForSameEFMigration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var project1 = builder.AddProject<Projects.ServiceB>("project1")
            .WaitFor(migrations);
        var project2 = builder.AddProject<Projects.ServiceC>("project2")
            .WaitFor(migrations);

        var wait1 = project1.Resource.Annotations.OfType<WaitAnnotation>().FirstOrDefault(w => w.Resource == migrations.Resource);
        var wait2 = project2.Resource.Annotations.OfType<WaitAnnotation>().FirstOrDefault(w => w.Resource == migrations.Resource);

        Assert.NotNull(wait1);
        Assert.NotNull(wait2);
    }

    [Fact]
    public void ResourceCanWaitForMultipleEFMigrations()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations1 = project.AddEFMigrations("migrations1", typeof(TestDbContext).FullName!);
        var migrations2 = project.AddEFMigrations("migrations2", typeof(AnotherDbContext).FullName!);

        var anotherProject = builder.AddProject<Projects.ServiceB>("anotherproject")
            .WaitFor(migrations1)
            .WaitFor(migrations2);

        var waitAnnotations = anotherProject.Resource.Annotations.OfType<WaitAnnotation>().ToList();
        Assert.Equal(2, waitAnnotations.Count);
        Assert.Contains(waitAnnotations, w => w.Resource == migrations1.Resource);
        Assert.Contains(waitAnnotations, w => w.Resource == migrations2.Resource);
    }

    [Fact]
    public void WaitForWithRunDatabaseUpdateOnStartConfigurationWorks()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .RunDatabaseUpdateOnStart();

        var anotherProject = builder.AddProject<Projects.ServiceB>("anotherproject")
            .WaitFor(migrations);

        Assert.NotNull(anotherProject.Resource.Annotations.OfType<WaitAnnotation>().FirstOrDefault());
    }

    [Fact]
    public void ContainerResourceCanWaitForEFMigration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var container = builder.AddContainer("mycontainer", "someimage")
            .WaitFor(migrations);

        var waitAnnotation = container.Resource.Annotations.OfType<WaitAnnotation>().FirstOrDefault();
        Assert.NotNull(waitAnnotation);
        Assert.Equal(migrations.Resource, waitAnnotation.Resource);
    }

    [Fact]
    public void EFMigrationResourceInAppModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        _ = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify migration resource is in the app model
        var migrationResource = appModel.Resources.OfType<EFMigrationResource>().FirstOrDefault();
        Assert.NotNull(migrationResource);
        Assert.Equal("mymigrations", migrationResource.Name);
    }

    // Test classes for DbContext types
    private sealed class TestDbContext { }
    private sealed class AnotherDbContext { }
}
