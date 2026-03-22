// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.DashboardService.Proto.V1;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Tests.Helpers;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Tests.Utils.Grpc;
using Aspire.Hosting.Utils;
using Aspire.Shared.ConsoleLogs;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DashboardServiceImpl = Aspire.Hosting.Dashboard.DashboardService;
using Resource = Aspire.Hosting.ApplicationModel.Resource;

namespace Aspire.Hosting.Tests.Dashboard;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

[Trait("Partition", "3")]
public class DashboardServiceTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task WatchResourceConsoleLogs_NoFollow_ResultsEnd()
    {
        // Arrange
        const int LongLineCharacters = DashboardServiceImpl.LogMaxBatchCharacters / 3;

        var getConsoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<LogEntry>>();
        var consoleLogsService = new TestConsoleLogsService(name => getConsoleLogsChannel);

        var resourceLoggerService = new ResourceLoggerService();
        resourceLoggerService.SetConsoleLogsService(consoleLogsService);

        var resourceNotificationService = CreateResourceNotificationService(resourceLoggerService);
        var dashboardServiceData = CreateDashboardServiceData(resourceLoggerService: resourceLoggerService, resourceNotificationService: resourceNotificationService);
        var dashboardService = new DashboardServiceImpl(dashboardServiceData, new TestHostEnvironment(), new TestHostApplicationLifetime(), new ConfigurationBuilder().Build(), NullLogger<DashboardServiceImpl>.Instance);

        var logger = resourceLoggerService.GetLogger("test-resource");

        // Three long lines
        logger.LogInformation(new string('1', LongLineCharacters));
        logger.LogInformation(new string('2', LongLineCharacters));
        logger.LogInformation(new string('3', LongLineCharacters));
        logger.LogInformation("Test1");
        logger.LogInformation("Test2");

        var context = TestServerCallContext.Create();
        var writer = new TestServerStreamWriter<WatchResourceConsoleLogsUpdate>(context);

        // Act
        var task = dashboardService.WatchResourceConsoleLogs(
            new WatchResourceConsoleLogsRequest { ResourceName = "test-resource", SuppressFollow = true },
            writer,
            context);

        // Assert
        var update1 = await writer.ReadNextAsync().DefaultTimeout();
        Assert.Collection(update1.LogLines,
            l => Assert.Equal(LongLineCharacters, l.Text.Split(' ')[1].Length),
            l => Assert.Equal(LongLineCharacters, l.Text.Split(' ')[1].Length));

        var update2 = await writer.ReadNextAsync().DefaultTimeout();
        Assert.Collection(update2.LogLines,
            l => Assert.Equal(LongLineCharacters, l.Text.Split(' ')[1].Length),
            l => Assert.Equal("Test1", l.Text.Split(' ')[1]),
            l => Assert.Equal("Test2", l.Text.Split(' ')[1]));

        await getConsoleLogsChannel.Writer.WriteAsync([LogEntry.Create(null, "Test3", isErrorMessage: false)]);

        var update3 = await writer.ReadNextAsync().DefaultTimeout();
        Assert.Collection(update3.LogLines,
            l => Assert.Equal("Test3", l.Text));

        Assert.False(task.IsCompleted, "Waiting for channel to complete.");

        getConsoleLogsChannel.Writer.TryComplete();

        await task.DefaultTimeout();
    }

    [Fact]
    public async Task WatchResourceConsoleLogs_LargePendingData_BatchResults()
    {
        // Arrange
        const int LongLineCharacters = DashboardServiceImpl.LogMaxBatchCharacters / 3;
        var resourceLoggerService = new ResourceLoggerService();
        var resourceNotificationService = CreateResourceNotificationService(resourceLoggerService);
        var dashboardServiceData = CreateDashboardServiceData(resourceLoggerService: resourceLoggerService, resourceNotificationService: resourceNotificationService);
        var dashboardService = new DashboardServiceImpl(dashboardServiceData, new TestHostEnvironment(), new TestHostApplicationLifetime(), new ConfigurationBuilder().Build(), NullLogger<DashboardServiceImpl>.Instance);

        var logger = resourceLoggerService.GetLogger("test-resource");

        // Exceed limit line
        logger.LogInformation(new string('1', DashboardServiceImpl.LogMaxBatchCharacters));
        // Three long lines
        logger.LogInformation(new string('2', LongLineCharacters));
        logger.LogInformation(new string('3', LongLineCharacters));
        logger.LogInformation(new string('4', LongLineCharacters));

        var context = TestServerCallContext.Create();
        var writer = new TestServerStreamWriter<WatchResourceConsoleLogsUpdate>(context);

        // Act
        var task = dashboardService.WatchResourceConsoleLogs(
            new WatchResourceConsoleLogsRequest { ResourceName = "test-resource" },
            writer,
            context);

        // Assert
        var exceedLimitUpdate = await writer.ReadNextAsync().DefaultTimeout();
        Assert.Collection(exceedLimitUpdate.LogLines,
            l => Assert.Equal(DashboardServiceImpl.LogMaxBatchCharacters, l.Text.Length));

        var longLinesUpdate1 = await writer.ReadNextAsync().DefaultTimeout();
        Assert.Collection(longLinesUpdate1.LogLines,
            l => Assert.Equal(LongLineCharacters, l.Text.Split(' ')[1].Length),
            l => Assert.Equal(LongLineCharacters, l.Text.Split(' ')[1].Length));

        var longLinesUpdate2 = await writer.ReadNextAsync().DefaultTimeout();
        Assert.Collection(longLinesUpdate2.LogLines,
            l => Assert.Equal(LongLineCharacters, l.Text.Split(' ')[1].Length));

        resourceLoggerService.Complete("test-resource");
        await task.DefaultTimeout();
    }

    [Fact]
    public async Task WatchResources_ResourceHasCommands_CommandsSentWithResponse()
    {
        // Arrange
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddXunit(testOutputHelper);
        });

        var logger = loggerFactory.CreateLogger<DashboardServiceTests>();
        var resourceLoggerService = new ResourceLoggerService();
        var resourceNotificationService = CreateResourceNotificationService(resourceLoggerService);
        using var dashboardServiceData = CreateDashboardServiceData(loggerFactory: loggerFactory, resourceLoggerService: resourceLoggerService, resourceNotificationService: resourceNotificationService);
        var dashboardService = new DashboardServiceImpl(dashboardServiceData, new TestHostEnvironment(), new TestHostApplicationLifetime(), new ConfigurationBuilder().Build(), loggerFactory.CreateLogger<DashboardServiceImpl>());

        var testResource = new TestResource("test-resource");
        using var applicationBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper: testOutputHelper);
        var builder = applicationBuilder.AddResource(testResource);
        builder.WithCommand(
            name: "TestName",
            displayName: "Display name!",
            executeCommand: c => Task.FromResult(CommandResults.Success()),
            commandOptions: new()
            {
                UpdateState = c => Aspire.Hosting.ApplicationModel.ResourceCommandState.Enabled,
                Description = "Display description!",
                Parameter = new[] { "One", "Two" },
                ConfirmationMessage = "Confirmation message!",
                IconName = "Icon name!",
                IconVariant = Aspire.Hosting.ApplicationModel.IconVariant.Filled,
                IsHighlighted = true
            });

        logger.LogInformation("Publishing resource.");
        await resourceNotificationService.PublishUpdateAsync(testResource, s =>
        {
            return s with { State = new ResourceStateSnapshot("Starting", null) };
        }).DefaultTimeout();

        logger.LogInformation("Waiting for the resource with a command. Required so added resource is always in the service's initial data collection");
        await dashboardServiceData.WaitForResourceAsync(testResource.Name, r =>
        {
            return r.Commands.Length == 1;
        }).DefaultTimeout();

        var cts = new CancellationTokenSource();
        var context = TestServerCallContext.Create(cancellationToken: cts.Token);
        var writer = new TestServerStreamWriter<WatchResourcesUpdate>(context);

        // Act
        logger.LogInformation("Calling WatchResources.");
        var task = dashboardService.WatchResources(
            new WatchResourcesRequest(),
            writer,
            context);

        // Assert
        logger.LogInformation("Reading result from writer.");
        var update = await writer.ReadNextAsync().DefaultTimeout();

        logger.LogInformation($"Initial data count: {update.InitialData.Resources.Count}");
        var resourceData = Assert.Single(update.InitialData.Resources);

        logger.LogInformation($"Commands count: {resourceData.Commands.Count}");
        var commandData = Assert.Single(resourceData.Commands);

        Assert.Equal("TestName", commandData.Name);
        Assert.Equal("Display name!", commandData.DisplayName);
        Assert.Equal("Display description!", commandData.DisplayDescription);
        Assert.Equal(Value.ForList(Value.ForString("One"), Value.ForString("Two")), commandData.Parameter);
        Assert.Equal("Confirmation message!", commandData.ConfirmationMessage);
        Assert.Equal("Icon name!", commandData.IconName);
        Assert.Equal(DashboardService.Proto.V1.IconVariant.Filled, commandData.IconVariant);
        Assert.True(commandData.IsHighlighted);

        await CancelTokenAndAwaitTask(cts, task).DefaultTimeout();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task WatchInteractions_PromptMessageBoxAsync_CompleteOnResponse(bool? result)
    {
        // Arrange
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddXunit(testOutputHelper);
        });

        var logger = loggerFactory.CreateLogger<DashboardServiceTests>();
        var interactionService = new InteractionService(
            loggerFactory.CreateLogger<InteractionService>(),
            new DistributedApplicationOptions(),
            new ServiceCollection().BuildServiceProvider(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var dashboardServiceData = CreateDashboardServiceData(loggerFactory: loggerFactory, interactionService: interactionService);
        var dashboardService = new DashboardServiceImpl(dashboardServiceData, new TestHostEnvironment(), new TestHostApplicationLifetime(), new ConfigurationBuilder().Build(), loggerFactory.CreateLogger<DashboardServiceImpl>());

        var cts = new CancellationTokenSource();
        var context = TestServerCallContext.Create(cancellationToken: cts.Token);
        var writer = new TestServerStreamWriter<WatchInteractionsResponseUpdate>(context);
        var reader = new TestAsyncStreamReader<WatchInteractionsRequestUpdate>(context);

        // Act
        logger.LogInformation("Calling WatchInteractions.");
        var task = dashboardService.WatchInteractions(
            reader,
            writer,
            context);

        var resultTask = interactionService.PromptMessageBoxAsync(
            title: "Title!",
            message: "Message!");

        // Assert
        logger.LogInformation("Reading result from writer.");
        var update = await writer.ReadNextAsync().DefaultTimeout();

        Assert.NotEqual(0, update.InteractionId);
        Assert.Equal(WatchInteractionsResponseUpdate.KindOneofCase.MessageBox, update.KindCase);

        Assert.False(resultTask.IsCompleted);

        logger.LogInformation("Send result to reader.");
        if (result != null)
        {
            update.MessageBox.Result = result.Value;
            reader.AddMessage(new WatchInteractionsRequestUpdate
            {
                InteractionId = update.InteractionId,
                MessageBox = update.MessageBox
            });

            Assert.Equal(result, (await resultTask.DefaultTimeout()).Data);
        }
        else
        {
            reader.AddMessage(new WatchInteractionsRequestUpdate
            {
                InteractionId = update.InteractionId,
                Complete = new InteractionComplete()
            });

            Assert.True((await resultTask.DefaultTimeout()).Canceled);
        }

        await CancelTokenAndAwaitTask(cts, task).DefaultTimeout();
    }

    [Fact]
    public async Task WatchInteractions_NoExplicitLabel_LabelIsName()
    {
        // Arrange
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddXunit(testOutputHelper);
        });

        var logger = loggerFactory.CreateLogger<DashboardServiceTests>();
        var interactionService = new InteractionService(
            loggerFactory.CreateLogger<InteractionService>(),
            new DistributedApplicationOptions(),
            new ServiceCollection().BuildServiceProvider(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var dashboardServiceData = CreateDashboardServiceData(loggerFactory: loggerFactory, interactionService: interactionService);
        var dashboardService = new DashboardServiceImpl(dashboardServiceData, new TestHostEnvironment(), new TestHostApplicationLifetime(), new ConfigurationBuilder().Build(), loggerFactory.CreateLogger<DashboardServiceImpl>());

        var cts = new CancellationTokenSource();
        var context = TestServerCallContext.Create(cancellationToken: cts.Token);
        var writer = new TestServerStreamWriter<WatchInteractionsResponseUpdate>(context);
        var reader = new TestAsyncStreamReader<WatchInteractionsRequestUpdate>(context);

        // Act
        logger.LogInformation("Calling WatchInteractions.");
        var task = dashboardService.WatchInteractions(
            reader,
            writer,
            context);

        var resultTask = interactionService.PromptInputAsync(
            title: "Title!",
            message: "Message!",
            new Aspire.Hosting.InteractionInput { Name = "Input", InputType = Aspire.Hosting.InputType.Text });

        // Assert
        logger.LogInformation("Reading result from writer.");
        var update = await writer.ReadNextAsync().DefaultTimeout();

        Assert.NotEqual(0, update.InteractionId);
        Assert.Equal(WatchInteractionsResponseUpdate.KindOneofCase.InputsDialog, update.KindCase);
        Assert.Equal("Input", Assert.Single(update.InputsDialog.InputItems).Label);

        await CancelTokenAndAwaitTask(cts, task).DefaultTimeout();
    }

    [Fact]
    public async Task WatchInteractions_PromptInputAsync_CompleteOnCancelResponse()
    {
        // Arrange
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddXunit(testOutputHelper);
        });

        var logger = loggerFactory.CreateLogger<DashboardServiceTests>();
        var interactionService = new InteractionService(
            loggerFactory.CreateLogger<InteractionService>(),
            new DistributedApplicationOptions(),
            new ServiceCollection().BuildServiceProvider(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var dashboardServiceData = CreateDashboardServiceData(loggerFactory: loggerFactory, interactionService: interactionService);
        var dashboardService = new DashboardServiceImpl(dashboardServiceData, new TestHostEnvironment(), new TestHostApplicationLifetime(), new ConfigurationBuilder().Build(), loggerFactory.CreateLogger<DashboardServiceImpl>());

        var cts = new CancellationTokenSource();
        var context = TestServerCallContext.Create(cancellationToken: cts.Token);
        var writer = new TestServerStreamWriter<WatchInteractionsResponseUpdate>(context);
        var reader = new TestAsyncStreamReader<WatchInteractionsRequestUpdate>(context);

        // Act
        logger.LogInformation("Calling WatchInteractions.");
        var task = dashboardService.WatchInteractions(
            reader,
            writer,
            context);

        var resultTask = interactionService.PromptInputAsync(
            title: "Title!",
            message: "Message!",
            new Aspire.Hosting.InteractionInput { Name = "Input", InputType = Aspire.Hosting.InputType.Text, Label = "Input" });

        // Assert
        logger.LogInformation("Reading result from writer.");
        var update = await writer.ReadNextAsync().DefaultTimeout();

        Assert.NotEqual(0, update.InteractionId);
        Assert.Equal(WatchInteractionsResponseUpdate.KindOneofCase.InputsDialog, update.KindCase);

        Assert.False(resultTask.IsCompleted);

        logger.LogInformation("Send result to reader.");
        reader.AddMessage(new WatchInteractionsRequestUpdate
        {
            InteractionId = update.InteractionId,
            Complete = new InteractionComplete()
        });

        var result = await resultTask.DefaultTimeout();
        Assert.True(result.Canceled);
        Assert.Null(result.Data);

        await CancelTokenAndAwaitTask(cts, task).DefaultTimeout();
    }

    [Fact]
    public async Task WatchInteractions_ReaderError_CompleteWithError()
    {
        // Arrange
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddXunit(testOutputHelper);
        });

        var logger = loggerFactory.CreateLogger<DashboardServiceTests>();
        var interactionService = new InteractionService(
            loggerFactory.CreateLogger<InteractionService>(),
            new DistributedApplicationOptions(),
            new ServiceCollection().BuildServiceProvider(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var dashboardServiceData = CreateDashboardServiceData(loggerFactory: loggerFactory, interactionService: interactionService);
        var dashboardService = new DashboardServiceImpl(dashboardServiceData, new TestHostEnvironment(), new TestHostApplicationLifetime(), new ConfigurationBuilder().Build(), loggerFactory.CreateLogger<DashboardServiceImpl>());

        var cts = new CancellationTokenSource();
        var context = TestServerCallContext.Create(cancellationToken: cts.Token);
        var writer = new TestServerStreamWriter<WatchInteractionsResponseUpdate>(context);
        var reader = new TestAsyncStreamReader<WatchInteractionsRequestUpdate>(context);

        // Act
        logger.LogInformation("Calling WatchInteractions.");
        var task = dashboardService.WatchInteractions(
            reader,
            writer,
            context);

        reader.Complete(new InvalidOperationException("Error!"));

        // Assert
        await Assert.ThrowsAnyAsync<Exception>(() => task).DefaultTimeout();
    }

    [Fact]
    public async Task WatchInteractions_WriterError_CompleteWithError()
    {
        // Arrange
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddXunit(testOutputHelper);
        });

        var logger = loggerFactory.CreateLogger<DashboardServiceTests>();
        var interactionService = new InteractionService(
            loggerFactory.CreateLogger<InteractionService>(),
            new DistributedApplicationOptions(),
            new ServiceCollection().BuildServiceProvider(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var dashboardServiceData = CreateDashboardServiceData(loggerFactory: loggerFactory, interactionService: interactionService);
        var dashboardService = new DashboardServiceImpl(dashboardServiceData, new TestHostEnvironment(), new TestHostApplicationLifetime(), new ConfigurationBuilder().Build(), loggerFactory.CreateLogger<DashboardServiceImpl>());

        var cts = new CancellationTokenSource();
        var context = TestServerCallContext.Create(cancellationToken: cts.Token);
        var writer = new TestServerStreamWriter<WatchInteractionsResponseUpdate>(context);
        var reader = new TestAsyncStreamReader<WatchInteractionsRequestUpdate>(context);

        // Act
        logger.LogInformation("Calling WatchInteractions.");
        var task = dashboardService.WatchInteractions(
            reader,
            writer,
            context);

        writer.Complete(new InvalidOperationException("Error!"));

        _ = interactionService.PromptMessageBoxAsync(
            title: "Title!",
            message: "Message!");

        // Assert
        await Assert.ThrowsAnyAsync<Exception>(() => task).DefaultTimeout();
    }

    [Fact]
    public void WithCommandOverloadNotAmbiguous()
    {
        var testResource = new TestResource("test-resource");
        using var applicationBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper: testOutputHelper);
        var builder = applicationBuilder.AddResource(testResource);
        builder.WithCommand(
            name: "TestName",
            displayName: "Display name!",
            executeCommand: c => Task.FromResult(CommandResults.Success()));

        // This test simply needs to compile.
        Assert.True(true);
    }

    [Fact]
    public async Task GetApplicationInformation_ReadsFromConfiguration()
    {
        // Arrange
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppHost:DashboardApplicationName"] = "MyCustomAppName"
        });
        var configuration = configBuilder.Build();

        var dashboardServiceData = CreateDashboardServiceData();
        var hostEnvironment = new TestHostEnvironment
        {
            ApplicationName = "DefaultAppName"
        };
        var dashboardService = new DashboardServiceImpl(
            dashboardServiceData,
            hostEnvironment,
            new TestHostApplicationLifetime(),
            configuration,
            NullLogger<DashboardServiceImpl>.Instance);

        var context = TestServerCallContext.Create();

        // Act
        var response = await dashboardService.GetApplicationInformation(
            new ApplicationInformationRequest(),
            context);

        // Assert
        Assert.Equal("MyCustomAppName", response.ApplicationName);
    }

    [Fact]
    public async Task GetApplicationInformation_FallsBackToEnvironmentApplicationName()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build(); // Empty configuration

        var dashboardServiceData = CreateDashboardServiceData();
        var hostEnvironment = new TestHostEnvironment
        {
            ApplicationName = "FallbackAppName"
        };
        var dashboardService = new DashboardServiceImpl(
            dashboardServiceData,
            hostEnvironment,
            new TestHostApplicationLifetime(),
            configuration,
            NullLogger<DashboardServiceImpl>.Instance);

        var context = TestServerCallContext.Create();

        // Act
        var response = await dashboardService.GetApplicationInformation(
            new ApplicationInformationRequest(),
            context);

        // Assert
        Assert.Equal("FallbackAppName", response.ApplicationName);
    }

    [Fact]
    public async Task GetApplicationInformation_StripsAppHostSuffix()
    {
        // Arrange
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppHost:DashboardApplicationName"] = "MyApp.AppHost"
        });
        var configuration = configBuilder.Build();

        var dashboardServiceData = CreateDashboardServiceData();
        var dashboardService = new DashboardServiceImpl(
            dashboardServiceData,
            new TestHostEnvironment(),
            new TestHostApplicationLifetime(),
            configuration,
            NullLogger<DashboardServiceImpl>.Instance);

        var context = TestServerCallContext.Create();

        // Act
        var response = await dashboardService.GetApplicationInformation(
            new ApplicationInformationRequest(),
            context);

        // Assert
        // The ComputeApplicationName method should strip the .AppHost suffix
        Assert.Equal("MyApp", response.ApplicationName);
    }

    private static DashboardServiceData CreateDashboardServiceData(
        ResourceLoggerService? resourceLoggerService = null,
        ResourceNotificationService? resourceNotificationService = null,
        ILoggerFactory? loggerFactory = null,
        InteractionService? interactionService = null)
    {
        resourceLoggerService ??= new ResourceLoggerService();
        loggerFactory ??= NullLoggerFactory.Instance;
        resourceNotificationService ??= CreateResourceNotificationService(resourceLoggerService);
        interactionService ??= new InteractionService(
            NullLogger<InteractionService>.Instance,
            new DistributedApplicationOptions(),
            new ServiceCollection().BuildServiceProvider(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        return new DashboardServiceData(
            resourceNotificationService,
            resourceLoggerService,
            loggerFactory.CreateLogger<DashboardServiceData>(),
            new ResourceCommandService(resourceNotificationService, resourceLoggerService, new ServiceCollection().BuildServiceProvider()),
            interactionService);
    }

    private static ResourceNotificationService CreateResourceNotificationService(ResourceLoggerService resourceLoggerService)
    {
        return new ResourceNotificationService(NullLogger<ResourceNotificationService>.Instance, new TestHostApplicationLifetime(), new ServiceCollection().BuildServiceProvider(), resourceLoggerService);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = default!;
        public IFileProvider ContentRootFileProvider { get; set; } = default!;
        public string ContentRootPath { get; set; } = default!;
        public string EnvironmentName { get; set; } = default!;
    }

    private sealed class TestResource(string name) : Resource(name)
    {
    }

    private static async Task CancelTokenAndAwaitTask(CancellationTokenSource cts, Task task)
    {
        await cts.CancelAsync();

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Ok if this error is thrown.
        }
    }
}

#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
