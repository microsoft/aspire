// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using Aspire.DashboardService.Proto.V1;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Tests.Helpers;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Tests.Utils.Grpc;
using Aspire.Hosting.Terminals;
using Aspire.Hosting.Utils;
using Aspire.Shared.ConsoleLogs;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hex1b;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using DashboardServiceImpl = Aspire.Hosting.Dashboard.DashboardService;
using Resource = Aspire.Hosting.ApplicationModel.Resource;
using WriteContext = Microsoft.Extensions.Logging.Testing.WriteContext;

#pragma warning disable ASPIRETERMINAL002 // Test consumer of the experimental AppHost terminal API.

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
        builder.WithCommand(
            name: "UnknownStateName",
            displayName: "Unknown state display name",
            executeCommand: c => Task.FromResult(CommandResults.Success()),
            commandOptions: new()
            {
                UpdateState = c => (Hosting.ApplicationModel.ResourceCommandState)999
            });

        logger.LogInformation("Publishing resource.");
        await resourceNotificationService.PublishUpdateAsync(testResource, s =>
        {
            return s with { State = new ResourceStateSnapshot("Starting", null) };
        }).DefaultTimeout();

        logger.LogInformation("Waiting for the resource with a command. Required so added resource is always in the service's initial data collection");
        await dashboardServiceData.WaitForResourceAsync(testResource.Name, r =>
        {
            return r.Commands.Length == 3;
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

        logger.LogInformation("Reading result from writer.");
        var readUpdateTask = writer.ReadNextAsync().DefaultTimeout();
        var completedTask = await Task.WhenAny(task, readUpdateTask).DefaultTimeout();
        await completedTask.DefaultTimeout();
        Assert.Same(readUpdateTask, completedTask);

        var update = await readUpdateTask;
        Assert.False(task.IsCompleted, "WatchResources should remain active until cancellation.");

        logger.LogInformation($"Initial data count: {update.InitialData.Resources.Count}");
        var resourceData = Assert.Single(update.InitialData.Resources);

        logger.LogInformation($"Commands count: {resourceData.Commands.Count}");
        Assert.Collection(resourceData.Commands,
            commandData =>
            {
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
                Assert.Equal(DashboardService.Proto.V1.ResourceCommandState.Enabled, commandData.State);
            },
            commandData =>
            {
                Assert.Equal("UnknownStateName", commandData.Name);
                Assert.Equal("Unknown state display name", commandData.DisplayName);
                Assert.Equal(DashboardService.Proto.V1.ResourceCommandState.Hidden, commandData.State);
            });

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
            new ConfigurationBuilder().Build(),
            new TestInteractionFileUploadStore());
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
            new ConfigurationBuilder().Build(),
            new TestInteractionFileUploadStore());
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
            new ConfigurationBuilder().Build(),
            new TestInteractionFileUploadStore());
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
            new ConfigurationBuilder().Build(),
            new TestInteractionFileUploadStore());
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
            new ConfigurationBuilder().Build(),
            new TestInteractionFileUploadStore());
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
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        fileUploadStore.StartInteraction(1, [("File", InteractionHelpers.MaxFileCount)]);
        var dashboardService = CreateDashboardService(dashboardServiceData, fileUploadStore: fileUploadStore);

        var data = new byte[1024]; // 1 KB
        Array.Fill(data, (byte)'A');

        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<UploadFileChunk>(context);
        requestStream.AddMessage(new UploadFileChunk { FileName = "test.txt", Data = ByteString.CopyFrom(data), InteractionId = 1, InputName = "File" });
        requestStream.Complete();

        var response = await dashboardService.UploadFile(requestStream, context);

        Assert.NotNull(response.FileId);
        Assert.NotEmpty(response.FileId);
        var filePath = Assert.IsType<string>(fileUploadStore.GetFilePath(response.FileId, 1, "File"));

        fileUploadStore.CancelInteraction(1);

        Assert.Null(fileUploadStore.GetFilePath(response.FileId, 1, "File"));
        Assert.False(File.Exists(filePath));
    }

    [Theory]
    [InlineData("../../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\system32\\evil.exe", "evil.exe")]
    [InlineData("bad*.txt", "bad*.txt")]
    [InlineData("bad:name.txt", "bad:name.txt")]
    [InlineData("CON.txt", "CON.txt")]
    [InlineData("bad\0name.txt", "bad\0name.txt")]
    public async Task UploadFile_MaliciousFileName_UsesRandomDiskName(string fileName, string expectedFileName)
    {
        var dashboardServiceData = CreateDashboardServiceData();
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        fileUploadStore.StartInteraction(1, [("File", 1)]);
        var dashboardService = CreateDashboardService(dashboardServiceData, fileUploadStore: fileUploadStore);

        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<UploadFileChunk>(context);
        requestStream.AddMessage(new UploadFileChunk { FileName = fileName, InteractionId = 1, InputName = "File" });
        requestStream.Complete();

        var response = await dashboardService.UploadFile(requestStream, context);

        var filePath = Assert.IsType<string>(fileUploadStore.GetFilePath(response.FileId, 1, "File"));
        Assert.True(File.Exists(filePath));
        Assert.NotEqual(expectedFileName, Path.GetFileName(filePath));
        Assert.Equal(expectedFileName, Assert.Single(fileUploadStore.GetCompletedFiles(1, "File")).Name);
    }

    [Fact]
    public async Task UploadFile_ExceedsInputFileCountLimit_ThrowsFailedPrecondition()
    {
        var dashboardServiceData = CreateDashboardServiceData();
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        fileUploadStore.StartInteraction(1, [("File", 1)]);
        var dashboardService = CreateDashboardService(dashboardServiceData, fileUploadStore: fileUploadStore);

        var firstContext = TestServerCallContext.Create();
        var firstRequestStream = new TestAsyncStreamReader<UploadFileChunk>(firstContext);
        firstRequestStream.AddMessage(new UploadFileChunk { FileName = "first.txt", InteractionId = 1, InputName = "File" });
        firstRequestStream.Complete();
        await dashboardService.UploadFile(firstRequestStream, firstContext);

        var secondContext = TestServerCallContext.Create();
        var secondRequestStream = new TestAsyncStreamReader<UploadFileChunk>(secondContext);
        secondRequestStream.AddMessage(new UploadFileChunk { FileName = "second.txt", InteractionId = 1, InputName = "File" });
        secondRequestStream.Complete();

        var exception = await Assert.ThrowsAsync<RpcException>(() => dashboardService.UploadFile(secondRequestStream, secondContext));
        Assert.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
        Assert.Equal("File input 'File' accepts at most 1 file.", exception.Status.Detail);
    }

    [Fact]
    public async Task UploadFile_ExceedsConfiguredSizeLimit_ThrowsResourceExhausted()
    {
        var dashboardServiceData = CreateDashboardServiceData();
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        fileUploadStore.StartInteraction(1, [("File", InteractionHelpers.MaxFileCount)]);
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
        requestStream.AddMessage(new UploadFileChunk { FileName = "large.txt", Data = ByteString.CopyFrom(data), InteractionId = 1, InputName = "File" });
        requestStream.Complete();

        var ex = await Assert.ThrowsAsync<RpcException>(() => dashboardService.UploadFile(requestStream, context));
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);

        fileUploadStore.CancelInteraction(1);
        fileUploadStore.StartInteraction(1, [("File", InteractionHelpers.MaxFileCount)]);
        var (_, replacementPath) = fileUploadStore.CreateEntry("replacement.txt", 1, "File");
        Assert.True(File.Exists(replacementPath));
    }

    [Fact]
    public async Task UploadFile_ExceedsLimitAcrossMultipleChunks_ThrowsResourceExhausted()
    {
        var dashboardServiceData = CreateDashboardServiceData();
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        fileUploadStore.StartInteraction(1, [("File", InteractionHelpers.MaxFileCount)]);
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
        requestStream.AddMessage(new UploadFileChunk { FileName = "large.txt", Data = ByteString.CopyFrom(chunk1), InteractionId = 1, InputName = "File" });
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
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        fileUploadStore.StartInteraction(1, [("File", InteractionHelpers.MaxFileCount)]);
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
        requestStream.AddMessage(new UploadFileChunk { FileName = "medium.bin", Data = ByteString.CopyFrom(data), InteractionId = 1, InputName = "File" });
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
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        var dashboardService = CreateDashboardService(dashboardServiceData, fileUploadStore: fileUploadStore);

        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<UploadFileChunk>(context);
        requestStream.Complete(); // empty stream

        var ex = await Assert.ThrowsAsync<RpcException>(() => dashboardService.UploadFile(requestStream, context));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Theory]
    [InlineData("", 1, "File", "First chunk must include a file name.")]
    [InlineData("file.txt", 0, "File", "First chunk must include an interaction ID.")]
    [InlineData("file.txt", 1, "", "First chunk must include an input name.")]
    public async Task UploadFile_FirstChunkMissingRequiredMetadata_ThrowsInvalidArgument(
        string fileName,
        int interactionId,
        string inputName,
        string expectedMessage)
    {
        var dashboardServiceData = CreateDashboardServiceData();
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        fileUploadStore.StartInteraction(1, [("File", InteractionHelpers.MaxFileCount)]);
        var dashboardService = CreateDashboardService(dashboardServiceData, fileUploadStore: fileUploadStore);

        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<UploadFileChunk>(context);
        requestStream.AddMessage(new UploadFileChunk { FileName = fileName, InteractionId = interactionId, InputName = inputName });
        requestStream.Complete();

        var exception = await Assert.ThrowsAsync<RpcException>(() => dashboardService.UploadFile(requestStream, context));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Equal(expectedMessage, exception.Status.Detail);
    }

    [Fact]
    public async Task UploadFile_UnknownInteraction_RejectsUpload()
    {
        var dashboardServiceData = CreateDashboardServiceData();
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        var dashboardService = CreateDashboardService(dashboardServiceData, fileUploadStore: fileUploadStore);

        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<UploadFileChunk>(context);
        requestStream.AddMessage(new UploadFileChunk { FileName = "file.txt", InteractionId = 1, InputName = "File" });
        requestStream.Complete();

        var exception = await Assert.ThrowsAsync<RpcException>(() => dashboardService.UploadFile(requestStream, context));

        Assert.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
        Assert.Equal("Interaction '1' is not accepting file uploads.", exception.Status.Detail);
    }

    [Fact]
    public async Task SendInteractionRequestAsync_ClientFileTypeForTextInput_DoesNotAttachFile()
    {
        var fileUploadStore = new TestInteractionFileUploadStore();
        var interactionService = new InteractionService(
            NullLogger<InteractionService>.Instance,
            new DistributedApplicationOptions(),
            new ServiceCollection().BuildServiceProvider(),
            new ConfigurationBuilder().Build(),
            fileUploadStore);
        using var dashboardServiceData = CreateDashboardServiceData(interactionService: interactionService, fileUploadStore: fileUploadStore);
        var fileInput = new InteractionInput { Name = "File", InputType = InputType.File };
        var textInput = new InteractionInput { Name = "Text", InputType = InputType.Text };
        var resultTask = interactionService.PromptInputsAsync("Inputs", "Enter values", [fileInput, textInput]);
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        var (fileId, _) = fileUploadStore.CreateEntry("spoofed.txt", interaction.InteractionId, textInput.Name);
        var value = $"[{{\"Id\":\"{fileId}\",\"Name\":\"spoofed.txt\"}}]";

        var request = new WatchInteractionsRequestUpdate
        {
            InteractionId = interaction.InteractionId,
            InputsDialog = new InteractionInputsDialog()
        };
        request.InputsDialog.InputItems.Add(new Aspire.DashboardService.Proto.V1.InteractionInput
        {
            Name = textInput.Name,
            InputType = Aspire.DashboardService.Proto.V1.InputType.File,
            Value = value
        });

        await dashboardServiceData.SendInteractionRequestAsync(request, CancellationToken.None);
        var result = await resultTask;
        var resultInputs = Assert.IsType<InteractionInputCollection>(result.Data);

        Assert.Empty(resultInputs[textInput.Name].GetFiles());
        Assert.Equal(value, resultInputs[textInput.Name].Value);
    }

    [Fact]
    public async Task SendInteractionRequestAsync_UsesAuthoritativeFilesAndDisposeDeletesUploads()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        var interactionService = new InteractionService(
            NullLogger<InteractionService>.Instance,
            new DistributedApplicationOptions(),
            new ServiceCollection().BuildServiceProvider(),
            new ConfigurationBuilder().Build(),
            fileUploadStore);
        using var dashboardServiceData = CreateDashboardServiceData(interactionService: interactionService, fileUploadStore: fileUploadStore);
        var input = new InteractionInput { Name = "File", InputType = InputType.File, Required = true, AllowMultipleFiles = true };
        var resultTask = interactionService.PromptInputAsync("Upload", "Select a file", input);
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        var (fileId, filePath) = fileUploadStore.CreateEntry("document.txt", interaction.InteractionId, input.Name);
        var (secondFileId, secondFilePath) = fileUploadStore.CreateEntry("second.txt", interaction.InteractionId, input.Name);
        await File.WriteAllTextAsync(filePath, "content");
        await File.WriteAllTextAsync(secondFilePath, "second content");
        fileUploadStore.CompleteUpload(interaction.InteractionId, fileId);
        fileUploadStore.CompleteUpload(interaction.InteractionId, secondFileId);
        var request = new WatchInteractionsRequestUpdate
        {
            InteractionId = interaction.InteractionId,
            InputsDialog = new InteractionInputsDialog()
        };
        request.InputsDialog.InputItems.Add(new Aspire.DashboardService.Proto.V1.InteractionInput
        {
            Name = input.Name,
            InputType = Aspire.DashboardService.Proto.V1.InputType.File,
            Value = $"[{{\"Id\":\"{fileId}\",\"Name\":\"spoofed.txt\"}},{{\"Id\":\"{secondFileId}\",\"Name\":\"also-spoofed.txt\"}}]"
        });
        await dashboardServiceData.SendInteractionRequestAsync(request, CancellationToken.None);
        var result = await resultTask;
        var resultInput = Assert.IsType<InteractionInput>(result.Data);
        var files = resultInput.GetFiles();
        Assert.Equal(2, files.Count);
        var file = files[0];
        var secondFile = files[1];
        Assert.Equal(filePath, file.FilePath);
        Assert.Equal("document.txt", file.Name);
        Assert.Equal(secondFilePath, secondFile.FilePath);
        Assert.Equal("second.txt", secondFile.Name);
        Assert.True(File.Exists(filePath));
        Assert.True(File.Exists(secondFilePath));
        Assert.Equal("content", Encoding.UTF8.GetString(await file.ReadAllBytesAsync()));
        await using var stream = file.OpenRead();
        using var reader = new StreamReader(stream);
        Assert.Equal("content", await reader.ReadToEndAsync());
        var readAllBytesTask = file.ReadAllBytesAsync();

        files.Dispose();
        files.Dispose();

        Assert.False(File.Exists(filePath));
        Assert.False(File.Exists(secondFilePath));
        Assert.Equal("content", Encoding.UTF8.GetString(await readAllBytesTask));
        Assert.Null(fileUploadStore.GetFilePath(fileId, interaction.InteractionId, input.Name));
        Assert.Null(fileUploadStore.GetFilePath(secondFileId, interaction.InteractionId, input.Name));
        Assert.Throws<ObjectDisposedException>(file.OpenRead);
        await Assert.ThrowsAsync<ObjectDisposedException>(ReadAllBytesAfterDisposeAsync);

        Task ReadAllBytesAfterDisposeAsync() => file.ReadAllBytesAsync();
    }

    [Fact]
    public async Task SendInteractionRequestAsync_MismatchedFiles_Throws()
    {
        var fileUploadStore = new TestInteractionFileUploadStore();
        var interactionService = new InteractionService(
            NullLogger<InteractionService>.Instance,
            new DistributedApplicationOptions(),
            new ServiceCollection().BuildServiceProvider(),
            new ConfigurationBuilder().Build(),
            fileUploadStore);
        using var dashboardServiceData = CreateDashboardServiceData(interactionService: interactionService, fileUploadStore: fileUploadStore);
        var input = new InteractionInput { Name = "File", InputType = InputType.File };
        var resultTask = interactionService.PromptInputAsync("Upload", "Select a file", input);
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        var (fileId, _) = fileUploadStore.CreateEntry("document.txt", interaction.InteractionId, input.Name);
        fileUploadStore.CompleteUpload(interaction.InteractionId, fileId);
        var request = new WatchInteractionsRequestUpdate
        {
            InteractionId = interaction.InteractionId,
            InputsDialog = new InteractionInputsDialog()
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dashboardServiceData.SendInteractionRequestAsync(request, CancellationToken.None));

        Assert.Equal("Submitted files for input 'File' do not match the completed uploads.", exception.Message);
        await dashboardServiceData.SendInteractionRequestAsync(
            new WatchInteractionsRequestUpdate
            {
                InteractionId = interaction.InteractionId,
                Complete = new InteractionComplete()
            },
            CancellationToken.None);
        Assert.True((await resultTask).Canceled);
    }

    [Fact]
    public async Task UploadFile_ThenResolveFiles_ResolvesCorrectly()
    {
        var dashboardServiceData = CreateDashboardServiceData();
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        fileUploadStore.StartInteraction(1, [("CertInput", InteractionHelpers.MaxFileCount)]);
        var dashboardService = CreateDashboardService(dashboardServiceData, fileUploadStore: fileUploadStore);

        // Upload a file
        var data = Encoding.UTF8.GetBytes("certificate-content");
        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<UploadFileChunk>(context);
        requestStream.AddMessage(new UploadFileChunk { FileName = "cert.pem", Data = ByteString.CopyFrom(data), InteractionId = 1, InputName = "CertInput" });
        requestStream.Complete();

        var uploadResponse = await dashboardService.UploadFile(requestStream, context);

        var json = $"[{{\"Id\":\"{uploadResponse.FileId}\"}}]";
        var resolvedFiles = fileUploadStore.GetCompletedFiles(1, "CertInput");
        InteractionFileUploadStore.ValidateFileReferences(json, "CertInput", resolvedFiles);

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
    public void ResolveFiles_UnknownInteraction_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        var result = fileUploadStore.GetCompletedFiles(1, "TestInput");
        InteractionFileUploadStore.ValidateFileReferences(jsonValue: null, "TestInput", result);

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveFiles_UnknownInput_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        fileUploadStore.StartInteraction(1, [("File", 1)]);
        var (fileId, _) = fileUploadStore.CreateEntry("file.txt", 1, "File");
        fileUploadStore.CompleteUpload(1, fileId);

        var result = fileUploadStore.GetCompletedFiles(1, "OtherFile");
        InteractionFileUploadStore.ValidateFileReferences(jsonValue: null, "OtherFile", result);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachTerminal_WorkloadEndedReportsStatusWithoutHmpHandshake(bool endedBeforeAttach)
    {
        using var serviceData = CreateDashboardServiceData();
        await using var terminalService = TestTerminalService.Create();
        var service = CreateDashboardService(serviceData, terminalService: terminalService);
        var output = new Pipe();
        await using var reader = output.Reader.AsStream();
        await using var writer = output.Writer.AsStream();
        var workload = new StreamWorkloadAdapter(reader, Stream.Null);
        await using var terminal = terminalService.CreateTerminal("Ended", TerminalPlacement.Dock,
            Hex1bTerminal.CreateBuilder().WithWorkload(workload));
        terminal.Start();
        await writer.WriteAsync("ready\r\n"u8.ToArray());
        await terminal.WaitForTextAsync("ready").DefaultTimeout();

        if (endedBeforeAttach)
        {
            workload.SignalDisconnected();
            await Assert.IsType<Hex1bAspireTerminal>(terminal).WorkloadEnded.DefaultTimeout();
        }

        using var cts = new CancellationTokenSource();
        var context = TestServerCallContext.Create(cancellationToken: cts.Token);
        var requests = new TestAsyncStreamReader<TerminalClientFrame>(context);
        var responses = new TestServerStreamWriter<TerminalServerFrame>(context);
        requests.AddMessage(new TerminalClientFrame { TerminalId = terminal.Id });
        var attachment = service.AttachTerminal(requests, responses, context);
        try
        {
            if (!endedBeforeAttach)
            {
                workload.SignalDisconnected();
            }

            var status = await responses.ReadNextAsync().DefaultTimeout();
            Assert.True(status.Ended);
            Assert.True(status.Data.IsEmpty);
            Assert.False(attachment.IsCompleted);
            Assert.True(terminalService.TryGetTerminal(terminal.Id, out _));
        }
        finally
        {
            await cts.CancelAsync();
            await attachment.DefaultTimeout();
        }
    }

    [Fact]
    public async Task CloseTerminal_UnknownId_Succeeds()
    {
        using var serviceData = CreateDashboardServiceData();
        await using var terminalService = TestTerminalService.Create();
        var service = CreateDashboardService(serviceData, terminalService: terminalService);

        var response = await service.CloseTerminal(
            new CloseTerminalRequest { TerminalId = "unknown" }, TestServerCallContext.Create()).DefaultTimeout();

        Assert.NotNull(response);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseTerminal_DisposesTerminal_AndRepeatedCloseSucceeds(bool started)
    {
        using var serviceData = CreateDashboardServiceData();
        await using var terminalService = TestTerminalService.Create();
        var service = CreateDashboardService(serviceData, terminalService: terminalService);
        var output = new Pipe();
        await using var reader = output.Reader.AsStream();
        await using var writer = output.Writer.AsStream();
        await using var terminal = terminalService.CreateTerminal("Close", TerminalPlacement.Dock,
            Hex1bTerminal.CreateBuilder().WithWorkload(new StreamWorkloadAdapter(reader, Stream.Null)));
        if (started)
        {
            terminal.Start();
        }

        var request = new CloseTerminalRequest { TerminalId = terminal.Id };
        var response = await service.CloseTerminal(request, TestServerCallContext.Create()).DefaultTimeout();

        Assert.NotNull(response);
        Assert.False(terminalService.TryGetTerminal(terminal.Id, out _));
        Assert.True(terminal.DisposeAsync().AsTask().IsCompletedSuccessfully);
        Assert.NotNull(await service.CloseTerminal(request, TestServerCallContext.Create()).DefaultTimeout());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseTerminal_PendingTransportCleanup_TimeoutOrCancellationDoesNotReleaseTransport(bool cancelRpc)
    {
        using var serviceData = CreateDashboardServiceData();
        await using var terminalService = TestTerminalService.Create();
        var service = CreateDashboardService(serviceData, terminalService: terminalService);
        var output = new Pipe();
        await using var outputReader = output.Reader.AsStream();
        await using var outputWriter = output.Writer.AsStream();
        await using var terminal = terminalService.CreateTerminal("Closing", TerminalPlacement.Dock,
            Hex1bTerminal.CreateBuilder().WithWorkload(new StreamWorkloadAdapter(outputReader, Stream.Null)));
        var (serverStream, clientStream) = TestDuplexStream.CreatePair();
        using var serverOwner = serverStream;
        using var clientOwner = clientStream;
        using var gated = new GatedTerminalWriteStream(serverStream);
        using var clientCts = new CancellationTokenSource();
        using var rpcCts = new CancellationTokenSource();
        await using var client = Hex1bTerminal.CreateBuilder().WithHeadless().WithHmp1Stream(clientStream).Build();
        var attachment = terminalService.AttachAsync(terminal.Id, gated, _ => Task.CompletedTask, CancellationToken.None);
        var run = client.RunAsync(clientCts.Token);

        try
        {
            await gated.WriteStarted.DefaultTimeout();
            var stopwatch = Stopwatch.StartNew();
            var close = service.CloseTerminal(
                new CloseTerminalRequest { TerminalId = terminal.Id },
                TestServerCallContext.Create(cancellationToken: rpcCts.Token));
            await gated.WriteCancelled.DefaultTimeout();
            var cleanup = terminal.DisposeAsync().AsTask();

            if (cancelRpc)
            {
                await rpcCts.CancelAsync();
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => close).DefaultTimeout();
                Assert.Equal(rpcCts.Token, exception.CancellationToken);
            }
            else
            {
                Assert.Equal(10, DashboardServiceImpl.CloseTerminalTimeoutSeconds);
                var exception = await Assert.ThrowsAsync<RpcException>(() => close).TimeoutAfter(TimeSpan.FromSeconds(30));
                Assert.Equal(StatusCode.DeadlineExceeded, exception.StatusCode);
                // Allow timer granularity without accepting a shorter production timeout.
                Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(9.9), $"Close timed out after {stopwatch.Elapsed}.");
            }

            Assert.False(cleanup.IsCompleted);
            Assert.False(attachment.IsCompleted);
            Assert.False(terminalService.TryGetTerminal(terminal.Id, out _));
            gated.ReleaseWrite();
            await cleanup.DefaultTimeout();
            await attachment.DefaultTimeout();
        }
        finally
        {
            gated.ReleaseWrite();
            await terminal.DisposeAsync().AsTask().DefaultTimeout();
            await attachment.DefaultTimeout();
            await clientCts.CancelAsync();
            try
            {
                await run.DefaultTimeout();
            }
            catch (OperationCanceledException) when (clientCts.IsCancellationRequested)
            {
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CloseTerminal_BlockingDisposal_TimeoutOrCancellationObservesLateFailure(bool fail, bool cancelRpc)
    {
        using var serviceData = CreateDashboardServiceData();
        await using var terminalService = TestTerminalService.Create();
        var sink = new TestSink();
        var logs = Channel.CreateUnbounded<WriteContext>();
        sink.MessageLogged += log => logs.Writer.TryWrite(log);
        var logger = new TestLogger<DashboardServiceImpl>(new TestLoggerFactory(sink, enabled: true));
        var service = CreateDashboardService(serviceData, logger: logger, terminalService: terminalService);
        using var rpcCts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TestAspireTerminal("blocking")
        {
            OnDispose = () =>
            {
                started.TrySetResult();
                try
                {
                    // A synchronous cancellation callback can block before DisposeAsync even returns a task.
                    release.Task.GetAwaiter().GetResult();
                    return ValueTask.CompletedTask;
                }
                finally
                {
                    completed.TrySetResult();
                }
            }
        };

        try
        {
            // Keep the test's gate releasable even if disposal regresses to blocking the RPC synchronously.
            var close = Task.Run(() => service.CloseTerminalAsync(terminal, rpcCts.Token));
            await started.Task.DefaultTimeout();
            if (cancelRpc)
            {
                await rpcCts.CancelAsync();
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => close).DefaultTimeout();
                Assert.Equal(rpcCts.Token, exception.CancellationToken);
            }
            else
            {
                var exception = await Assert.ThrowsAsync<RpcException>(() => close).TimeoutAfter(TimeSpan.FromSeconds(30));
                Assert.Equal(StatusCode.DeadlineExceeded, exception.StatusCode);
            }
            Assert.False(completed.Task.IsCompleted);

            var failure = new InvalidOperationException("Disposal callback failed.");
            if (fail)
            {
                release.TrySetException(failure);
            }
            else
            {
                release.TrySetResult();
            }
            await completed.Task.DefaultTimeout();

            if (fail)
            {
                var log = await logs.Reader.ReadAsync().AsTask().DefaultTimeout();
                Assert.Equal(LogLevel.Error, log.LogLevel);
                Assert.Equal($"Failed to dispose terminal {terminal.Id}.", log.Message);
                var aggregate = Assert.IsType<AggregateException>(log.Exception);
                Assert.Same(failure, Assert.Single(aggregate.Flatten().InnerExceptions));
            }
        }
        finally
        {
            release.TrySetResult();
            await completed.Task.DefaultTimeout();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CloseTerminal_DisposalFailure_IsLoggedAndPropagated(bool timeoutException, bool synchronous)
    {
        using var serviceData = CreateDashboardServiceData();
        await using var terminalService = TestTerminalService.Create();
        var sink = new TestSink();
        var logs = Channel.CreateUnbounded<WriteContext>();
        sink.MessageLogged += log => logs.Writer.TryWrite(log);
        var logger = new TestLogger<DashboardServiceImpl>(new TestLoggerFactory(sink, enabled: true));
        var service = CreateDashboardService(serviceData, logger: logger, terminalService: terminalService);
        Exception failure = timeoutException ? new TimeoutException("Workload timeout.") : new InvalidOperationException("Disposal failed.");
        var terminal = new TestAspireTerminal("failing")
        {
            OnDispose = () => synchronous ? throw failure : ValueTask.FromException(failure)
        };

        var exception = await Record.ExceptionAsync(() => service.CloseTerminalAsync(terminal, CancellationToken.None)).DefaultTimeout();

        Assert.Same(failure, exception);
        var log = await logs.Reader.ReadAsync().AsTask().DefaultTimeout();
        Assert.Equal(LogLevel.Error, log.LogLevel);
        Assert.Equal($"Failed to dispose terminal {terminal.Id}.", log.Message);
        Assert.Same(failure, Assert.Single(Assert.IsType<AggregateException>(log.Exception).InnerExceptions));
    }

    private static DashboardServiceImpl CreateDashboardService(
        DashboardServiceData dashboardServiceData,
        IHostEnvironment? hostEnvironment = null,
        IConfiguration? configuration = null,
        ILogger<DashboardServiceImpl>? logger = null,
        IInteractionFileUploadStore? fileUploadStore = null,
                        TerminalService? terminalService = null)
    {
        return new DashboardServiceImpl(
            dashboardServiceData,
            hostEnvironment ?? new TestHostEnvironment(),
            new TestHostApplicationLifetime(),
            configuration ?? new ConfigurationBuilder().Build(),
            logger ?? NullLogger<DashboardServiceImpl>.Instance,
            fileUploadStore ?? new TestInteractionFileUploadStore(),
            terminalService ?? TestTerminalService.Create());
    }

    private static DashboardServiceData CreateDashboardServiceData(
        ResourceLoggerService? resourceLoggerService = null,
        ResourceNotificationService? resourceNotificationService = null,
        ILoggerFactory? loggerFactory = null,
        InteractionService? interactionService = null,
        IInteractionFileUploadStore? fileUploadStore = null)
    {
        resourceLoggerService ??= new ResourceLoggerService();
        loggerFactory ??= NullLoggerFactory.Instance;
        resourceNotificationService ??= CreateResourceNotificationService(resourceLoggerService);
        fileUploadStore ??= new TestInteractionFileUploadStore();
        interactionService ??= new InteractionService(
            NullLogger<InteractionService>.Instance,
            new DistributedApplicationOptions(),
            new ServiceCollection().BuildServiceProvider(),
            new ConfigurationBuilder().Build(),
            fileUploadStore);

        return new DashboardServiceData(
            resourceNotificationService,
            resourceLoggerService,
            loggerFactory.CreateLogger<DashboardServiceData>(),
            new ResourceCommandService(resourceNotificationService, resourceLoggerService, new ServiceCollection().BuildServiceProvider()),
            interactionService,
            fileUploadStore);
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

    private static InteractionFileUploadStore CreateFileUploadStore(IFileSystemService fileSystemService) =>
        new(fileSystemService, NullLogger<InteractionFileUploadStore>.Instance);
}
