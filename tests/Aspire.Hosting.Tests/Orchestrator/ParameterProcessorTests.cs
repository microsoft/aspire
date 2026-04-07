// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Dashboard.Model;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Pipelines.Internal;
using Aspire.Hosting.Resources;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREUSERSECRETS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

namespace Aspire.Hosting.Tests.Orchestrator;

[Trait("Partition", "3")]
public class ParameterProcessorTests
{
    [Fact]
    public async Task InitializeParametersAsync_WithValidParameters_SetsRunningState()
    {
        // Arrange
        var parameterProcessor = CreateParameterProcessor();
        var parameters = new[]
        {
            CreateParameterResource("param1", "value1"),
            CreateParameterResource("param2", "value2")
        };

        // Act
        await parameterProcessor.InitializeParametersAsync(parameters).DefaultTimeout();

        // Assert
        foreach (var param in parameters)
        {
            Assert.NotNull(param.WaitForValueTcs);
            Assert.True(param.WaitForValueTcs.Task.IsCompletedSuccessfully);
#pragma warning disable CS0618 // Type or member is obsolete
            Assert.Equal(param.Value, await param.WaitForValueTcs.Task.DefaultTimeout());
#pragma warning restore CS0618 // Type or member is obsolete
        }
    }

    [Fact]
    public async Task InitializeParametersAsync_WithValidParametersAndDashboardEnabled_SetsRunningState()
    {
        // Arrange
        var interactionService = CreateInteractionService(disableDashboard: false);
        var parameterProcessor = CreateParameterProcessor(interactionService: interactionService, disableDashboard: false);
        var parameters = new[]
        {
            CreateParameterResource("param1", "value1"),
            CreateParameterResource("param2", "value2")
        };

        // Act
        await parameterProcessor.InitializeParametersAsync(parameters).DefaultTimeout();

        // Assert
        foreach (var param in parameters)
        {
            Assert.NotNull(param.WaitForValueTcs);
            Assert.True(param.WaitForValueTcs.Task.IsCompletedSuccessfully);
#pragma warning disable CS0618 // Type or member is obsolete
            Assert.Equal(param.Value, await param.WaitForValueTcs.Task.DefaultTimeout());
#pragma warning restore CS0618 // Type or member is obsolete
        }
    }

    [Fact]
    public async Task InitializeParametersAsync_WithSecretParameter_MarksAsSecret()
    {
        // Arrange
        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var parameterProcessor = CreateParameterProcessor(notificationService: notificationService);
        var secretParam = CreateParameterResource("secret", "secretValue", secret: true);

        var updates = new List<(IResource Resource, CustomResourceSnapshot Snapshot)>();
        var watchTask = Task.Run(async () =>
        {
            await foreach (var resourceEvent in notificationService.WatchAsync().ConfigureAwait(false))
            {
                updates.Add((resourceEvent.Resource, resourceEvent.Snapshot));
                break; // Only collect the first update
            }
        });

        // Act
        await parameterProcessor.InitializeParametersAsync([secretParam]).DefaultTimeout();

        // Wait for the notification
        await watchTask.DefaultTimeout();

        // Assert
        var (resource, snapshot) = Assert.Single(updates);
        Assert.Same(secretParam, resource);
        Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
    }

    [Fact]
    public async Task InitializeParametersAsync_WithMissingParameterValue_AddsToUnresolvedWhenInteractionAvailable()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var parameterProcessor = CreateParameterProcessor(interactionService: interactionService);
        var parameterWithMissingValue = CreateParameterWithMissingValue("missingParam");

        // Act
        await parameterProcessor.InitializeParametersAsync([parameterWithMissingValue]).DefaultTimeout();

