// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Tests.Shared.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ResourcesSelectHelpersTests
{
    [Fact]
    public void GetResource_SameNameAsReplica_GetInstance()
    {
        // Arrange
        var appVMs = ResourcesSelectHelpers.CreateResources(new List<OtlpResource>
        {
            CreateOtlpResource(name: "multiple", instanceId: "instance"),
            CreateOtlpResource(name: "multiple", instanceId: "instanceabc"),
            CreateOtlpResource(name: "singleton", instanceId: "instanceabc")
        });

        Assert.Collection(appVMs,
            app =>
            {
                Assert.Equal("multiple", app.Name);
                Assert.Equal(OtlpResourceType.ResourceGrouping, app.Id!.Type);
                Assert.Null(app.Id!.InstanceId);
            },
            app =>
            {
                Assert.Equal("multiple-instance", app.Name);
                Assert.Equal(OtlpResourceType.Instance, app.Id!.Type);
                Assert.Equal("multiple-instance", app.Id!.InstanceId);
            },
            app =>
            {
                Assert.Equal("multiple-instanceabc", app.Name);
                Assert.Equal(OtlpResourceType.Instance, app.Id!.Type);
                Assert.Equal("multiple-instanceabc", app.Id!.InstanceId);
            },
            app =>
            {
                Assert.Equal("singleton", app.Name);
                Assert.Equal(OtlpResourceType.Singleton, app.Id!.Type);
                Assert.Equal("singleton-instanceabc", app.Id!.InstanceId);
            });

        // Act
        var app = appVMs.GetResource(NullLogger.Instance, "multiple-instanceabc", canSelectGrouping: false, null!);

        // Assert
        Assert.Equal("multiple-instanceabc", app.Id!.InstanceId);
        Assert.Equal(OtlpResourceType.Instance, app.Id!.Type);
    }

    [Fact]
    public void GetResource_ShortenedGuidInstanceId_GetInstance()
    {
        var appVMs = ResourcesSelectHelpers.CreateResources(new List<OtlpResource>
        {
            CreateOtlpResource(name: "dotnet-cli", instanceId: "aaaaaaaa-bbbb-cccc-dddd-eeee6a764b7d"),
            CreateOtlpResource(name: "dotnet-cli", instanceId: "aaaaaaaa-bbbb-cccc-dddd-eeee12345678")
        });

        var app = appVMs.GetResource(NullLogger.Instance, "dotnet-cli-6a764b7d", canSelectGrouping: true, null!);

        Assert.Equal("dotnet-cli-6a764b7d", app.Name);
        Assert.Equal("dotnet-cli-aaaaaaaa-bbbb-cccc-dddd-eeee6a764b7d", app.Id!.InstanceId);
        Assert.Equal(OtlpResourceType.Instance, app.Id.Type);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void GetResource_ShortenedGuidInstanceIdMatchesResourceName_PreferExactMatch(bool isReplicaSet, bool canSelectGrouping)
    {
        var resources = new List<OtlpResource>
        {
            CreateOtlpResource(name: "dotnet-cli", instanceId: "aaaaaaaa-bbbb-cccc-dddd-eeee6a764b7d"),
            CreateOtlpResource(name: "dotnet-cli", instanceId: "aaaaaaaa-bbbb-cccc-dddd-eeee12345678"),
            CreateOtlpResource(name: "dotnet-cli-6a764b7d", instanceId: "other-instance")
        };
        if (isReplicaSet)
        {
            resources.Add(CreateOtlpResource(name: "dotnet-cli-6a764b7d", instanceId: "another-instance"));
        }
        var appVMs = ResourcesSelectHelpers.CreateResources(resources);
        var expected = appVMs.Single(app => app.Id!.ReplicaSetName == "dotnet-cli-6a764b7d" && app.Id.Type != OtlpResourceType.Instance);
        var testSink = new TestSink();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(testSink)));

        var app = appVMs.GetResource(factory.CreateLogger("Test"), "dotnet-cli-6a764b7d", canSelectGrouping, null!);

        Assert.Same(expected, app);
        Assert.Empty(testSink.Writes);

        var replica = appVMs.Single(app => app.Id!.InstanceId == "dotnet-cli-aaaaaaaa-bbbb-cccc-dddd-eeee6a764b7d");
        Assert.Same(replica, appVMs.GetResource(NullLogger.Instance, replica.Id!.InstanceId, canSelectGrouping, null!));
    }

    [Fact]
    public void GetResource_ShortenedGuidInstanceIdMatchesDisabledGroup_GetInstance()
    {
        var appVMs = ResourcesSelectHelpers.CreateResources(new List<OtlpResource>
        {
            CreateOtlpResource(name: "dotnet-cli", instanceId: "aaaaaaaa-bbbb-cccc-dddd-eeee6a764b7d"),
            CreateOtlpResource(name: "dotnet-cli", instanceId: "aaaaaaaa-bbbb-cccc-dddd-eeee12345678"),
            CreateOtlpResource(name: "dotnet-cli-6a764b7d", instanceId: "other-instance"),
            CreateOtlpResource(name: "dotnet-cli-6a764b7d", instanceId: "another-instance")
        });
        var expected = appVMs.Single(app => app.Id!.InstanceId == "dotnet-cli-aaaaaaaa-bbbb-cccc-dddd-eeee6a764b7d");

        var app = appVMs.GetResource(NullLogger.Instance, "dotnet-cli-6a764b7d", canSelectGrouping: false, null!);

        Assert.Same(expected, app);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetResource_ShortenedGuidInstanceIdCollision_UseFirstAndLogWarning(bool canSelectGrouping)
    {
        var appVMs = ResourcesSelectHelpers.CreateResources(new List<OtlpResource>
        {
            CreateOtlpResource(name: "dotnet-cli", instanceId: "aaaaaaaa-bbbb-cccc-dddd-eeee6a764b7d"),
            CreateOtlpResource(name: "dotnet-cli", instanceId: "11111111-2222-3333-4444-55556a764b7d")
        });
        var instances = appVMs.Where(app => app.Id!.Type == OtlpResourceType.Instance).ToList();
        Assert.Collection(instances,
            app =>
            {
                Assert.Equal("dotnet-cli-6a764b7d", app.Name);
                Assert.Equal("dotnet-cli-aaaaaaaa-bbbb-cccc-dddd-eeee6a764b7d", app.Id!.InstanceId);
            },
            app =>
            {
                Assert.Equal("dotnet-cli-6a764b7d", app.Name);
                Assert.Equal("dotnet-cli-11111111-2222-3333-4444-55556a764b7d", app.Id!.InstanceId);
            });

        var testSink = new TestSink();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(testSink)));
        var logger = factory.CreateLogger("Test");

        var app = appVMs.GetResource(logger, "dotnet-cli-6a764b7d", canSelectGrouping, null!);

        Assert.Same(instances[0], app);
        Assert.Equal(LogLevel.Warning, Assert.Single(testSink.Writes).LogLevel);

        foreach (var instance in instances)
        {
            Assert.Same(instance, appVMs.GetResource(logger, instance.Id!.InstanceId, canSelectGrouping, null!));
        }

        Assert.Single(testSink.Writes);
    }

    [Fact]
    public void GetResource_NullInstanceId_GetInstance()
    {
        // Arrange
        var appVMs = ResourcesSelectHelpers.CreateResources(new List<OtlpResource>
        {
            CreateOtlpResource(name: "singleton", instanceId: null)
        });

        // Act
        var app = appVMs.GetResource(NullLogger.Instance, "singleton", canSelectGrouping: false, null!);

        // Assert
        Assert.Equal("singleton", app.Id!.InstanceId);
        Assert.Equal(OtlpResourceType.Singleton, app.Id!.Type);
    }

    [Fact]
    public void GetResource_EndWithDash_GetInstance()
    {
        // Arrange
        var appVMs = ResourcesSelectHelpers.CreateResources(new List<OtlpResource>
        {
            CreateOtlpResource(name: "singleton-", instanceId: null)
        });

        // Act
        var app = appVMs.GetResource(NullLogger.Instance, "singleton-", canSelectGrouping: false, null!);

        // Assert
        Assert.Equal("singleton-", app.Id!.InstanceId);
        Assert.Equal(OtlpResourceType.Singleton, app.Id!.Type);
    }

    [Theory]
    [InlineData("singleton-", "", true)]
    [InlineData("singleton-", null, false)]
    [InlineData("singleton", "", true)]
    [InlineData("singleton", null, true)]
    public void GetResource_EmptyOrNullInstanceId_GetInstance(string name, string? instanceId, bool found)
    {
        // Arrange
        var appVMs = ResourcesSelectHelpers.CreateResources(new List<OtlpResource>
        {
            CreateOtlpResource(name: "singleton", instanceId: instanceId)
        });

        // Act
        var app = appVMs.GetResource(NullLogger.Instance, name, canSelectGrouping: false, null!);

        // Assert
        Assert.Equal(found, app != null);
    }

    [Fact]
    public void GetResource_NameDifferentByCase_Merge()
    {
        // Arrange
        var appVMs = ResourcesSelectHelpers.CreateResources(new List<OtlpResource>
        {
            CreateOtlpResource(name: "name", instanceId: "instance"),
            CreateOtlpResource(name: "NAME", instanceId: "instanceabc")
        });

        Assert.Collection(appVMs,
            app =>
            {
                Assert.Equal("name", app.Name);
                Assert.Equal(OtlpResourceType.ResourceGrouping, app.Id!.Type);
                Assert.Null(app.Id!.InstanceId);
            },
            app =>
            {
                Assert.Equal("NAME-instance", app.Name);
                Assert.Equal(OtlpResourceType.Instance, app.Id!.Type);
                Assert.Equal("name-instance", app.Id!.InstanceId);
            },
            app =>
            {
                Assert.Equal("NAME-instanceabc", app.Name);
                Assert.Equal(OtlpResourceType.Instance, app.Id!.Type);
                Assert.Equal("NAME-instanceabc", app.Id!.InstanceId);
            });

        var testSink = new TestSink();
        var factory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(testSink)));
        var logger = factory.CreateLogger("Test");

        // Act
        var app1 = appVMs.GetResource(logger, "name-instance", canSelectGrouping: false, null!);

        // Assert
        Assert.Equal("name-instance", app1.Id!.InstanceId);
        Assert.Equal(OtlpResourceType.Instance, app1.Id!.Type);
        Assert.Empty(testSink.Writes);

        // Act
        var app2 = appVMs.GetResource(logger, "name-instanceabc", canSelectGrouping: false, null!);

        // Assert
        Assert.Equal("NAME-instanceabc", app2.Id!.InstanceId);
        Assert.Equal(OtlpResourceType.Instance, app2.Id!.Type);
        Assert.Empty(testSink.Writes);
    }

    [Fact]
    public void GetResource_MultipleMatches_UseFirst()
    {
        // Arrange
        var apps = new Dictionary<string, OtlpResource>();

        var appVMs = new List<SelectViewModel<ResourceTypeDetails>>
        {
            new SelectViewModel<ResourceTypeDetails>() { Name = "test", Id = ResourceTypeDetails.CreateSingleton("test-abc", "test") },
            new SelectViewModel<ResourceTypeDetails>() { Name = "test", Id = ResourceTypeDetails.CreateSingleton("test-def", "test") }
        };

        var testSink = new TestSink();
        var factory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(testSink)));

        // Act
        var app = appVMs.GetResource(factory.CreateLogger("Test"), "test", canSelectGrouping: false, null!);

        // Assert
        Assert.Equal("test-abc", app.Id!.InstanceId);
        Assert.Equal(OtlpResourceType.Singleton, app.Id!.Type);
        Assert.Single(testSink.Writes);
    }

    [Fact]
    public void GetResource_SelectGroup_NotEnabled_ReturnNull()
    {
        // Arrange
        var appVMs = ResourcesSelectHelpers.CreateResources(new List<OtlpResource>
        {
            CreateOtlpResource(name: "app", instanceId: "123"),
            CreateOtlpResource(name: "app", instanceId: "456")
        });

        Assert.Collection(appVMs,
            app =>
            {
                Assert.Equal("app", app.Name);
                Assert.Equal(OtlpResourceType.ResourceGrouping, app.Id!.Type);
                Assert.Null(app.Id!.InstanceId);
            },
            app =>
            {
                Assert.Equal("app-123", app.Name);
                Assert.Equal(OtlpResourceType.Instance, app.Id!.Type);
                Assert.Equal("app-123", app.Id!.InstanceId);
            },
            app =>
            {
                Assert.Equal("app-456", app.Name);
                Assert.Equal(OtlpResourceType.Instance, app.Id!.Type);
                Assert.Equal("app-456", app.Id!.InstanceId);
            });

        // Act
        var app = appVMs.GetResource(NullLogger.Instance, "app", canSelectGrouping: false, null!);

        // Assert
        Assert.Null(app);
    }

    [Fact]
    public void GetResource_SelectGroup_Enabled_ReturnGroup()
    {
        // Arrange
        var appVMs = ResourcesSelectHelpers.CreateResources(new List<OtlpResource>
        {
            CreateOtlpResource(name: "app", instanceId: "123"),
            CreateOtlpResource(name: "app", instanceId: "456")
        });

        Assert.Collection(appVMs,
            app =>
            {
                Assert.Equal("app", app.Name);
                Assert.Equal(OtlpResourceType.ResourceGrouping, app.Id!.Type);
                Assert.Null(app.Id!.InstanceId);
            },
            app =>
            {
                Assert.Equal("app-123", app.Name);
                Assert.Equal(OtlpResourceType.Instance, app.Id!.Type);
                Assert.Equal("app-123", app.Id!.InstanceId);
            },
            app =>
            {
                Assert.Equal("app-456", app.Name);
                Assert.Equal(OtlpResourceType.Instance, app.Id!.Type);
                Assert.Equal("app-456", app.Id!.InstanceId);
            });

        // Act
        var app = appVMs.GetResource(NullLogger.Instance, "app", canSelectGrouping: true, null!);

        // Assert
        Assert.Equal("app", app.Name);
        Assert.Equal(OtlpResourceType.ResourceGrouping, app.Id!.Type);
    }

    private static OtlpResource CreateOtlpResource(string name, string? instanceId)
    {
        var resource = new Resource();
        resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = name } });
        if (instanceId != null)
        {
            resource.Attributes.Add(new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = instanceId } });
        }

        var key = OtlpHelpers.GetResourceKey(resource);

        return new OtlpResource(key.Name, key.InstanceId, uninstrumentedPeer: false, TelemetryTestHelpers.CreateContext());
    }
}
