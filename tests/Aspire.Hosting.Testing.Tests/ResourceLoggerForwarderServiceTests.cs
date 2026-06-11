// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Projects;
using Xunit;

namespace Aspire.Hosting.Testing.Tests;

public class ResourceLoggerForwarderServiceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task BackgroundServiceIsRegisteredInServiceProvider()
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<TestingAppHost1_AppHost>();
        Assert.Contains(appHost.Services, sd =>
            sd.ServiceType == typeof(IHostedService)
            && sd.ImplementationType == typeof(ResourceLoggerForwarderService)
            && sd.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public async Task ExecuteDoesNotThrowOperationCanceledWhenAppStoppingTokenSignaled()
    {
        var hostApplicationLifetime = new TestHostApplicationLifetime();
        var resourceLoggerService = new ResourceLoggerService();
        var resourceNotificationService = CreateResourceNotificationService(hostApplicationLifetime, resourceLoggerService);
        var hostEnvironment = new HostingEnvironment();
        var loggerFactory = new NullLoggerFactory();
        var resourceLogForwarder = new ResourceLoggerForwarderService(resourceNotificationService, resourceLoggerService, hostEnvironment, loggerFactory);

        // use a task to signal when the resourceLogForwarder has started executing
        var subscribedTcs = new TaskCompletionSource();
        var subscriberLoop = Task.Run(async () =>
        {
            await foreach (var sub in resourceLoggerService.WatchAnySubscribersAsync(hostApplicationLifetime.ApplicationStopping))
            {
                subscribedTcs.TrySetResult();
                return;
            }
        });

        await resourceLogForwarder.StartAsync(hostApplicationLifetime.ApplicationStopping);

        Assert.NotNull(resourceLogForwarder.ExecuteTask);
        Assert.Equal(TaskStatus.WaitingForActivation, resourceLogForwarder.ExecuteTask.Status);

        // Publish an update to the resource to kickstart the notification service loop
        var myresource = new CustomResource("myresource");
        await resourceNotificationService.PublishUpdateAsync(myresource, snapshot => snapshot with { State = "Running" });

        // Wait for the log stream to begin
        await subscribedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Signal the stopping token
        hostApplicationLifetime.StopApplication();

        await resourceLogForwarder.ExecuteTask;
    }

    [Fact]
    public async Task ResourceLogsAreForwardedToHostLogging()
    {
        var hostApplicationLifetime = new TestHostApplicationLifetime();
        var resourceLoggerService = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var resourceNotificationService = CreateResourceNotificationService(hostApplicationLifetime, resourceLoggerService);
        var hostEnvironment = new HostingEnvironment { ApplicationName = "TestApp.AppHost" };
        var fakeLoggerProvider = new FakeLoggerProvider();
        var fakeLoggerFactory = new LoggerFactory([fakeLoggerProvider, new XunitLoggerProvider(output)]);
        var resourceLogForwarder = new ResourceLoggerForwarderService(resourceNotificationService, resourceLoggerService, hostEnvironment, fakeLoggerFactory);

        var subscribedTcs = new TaskCompletionSource();
        var subscriberLoop = Task.Run(async () =>
        {
            await foreach (var sub in resourceLoggerService.WatchAnySubscribersAsync(hostApplicationLifetime.ApplicationStopping))
            {
                if (sub.AnySubscribers && sub.Name == "myresource")
                {
                    subscribedTcs.TrySetResult();
                }
            }
        });

        var expectedLogCountTcs = new TaskCompletionSource();
        var expectedLogCount = 6;
        var logCount = 0;
        resourceLogForwarder.OnResourceLog = resourceId =>
        {
            logCount++;
            if (logCount >= expectedLogCount)
            {
                expectedLogCountTcs.TrySetResult();
            }
        };

        await resourceLogForwarder.StartAsync(hostApplicationLifetime.ApplicationStopping);

        // Publish an update to the resource to kickstart the notification service loop
        var myresource = new CustomResource("myresource");
        await resourceNotificationService.PublishUpdateAsync(myresource, snapshot => snapshot with { State = "Running" });

        // Wait for the log stream to begin
        await subscribedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Log messages to the resource
        fakeLoggerProvider.Collector.Clear();

        var resourceLogger = resourceLoggerService.GetLogger(myresource);
        resourceLogger.LogTrace("Test trace message");
        resourceLogger.LogDebug("Test debug message");
        resourceLogger.LogInformation("Test information message");
        resourceLogger.LogWarning("Test warning message");
        resourceLogger.LogError("Test error message");
        resourceLogger.LogCritical("Test critical message");

        // Wait for the 6 log messages or timeout
        await expectedLogCountTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Complete the resource log stream and wait for it to end
        resourceLoggerService.Complete(myresource);
        hostApplicationLifetime.StopApplication();

        // Get the logs from the fake logger
        var hostLogs = fakeLoggerProvider.Collector.GetSnapshot();

        // Line number is pre-pended to message
        // Category is derived from the application name and resource name
        // Logs sent at information level or lower are logged as information, otherwise they are logged as error
        Assert.Collection(hostLogs,
            log => { Assert.Equal(LogLevel.Information, log.Level); Assert.Equal("1: 2000-12-29T20:59:59.0000000Z Test trace message", log.Message); Assert.Equal("TestApp.AppHost.Resources.myresource", log.Category); },
            log => { Assert.Equal(LogLevel.Information, log.Level); Assert.Equal("2: 2000-12-29T20:59:59.0000000Z Test debug message", log.Message); Assert.Equal("TestApp.AppHost.Resources.myresource", log.Category); },
            log => { Assert.Equal(LogLevel.Information, log.Level); Assert.Equal("3: 2000-12-29T20:59:59.0000000Z Test information message", log.Message); Assert.Equal("TestApp.AppHost.Resources.myresource", log.Category); },
            log => { Assert.Equal(LogLevel.Information, log.Level); Assert.Equal("4: 2000-12-29T20:59:59.0000000Z Test warning message", log.Message); Assert.Equal("TestApp.AppHost.Resources.myresource", log.Category); },
            log => { Assert.Equal(LogLevel.Error, log.Level); Assert.Equal("5: 2000-12-29T20:59:59.0000000Z Test error message", log.Message); Assert.Equal("TestApp.AppHost.Resources.myresource", log.Category); },
            log => { Assert.Equal(LogLevel.Error, log.Level); Assert.Equal("6: 2000-12-29T20:59:59.0000000Z Test critical message", log.Message); Assert.Equal("TestApp.AppHost.Resources.myresource", log.Category); });
    }

    private static ResourceNotificationService CreateResourceNotificationService(TestHostApplicationLifetime hostApplicationLifetime, ResourceLoggerService resourceLoggerService)
    {
        return new ResourceNotificationService(NullLogger<ResourceNotificationService>.Instance, hostApplicationLifetime, new ServiceCollection().BuildServiceProvider(), resourceLoggerService);
    }

    private sealed class CustomResource(string name) : Resource(name)
    {

    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _startedCts = new();
        private readonly CancellationTokenSource _stoppingCts = new();
        private readonly CancellationTokenSource _stoppedCts = new();

        public TestHostApplicationLifetime(bool startStarted = true)
        {
            ApplicationStarted = _startedCts.Token;
            ApplicationStopping = _stoppingCts.Token;
            ApplicationStopped = _stoppedCts.Token;

            if (startStarted)
            {
                _startedCts.Cancel();
            }
        }

        public CancellationToken ApplicationStarted { get; }
        public CancellationToken ApplicationStopped { get; }
        public CancellationToken ApplicationStopping { get; }

        public void StartApplication()
        {
            _startedCts.Cancel();
        }

        public void StopApplication()
        {
            _stoppingCts.Cancel();
            _stoppedCts.Cancel();
        }

        public void Dispose()
        {
            _stoppingCts.Dispose();
            _stoppedCts.Dispose();
        }
    }
}
