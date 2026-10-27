// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.EntityFrameworkCore.Tests;

public class EFMigrationResourceTests
{
    [Fact]
    public void SharedIdentityAndMigrationConsumerApisAreStable()
    {
        Assert.Null(typeof(IDotnetProgramResource).GetCustomAttribute<ExperimentalAttribute>());

        var constructor = typeof(EFMigrationResource).GetConstructor(
            [typeof(string), typeof(IDotnetProgramResource), typeof(string)]);
        Assert.NotNull(constructor);
        Assert.Null(constructor.GetCustomAttribute<ExperimentalAttribute>());

        var methods = typeof(EFResourceBuilderExtensions).GetMethods()
            .Where(method => method.Name == nameof(EFResourceBuilderExtensions.AddEFMigrations)
                && method.GetParameters()[0].ParameterType == typeof(IResourceBuilder<IDotnetProgramResource>))
            .ToArray();

        Assert.Equal(4, methods.Length);
        Assert.All(methods, method => Assert.Null(method.GetCustomAttribute<ExperimentalAttribute>()));
    }

    [Theory]
    [InlineData("Service.csproj")]
    [InlineData("Service.fsproj")]
    [InlineData("Service.vbproj")]
    public void GeneralizedConstructorCapturesDataWithoutRegisteringResource(string path)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddDotnetProject("project", path, options => options.ExcludeLaunchProfile = true);
        var resources = builder.Resources.ToArray();

        var resource = new EFMigrationResource("migrations", (IDotnetProgramResource)project.Resource, "App.Context");

        Assert.Same(project.Resource, resource.StartupProjectResource);
        Assert.Equal("App.Context", resource.DbContextTypeName);
        Assert.Equal<IResource>(resources, builder.Resources);
        Assert.Empty(resource.Annotations);
    }

    [Fact]
    public void GeneralizedConstructorRejectsNullStartupProject()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new EFMigrationResource("migrations", (IDotnetProgramResource)null!, null));

        Assert.Equal("startupProjectResource", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void GeneralizedFactoryOverloadsRegisterMigration(int overload)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        IResourceBuilder<IDotnetProgramResource> project =
            builder.AddDotnetProject("project", "Service.csproj", options => options.ExcludeLaunchProfile = true);

        var migrations = overload switch
        {
            0 => project.AddEFMigrations("migrations"),
            1 => project.AddEFMigrations("migrations", "App.Context"),
            2 => project.AddEFMigrations("migrations", configureToolResource: null),
            3 => project.AddEFMigrations("migrations", "App.Context", configureToolResource: null),
            _ => throw new ArgumentOutOfRangeException(nameof(overload))
        };

        Assert.Same(project.Resource, migrations.Resource.StartupProjectResource);
        Assert.Equal(overload is 1 or 3 ? "App.Context" : null, migrations.Resource.DbContextTypeName);
        Assert.Same(migrations.Resource, Assert.Single(builder.Resources.OfType<EFMigrationResource>()));
        Assert.NotEmpty(migrations.Resource.Annotations);
    }
}
