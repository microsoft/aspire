// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Text;
using System.Threading.Channels;
using Aspire.DashboardService.Proto.V1;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Tests.Helpers;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Tests.Utils.Grpc;
using Aspire.Hosting.Utils;
using Aspire.Shared.ConsoleLogs;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
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
        var dashboardService = CreateDashboardService(dashboardServiceData);

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
        var dashboardService = CreateDashboardService(dashboardServiceData);

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
        var dashboardService = CreateDashboardService(dashboardServiceData, logger: loggerFactory.CreateLogger<DashboardServiceImpl>());

        var testResource = new TestResource("test-resource");
        using var applicationBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper: testOutputHelper);
        var builder = applicationBuilder.AddResource(testResource);
#pragma warning disable CS0618 // Parameter is obsolete but this verifies dashboard wire compatibility.
        builder.WithCommand(
            name: "TestName",
            displayName: "Display name!",
            executeCommand: c => Task.FromResult(CommandResults.Success()),
            commandOptions: new()
            {
                UpdateState = c => Hosting.ApplicationModel.ResourceCommandState.Enabled,
                Description = "Display description!",
                Parameter = new[] { "One", "Two" },
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "selector",
                        Label = "Selector",
                        Description = "CSS selector to click.",
                        InputType = InputType.Text,
                        Required = true,
                        Placeholder = "#submit"
                    }
                ],
                ConfirmationMessage = "Confirmation message!",
                IconName = "Icon name!",
                IconVariant = Hosting.ApplicationModel.IconVariant.Filled,
                IsHighlighted = true
            });