        // Assert
        Assert.NotNull(parameterWithMissingValue.WaitForValueTcs);
        Assert.False(parameterWithMissingValue.WaitForValueTcs.Task.IsCompleted);
    }

    [Fact]
    public async Task InitializeParametersAsync_WithMissingParameterValue_SetsExceptionWhenInteractionNotAvailable()
    {
        // Arrange
        var interactionService = CreateInteractionService(disableDashboard: true);
        var parameterProcessor = CreateParameterProcessor(interactionService: interactionService);
        var parameterWithMissingValue = CreateParameterWithMissingValue("missingParam");

        // Act
        await parameterProcessor.InitializeParametersAsync([parameterWithMissingValue]).DefaultTimeout();

        // Assert
        Assert.NotNull(parameterWithMissingValue.WaitForValueTcs);
        Assert.True(parameterWithMissingValue.WaitForValueTcs.Task.IsCompleted);
        Assert.True(parameterWithMissingValue.WaitForValueTcs.Task.IsFaulted);
        Assert.IsType<MissingParameterValueException>(parameterWithMissingValue.WaitForValueTcs.Task.Exception?.InnerException);
    }

    [Fact]
    public async Task InitializeParametersAsync_WithMissingParameterValueAndDashboardEnabled_LeavesUnresolved()
    {
        // Arrange
        var interactionService = CreateInteractionService(disableDashboard: false);
        var parameterProcessor = CreateParameterProcessor(interactionService: interactionService, disableDashboard: false);
        var parameterWithMissingValue = CreateParameterWithMissingValue("missingParam");

        // Act
        await parameterProcessor.InitializeParametersAsync([parameterWithMissingValue]).DefaultTimeout();

        // Assert - Parameter should remain unresolved when dashboard is enabled
        Assert.NotNull(parameterWithMissingValue.WaitForValueTcs);
        Assert.False(parameterWithMissingValue.WaitForValueTcs.Task.IsCompleted);
    }

    [Fact]
    public async Task InitializeParametersAsync_WithNonMissingParameterException_SetsException()
    {
        // Arrange
        var parameterProcessor = CreateParameterProcessor();
        var parameterWithError = CreateParameterWithGenericError("errorParam");

        // Act
        await parameterProcessor.InitializeParametersAsync([parameterWithError]).DefaultTimeout();

        // Assert
        Assert.NotNull(parameterWithError.WaitForValueTcs);
        Assert.True(parameterWithError.WaitForValueTcs.Task.IsCompleted);
        Assert.True(parameterWithError.WaitForValueTcs.Task.IsFaulted);
        Assert.IsType<InvalidOperationException>(parameterWithError.WaitForValueTcs.Task.Exception?.InnerException);
    }

    [Fact]
    public async Task HandleUnresolvedParametersAsync_WithMultipleUnresolvedParameters_CreatesInteractions()
    {
        // Arrange
        var testInteractionService = new TestInteractionService();
        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var mockUserSecretsManager = new MockUserSecretsManager();
        var parameterProcessor = CreateParameterProcessor(
            notificationService: notificationService,
            interactionService: testInteractionService,
            userSecretsManager: mockUserSecretsManager);
        var param1 = CreateParameterWithMissingValue("param1");
        var param2 = CreateParameterWithMissingValue("param2");
        var secretParam = CreateParameterWithMissingValue("secretParam", secret: true);

        List<ParameterResource> parameters = [param1, param2, secretParam];

        foreach (var param in parameters)
        {
            // Initialize the parameters' WaitForValueTcs
            param.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var updates = notificationService.WatchAsync().GetAsyncEnumerator();

        // Act - Start handling unresolved parameters
        var handleTask = parameterProcessor.HandleUnresolvedParametersAsync(parameters, CancellationToken.None);

        // Assert - Wait for the first interaction (message bar)
        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(InteractionStrings.ParametersBarTitle, messageBarInteraction.Title);
        Assert.Equal(InteractionStrings.ParametersBarMessage, messageBarInteraction.Message);

        // Complete the message bar interaction to proceed to inputs dialog
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true)); // Data = true (user clicked Enter Values)

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(InteractionStrings.ParametersInputsTitle, inputsInteraction.Title);
        Assert.Equal(InteractionStrings.ParametersInputsMessage, inputsInteraction.Message);
        Assert.True(inputsInteraction.Options!.EnableMessageMarkdown);

        Assert.Collection(inputsInteraction.Inputs,
            input =>
            {
                Assert.Equal("param1", input.Label);
                Assert.Equal(InputType.Text, input.InputType);
                Assert.False(input.Required);
            },
            input =>
            {
                Assert.Equal("param2", input.Label);
                Assert.Equal(InputType.Text, input.InputType);
                Assert.False(input.Required);
            },
            input =>
            {
                Assert.Equal("secretParam", input.Label);
                Assert.Equal(InputType.SecretText, input.InputType);
                Assert.False(input.Required);
            },
            input =>
            {
                Assert.Equal(InteractionStrings.ParametersInputsRememberLabel, input.Label);
                Assert.Equal(InputType.Boolean, input.InputType);
                Assert.False(input.Required);
            });

        inputsInteraction.Inputs["param1"].Value = "value1";
        inputsInteraction.Inputs["param2"].Value = "value2";
        inputsInteraction.Inputs["secretParam"].Value = "secretValue";

        inputsInteraction.CompletionTcs.SetResult(InteractionResult.Ok(inputsInteraction.Inputs));

        // Wait for the handle task to complete
        await handleTask.DefaultTimeout();

        // Assert - All parameters should now be resolved
        Assert.True(param1.WaitForValueTcs!.Task.IsCompletedSuccessfully);
        Assert.True(param2.WaitForValueTcs!.Task.IsCompletedSuccessfully);
        Assert.True(secretParam.WaitForValueTcs!.Task.IsCompletedSuccessfully);
        Assert.Equal("value1", await param1.WaitForValueTcs.Task.DefaultTimeout());
        Assert.Equal("value2", await param2.WaitForValueTcs.Task.DefaultTimeout());
        Assert.Equal("secretValue", await secretParam.WaitForValueTcs.Task.DefaultTimeout());

        // Notification service should have received updates for each parameter
        // Marking them as Running with the provided values
        await updates.MoveNextAsync().DefaultTimeout();
        Assert.Equal(KnownResourceStates.Running, updates.Current.Snapshot.State?.Text);
        Assert.Equal("value1", updates.Current.Snapshot.Properties.FirstOrDefault(p => p.Name == KnownProperties.Parameter.Value)?.Value);

        await updates.MoveNextAsync().DefaultTimeout();
        Assert.Equal(KnownResourceStates.Running, updates.Current.Snapshot.State?.Text);
        Assert.Equal("value2", updates.Current.Snapshot.Properties.FirstOrDefault(p => p.Name == KnownProperties.Parameter.Value)?.Value);

        await updates.MoveNextAsync().DefaultTimeout();
        Assert.Equal(KnownResourceStates.Running, updates.Current.Snapshot.State?.Text);
        Assert.Equal("secretValue", updates.Current.Snapshot.Properties.FirstOrDefault(p => p.Name == KnownProperties.Parameter.Value)?.Value);
        Assert.True(updates.Current.Snapshot.Properties.FirstOrDefault(p => p.Name == KnownProperties.Parameter.Value)?.IsSensitive ?? false);
    }

    [Fact]
    public async Task HandleUnresolvedParametersAsync_WhenUserCancelsInteraction_ParametersRemainUnresolved()
    {
        // Arrange
        var testInteractionService = new TestInteractionService();
        var parameterProcessor = CreateParameterProcessor(interactionService: testInteractionService);
        var parameterWithMissingValue = CreateParameterWithMissingValue("missingParam");

        parameterWithMissingValue.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act - Start handling unresolved parameters
        _ = parameterProcessor.HandleUnresolvedParametersAsync([parameterWithMissingValue], CancellationToken.None);

        // Wait for the message bar interaction
        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(InteractionStrings.ParametersBarTitle, messageBarInteraction.Title);

        // Complete the message bar interaction with false (user chose not to enter values)
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Cancel<bool>());

        // Assert that the message bar will show up again if there are still unresolved parameters
        var nextMessageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(InteractionStrings.ParametersBarTitle, nextMessageBarInteraction.Title);

        // Assert - Parameter should remain unresolved since user cancelled
        Assert.NotNull(parameterWithMissingValue.WaitForValueTcs);
        Assert.False(parameterWithMissingValue.WaitForValueTcs.Task.IsCompleted);
    }

    [Fact]
    public async Task InitializeParametersAsync_WithEmptyParameterList_CompletesSuccessfully()
    {
        // Arrange
        var parameterProcessor = CreateParameterProcessor();

        // Act & Assert - Should not throw
        await parameterProcessor.InitializeParametersAsync([]).DefaultTimeout();
    }

    [Fact]
    public async Task InitializeParametersAsync_WithMissingParameterValue_LogsWarningWithoutException()
    {
        // Arrange
        var loggerService = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var interactionService = CreateInteractionService();
        var parameterProcessor = CreateParameterProcessor(
            loggerService: loggerService,
            interactionService: interactionService);
        var parameterWithMissingValue = CreateParameterWithMissingValue("missingParam");

        // Set up log watching
        var logsTask = ConsoleLoggingTestHelpers.WatchForLogsAsync(loggerService, 1, parameterWithMissingValue);

        // Act
        await parameterProcessor.InitializeParametersAsync([parameterWithMissingValue]).DefaultTimeout();

        // Wait for logs to be written
        var logs = await logsTask.DefaultTimeout();

        // Assert - Should log warning without exception details
        Assert.Single(logs);
        var logEntry = logs[0];
        Assert.Contains("Parameter resource missingParam could not be initialized. Waiting for user input.", logEntry.Content);
        Assert.False(logEntry.IsErrorMessage);
    }

    [Fact]
    public async Task InitializeParametersAsync_WithNonMissingParameterException_LogsErrorWithException()
    {
        // Arrange
        var loggerService = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var parameterProcessor = CreateParameterProcessor(loggerService: loggerService);
        var parameterWithError = CreateParameterWithGenericError("errorParam");

        // Set up log watching
        var logsTask = ConsoleLoggingTestHelpers.WatchForLogsAsync(loggerService, 1, parameterWithError);

        // Act
        await parameterProcessor.InitializeParametersAsync([parameterWithError]).DefaultTimeout();

        // Wait for logs to be written
        var logs = await logsTask.DefaultTimeout();

        // Assert - Should log error message
        Assert.Single(logs);
        var logEntry = logs[0];
        Assert.Contains("Failed to initialize parameter resource errorParam.", logEntry.Content);
        Assert.True(logEntry.IsErrorMessage);
    }

    [Fact]
    public async Task HandleUnresolvedParametersAsync_WithResolvedParameter_LogsResolutionViaInteraction()
    {
        // Arrange
        var loggerService = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var testInteractionService = new TestInteractionService();
        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var parameterProcessor = CreateParameterProcessor(
            notificationService: notificationService,
            loggerService: loggerService,
            interactionService: testInteractionService);
        var parameter = CreateParameterWithMissingValue("testParam");

        parameter.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Set up log watching
        var logsTask = ConsoleLoggingTestHelpers.WatchForLogsAsync(loggerService, 1, parameter);

        // Act - Start handling unresolved parameters
        var handleTask = parameterProcessor.HandleUnresolvedParametersAsync([parameter], CancellationToken.None);

        // Wait for the message bar interaction
        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true));

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        inputsInteraction.Inputs["testParam"].Value = "testValue";
        inputsInteraction.CompletionTcs.SetResult(InteractionResult.Ok(inputsInteraction.Inputs));

        // Wait for the handle task to complete
        await handleTask.DefaultTimeout();

        // Wait for logs to be written
        var logs = await logsTask.DefaultTimeout();

        // Assert - Should log that parameter was resolved via user interaction
        Assert.Single(logs);
        var logEntry = logs[0];
        Assert.Contains("Parameter resource testParam has been resolved via user interaction.", logEntry.Content);
        Assert.False(logEntry.IsErrorMessage);
    }

    [Fact]
    public async Task HandleUnresolvedParametersAsync_WithParameterDescriptions_CreatesInputsWithDescriptions()
    {
        // Arrange
        var testInteractionService = new TestInteractionService();
        var mockUserSecretsManager = new MockUserSecretsManager();
        var parameterProcessor = CreateParameterProcessor(
            interactionService: testInteractionService,
            userSecretsManager: mockUserSecretsManager);

        var param1 = CreateParameterWithMissingValue("param1");
        param1.Description = "This is a test parameter";
        param1.EnableDescriptionMarkdown = false;

        var param2 = CreateParameterWithMissingValue("param2");
        param2.Description = "This parameter has **markdown** formatting";
        param2.EnableDescriptionMarkdown = true;

        List<ParameterResource> parameters = [param1, param2];

        foreach (var param in parameters)
        {
            param.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Act
        _ = parameterProcessor.HandleUnresolvedParametersAsync(parameters, CancellationToken.None);

        // Wait for the message bar interaction and complete it
        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true));

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();

        // Assert
        Assert.Equal(3, inputsInteraction.Inputs.Count); // 2 parameters + 1 save option

        var param1Input = inputsInteraction.Inputs["param1"];
        Assert.Equal("param1", param1Input.Label);
        Assert.Equal("This is a test parameter", param1Input.Description);
        Assert.False(param1Input.EnableDescriptionMarkdown);
        Assert.Equal(InputType.Text, param1Input.InputType);

        var param2Input = inputsInteraction.Inputs["param2"];
        Assert.Equal("param2", param2Input.Label);
        Assert.Equal("This parameter has **markdown** formatting", param2Input.Description);
        Assert.True(param2Input.EnableDescriptionMarkdown);
        Assert.Equal(InputType.Text, param2Input.InputType);
    }

    [Fact]
    public async Task HandleUnresolvedParametersAsync_WithSecretParameterWithDescription_CreatesSecretInput()
    {
        // Arrange
        var testInteractionService = new TestInteractionService();
        var mockUserSecretsManager = new MockUserSecretsManager();
        var parameterProcessor = CreateParameterProcessor(
            interactionService: testInteractionService,
            userSecretsManager: mockUserSecretsManager);

        var secretParam = CreateParameterWithMissingValue("secretParam", secret: true);
        secretParam.Description = "This is a secret parameter";
        secretParam.EnableDescriptionMarkdown = false;

        List<ParameterResource> parameters = [secretParam];
        secretParam.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        _ = parameterProcessor.HandleUnresolvedParametersAsync(parameters, CancellationToken.None);

        // Wait for the message bar interaction and complete it
        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true));

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();

        // Assert
        Assert.Equal(2, inputsInteraction.Inputs.Count); // 1 secret parameter + 1 save option

        var secretInput = inputsInteraction.Inputs["secretParam"];
        Assert.Equal("secretParam", secretInput.Label);
        Assert.Equal("This is a secret parameter", secretInput.Description);
        Assert.False(secretInput.EnableDescriptionMarkdown);
        Assert.Equal(InputType.SecretText, secretInput.InputType);
    }

    [Fact]
    public async Task HandleUnresolvedParametersAsync_WhenUserSecretsNotAvailable_ShowsDisabledSaveCheckbox()
    {
        // Arrange
        var testInteractionService = new TestInteractionService();
        var noopUserSecretsManager = UserSecrets.NoopUserSecretsManager.Instance;
        var parameterProcessor = CreateParameterProcessor(
            interactionService: testInteractionService,
            userSecretsManager: noopUserSecretsManager);

        var param = CreateParameterWithMissingValue("param1");
        param.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        List<ParameterResource> parameters = [param];

        // Act
        _ = parameterProcessor.HandleUnresolvedParametersAsync(parameters, CancellationToken.None);

        // Wait for the message bar interaction and complete it
        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true));

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync();

        // Assert - Should have 2 inputs (parameter + disabled save checkbox)
        Assert.Equal(2, inputsInteraction.Inputs.Count);

        var paramInput = inputsInteraction.Inputs["param1"];
        Assert.Equal("param1", paramInput.Label);

        var saveCheckbox = inputsInteraction.Inputs[ParameterProcessor.SaveToUserSecretsName];
        Assert.Equal(InteractionStrings.ParametersInputsRememberLabel, saveCheckbox.Label);
        Assert.Equal(InputType.Boolean, saveCheckbox.InputType);
        Assert.True(saveCheckbox.Disabled); // Should be disabled when user secrets not available
        Assert.Equal(InteractionStrings.ParametersInputsRememberDescriptionNotConfigured, saveCheckbox.Description);
        Assert.True(saveCheckbox.EnableDescriptionMarkdown);
    }

    [Fact]
    public async Task HandleUnresolvedParametersAsync_WhenUserSecretsAvailable_ShowsEnabledSaveCheckbox()
    {
        // Arrange
        var testInteractionService = new TestInteractionService();
        var mockUserSecretsManager = new MockUserSecretsManager();
        var parameterProcessor = CreateParameterProcessor(
            interactionService: testInteractionService,
            userSecretsManager: mockUserSecretsManager);

        var param = CreateParameterWithMissingValue("param1");
        param.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        List<ParameterResource> parameters = [param];

        // Act
        _ = parameterProcessor.HandleUnresolvedParametersAsync(parameters, CancellationToken.None);

        // Wait for the message bar interaction and complete it
        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true));

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync();

        // Assert - Should have 2 inputs (parameter + enabled save checkbox)
        Assert.Equal(2, inputsInteraction.Inputs.Count);

        var paramInput = inputsInteraction.Inputs["param1"];
        Assert.Equal("param1", paramInput.Label);

        var saveCheckbox = inputsInteraction.Inputs[ParameterProcessor.SaveToUserSecretsName];
        Assert.Equal(InteractionStrings.ParametersInputsRememberLabel, saveCheckbox.Label);
        Assert.Equal(InputType.Boolean, saveCheckbox.InputType);
        Assert.False(saveCheckbox.Disabled); // Should be enabled when user secrets are available
        Assert.Null(saveCheckbox.Description); // No description when enabled
        Assert.True(saveCheckbox.EnableDescriptionMarkdown);
    }

    [Fact]
    public async Task InitializeParametersAsync_WithDistributedApplicationModel_CollectsAndInitializesAllParameters()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var explicitParam = builder.AddParameter("explicitParam", () => "explicitValue");
        var referencedParam = builder.AddParameter("referencedParam", () => "referencedValue");

        // Create a container that references the parameter in an environment variable
        builder.AddContainer("testContainer", "nginx")
               .WithEnvironment("TEST_ENV", referencedParam);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parameterProcessor = CreateParameterProcessor();

        // Act
        await parameterProcessor.InitializeParametersAsync(model).DefaultTimeout();

        // Assert
        var explicitParameterResource = model.Resources.OfType<ParameterResource>().First(p => p.Name == "explicitParam");
        var referencedParameterResource = model.Resources.OfType<ParameterResource>().First(p => p.Name == "referencedParam");

        Assert.NotNull(explicitParameterResource.WaitForValueTcs);
        Assert.NotNull(referencedParameterResource.WaitForValueTcs);
        Assert.True(explicitParameterResource.WaitForValueTcs.Task.IsCompletedSuccessfully);
        Assert.True(referencedParameterResource.WaitForValueTcs.Task.IsCompletedSuccessfully);
        Assert.Equal("explicitValue", await explicitParameterResource.WaitForValueTcs.Task.DefaultTimeout());
        Assert.Equal("referencedValue", await referencedParameterResource.WaitForValueTcs.Task.DefaultTimeout());
    }

    [Fact]
    public async Task InitializeParametersAsync_WithDistributedApplicationModel_EmptyModel_CompletesSuccessfully()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parameterProcessor = CreateParameterProcessor();

        // Act & Assert - Should not throw
        await parameterProcessor.InitializeParametersAsync(model).DefaultTimeout();
    }

    [Fact]
    public async Task InitializeParametersAsync_WithDistributedApplicationModel_NoParameterReferences_InitializesExplicitOnly()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var explicitParam = builder.AddParameter("explicitParam", () => "explicitValue");

        // Add a container without parameter references
        builder.AddContainer("testContainer", "nginx")
               .WithEnvironment("TEST_ENV", "staticValue");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parameterProcessor = CreateParameterProcessor();

        // Act
        await parameterProcessor.InitializeParametersAsync(model).DefaultTimeout();

        // Assert
        var explicitParameterResource = model.Resources.OfType<ParameterResource>().Single();
        Assert.Equal("explicitParam", explicitParameterResource.Name);
        Assert.NotNull(explicitParameterResource.WaitForValueTcs);
        Assert.True(explicitParameterResource.WaitForValueTcs.Task.IsCompletedSuccessfully);
        Assert.Equal("explicitValue", await explicitParameterResource.WaitForValueTcs.Task.DefaultTimeout());
    }

    [Fact]
    public async Task InitializeParametersAsync_WithDistributedApplicationModel_WithEnvironmentVariableReferences()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var referencedParam = builder.AddParameter("envParam", () => "envValue");

        builder.AddContainer("testContainer", "nginx")
               .WithEnvironment("TEST_ENV", referencedParam);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parameterProcessor = CreateParameterProcessor();

        // Act
        await parameterProcessor.InitializeParametersAsync(model).DefaultTimeout();

        // Assert
        var parameterResource = model.Resources.OfType<ParameterResource>().Single();
        Assert.Equal("envParam", parameterResource.Name);
        Assert.NotNull(parameterResource.WaitForValueTcs);
        Assert.True(parameterResource.WaitForValueTcs.Task.IsCompletedSuccessfully);
        Assert.Equal("envValue", await parameterResource.WaitForValueTcs.Task.DefaultTimeout());
    }

    [Fact]
    public async Task InitializeParametersAsync_WithDistributedApplicationModel_WaitForResolution_True()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var param = builder.AddParameter("testParam", () => "testValue");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parameterProcessor = CreateParameterProcessor();

        // Act
        await parameterProcessor.InitializeParametersAsync(model, waitForResolution: true).DefaultTimeout();

        // Assert
        var parameterResource = model.Resources.OfType<ParameterResource>().Single();
        Assert.NotNull(parameterResource.WaitForValueTcs);
        Assert.True(parameterResource.WaitForValueTcs.Task.IsCompletedSuccessfully);
        Assert.Equal("testValue", await parameterResource.WaitForValueTcs.Task.DefaultTimeout());
    }

    [Fact]
    public async Task InitializeParametersAsync_WithDistributedApplicationModel_WaitForResolution_False()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var param = builder.AddParameter("testParam", () => "testValue");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parameterProcessor = CreateParameterProcessor();

        // Act
        await parameterProcessor.InitializeParametersAsync(model, waitForResolution: false).DefaultTimeout();

        // Assert
        var parameterResource = model.Resources.OfType<ParameterResource>().Single();
        Assert.NotNull(parameterResource.WaitForValueTcs);
        Assert.True(parameterResource.WaitForValueTcs.Task.IsCompletedSuccessfully);
        Assert.Equal("testValue", await parameterResource.WaitForValueTcs.Task.DefaultTimeout());
    }

    [Fact]
    public async Task InitializeParametersAsync_WithDistributedApplicationModel_WithMissingParameterValues_HandlesCorrectly()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var missingParam = builder.AddParameter("missingParam", () => throw new MissingParameterValueException("Parameter 'missingParam' is missing"));

        builder.AddContainer("testContainer", "nginx")
               .WithEnvironment("TEST_ENV", missingParam);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var interactionService = CreateInteractionService();
        var parameterProcessor = CreateParameterProcessor(interactionService: interactionService);

        // Act
        await parameterProcessor.InitializeParametersAsync(model).DefaultTimeout();

        // Assert
        var parameterResource = model.Resources.OfType<ParameterResource>().Single();
        Assert.NotNull(parameterResource.WaitForValueTcs);
        Assert.False(parameterResource.WaitForValueTcs.Task.IsCompleted);
    }

    [Fact]
    public async Task InitializeParametersAsync_WithDistributedApplicationModel_HandlesCircularReferences()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var param1 = builder.AddParameter("param1", () => "value1");
        var param2 = builder.AddParameter("param2", () => "value2");

        // Create a scenario that could potentially create circular references
        builder.AddContainer("container1", "nginx")
               .WithEnvironment("ENV1", param1)
               .WithEnvironment("ENV2", param2);

        builder.AddContainer("container2", "nginx")
               .WithEnvironment("ENV3", param1);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parameterProcessor = CreateParameterProcessor();

        // Act - Should not hang or throw due to circular references
        await parameterProcessor.InitializeParametersAsync(model).DefaultTimeout();

        // Assert
        var parameters = model.Resources.OfType<ParameterResource>().ToList();
        Assert.Equal(2, parameters.Count);

        foreach (var param in parameters)
        {
            Assert.NotNull(param.WaitForValueTcs);
            Assert.True(param.WaitForValueTcs.Task.IsCompletedSuccessfully);
        }
    }

    [Fact]
    public async Task InitializeParametersAsync_UsesExecutionContextOptions_DoesNotThrow()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(new MockDeploymentStateManager());
        var param = builder.AddParameter("testParam", () => "testValue");

        var serviceProviderAccessed = false;
        builder.AddContainer("testContainer", "nginx")
               .WithEnvironment(context =>
               {
                   // This should not throw InvalidOperationException
                   // when using the proper execution context constructor
                   var sp = context.ExecutionContext.ServiceProvider;
                   serviceProviderAccessed = sp is not null;
                   context.EnvironmentVariables["TEST_ENV"] = param;
               });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Get the ParameterProcessor from the built app's service provider
        // This ensures it has the proper execution context with ServiceProvider
        var parameterProcessor = app.Services.GetRequiredService<ParameterProcessor>();

        // Act - Should not throw InvalidOperationException about IServiceProvider not being available
        await parameterProcessor.InitializeParametersAsync(model).DefaultTimeout();

        // Assert
        Assert.True(serviceProviderAccessed);
        var parameterResource = model.Resources.OfType<ParameterResource>().Single();
        Assert.NotNull(parameterResource.WaitForValueTcs);
        Assert.True(parameterResource.WaitForValueTcs.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task InitializeParametersAsync_SkipsResourcesExcludedFromPublish()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var param = builder.AddParameter("excludedParam", () => "excludedValue");

        var excludedContainer = builder.AddContainer("excludedContainer", "nginx")
               .WithEnvironment(context =>
               {
                   context.EnvironmentVariables["EXCLUDED_ENV"] = param;
               });

        // Mark the container as excluded from publish
        excludedContainer.ExcludeFromManifest();

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parameterProcessor = CreateParameterProcessor();

        // Act - The excluded container should be skipped during parameter collection
        await parameterProcessor.InitializeParametersAsync(model).DefaultTimeout();

        // Assert
        // The environment callback should have been invoked during parameter collection
        // because we now create a publish execution context to collect dependent parameters
        // However, since we filter out excluded resources, the parameter should not be initialized
        // unless it's explicitly in the model
        var parameters = model.Resources.OfType<ParameterResource>().ToList();
        Assert.Single(parameters);

        var parameterResource = parameters[0];
        Assert.Equal("excludedParam", parameterResource.Name);

        // The parameter should be initialized since it's explicitly in the model
        Assert.NotNull(parameterResource.WaitForValueTcs);
        Assert.True(parameterResource.WaitForValueTcs.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ProcessParameterAsync_WithInteractionServiceAvailable_AddsSetParameterCommand()
    {
        // Arrange
        var testInteractionService = new TestInteractionService { IsAvailable = true };
        var parameterProcessor = CreateParameterProcessor(interactionService: testInteractionService);
        var parameter = CreateParameterResource("testParam", "testValue");

        // Act
        await parameterProcessor.InitializeParametersAsync([parameter]).DefaultTimeout();

        // Assert - Command should be added when interaction service is available
        var setValueCommand = parameter.Annotations.OfType<ResourceCommandAnnotation>()
            .SingleOrDefault(a => a.Name == KnownResourceCommands.SetParameterCommand);
        Assert.NotNull(setValueCommand);
        Assert.Equal(CommandStrings.SetParameterName, setValueCommand.DisplayName);
        Assert.Equal(CommandStrings.SetParameterDescription, setValueCommand.DisplayDescription);
        Assert.True(setValueCommand.IsHighlighted);
    }

    [Fact]
    public async Task ProcessParameterAsync_WithInteractionServiceNotAvailable_DoesNotAddSetParameterCommand()
    {
        // Arrange
        var testInteractionService = new TestInteractionService { IsAvailable = false };
        var parameterProcessor = CreateParameterProcessor(interactionService: testInteractionService);
        var parameter = CreateParameterResource("testParam", "testValue");

        // Act
        await parameterProcessor.InitializeParametersAsync([parameter]).DefaultTimeout();

        // Assert - Command should not be added when interaction service is not available
        var setValueCommand = parameter.Annotations.OfType<ResourceCommandAnnotation>()
            .SingleOrDefault(a => a.Name == KnownResourceCommands.SetParameterCommand);
        Assert.Null(setValueCommand);
    }

    [Fact]
    public async Task SetParameterAsync_WithUserInput_UpdatesParameterValue()
    {
        // Arrange
        var testInteractionService = new TestInteractionService { IsAvailable = true };
        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var parameterProcessor = CreateParameterProcessor(
            notificationService: notificationService,
            interactionService: testInteractionService);
        var parameter = CreateParameterResource("testParam", "initialValue");

        // Initialize the parameter
        await parameterProcessor.InitializeParametersAsync([parameter]).DefaultTimeout();

        // Reset WaitForValueTcs to track updates
        parameter.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act - Start the SetParameterAsync task
        var setValueTask = Task.Run(async () =>
        {
            await parameterProcessor.SetParameterAsync(parameter);
        });

        // Wait for the input dialog to be presented
        var inputInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(InteractionStrings.SetParameterTitle, inputInteraction.Title);
        Assert.Equal(InteractionStrings.SetParameterMessage, inputInteraction.Message);
        // Should have 2 inputs: parameter value input + SaveToUserSecrets checkbox (in run mode)
        Assert.Equal(2, inputInteraction.Inputs.Count);
        Assert.Equal("testParam", inputInteraction.Inputs["testParam"].Label);
        // Existing value should be pre-populated
        Assert.Equal("initialValue", inputInteraction.Inputs["testParam"].Value);
        // SaveToUserSecrets shouldn't be true because the existing value isn't saved to sate.
        Assert.Null(inputInteraction.Inputs[ParameterProcessor.SaveToUserSecretsName].Value);

        // Complete the interaction with a new value
        inputInteraction.Inputs["testParam"].Value = "newValue";
        inputInteraction.CompletionTcs.SetResult(InteractionResult.Ok(inputInteraction.Inputs));

        // Wait for the set value task to complete
        await setValueTask.DefaultTimeout();

        // Assert - Parameter value should be updated
        Assert.Equal("newValue", await parameter.GetValueAsync(CancellationToken.None).DefaultTimeout());
    }

    [Fact]
    public async Task SetParameterAsync_WhenUserCancels_ParameterValueUnchanged()
    {
        // Arrange
        var testInteractionService = new TestInteractionService { IsAvailable = true };
        var parameterProcessor = CreateParameterProcessor(interactionService: testInteractionService);
        var parameter = CreateParameterResource("testParam", "initialValue");

        // Initialize the parameter
        await parameterProcessor.InitializeParametersAsync([parameter]).DefaultTimeout();

        // Reset WaitForValueTcs to track updates
        var originalTcs = parameter.WaitForValueTcs;
        parameter.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act - Start the SetParameterAsync task
        var setValueTask = Task.Run(async () =>
        {
            await parameterProcessor.SetParameterAsync(parameter);
        });

        // Wait for the input dialog to be presented
        var inputInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();

        // Cancel the interaction
        inputInteraction.CompletionTcs.SetResult(InteractionResult.Cancel<InteractionInputCollection>());

        // Wait for the set value task to complete
        await setValueTask.DefaultTimeout();

        // Assert - Parameter value should remain unchanged (WaitForValueTcs not set)
        Assert.False(parameter.WaitForValueTcs!.Task.IsCompleted);
    }

    [Fact]
    public async Task SetParameterAsync_WithSecretParameter_UsesSecretTextInput()
    {
        // Arrange
        var testInteractionService = new TestInteractionService { IsAvailable = true };
        var parameterProcessor = CreateParameterProcessor(interactionService: testInteractionService);
        var parameter = CreateParameterResource("secretParam", "secretValue", secret: true);

        // Initialize the parameter
        await parameterProcessor.InitializeParametersAsync([parameter]).DefaultTimeout();

        // Reset WaitForValueTcs to track updates
        parameter.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act - Start the SetParameterAsync task
        var setValueTask = Task.Run(async () =>
        {
            await parameterProcessor.SetParameterAsync(parameter);
        });

        // Wait for the input dialog to be presented
        var inputInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();

        // Assert - Should use SecretText input type for secret parameters
        Assert.Equal(2, inputInteraction.Inputs.Count);
        Assert.Equal(InputType.SecretText, inputInteraction.Inputs["secretParam"].InputType);
        // Existing value should be pre-populated for secrets too
        Assert.Equal("secretValue", inputInteraction.Inputs["secretParam"].Value);

        // Complete the interaction
        inputInteraction.Inputs["secretParam"].Value = "newSecretValue";
        inputInteraction.CompletionTcs.SetResult(InteractionResult.Ok(inputInteraction.Inputs));

        await setValueTask.DefaultTimeout();

        // Assert - Parameter value should be updated
        Assert.Equal("newSecretValue", await parameter.WaitForValueTcs!.Task.DefaultTimeout());
    }

    [Fact]
    public async Task SetParameterAsync_ResolvingLastParameter_CancelsPromptNotification()
    {
        // Arrange
        var testInteractionService = new TestInteractionService { IsAvailable = true };
        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var parameterProcessor = CreateParameterProcessor(
            notificationService: notificationService,
            interactionService: testInteractionService);

        var parameter = CreateParameterWithMissingValue("testParam");

        // Use InitializeParametersAsync to properly set up the internal state
        // This will trigger HandleUnresolvedParametersAsync in a background task
        // with the internal _allParametersResolvedCts.Token
        await parameterProcessor.InitializeParametersAsync([parameter]).DefaultTimeout();

        // Wait for the notification to appear from the background task
        var notificationInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(InteractionStrings.ParametersBarTitle, notificationInteraction.Title);

        // Capture the cancellation token passed to the notification
        var notificationCancellationToken = notificationInteraction.CancellationToken;
        Assert.False(notificationCancellationToken.IsCancellationRequested);

        // Now use SetParameterAsync to resolve the parameter (which is the last unresolved parameter)
        var setValueTask = Task.Run(async () =>
        {
            await parameterProcessor.SetParameterAsync(parameter);
        });

        // Wait for the SetParameterAsync input dialog to appear
        var inputInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(InteractionStrings.SetParameterTitle, inputInteraction.Title);

        // Complete the SetParameterAsync interaction with a value
        inputInteraction.Inputs["testParam"].Value = "resolvedValue";
        inputInteraction.CompletionTcs.SetResult(InteractionResult.Ok(inputInteraction.Inputs));

        await setValueTask.DefaultTimeout();

        // The parameter should be resolved with the correct value
        Assert.Equal("resolvedValue", await parameter.GetValueAsync(CancellationToken.None).DefaultTimeout());

        // Assert - The notification's cancellation token should now be canceled
        // because the last parameter was resolved via SetParameterAsync
        Assert.True(notificationCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task SetParameterAsync_CalledTwice_SecondInteractionShowsPreviousValueAndSaveChecked()
    {
        // Arrange
        var testInteractionService = new TestInteractionService { IsAvailable = true };
        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var capturingStateManager = new CapturingMockDeploymentStateManager();
        var parameterProcessor = CreateParameterProcessor(
            notificationService: notificationService,
            interactionService: testInteractionService,
            deploymentStateManager: capturingStateManager);

        var parameter = CreateParameterWithMissingValue("testParam");

        // Initialize the parameter - this starts HandleUnresolvedParametersAsync in background
        await parameterProcessor.InitializeParametersAsync([parameter]).DefaultTimeout();

        // Wait for the notification to appear from the background task
        var notificationInteraction = await testInteractionService.Interactions.Reader.ReadAsync().AsTask().DefaultTimeout();
        Assert.Equal(InteractionStrings.ParametersBarTitle, notificationInteraction.Title);

        // First SetParameterAsync call - set and save a value
        var firstSetValueTask = Task.Run(async () =>
        {
            await parameterProcessor.SetParameterAsync(parameter);
        });

        // Wait for the first input dialog
        var firstInputInteraction = await testInteractionService.Interactions.Reader.ReadAsync().AsTask().DefaultTimeout();
        Assert.Equal(InteractionStrings.SetParameterTitle, firstInputInteraction.Title);

        // First time: no saved state, so SaveToUserSecrets should be null/unchecked
        Assert.Null(firstInputInteraction.Inputs[ParameterProcessor.SaveToUserSecretsName].Value);

        // Set the value and enable save
        firstInputInteraction.Inputs["testParam"].Value = "firstValue";
        firstInputInteraction.Inputs[ParameterProcessor.SaveToUserSecretsName].Value = "true";
        firstInputInteraction.CompletionTcs.SetResult(InteractionResult.Ok(firstInputInteraction.Inputs));

        await firstSetValueTask.DefaultTimeout();

        // Verify first value was set
        Assert.Equal("firstValue", await parameter.GetValueAsync(CancellationToken.None).DefaultTimeout());

        // Second SetParameterAsync call - should show previously set value
        var secondSetValueTask = Task.Run(async () =>
        {
            await parameterProcessor.SetParameterAsync(parameter);
        });

        // Wait for the second input dialog
        var secondInputInteraction = await testInteractionService.Interactions.Reader.ReadAsync().AsTask().DefaultTimeout();
        Assert.Equal(InteractionStrings.SetParameterTitle, secondInputInteraction.Title);

        // Assert - Second interaction should have the previously set value pre-populated
        Assert.Equal("firstValue", secondInputInteraction.Inputs["testParam"].Value);

        // Assert - SaveToUserSecrets should be checked (true) since parameter has saved state
        Assert.Equal("true", secondInputInteraction.Inputs[ParameterProcessor.SaveToUserSecretsName].Value);

        // Complete the second interaction with a new value
        secondInputInteraction.Inputs["testParam"].Value = "secondValue";
        secondInputInteraction.CompletionTcs.SetResult(InteractionResult.Ok(secondInputInteraction.Inputs));

        await secondSetValueTask.DefaultTimeout();

        // Verify second value was set
        Assert.Equal("secondValue", await parameter.GetValueAsync(CancellationToken.None).DefaultTimeout());
    }

    private static ParameterProcessor CreateParameterProcessor(
        ResourceNotificationService? notificationService = null,
        ResourceLoggerService? loggerService = null,
        IInteractionService? interactionService = null,
        ILogger<ParameterProcessor>? logger = null,
        bool disableDashboard = true,
        DistributedApplicationExecutionContext? executionContext = null,
        IDeploymentStateManager? deploymentStateManager = null,
        IUserSecretsManager? userSecretsManager = null)
    {
        return new ParameterProcessor(
            notificationService ?? ResourceNotificationServiceTestHelpers.Create(),
            loggerService ?? new ResourceLoggerService(),
            interactionService ?? CreateInteractionService(disableDashboard),
            logger ?? new NullLogger<ParameterProcessor>(),
            executionContext ?? new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            deploymentStateManager ?? new MockDeploymentStateManager(),
            userSecretsManager ?? UserSecrets.NoopUserSecretsManager.Instance
        );
    }

    private static InteractionService CreateInteractionService(bool disableDashboard = false)
    {
        return new InteractionService(
            new NullLogger<InteractionService>(),
            new DistributedApplicationOptions { DisableDashboard = disableDashboard },
            new ServiceCollection().BuildServiceProvider(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
    }

    private sealed class MockDeploymentStateManager : IDeploymentStateManager
    {
        public string? StateFilePath => null;

        public Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DeploymentStateSection(sectionName, [], 0));
        }

        public Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private static ParameterResource CreateParameterResource(string name, string value, bool secret = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"Parameters:{name}"] = value })
            .Build();

        return new ParameterResource(name, _ => configuration[$"Parameters:{name}"] ?? throw new MissingParameterValueException($"Parameter '{name}' is missing"), secret);
    }

    private static ParameterResource CreateParameterWithMissingValue(string name, bool secret = false)
    {
        return new ParameterResource(name, _ => throw new MissingParameterValueException($"Parameter '{name}' is missing"), secret: secret);
    }

    private static ParameterResource CreateParameterWithGenericError(string name)
    {
        return new ParameterResource(name, _ => throw new InvalidOperationException($"Generic error for parameter '{name}'"), secret: false);
    }

    [Fact]
    public async Task InitializeParametersAsync_WithGenerateParameterDefaultInPublishMode_DoesNotThrowWhenValueExists()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Parameters:generatedParam"] = "existingValue" })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        var serviceProvider = services.BuildServiceProvider();

        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Publish, "manifest")
            {
                ServiceProvider = serviceProvider
            });

        var parameterProcessor = CreateParameterProcessor(executionContext: executionContext);

        var parameterWithGenerateDefault = new ParameterResource(
            "generatedParam",
            parameterDefault => configuration["Parameters:generatedParam"] ?? parameterDefault?.GetDefaultValue() ?? throw new MissingParameterValueException("Parameter 'generatedParam' is missing"),
            secret: false)
        {
            Default = new GenerateParameterDefault()
        };

        // Act
        await parameterProcessor.InitializeParametersAsync([parameterWithGenerateDefault]).DefaultTimeout();

        // Assert - Should succeed because value exists in configuration
        Assert.NotNull(parameterWithGenerateDefault.WaitForValueTcs);
        Assert.True(parameterWithGenerateDefault.WaitForValueTcs.Task.IsCompletedSuccessfully);
        Assert.Equal("existingValue", await parameterWithGenerateDefault.WaitForValueTcs.Task.DefaultTimeout());
    }

    [Fact]
    public async Task ConnectionStringParameterStateIsSavedWithCorrectKey()
    {
        var capturingStateManager = new CapturingMockDeploymentStateManager();
        var testInteractionService = new TestInteractionService();
        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var mockUserSecretsManager = new MockUserSecretsManager();
        var parameterProcessor = CreateParameterProcessor(
            notificationService: notificationService,
            interactionService: testInteractionService,
            deploymentStateManager: capturingStateManager,
            userSecretsManager: mockUserSecretsManager);

        var connectionStringParam = new ConnectionStringParameterResource(
            "mydb",
            _ => throw new MissingParameterValueException("Connection string 'mydb' is missing"),
            null);
        connectionStringParam.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        List<ParameterResource> parameters = [connectionStringParam];

        var handleTask = parameterProcessor.HandleUnresolvedParametersAsync(parameters, CancellationToken.None);

        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true));

        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        inputsInteraction.Inputs["mydb"].Value = "Server=localhost;Database=mydb";
        inputsInteraction.Inputs[ParameterProcessor.SaveToUserSecretsName].Value = "true";
        inputsInteraction.CompletionTcs.SetResult(InteractionResult.Ok(inputsInteraction.Inputs));

        await handleTask.DefaultTimeout();

        // Verify the value was saved correctly in the flattened state
        Assert.True(capturingStateManager.State.TryGetPropertyValue("ConnectionStrings:mydb", out var valueNode));
        Assert.Equal("Server=localhost;Database=mydb", valueNode?.GetValue<string>());

        // Verify the entire state structure as JSON (mimics what gets saved to disk)
        await VerifyJson(capturingStateManager.State.ToJsonString());
    }

    [Fact]
    public async Task RegularParameterStateIsSavedWithCorrectKey()
    {
        var capturingStateManager = new CapturingMockDeploymentStateManager();
        var testInteractionService = new TestInteractionService();
        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var mockUserSecretsManager = new MockUserSecretsManager();
        var parameterProcessor = CreateParameterProcessor(
            notificationService: notificationService,
            interactionService: testInteractionService,
            deploymentStateManager: capturingStateManager,
            userSecretsManager: mockUserSecretsManager);

        var regularParam = new ParameterResource(
            "myparam",
            _ => throw new MissingParameterValueException("Parameter 'myparam' is missing"),
            secret: false);
        regularParam.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        List<ParameterResource> parameters = [regularParam];

        var handleTask = parameterProcessor.HandleUnresolvedParametersAsync(parameters, CancellationToken.None);

        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true));

        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        inputsInteraction.Inputs["myparam"].Value = "myvalue";
        inputsInteraction.Inputs[ParameterProcessor.SaveToUserSecretsName].Value = "true";
        inputsInteraction.CompletionTcs.SetResult(InteractionResult.Ok(inputsInteraction.Inputs));

        await handleTask.DefaultTimeout();

        // Verify the value was saved correctly in the flattened state
        Assert.True(capturingStateManager.State.TryGetPropertyValue("Parameters:myparam", out var valueNode));
        Assert.Equal("myvalue", valueNode?.GetValue<string>());

        // Verify the entire state structure as JSON (mimics what gets saved to disk)
        await VerifyJson(capturingStateManager.State.ToJsonString());
    }

    [Fact]
    public async Task CustomConfigurationKeyParameterStateIsSavedWithCorrectKey()
    {
        var capturingStateManager = new CapturingMockDeploymentStateManager();
        var testInteractionService = new TestInteractionService();
        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var mockUserSecretsManager = new MockUserSecretsManager();
        var parameterProcessor = CreateParameterProcessor(
            notificationService: notificationService,
            interactionService: testInteractionService,
            deploymentStateManager: capturingStateManager,
            userSecretsManager: mockUserSecretsManager);

        var customParam = new ParameterResource(
            "customparam",
            _ => throw new MissingParameterValueException("Parameter 'customparam' is missing"),
            secret: false)
        {
            ConfigurationKey = "MyCustomSection:MyCustomKey"
        };
        customParam.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        List<ParameterResource> parameters = [customParam];

        var handleTask = parameterProcessor.HandleUnresolvedParametersAsync(parameters, CancellationToken.None);

        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true));

        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        inputsInteraction.Inputs["customparam"].Value = "customvalue";
        inputsInteraction.Inputs[ParameterProcessor.SaveToUserSecretsName].Value = "true";
        inputsInteraction.CompletionTcs.SetResult(InteractionResult.Ok(inputsInteraction.Inputs));

        await handleTask.DefaultTimeout();

        // Verify the value was saved correctly in the flattened state
        Assert.True(capturingStateManager.State.TryGetPropertyValue("MyCustomSection:MyCustomKey", out var valueNode));
        Assert.Equal("customvalue", valueNode?.GetValue<string>());

        // Verify the entire state structure as JSON (mimics what gets saved to disk)
        await VerifyJson(capturingStateManager.State.ToJsonString());
    }

    [Fact]
    public async Task SetParameterAsync_WithSavedState_OnlyShowsValueAndSaveInputs()
    {
        // Arrange
        var capturingStateManager = new CapturingMockDeploymentStateManager();
        var testInteractionService = new TestInteractionService { IsAvailable = true };
        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var parameterProcessor = CreateParameterProcessor(
            notificationService: notificationService,
            interactionService: testInteractionService,
            deploymentStateManager: capturingStateManager);

        var parameter = CreateParameterResource("testParam", "initialValue");

        // Initialize the parameter
        await parameterProcessor.InitializeParametersAsync([parameter]).DefaultTimeout();

        // First SetParameterAsync call - set and save a value to establish saved state
        var firstSetValueTask = Task.Run(async () =>
        {
            await parameterProcessor.SetParameterAsync(parameter);
        });

        var firstInputInteraction = await testInteractionService.Interactions.Reader.ReadAsync().AsTask().DefaultTimeout();
        // First time: should have 2 inputs (value + save)
        Assert.Equal(2, firstInputInteraction.Inputs.Count);

        // Set the value and save it
        firstInputInteraction.Inputs["testParam"].Value = "savedValue";
        firstInputInteraction.Inputs[ParameterProcessor.SaveToUserSecretsName].Value = "true";
        firstInputInteraction.CompletionTcs.SetResult(InteractionResult.Ok(firstInputInteraction.Inputs));

        await firstSetValueTask.DefaultTimeout();

        // Second SetParameterAsync call - should still only have 2 inputs (delete is now a separate command)
        var secondSetValueTask = Task.Run(async () =>
        {
            await parameterProcessor.SetParameterAsync(parameter);
        });

        var secondInputInteraction = await testInteractionService.Interactions.Reader.ReadAsync().AsTask().DefaultTimeout();

        // Assert - Should have 2 inputs: value + save (delete is now a separate command)
        Assert.Equal(2, secondInputInteraction.Inputs.Count);
        Assert.True(secondInputInteraction.Inputs.ContainsName("testParam"));
        Assert.True(secondInputInteraction.Inputs.ContainsName(ParameterProcessor.SaveToUserSecretsName));

        // Complete the interaction
        secondInputInteraction.CompletionTcs.SetResult(InteractionResult.Ok(secondInputInteraction.Inputs));

        await secondSetValueTask.DefaultTimeout();
    }

    [Fact]
    public async Task DeleteParameterAsync_DeletesFromDeploymentState()
    {
        // Arrange
        var capturingStateManager = new CapturingMockDeploymentStateManager();
        var testInteractionService = new TestInteractionService { IsAvailable = true };
        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var parameterProcessor = CreateParameterProcessor(
            notificationService: notificationService,
            interactionService: testInteractionService,
            deploymentStateManager: capturingStateManager);

        var parameter = CreateParameterResource("testParam", "initialValue");

        // Initialize the parameter
        await parameterProcessor.InitializeParametersAsync([parameter]).DefaultTimeout();

        // First SetParameterAsync call - set and save a value to establish saved state
        var firstSetValueTask = Task.Run(async () =>
        {
            await parameterProcessor.SetParameterAsync(parameter);
        });

        var firstInputInteraction = await testInteractionService.Interactions.Reader.ReadAsync().AsTask().DefaultTimeout();
        firstInputInteraction.Inputs["testParam"].Value = "savedValue";
        firstInputInteraction.Inputs[ParameterProcessor.SaveToUserSecretsName].Value = "true";
        firstInputInteraction.CompletionTcs.SetResult(InteractionResult.Ok(firstInputInteraction.Inputs));

        await firstSetValueTask.DefaultTimeout();

        // Verify value was saved
        Assert.True(capturingStateManager.State.Count > 0);

        // Call DeleteParameterAsync to delete the value - need to run in background as it shows a prompt
        var deleteTask = Task.Run(async () =>
        {
            await parameterProcessor.DeleteParameterAsync(parameter);
        });

        // Wait for the delete confirmation dialog
        var deleteConfirmation = await testInteractionService.Interactions.Reader.ReadAsync().AsTask().DefaultTimeout();
        Assert.Equal(InteractionStrings.DeleteParameterTitle, deleteConfirmation.Title);
        // Should have delete from user secrets checkbox since value is saved
        Assert.True(deleteConfirmation.Inputs.ContainsName(ParameterProcessor.DeleteFromUserSecretsName));
        Assert.Null(deleteConfirmation.Inputs[ParameterProcessor.DeleteFromUserSecretsName].Value);

        // Confirm the deletion with delete from user secrets checked
        deleteConfirmation.Inputs[ParameterProcessor.DeleteFromUserSecretsName].Value = "true";
        deleteConfirmation.CompletionTcs.SetResult(InteractionResult.Ok(deleteConfirmation.Inputs));

        await deleteTask.DefaultTimeout();

        // Assert - State should be cleared
        // The section still exists but should have no data
        var section = await capturingStateManager.AcquireSectionAsync($"Parameters:{parameter.Name}").DefaultTimeout();
        Assert.Empty(section.Data);
    }

    [Fact]
    public async Task SetParameterAsync_WithoutSavedState_DoesNotShowDeleteCheckbox()
    {
        // Arrange
        var testInteractionService = new TestInteractionService { IsAvailable = true };
        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var parameterProcessor = CreateParameterProcessor(
            notificationService: notificationService,
            interactionService: testInteractionService);

        var parameter = CreateParameterResource("testParam", "initialValue");

        // Initialize the parameter
        await parameterProcessor.InitializeParametersAsync([parameter]).DefaultTimeout();

        // Act - Start the SetParameterAsync task
        var setValueTask = Task.Run(async () =>
        {
            await parameterProcessor.SetParameterAsync(parameter);
        });

        // Wait for the input dialog to be presented
        var inputInteraction = await testInteractionService.Interactions.Reader.ReadAsync().AsTask().DefaultTimeout();

        // Assert - Should only have 2 inputs (value + save), no delete checkbox
        Assert.Equal(2, inputInteraction.Inputs.Count);
        Assert.True(inputInteraction.Inputs.ContainsName("testParam"));
        Assert.True(inputInteraction.Inputs.ContainsName(ParameterProcessor.SaveToUserSecretsName));
        Assert.False(inputInteraction.Inputs.ContainsName("DeleteParameter"));

        // Complete the interaction
        inputInteraction.CompletionTcs.SetResult(InteractionResult.Cancel<InteractionInputCollection>());
        await setValueTask.DefaultTimeout();
    }

    [Fact]
    public async Task DeleteParameterAsync_AddsParameterBackToUnresolvedAndStartsResolutionTask()
    {
        // Arrange
        var capturingStateManager = new CapturingMockDeploymentStateManager();
        var testInteractionService = new TestInteractionService { IsAvailable = true };
        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var parameterProcessor = CreateParameterProcessor(
            notificationService: notificationService,
            interactionService: testInteractionService,
            deploymentStateManager: capturingStateManager);

        var parameter = CreateParameterResource("testParam", "initialValue");

        // Initialize the parameter
        await parameterProcessor.InitializeParametersAsync([parameter]).DefaultTimeout();

        // First SetParameterAsync call - set and save a value to establish saved state
        var firstSetValueTask = Task.Run(async () =>
        {
            await parameterProcessor.SetParameterAsync(parameter);
        });

        var firstInputInteraction = await testInteractionService.Interactions.Reader.ReadAsync().AsTask().DefaultTimeout();
        firstInputInteraction.Inputs["testParam"].Value = "savedValue";
        firstInputInteraction.Inputs[ParameterProcessor.SaveToUserSecretsName].Value = "true";
        firstInputInteraction.CompletionTcs.SetResult(InteractionResult.Ok(firstInputInteraction.Inputs));

        await firstSetValueTask.DefaultTimeout();

        // Call DeleteParameterAsync to delete the value - need to run in background as it shows a prompt
        var deleteTask = Task.Run(async () =>
        {
            await parameterProcessor.DeleteParameterAsync(parameter);
        });

        // Wait for the delete confirmation dialog
        var deleteConfirmation = await testInteractionService.Interactions.Reader.ReadAsync().AsTask().DefaultTimeout();
        Assert.Equal(InteractionStrings.DeleteParameterTitle, deleteConfirmation.Title);

        // Confirm the deletion
        deleteConfirmation.CompletionTcs.SetResult(InteractionResult.Ok(deleteConfirmation.Inputs));

        await deleteTask.DefaultTimeout();

        // After delete, the resolution task should start and show a notification
        var notificationInteraction = await testInteractionService.Interactions.Reader.ReadAsync().AsTask().DefaultTimeout();
        Assert.Equal(InteractionStrings.ParametersBarTitle, notificationInteraction.Title);

        // Dismiss the notification to proceed to inputs dialog
        notificationInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true));

        // The inputs dialog should appear with the deleted parameter
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync().AsTask().DefaultTimeout();
        Assert.Equal(InteractionStrings.ParametersInputsTitle, inputsInteraction.Title);
        Assert.True(inputsInteraction.Inputs.ContainsName("testParam"));

        // Complete the interaction with a new value
        inputsInteraction.Inputs["testParam"].Value = "newValue";
        inputsInteraction.CompletionTcs.SetResult(InteractionResult.Ok(inputsInteraction.Inputs));

        // Verify the parameter was resolved with the new value
        Assert.Equal("newValue", await parameter.GetValueAsync(CancellationToken.None).DefaultTimeout());
    }

    private sealed class CapturingMockDeploymentStateManager : IDeploymentStateManager
    {
        // Stores the entire state in an unflattened structure in memory, then flattens for verification
        // to mimic FileDeploymentStateManager behavior
        private readonly JsonObject _unflattenedState = [];
        private JsonObject? _flattenedState;

        // Provides the flattened state for verification, matching what FileDeploymentStateManager saves to disk
        public JsonObject State => _flattenedState ?? [];
        public string? StateFilePath => null;

        public Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default)
        {
            // Return existing section data if it exists, otherwise return empty
            var sectionData = _unflattenedState.TryGetPropertyValue(sectionName, out var sectionNode) && sectionNode is JsonObject obj
                ? obj.DeepClone().AsObject()
                : null;

            return Task.FromResult(new DeploymentStateSection(sectionName, sectionData, 0));
        }

        public Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            // Increment version to allow multiple saves with the same instance (mimics FileDeploymentStateManager)
            section.Version++;

            // Store the section data in the unflattened state object
            _unflattenedState[section.SectionName] = section.Data.DeepClone().AsObject();

            // Flatten the state to mimic what FileDeploymentStateManager saves to disk
            _flattenedState = JsonFlattener.FlattenJsonObject(_unflattenedState);

            return Task.CompletedTask;
        }

        public Task DeleteSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            // Increment version to allow multiple saves with the same instance (mimics FileDeploymentStateManager)
            section.Version++;

            // Remove the section from the unflattened state object
            _unflattenedState.Remove(section.SectionName);

            // Flatten the state to mimic what FileDeploymentStateManager saves to disk
            _flattenedState = JsonFlattener.FlattenJsonObject(_unflattenedState);

            return Task.CompletedTask;
        }
    }
}
