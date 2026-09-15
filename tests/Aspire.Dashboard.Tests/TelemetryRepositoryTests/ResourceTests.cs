// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.TelemetryRepositoryTests;

public abstract class ResourceTests : TelemetryRepositoryTestBase
{
    [Fact]
    public async Task GetResourceByCompositeName()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        await AddResource(repositoryContext.Repository, "app2");
        await AddResource(repositoryContext.Repository, "app1");

        // Act 1
        var resources = repositoryContext.Repository.GetResources();

        // Assert 1
        Assert.Collection(resources,
            app =>
            {
                Assert.Equal("app1", app.ResourceName);
                Assert.Equal("TestId", app.InstanceId);
            },
            app =>
            {
                Assert.Equal("app2", app.ResourceName);
                Assert.Equal("TestId", app.InstanceId);
            });

        // Act 2
        var app1 = repositoryContext.Repository.GetResourceByCompositeName("app1-TestId");
        var app2 = repositoryContext.Repository.GetResourceByCompositeName("APP2-TESTID");
        var notFound = repositoryContext.Repository.GetResourceByCompositeName("APP2_TESTID");

        // Assert 2
        Assert.NotNull(app1);
        Assert.Equal("app1", app1.ResourceName);
        Assert.Equal(resources[0], app1);

        Assert.NotNull(app2);
        Assert.Equal("app2", app2.ResourceName);
        Assert.Equal(resources[1], app2);

        Assert.Null(notFound);
    }

    [Fact]
    public async Task GetResources_WithNameAndNoKey()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        await AddResource(repositoryContext.Repository, "app2");
        await AddResource(repositoryContext.Repository, "app1", instanceId: "123");
        await AddResource(repositoryContext.Repository, "app1", instanceId: "456");

        // Act 1
        var resources1 = repositoryContext.Repository.GetResources(new ResourceKey("app1", InstanceId: null));

        // Assert 1
        Assert.Collection(resources1,
            app =>
            {
                Assert.Equal("app1", app.ResourceName);
                Assert.Equal("123", app.InstanceId);
            },
            app =>
            {
                Assert.Equal("app1", app.ResourceName);
                Assert.Equal("456", app.InstanceId);
            });

        // Act 2
        var resources2 = repositoryContext.Repository.GetResources(new ResourceKey("app2", InstanceId: null));

        // Assert 2
        Assert.Collection(resources2,
            app =>
            {
                Assert.Equal("app2", app.ResourceName);
                Assert.Equal("TestId", app.InstanceId);
            });
    }

    [Fact]
    public async Task GetResources_Order()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        await AddResource(repositoryContext.Repository, "app2");
        await AddResource(repositoryContext.Repository, "app1", instanceId: "def");
        await AddResource(repositoryContext.Repository, "app1", instanceId: "abc");

        // Act
        var resources = repositoryContext.Repository.GetResources();

        // Assert
        Assert.Collection(resources,
            app =>
            {
                Assert.Equal("app1", app.ResourceName);
                Assert.Equal("abc", app.InstanceId);
            },
            app =>
            {
                Assert.Equal("app1", app.ResourceName);
                Assert.Equal("def", app.InstanceId);
            },
            app =>
            {
                Assert.Equal("app2", app.ResourceName);
                Assert.Equal("TestId", app.InstanceId);
            });
    }

    [Fact]
    public async Task GetResourceName_GuidInstanceId_Shorten()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();
        var guid1 = "19572b19-d1c0-4a51-98b4-fcc2658f73d3";
        var guid2 = "f66e2b1e-f420-4a22-a067-8dd2f6fcda86";

        await AddResource(repositoryContext.Repository, "app1", guid1);
        await AddResource(repositoryContext.Repository, "app1", guid2);

        // Act
        var resources = repositoryContext.Repository.GetResources();

        var instance1Name = OtlpHelpers.GetResourceName(resources[0], resources);
        var instance2Name = OtlpHelpers.GetResourceName(resources[1], resources);

        // Assert
        Assert.Equal("app1-658f73d3", instance1Name);
        Assert.Equal("app1-f6fcda86", instance2Name);
    }

    [Fact]
    public async Task GetResourceName_Version7GuidInstanceId_ShortenedNamesDiffer()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        // Version 7 GUIDs created close in time share the same leading characters.
        var guid1 = "01890a5d-ac96-774b-bcce-b302099a8057";
        var guid2 = "01890a5d-ac96-7768-a3e2-34c4a0e9f6ad";

        await AddResource(repositoryContext.Repository, "app1", guid1);
        await AddResource(repositoryContext.Repository, "app1", guid2);

        // Act
        var resources = repositoryContext.Repository.GetResources();

        var instance1 = Assert.Single(resources, r => r.InstanceId == guid1);
        var instance2 = Assert.Single(resources, r => r.InstanceId == guid2);

        var instance1Name = OtlpHelpers.GetResourceName(instance1, resources);
        var instance2Name = OtlpHelpers.GetResourceName(instance2, resources);

        // Assert
        Assert.Equal("app1-099a8057", instance1Name);
        Assert.Equal("app1-a0e9f6ad", instance2Name);
    }

    private static async Task AddResource(ITelemetryRepository repository, string name, string? instanceId = null)
    {
        var addContext = new AddContext();
        await repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: name, instanceId: instanceId)
            }
        });

        Assert.Equal(0, addContext.FailureCount);
    }
}

public sealed class SqliteResourceTests : ResourceTests
{
}