#pragma warning restore CS0618
        builder.WithCommand(
            name: "HeadlessName",
            displayName: "Headless display name",
            executeCommand: c => Task.FromResult(CommandResults.Success()),
            commandOptions: new()
            {
                UpdateState = c => Hosting.ApplicationModel.ResourceCommandState.Enabled,
                Visibility = ResourceCommandVisibility.Api
            });

        logger.LogInformation("Publishing resource.");
        await resourceNotificationService.PublishUpdateAsync(testResource, s =>
        {
            return s with { State = new ResourceStateSnapshot("Starting", null) };
        }).DefaultTimeout();

        logger.LogInformation("Waiting for the resource with a command. Required so added resource is always in the service's initial data collection");
        await dashboardServiceData.WaitForResourceAsync(testResource.Name, r =>
        {
            return r.Commands.Length == 2;
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
#pragma warning disable CS0612 // Parameter is obsolete but still verified for compatibility.
        Assert.Equal(Value.ForList(Value.ForString("One"), Value.ForString("Two")), commandData.Parameter);
#pragma warning restore CS0612
        var argumentInput = Assert.Single(commandData.ArgumentInputs);
        Assert.Equal("selector", argumentInput.Name);
        Assert.Equal("Selector", argumentInput.Label);
        Assert.Equal("CSS selector to click.", argumentInput.Description);
        Assert.Equal(DashboardService.Proto.V1.InputType.Text, argumentInput.InputType);
        Assert.True(argumentInput.Required);
        Assert.Equal("#submit", argumentInput.Placeholder);
        Assert.Equal("Confirmation message!", commandData.ConfirmationMessage);
        Assert.Equal("Icon name!", commandData.IconName);
        Assert.Equal(DashboardService.Proto.V1.IconVariant.Filled, commandData.IconVariant);
        Assert.True(commandData.IsHighlighted);
        Assert.DoesNotContain(resourceData.Commands, command => command.Name == "HeadlessName");

        await CancelTokenAndAwaitTask(cts, task).DefaultTimeout();
    }

    [Fact]
    public async Task ExecuteResourceCommand_WithArguments_PassesArgumentsToCommand()
    {
        var resourceLoggerService = new ResourceLoggerService();
        var resourceNotificationService = CreateResourceNotificationService(resourceLoggerService);
        using var dashboardServiceData = CreateDashboardServiceData(resourceLoggerService: resourceLoggerService, resourceNotificationService: resourceNotificationService);
        var dashboardService = CreateDashboardService(dashboardServiceData);

        InteractionInputCollection? capturedArguments = null;
        var testResource = new TestResource("test-resource");
        using var applicationBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper: testOutputHelper);
        var builder = applicationBuilder.AddResource(testResource);
        builder.WithCommand(
            name: "click",
            displayName: "Click",
            executeCommand: c =>
            {
                capturedArguments = c.Arguments;
                return Task.FromResult(CommandResults.Success());
            },
            commandOptions: new()
            {
                Visibility = ResourceCommandVisibility.Api,
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "selector",
                        InputType = InputType.Text
                    },
                    new InteractionInput
                    {
                        Name = "clickCount",
                        InputType = InputType.Number
                    }
                ]
            });

        await resourceNotificationService.PublishUpdateAsync(testResource, s =>
        {
            return s with { State = new ResourceStateSnapshot("Running", null) };
        }).DefaultTimeout();

        var context = TestServerCallContext.Create();
        var response = await dashboardService.ExecuteResourceCommand(
            new ResourceCommandRequest
            {
                ResourceName = testResource.Name,
                CommandName = "click",
                Arguments =
                {
                    ["selector"] = Value.ForString("#submit"),
                    ["clickCount"] = Value.ForNumber(2)
                }
            },
            context);

        Assert.Equal(ResourceCommandResponseKind.Succeeded, response.Kind);
        Assert.NotNull(capturedArguments);
        Assert.Equal("#submit", capturedArguments.GetString("selector"));
        Assert.Equal(2, capturedArguments.GetInt32("clickCount"));
    }

    [Fact]
    public async Task ExecuteResourceCommand_WithUnknownArgument_ReturnsFailure()
    {
        var resourceLoggerService = new ResourceLoggerService();
        var resourceNotificationService = CreateResourceNotificationService(resourceLoggerService);
        using var dashboardServiceData = CreateDashboardServiceData(resourceLoggerService: resourceLoggerService, resourceNotificationService: resourceNotificationService);
        var dashboardService = CreateDashboardService(dashboardServiceData);

        var executed = false;
        var testResource = new TestResource("test-resource");
        using var applicationBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper: testOutputHelper);
        var builder = applicationBuilder.AddResource(testResource);
        builder.WithCommand(
            name: "click",
            displayName: "Click",
            executeCommand: c =>
            {
                executed = true;
                return Task.FromResult(CommandResults.Success());
            },
            commandOptions: new()
            {
                Visibility = ResourceCommandVisibility.Api,
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "selector",
                        InputType = InputType.Text,
                        Required = true
                    }
                ]
            });

        await resourceNotificationService.PublishUpdateAsync(testResource, s =>
        {
            return s with { State = new ResourceStateSnapshot("Running", null) };
        }).DefaultTimeout();

        var context = TestServerCallContext.Create();
        var response = await dashboardService.ExecuteResourceCommand(
            new ResourceCommandRequest
            {
                ResourceName = testResource.Name,
                CommandName = "click",
                Arguments =
                {
                    ["selecter"] = Value.ForString("#submit")
                }
            },
            context);

        Assert.Equal(ResourceCommandResponseKind.Failed, response.Kind);
        Assert.False(executed);
        Assert.Equal("Unknown argument 'selecter' for command 'click'.", response.Message);
    }

    [Fact]
    public async Task ExecuteResourceCommand_WithInvalidArguments_ReturnsValidationErrors()
    {
        var resourceLoggerService = new ResourceLoggerService();
        var resourceNotificationService = CreateResourceNotificationService(resourceLoggerService);
        using var dashboardServiceData = CreateDashboardServiceData(resourceLoggerService: resourceLoggerService, resourceNotificationService: resourceNotificationService);
        var dashboardService = CreateDashboardService(dashboardServiceData);

        var executed = false;
        var testResource = new TestResource("test-resource");
        using var applicationBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper: testOutputHelper);
        var builder = applicationBuilder.AddResource(testResource);
        builder.WithCommand(
            name: "validate",
            displayName: "Validate",
            executeCommand: c =>
            {
                executed = true;
                return Task.FromResult(CommandResults.Success());
            },
            commandOptions: new()
            {
                Visibility = ResourceCommandVisibility.Api,
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "target",
                        InputType = InputType.Text
                    }
                ],
                ValidateArguments = context =>
                {
                    var target = context.Inputs.Single(argument => argument.Name == "target");
                    context.AddValidationError(target, "Target must not be prod.");

                    return Task.CompletedTask;
                }
            });

        await resourceNotificationService.PublishUpdateAsync(testResource, s =>
        {
            return s with { State = new ResourceStateSnapshot("Running", null) };
        }).DefaultTimeout();

        var context = TestServerCallContext.Create();
        var response = await dashboardService.ExecuteResourceCommand(
            new ResourceCommandRequest
            {
                ResourceName = testResource.Name,
                CommandName = "validate",
                Arguments =
                {
                    ["target"] = Value.ForString("prod")
                }
            },
            context);

        Assert.Equal(ResourceCommandResponseKind.InvalidArguments, response.Kind);
        Assert.Equal("Command argument validation failed.", response.Message);
        Assert.False(executed);
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
            new ConfigurationBuilder().Build());
        using var dashboardServiceData = CreateDashboardServiceData(loggerFactory: loggerFactory, interactionService: interactionService);
        var dashboardService = CreateDashboardService(dashboardServiceData, logger: loggerFactory.CreateLogger<DashboardServiceImpl>());

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
            new ConfigurationBuilder().Build());
        using var dashboardServiceData = CreateDashboardServiceData(loggerFactory: loggerFactory, interactionService: interactionService);
        var dashboardService = CreateDashboardService(dashboardServiceData, logger: loggerFactory.CreateLogger<DashboardServiceImpl>());

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
            new InteractionInput { Name = "Input", InputType = InputType.Text });

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
            new ConfigurationBuilder().Build());
        using var dashboardServiceData = CreateDashboardServiceData(loggerFactory: loggerFactory, interactionService: interactionService);
        var dashboardService = CreateDashboardService(dashboardServiceData, logger: loggerFactory.CreateLogger<DashboardServiceImpl>());

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
            new InteractionInput { Name = "Input", InputType = InputType.Text, Label = "Input" });

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
            new ConfigurationBuilder().Build());
        using var dashboardServiceData = CreateDashboardServiceData(loggerFactory: loggerFactory, interactionService: interactionService);
        var dashboardService = CreateDashboardService(dashboardServiceData, logger: loggerFactory.CreateLogger<DashboardServiceImpl>());

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
            new ConfigurationBuilder().Build());
        using var dashboardServiceData = CreateDashboardServiceData(loggerFactory: loggerFactory, interactionService: interactionService);
        var dashboardService = CreateDashboardService(dashboardServiceData, logger: loggerFactory.CreateLogger<DashboardServiceImpl>());

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
        var dashboardService = CreateDashboardService(dashboardServiceData, hostEnvironment: hostEnvironment, configuration: configuration);

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
        var dashboardService = CreateDashboardService(dashboardServiceData, hostEnvironment: hostEnvironment, configuration: configuration);

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
        var dashboardService = CreateDashboardService(dashboardServiceData, configuration: configuration);

        var context = TestServerCallContext.Create();

        // Act
        var response = await dashboardService.GetApplicationInformation(
            new ApplicationInformationRequest(),
            context);

        // Assert
        // The ComputeApplicationName method should strip the .AppHost suffix
        Assert.Equal("MyApp", response.ApplicationName);
    }

    [Theory]
    [InlineData(InputType.File, 0, -1, 0)]       // File, no input max, no server limit → 0
    [InlineData(InputType.File, 0, 50, 50)]      // File, no input max, 50 MB server limit → 50 MB
    [InlineData(InputType.File, 5, 100, 5)]      // File, 5 MB input max below 100 MB server → uses input
    [InlineData(InputType.File, 200, 100, 100)]  // File, 200 MB input max exceeds 100 MB server → capped
    [InlineData(InputType.File, 50, -1, 50)]     // File, 50 MB input max, no server limit → uses input
    [InlineData(InputType.Text, 0, 100, 0)]      // Non-file, server limit not applied → 0
    public void CreateInteractionInputDto_MaxFileSize_RespectsLimits(
        InputType inputType, int inputMaxFileSizeMB, int serverLimitMB, int expectedMB)
    {
        var input = inputMaxFileSizeMB > 0
            ? new InteractionInput { Name = "TestInput", Label = "Test Input", InputType = inputType, MaxFileSize = inputMaxFileSizeMB * 1024 * 1024 }
            : new InteractionInput { Name = "TestInput", Label = "Test Input", InputType = inputType };

        long? serverLimit = serverLimitMB >= 0 ? serverLimitMB * 1024L * 1024 : null;
        var dto = DashboardServiceImpl.CreateInteractionInputDto(input, maxFileUploadSize: serverLimit);

        Assert.Equal(expectedMB * 1024L * 1024, dto.MaxFileSize);
    }

    [Fact]
    public async Task UploadFile_WithinSizeLimit_Succeeds()
    {
        var dashboardServiceData = CreateDashboardServiceData();
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);
        var dashboardService = CreateDashboardService(dashboardServiceData, fileUploadStore: fileUploadStore);

        var data = new byte[1024]; // 1 KB
        Array.Fill(data, (byte)'A');

        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<UploadFileChunk>(context);
        requestStream.AddMessage(new UploadFileChunk { FileName = "test.txt", Data = ByteString.CopyFrom(data) });
        requestStream.Complete();

        var response = await dashboardService.UploadFile(requestStream, context);

        Assert.NotNull(response.FileId);
        Assert.NotEmpty(response.FileId);
        Assert.NotNull(fileUploadStore.GetFilePath(response.FileId));
    }

    [Fact]
    public async Task UploadFile_ExceedsConfiguredSizeLimit_ThrowsResourceExhausted()
    {
        var dashboardServiceData = CreateDashboardServiceData();
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnownConfigNames.MaxFileUploadSize] = "1024" // 1 KB limit
            })
            .Build();
        var dashboardService = CreateDashboardService(dashboardServiceData, configuration: configuration, fileUploadStore: fileUploadStore);

        var data = new byte[2048]; // 2 KB - exceeds the 1 KB limit
        Array.Fill(data, (byte)'A');

        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<UploadFileChunk>(context);
        requestStream.AddMessage(new UploadFileChunk { FileName = "large.txt", Data = ByteString.CopyFrom(data) });
        requestStream.Complete();

        var ex = await Assert.ThrowsAsync<RpcException>(() => dashboardService.UploadFile(requestStream, context));
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
    }

    [Fact]
    public async Task UploadFile_ExceedsLimitAcrossMultipleChunks_ThrowsResourceExhausted()
    {
        var dashboardServiceData = CreateDashboardServiceData();
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnownConfigNames.MaxFileUploadSize] = "1500" // 1500 bytes limit
            })
            .Build();
        var dashboardService = CreateDashboardService(dashboardServiceData, configuration: configuration, fileUploadStore: fileUploadStore);

        var chunk1 = new byte[1024]; // 1 KB
        var chunk2 = new byte[1024]; // 1 KB - total 2 KB exceeds 1500
        Array.Fill(chunk1, (byte)'A');
        Array.Fill(chunk2, (byte)'B');

        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<UploadFileChunk>(context);
        requestStream.AddMessage(new UploadFileChunk { FileName = "large.txt", Data = ByteString.CopyFrom(chunk1) });
        requestStream.AddMessage(new UploadFileChunk { Data = ByteString.CopyFrom(chunk2) });
        requestStream.Complete();

        var ex = await Assert.ThrowsAsync<RpcException>(() => dashboardService.UploadFile(requestStream, context));
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
    }

    [Fact]
    public async Task UploadFile_ConfiguredSizeLimit_AllowsWithinLimitUploads()
    {
        var dashboardServiceData = CreateDashboardServiceData();
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnownConfigNames.MaxFileUploadSize] = "10485760" // 10 MB
            })
            .Build();
        var dashboardService = CreateDashboardService(dashboardServiceData, configuration: configuration, fileUploadStore: fileUploadStore);

        var data = new byte[1024 * 1024]; // 1 MB - within 10 MB limit
        Array.Fill(data, (byte)'A');

        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<UploadFileChunk>(context);
        requestStream.AddMessage(new UploadFileChunk { FileName = "medium.bin", Data = ByteString.CopyFrom(data) });
        requestStream.Complete();

        var response = await dashboardService.UploadFile(requestStream, context);

        Assert.NotNull(response.FileId);
        Assert.NotEmpty(response.FileId);
    }

    [Fact]
    public async Task UploadFile_EmptyStream_ThrowsInvalidArgument()
    {
        var dashboardServiceData = CreateDashboardServiceData();
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);
        var dashboardService = CreateDashboardService(dashboardServiceData, fileUploadStore: fileUploadStore);

        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<UploadFileChunk>(context);
        requestStream.Complete(); // empty stream

        var ex = await Assert.ThrowsAsync<RpcException>(() => dashboardService.UploadFile(requestStream, context));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task UploadFile_ThenResolveFileReferences_ResolvesCorrectly()
    {
        var dashboardServiceData = CreateDashboardServiceData();
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);
        var dashboardService = CreateDashboardService(dashboardServiceData, fileUploadStore: fileUploadStore);

        // Upload a file
        var data = Encoding.UTF8.GetBytes("certificate-content");
        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<UploadFileChunk>(context);
        requestStream.AddMessage(new UploadFileChunk { FileName = "cert.pem", Data = ByteString.CopyFrom(data) });
        requestStream.Complete();

        var uploadResponse = await dashboardService.UploadFile(requestStream, context);

        // Resolve the file reference using the same store
        var json = $"[{{\"Id\":\"{uploadResponse.FileId}\",\"Name\":\"cert.pem\"}}]";
        var resolvedFiles = FileUploadStore.ResolveFileReferences(fileUploadStore, json, "CertInput", NullLogger.Instance);

        Assert.NotNull(resolvedFiles);
        var file = Assert.Single(resolvedFiles);
        Assert.Equal(uploadResponse.FileId, file.Id);
        Assert.Equal("cert.pem", file.Name);
        Assert.True(File.Exists(file.FilePath));

        // Verify the file content was written correctly
        var content = await File.ReadAllBytesAsync(file.FilePath);
        Assert.Equal(data, content);
    }

    [Fact]
    public void ResolveFileReferences_UnknownId_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);
        var json = "[{\"Id\":\"nonexistent-id\",\"Name\":\"file.txt\"}]";

        var result = FileUploadStore.ResolveFileReferences(fileUploadStore, json, "TestInput", NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveFileReferences_MalformedJson_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);
        var json = "not-valid-json";

        var result = FileUploadStore.ResolveFileReferences(fileUploadStore, json, "TestInput", NullLogger.Instance);

        Assert.Null(result);
    }

    private static DashboardServiceImpl CreateDashboardService(
        DashboardServiceData dashboardServiceData,
        IHostEnvironment? hostEnvironment = null,
        IConfiguration? configuration = null,
        ILogger<DashboardServiceImpl>? logger = null,
        IFileUploadStore? fileUploadStore = null)
    {
        return new DashboardServiceImpl(
            dashboardServiceData,
            hostEnvironment ?? new TestHostEnvironment(),
            new TestHostApplicationLifetime(),
            configuration ?? new ConfigurationBuilder().Build(),
            logger ?? NullLogger<DashboardServiceImpl>.Instance,
            fileUploadStore ?? new TestFileUploadStore());
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
            new ConfigurationBuilder().Build());

        return new DashboardServiceData(
            resourceNotificationService,
            resourceLoggerService,
            loggerFactory.CreateLogger<DashboardServiceData>(),
            new ResourceCommandService(resourceNotificationService, resourceLoggerService, new ServiceCollection().BuildServiceProvider()),
            interactionService,
            new TestFileUploadStore());
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

