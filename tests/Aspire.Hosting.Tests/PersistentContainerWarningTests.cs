// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREUSERSECRETS001

using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.UserSecrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Hosting.Tests;

public class PersistentContainerWarningTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task PersistentContainerWithoutUserSecrets_LogsWarning()
    {
        var testSink = new TestSink();

        var services = new ServiceCollection();
        services.AddSingleton<IUserSecretsManager>(NoopUserSecretsManager.Instance);
        services.AddLogging(logging => logging.AddProvider(new TestLoggerProvider(testSink)));
        services.AddLogging(logging => logging.AddXunit(testOutputHelper));
        var serviceProvider = services.BuildServiceProvider();

        var resources = new ResourceCollection();
        var container = new ContainerResource("my-container");
        container.Annotations.Add(new ContainerLifetimeAnnotation { Lifetime = ContainerLifetime.Persistent });
        resources.Add(container);

        var model = new DistributedApplicationModel(resources);
        var beforeStartEvent = new BeforeStartEvent(serviceProvider, model);

        await BuiltInDistributedApplicationEventSubscriptionHandlers.WarnPersistentContainersWithoutUserSecrets(beforeStartEvent, CancellationToken.None);

        Assert.Contains(testSink.Writes, w => w.LogLevel == LogLevel.Warning && w.Message?.Contains("my-container") == true);
    }

    [Fact]
    public async Task PersistentContainerWithUserSecrets_DoesNotLogWarning()
    {
        var testSink = new TestSink();

        var services = new ServiceCollection();
        services.AddSingleton<IUserSecretsManager>(new MockUserSecretsManager());
        services.AddLogging(logging => logging.AddProvider(new TestLoggerProvider(testSink)));
        services.AddLogging(logging => logging.AddXunit(testOutputHelper));
        var serviceProvider = services.BuildServiceProvider();

        var resources = new ResourceCollection();
        var container = new ContainerResource("my-container");
        container.Annotations.Add(new ContainerLifetimeAnnotation { Lifetime = ContainerLifetime.Persistent });
        resources.Add(container);

        var model = new DistributedApplicationModel(resources);
        var beforeStartEvent = new BeforeStartEvent(serviceProvider, model);

        await BuiltInDistributedApplicationEventSubscriptionHandlers.WarnPersistentContainersWithoutUserSecrets(beforeStartEvent, CancellationToken.None);

        Assert.DoesNotContain(testSink.Writes, w => w.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public async Task SessionContainerWithoutUserSecrets_DoesNotLogWarning()
    {
        var testSink = new TestSink();

        var services = new ServiceCollection();
        services.AddSingleton<IUserSecretsManager>(NoopUserSecretsManager.Instance);
        services.AddLogging(logging => logging.AddProvider(new TestLoggerProvider(testSink)));
        services.AddLogging(logging => logging.AddXunit(testOutputHelper));
        var serviceProvider = services.BuildServiceProvider();

        var resources = new ResourceCollection();
        resources.Add(new ContainerResource("my-container"));

        var model = new DistributedApplicationModel(resources);
        var beforeStartEvent = new BeforeStartEvent(serviceProvider, model);

        await BuiltInDistributedApplicationEventSubscriptionHandlers.WarnPersistentContainersWithoutUserSecrets(beforeStartEvent, CancellationToken.None);

        Assert.DoesNotContain(testSink.Writes, w => w.LogLevel == LogLevel.Warning);
    }
}
