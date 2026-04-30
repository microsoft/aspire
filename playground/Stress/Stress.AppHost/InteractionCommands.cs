// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

internal static class InteractionCommands
{
    [AspireExportIgnore(Reason = "Uses interaction service callbacks and command handlers that are not ATS-compatible.")]
    public static IResourceBuilder<T> AddInteractionCommands<T>(this IResourceBuilder<T> resource) where T : IResource
    {
        resource
            .WithCommand("confirmation-interaction", "Confirmation interactions", executeCommand: async commandContext =>
            {
                var interactionService = commandContext.ServiceProvider.GetRequiredService<IInteractionService>();
                var resultTask1 = interactionService.PromptConfirmationAsync("Command confirmation", "Are you sure?", cancellationToken: commandContext.CancellationToken);
                var resultTask2 = interactionService.PromptMessageBoxAsync("Command confirmation", "Are you really sure?", new MessageBoxInteractionOptions { Intent = MessageIntent.Warning, ShowSecondaryButton = true }, cancellationToken: commandContext.CancellationToken);

                await Task.WhenAll(resultTask1, resultTask2);

                if (resultTask1.Result.Data != true || resultTask2.Result.Data != true)
                {
                    return CommandResults.Failure("Canceled");
                }

                _ = interactionService.PromptMessageBoxAsync("Command executed", "The command successfully executed.", new MessageBoxInteractionOptions { Intent = MessageIntent.Success, PrimaryButtonText = "Yeah!" });
                return CommandResults.Success();
            })
            .WithCommand("messagebar-interaction", "Messagebar interactions", executeCommand: async commandContext =>
            {
                await Task.Yield();

                var interactionService = commandContext.ServiceProvider.GetRequiredService<IInteractionService>();
                _ = interactionService.PromptNotificationAsync("Success bar", "The command successfully executed.", new NotificationInteractionOptions { Intent = MessageIntent.Success });
                _ = interactionService.PromptNotificationAsync("Information bar", "The command successfully executed.", new NotificationInteractionOptions { Intent = MessageIntent.Information });
                _ = interactionService.PromptNotificationAsync("Warning bar", "The command successfully executed.", new NotificationInteractionOptions { Intent = MessageIntent.Warning });
                _ = interactionService.PromptNotificationAsync("Error bar", "The command successfully executed.", new NotificationInteractionOptions { Intent = MessageIntent.Error, LinkText = "Click here for more information", LinkUrl = "https://www.microsoft.com" });
                _ = interactionService.PromptNotificationAsync("Confirmation bar", "The command successfully executed.", new NotificationInteractionOptions { Intent = MessageIntent.Confirmation });
                _ = interactionService.PromptNotificationAsync("No dismiss", "The command successfully executed.", new NotificationInteractionOptions { Intent = MessageIntent.Information, ShowDismiss = false });

                return CommandResults.Success();
            })
            .WithCommand("html-interaction", "HTML interactions", executeCommand: async commandContext =>
            {
                var interactionService = commandContext.ServiceProvider.GetRequiredService<IInteractionService>();

                _ = interactionService.PromptNotificationAsync("Success <strong>bar</strong>", "The **command** successfully executed.", new NotificationInteractionOptions { Intent = MessageIntent.Success });
                _ = interactionService.PromptNotificationAsync("Success <strong>bar</strong>", "The **command** successfully executed.", new NotificationInteractionOptions { Intent = MessageIntent.Success, EnableMessageMarkdown = true });
                _ = interactionService.PromptNotificationAsync("Success <strong>bar</strong>", "Multiline 1\r\n\r\nMultiline 2", new NotificationInteractionOptions { Intent = MessageIntent.Success, EnableMessageMarkdown = true });

                _ = interactionService.PromptMessageBoxAsync("Success <strong>bar</strong>", "The **command** successfully executed.", new MessageBoxInteractionOptions { Intent = MessageIntent.Success });
                _ = interactionService.PromptMessageBoxAsync("Success <strong>bar</strong>", "The **command** successfully executed.", new MessageBoxInteractionOptions { Intent = MessageIntent.Success, EnableMessageMarkdown = true });
                _ = interactionService.PromptMessageBoxAsync("Success <strong>bar</strong>", "Multiline 1\r\n\r\nMultiline 2", new MessageBoxInteractionOptions { Intent = MessageIntent.Success, EnableMessageMarkdown = true });

                var inputNoMarkdown = new InteractionInput { Name = "Name", Label = "<strong>Name</strong>", InputType = InputType.Text, Placeholder = "Enter <strong>your</strong> name.", Description = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Fusce id massa arcu. Morbi ac risus eget augue venenatis hendrerit. Morbi posuere, neque id efficitur ultrices, velit augue suscipit ante, vitae lacinia elit risus nec dui.\r\n\r\nFor more information about the `IInteractionService`, see https://learn.microsoft.com." };
                var inputHasMarkdown = new InteractionInput { Name = "Name", Label = "<strong>Name</strong>", InputType = InputType.Text, Placeholder = "Enter <strong>your</strong> name.", Description = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Fusce id massa arcu. Morbi ac risus eget augue venenatis hendrerit. Morbi posuere, neque id efficitur ultrices, velit augue suscipit ante, vitae lacinia elit risus nec dui.\r\n\r\nFor more information about the `IInteractionService`, see https://learn.microsoft.com.", EnableDescriptionMarkdown = true };

                _ = await interactionService.PromptInputAsync("Text <strong>request</strong>", "Provide **your** name. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Fusce id massa arcu. Morbi ac risus eget augue venenatis hendrerit. Morbi posuere, neque id efficitur ultrices, velit augue suscipit ante, vitae lacinia elit risus nec dui. For more information about the `IInteractionService`, see https://learn.microsoft.com.", inputNoMarkdown);
                _ = await interactionService.PromptInputAsync("Text <strong>request</strong>", "Provide **your** name. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Fusce id massa arcu. Morbi ac risus eget augue venenatis hendrerit. Morbi posuere, neque id efficitur ultrices, velit augue suscipit ante, vitae lacinia elit risus nec dui. For more information about the `IInteractionService`, see https://learn.microsoft.com.", inputHasMarkdown, new InputsDialogInteractionOptions { EnableMessageMarkdown = true });
                _ = await interactionService.PromptInputAsync("Text <strong>request</strong>", "Provide **your** name.\r\n\r\nLorem ipsum dolor sit amet, consectetur adipiscing elit. Fusce id massa arcu. Morbi ac risus eget augue venenatis hendrerit. Morbi posuere, neque id efficitur ultrices, velit augue suscipit ante, vitae lacinia elit risus nec dui.\r\n\r\nFor more information about the `IInteractionService`, see https://learn.microsoft.com.", inputHasMarkdown, new InputsDialogInteractionOptions { EnableMessageMarkdown = true });

                return CommandResults.Success();
            })
            .WithCommand("long-content-interaction", "Long content interactions", executeCommand: async commandContext =>
            {
                var interactionService = commandContext.ServiceProvider.GetRequiredService<IInteractionService>();

                var inputHasMarkdown = new InteractionInput { Name = "Name", Label = "<strong>Name</strong>", InputType = InputType.Text, Placeholder = "Enter <strong>your</strong> name.", Description = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Fusce id massa arcu. Morbi ac risus eget augue venenatis hendrerit. Morbi posuere, **neque id** efficitur ultrices, velit augue suscipit ante, vitae lacinia elit risus nec dui.", EnableDescriptionMarkdown = true };
                var choiceWithLongContent = new InteractionInput
                {
                    Name = "Choice",
                    InputType = InputType.Choice,
                    Label = "Choice with long content",
                    Placeholder = "Select a value. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Fusce id massa arcu. Morbi ac risus eget augue venenatis hendrerit. Morbi posuere, **neque id** efficitur ultrices, velit augue suscipit ante, vitae lacinia elit risus nec dui.",
                    Options = [
                        KeyValuePair.Create("option1", "Option 1 - Lorem ipsum dolor sit amet, consectetur adipiscing elit. Fusce id massa arcu. Morbi ac risus eget augue venenatis hendrerit. Morbi posuere, **neque id** efficitur ultrices, velit augue suscipit ante, vitae lacinia elit risus nec dui."),
                        KeyValuePair.Create("option2", "Option 2 - Lorem ipsum dolor sit amet, consectetur adipiscing elit. Fusce id massa arcu. Morbi ac risus eget augue venenatis hendrerit. Morbi posuere, **neque id** efficitur ultrices, velit augue suscipit ante, vitae lacinia elit risus nec dui.")
                    ]
                };
                var choiceCustomOptionsWithLongContent = new InteractionInput
                {
                    Name = "Combobox",
                    InputType = InputType.Choice,
                    Label = "Choice with long content",
                    AllowCustomChoice = true,
                    Placeholder = "Select a value. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Fusce id massa arcu. Morbi ac risus eget augue venenatis hendrerit. Morbi posuere, **neque id** efficitur ultrices, velit augue suscipit ante, vitae lacinia elit risus nec dui.",
                    Options = [
                        KeyValuePair.Create("option1", "Option 1 - Lorem ipsum dolor sit amet, consectetur adipiscing elit. Fusce id massa arcu. Morbi ac risus eget augue venenatis hendrerit. Morbi posuere, **neque id** efficitur ultrices, velit augue suscipit ante, vitae lacinia elit risus nec dui."),
                        KeyValuePair.Create("option2", "Option 2 - Lorem ipsum dolor sit amet, consectetur adipiscing elit. Fusce id massa arcu. Morbi ac risus eget augue venenatis hendrerit. Morbi posuere, **neque id** efficitur ultrices, velit augue suscipit ante, vitae lacinia elit risus nec dui.")
                    ]
                };

                _ = await interactionService.PromptInputsAsync(
                    "Text <strong>request</strong>",
                    "Provide **your** name. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Fusce id massa arcu. Morbi ac risus eget augue venenatis hendrerit. Morbi posuere, neque id efficitur ultrices, velit augue suscipit ante, vitae lacinia elit risus nec dui. For more information about the `IInteractionService`, see https://learn.microsoft.com.",
                    [inputHasMarkdown, choiceWithLongContent, choiceCustomOptionsWithLongContent],
                    new InputsDialogInteractionOptions { EnableMessageMarkdown = true });

                return CommandResults.Success();
            })
            .WithCommand("value-interaction", "Value interactions", executeCommand: async commandContext =>
            {
                var interactionService = commandContext.ServiceProvider.GetRequiredService<IInteractionService>();
                var result = await interactionService.PromptInputAsync(
                    title: "Text request",
                    message: "Provide your name",
                    inputLabel: "Name",
                    placeHolder: "Enter your name",
                    options: new InputsDialogInteractionOptions
                    {
                        ValidationCallback = context =>
                        {
                            var input = context.Inputs[0];
                            if (!string.IsNullOrEmpty(input.Value) && input.Value.Length < 3)
                            {
                                context.AddValidationError(input, "Name must be at least 3 characters long.");
                            }
                            return Task.CompletedTask;
                        }
                    },
                    cancellationToken: commandContext.CancellationToken);

                if (result.Canceled)
                {
                    return CommandResults.Failure("Canceled");
                }

                var resourceLoggerService = commandContext.ServiceProvider.GetRequiredService<ResourceLoggerService>();
                var logger = resourceLoggerService.GetLogger(commandContext.ResourceName);

                var input = result.Data;
                logger.LogInformation("Input: {Name} = {Value}", input.Name, input.Value);

                return CommandResults.Success();
            })
            .WithCommand("choice-no-placeholder", "Choice with no placeholder", executeCommand: async commandContext =>
            {
                var interactionService = commandContext.ServiceProvider.GetRequiredService<IInteractionService>();
                var dinnerInput = new InteractionInput
                {
                    Name = "Dinner",
                    InputType = InputType.Choice,
                    Label = "Dinner",
                    Required = true,
                    Options =
                    [
                        KeyValuePair.Create("pizza", "Pizza"),
                        KeyValuePair.Create("fried-chicken", "Fried chicken"),
                        KeyValuePair.Create("burger", "Burger")
                    ]
                };
                var requirementsInput = new InteractionInput
                {
                    Name = "Requirements",
                    InputType = InputType.Choice,
                    Label = "Requirements",
                    AllowCustomChoice = true,
                    Options =
                    [
                        KeyValuePair.Create("vegetarian", "Vegetarian"),
                        KeyValuePair.Create("vegan", "Vegan")
                    ]
                };
                var result = await interactionService.PromptInputsAsync(
                    title: "Text request",
                    message: "Provide your name",
                    inputs: [
                        dinnerInput,
                       requirementsInput
                    ],
                    cancellationToken: commandContext.CancellationToken);

                if (result.Canceled)
                {
                    return CommandResults.Failure("Canceled");
                }

                var resourceLoggerService = commandContext.ServiceProvider.GetRequiredService<ResourceLoggerService>();
                var logger = resourceLoggerService.GetLogger(commandContext.ResourceName);

                foreach (var updatedInput in result.Data)
                {
                    logger.LogInformation("Input: {Label} = {Value}", updatedInput.Label, updatedInput.Value);
                }

                return CommandResults.Success();
            })
            .WithCommand("input-interaction", "Input interactions", executeCommand: async commandContext =>
            {
                var interactionService = commandContext.ServiceProvider.GetRequiredService<IInteractionService>();
                var dinnerInput = new InteractionInput
                {
                    Name = "Dinner",
                    InputType = InputType.Choice,
                    Label = "Dinner",
                    Placeholder = "Select dinner",
                    Required = true,
                    Options =
                    [
                        KeyValuePair.Create("pizza", "Pizza"),
                        KeyValuePair.Create("fried-chicken", "Fried chicken"),
                        KeyValuePair.Create("burger", "Burger"),
                        KeyValuePair.Create("salmon", "Salmon"),
                        KeyValuePair.Create("chicken-pie", "Chicken pie"),
                        KeyValuePair.Create("sushi", "Sushi"),
                        KeyValuePair.Create("tacos", "Tacos"),
                        KeyValuePair.Create("pasta", "Pasta"),
                        KeyValuePair.Create("salad", "Salad"),
                        KeyValuePair.Create("steak", "Steak"),
                        KeyValuePair.Create("vegetarian", "Vegetarian"),
                        KeyValuePair.Create("sausage", "Sausage"),
                        KeyValuePair.Create("lasagne", "Lasagne"),
                        KeyValuePair.Create("fish-pie", "Fish pie"),
                        KeyValuePair.Create("soup", "Soup"),
                        KeyValuePair.Create("beef-stew", "Beef stew"),
                        KeyValuePair.Create("welsh-pie", "Llanfair­pwllgwyngyll­gogery­chwyrn­drobwll­llan­tysilio­gogo­goch pie"),
                    ]
                };
                var requirementsInput = new InteractionInput
                {
                    Name = "Requirements",
                    InputType = InputType.Choice,
                    Label = "Requirements",
                    Placeholder = "Select requirements",
                    AllowCustomChoice = true,
                    Options =
                    [
                        KeyValuePair.Create("vegetarian", "Vegetarian"),
                        KeyValuePair.Create("vegan", "Vegan")
                    ]
                };
                var numberOfPeopleInput = new InteractionInput { Name = "NumberOfPeople", InputType = InputType.Number, Label = "Number of people", Placeholder = "Enter number of people", Value = "2", Required = true };
                var inputs = new List<InteractionInput>
                {
                    new InteractionInput { Name = "Name", InputType = InputType.Text, Label = "Name", Placeholder = "Enter name", Required = true, MaxLength = 50 },
                    new InteractionInput { Name = "Password", InputType = InputType.SecretText, Label = "Password", Placeholder = "Enter password", Required = true, MaxLength = 20 },
                    dinnerInput,
                    numberOfPeopleInput,
                    requirementsInput,
                    new InteractionInput { Name = "RememberMe", InputType = InputType.Boolean, Label = "Remember me", Placeholder = "What does this do?", Required = true },
                };
                var result = await interactionService.PromptInputsAsync(
                    "Input request",
                    "Provide your name",
                    inputs,
                    options: new InputsDialogInteractionOptions
                    {
                        ValidationCallback = context =>
                        {
                            if (dinnerInput.Value == "steak" && int.TryParse(numberOfPeopleInput.Value, CultureInfo.InvariantCulture, out var i) && i > 4)
                            {
                                context.AddValidationError(numberOfPeopleInput, "Number of people can't be greater than 4 when eating steak.");
                            }
                            return Task.CompletedTask;
                        }
                    },
                    cancellationToken: commandContext.CancellationToken);

                if (result.Canceled)
                {
                    return CommandResults.Failure("Canceled");
                }

                var resourceLoggerService = commandContext.ServiceProvider.GetRequiredService<ResourceLoggerService>();
                var logger = resourceLoggerService.GetLogger(commandContext.ResourceName);

                foreach (var updatedInput in result.Data)
                {
                    logger.LogInformation("Input: {Name} = {Value}", updatedInput.Name, updatedInput.Value);
                }

                return CommandResults.Success();
            })
            .WithCommand("choice-interaction", "Choice interactions", executeCommand: async commandContext =>
            {
                var interactionService = commandContext.ServiceProvider.GetRequiredService<IInteractionService>();
                var predefinedOptionsInput = new InteractionInput
                {
                    Name = "PredefinedOptions",
                    InputType = InputType.Choice,
                    Placeholder = "Placeholder!",
                    Required = true,
                    Options = [
                        KeyValuePair.Create("option1", "Option 1"),
                        KeyValuePair.Create("option2", "Option 2"),
                        KeyValuePair.Create("option3", "Option 3")
                    ]
                };
                var customChoiceInput = new InteractionInput
                {
                    Name = "CustomChoice",
                    InputType = InputType.Choice,
                    Label = "Custom choice",
                    Placeholder = "Placeholder!",
                    AllowCustomChoice = true,
                    Required = true,
                    DynamicLoading = new InputLoadOptions
                    {
                        LoadCallback = async (context) =>
                        {
                            await Task.Delay(5000, context.CancellationToken);

                            // Simulate loading options from a database or web service.
                            context.Input.Options = [
                                KeyValuePair.Create("option1", "Option 1"),
                                KeyValuePair.Create("option2", "Option 2"),
                                KeyValuePair.Create("option3", "Option 3")
                            ];
                        }
                    }
                };
                var sharedDynamicOptions = new InputLoadOptions
                {
                    LoadCallback = async (context) =>
                    {
                        var dependsOnInput = context.AllInputs["PredefinedOptions"];

                        if (!string.IsNullOrEmpty(dependsOnInput.Value))
                        {
                            await Task.Delay(5000, context.CancellationToken);
                            var list = new List<KeyValuePair<string, string>>();
                            for (var i = 0; i < 3; i++)
                            {
                                list.Add(KeyValuePair.Create($"option{i}-{dependsOnInput.Value}", $"Option {i} - {dependsOnInput.Value}"));
                            }

                            context.Input.Disabled = false;
                            context.Input.Options = list;
                        }
                        else
                        {
                            context.Input.Disabled = true;
                        }
                    },
                    DependsOnInputs = ["PredefinedOptions"]
                };
                var dynamicInput = new InteractionInput
                {
                    Name = "Dynamic",
                    InputType = InputType.Choice,
                    Label = "Dynamic",
                    Placeholder = "Select dynamic value",
                    Required = true,
                    Disabled = true,
                    DynamicLoading = sharedDynamicOptions
                };
                var dynamicCustomChoiceInput = new InteractionInput
                {
                    Name = "DynamicCustomChoice",
                    InputType = InputType.Choice,
                    Label = "Dynamic custom choice",
                    Placeholder = "Select dynamic value",
                    AllowCustomChoice = true,
                    Required = true,
                    Disabled = true,
                    DynamicLoading = sharedDynamicOptions
                };
                var dynamicTextInput = new InteractionInput
                {
                    Name = "DynamicTextInput",
                    InputType = InputType.Text,
                    Placeholder = "Placeholder!",
                    Required = true,
                    Disabled = true,
                    DynamicLoading = new InputLoadOptions
                    {
                        DependsOnInputs = ["Dynamic"],
                        LoadCallback = async (context) =>
                        {
                            await Task.Delay(5000, context.CancellationToken);
                            var dependsOnInput = context.AllInputs["Dynamic"];
                            context.Input.Value = dependsOnInput.Value;
                        }
                    }
                };

                var inputs = new List<InteractionInput>
               {
                   customChoiceInput,
                   predefinedOptionsInput,
                   dynamicInput,
                   dynamicCustomChoiceInput,
                   dynamicTextInput
               };
                var result = await interactionService.PromptInputsAsync(
                    "Choice inputs",
                    "Range of choice inputs",
                    inputs,
                    options: new InputsDialogInteractionOptions
                    {
                        ValidationCallback = context =>
                        {
                            return Task.CompletedTask;
                        }
                    },
                    cancellationToken: commandContext.CancellationToken);

                if (result.Canceled)
                {
                    return CommandResults.Failure("Canceled");
                }

                var resourceLoggerService = commandContext.ServiceProvider.GetRequiredService<ResourceLoggerService>();
                var logger = resourceLoggerService.GetLogger(commandContext.ResourceName);

                foreach (var updatedInput in result.Data)
                {
                    logger.LogInformation("Input: {Name} = {Value}", updatedInput.Name, updatedInput.Value);
                }

                return CommandResults.Success();
            })
            .WithCommand("dynamic-error", "Dynamic error", executeCommand: async commandContext =>
            {
                var interactionService = commandContext.ServiceProvider.GetRequiredService<IInteractionService>();
                var predefinedOptionsInput = new InteractionInput
                {
                    Name = "PredefinedOptions",
                    InputType = InputType.Choice,
                    Placeholder = "Placeholder!",
                    Required = true,
                    Options = [
                        KeyValuePair.Create("option1", "Option 1"),
                        KeyValuePair.Create("option2", "Option 2"),
                        KeyValuePair.Create("option3", "Option 3")
                    ]
                };
                var customChoiceInput = new InteractionInput
                {
                    Name = "CustomChoice",
                    InputType = InputType.Choice,
                    Label = "Custom choice",
                    Placeholder = "Placeholder!",
                    AllowCustomChoice = true,
                    Required = true,
                    DynamicLoading = new InputLoadOptions
                    {
                        LoadCallback = async (context) =>
                        {
                            await Task.Delay(1000, context.CancellationToken);

                            throw new InvalidOperationException("Error!");
                        }
                    }
                };
                var dynamicInput = new InteractionInput
                {
                    Name = "Dynamic",
                    InputType = InputType.Choice,
                    Label = "Dynamic",
                    Placeholder = "Select dynamic value",
                    Required = true,
                    DynamicLoading = new InputLoadOptions
                    {
                        LoadCallback = async (context) =>
                        {
                            await Task.Delay(1000, context.CancellationToken);

                            var dependsOnInput = context.AllInputs["PredefinedOptions"];

                            if (dependsOnInput.Value == "option1")
                            {
                                throw new InvalidOperationException("Error!");
                            }

                            var list = new List<KeyValuePair<string, string>>();
                            for (var i = 0; i < 3; i++)
                            {
                                list.Add(KeyValuePair.Create($"option{i}-{dependsOnInput.Value}", $"Option {i} - {dependsOnInput.Value}"));
                            }

                            context.Input.Options = list;
                        },
                        DependsOnInputs = ["PredefinedOptions"]
                    }
                };

                var inputs = new List<InteractionInput>
               {
                   predefinedOptionsInput,
                   customChoiceInput,
                   dynamicInput
               };
                var result = await interactionService.PromptInputsAsync(
                    "Choice inputs",
                    "Range of choice inputs",
                    inputs,
                    options: new InputsDialogInteractionOptions
                    {
                        ValidationCallback = context =>
                        {
                            return Task.CompletedTask;
                        }
                    },
                    cancellationToken: commandContext.CancellationToken);

                if (result.Canceled)
                {
                    return CommandResults.Failure("Canceled");
                }

                var resourceLoggerService = commandContext.ServiceProvider.GetRequiredService<ResourceLoggerService>();
                var logger = resourceLoggerService.GetLogger(commandContext.ResourceName);

                foreach (var updatedInput in result.Data)
                {
                    logger.LogInformation("Input: {Name} = {Value}", updatedInput.Name, updatedInput.Value);
                }

                return CommandResults.Success();
            })
            .WithCommand("dismiss-interaction", "Dismiss interaction tests", executeCommand: commandContext =>
            {
                var interactionService = commandContext.ServiceProvider.GetRequiredService<IInteractionService>();

                RunInteractionWithDismissValues(nameof(IInteractionService.PromptNotificationAsync), (showDismiss, title) =>
                {
                    return interactionService.PromptNotificationAsync(
                        title: title,
                        message: string.Empty,
                        options: new NotificationInteractionOptions { ShowDismiss = showDismiss },
                        cancellationToken: commandContext.CancellationToken);
                });
                RunInteractionWithDismissValues(nameof(IInteractionService.PromptConfirmationAsync), (showDismiss, title) =>
                {
                    return interactionService.PromptConfirmationAsync(
                        title: title,
                        message: string.Empty,
                        options: new MessageBoxInteractionOptions { ShowDismiss = showDismiss },
                        cancellationToken: commandContext.CancellationToken);
                });
                RunInteractionWithDismissValues(nameof(IInteractionService.PromptMessageBoxAsync), (showDismiss, title) =>
                {
                    return interactionService.PromptMessageBoxAsync(
                        title: title,
                        message: string.Empty,
                        options: new MessageBoxInteractionOptions { ShowDismiss = showDismiss },
                        cancellationToken: commandContext.CancellationToken);
                });
                RunInteractionWithDismissValues(nameof(IInteractionService.PromptInputAsync), (showDismiss, title) =>
                {
                    return interactionService.PromptInputAsync(
                        title: title,
                        message: string.Empty,
                        inputLabel: "Input",
                        placeHolder: "Enter input",
                        options: new InputsDialogInteractionOptions { ShowDismiss = showDismiss },
                        cancellationToken: commandContext.CancellationToken);
                });

                return Task.FromResult(CommandResults.Success());
            })
            .WithCommand("many-values", "Many values", executeCommand: async commandContext =>
            {
                var interactionService = commandContext.ServiceProvider.GetRequiredService<IInteractionService>();
                var inputs = new List<InteractionInput>();
                for (var i = 0; i < 50; i++)
                {
                    inputs.Add(new InteractionInput
                    {
                        Name = $"Input{i + 1}",
                        InputType = InputType.Text,
                        Label = $"Input {i + 1}",
                        Placeholder = $"Enter input {i + 1}"
                    });
                }
                var result = await interactionService.PromptInputsAsync(
                    title: "Text request",
                    message: "Provide your name",
                    inputs: inputs,
                    cancellationToken: commandContext.CancellationToken);

                if (result.Canceled)
                {
                    return CommandResults.Failure("Canceled");
                }

                var resourceLoggerService = commandContext.ServiceProvider.GetRequiredService<ResourceLoggerService>();
                var logger = resourceLoggerService.GetLogger(commandContext.ResourceName);

                foreach (var input in result.Data)
                {
                    logger.LogInformation("Input: {Name} = {Value}", input.Name, input.Value);
                }

                return CommandResults.Success();
            })
            .WithCommand("azure-provisioning-simulation", "Azure provisioning simulation", executeCommand: async commandContext =>
            {
                var interactionService = commandContext.ServiceProvider.GetRequiredService<IInteractionService>();

                var tenantInput = new InteractionInput
                {
                    Name = "Tenant",
                    InputType = InputType.Choice,
                    Label = "Tenant ID",
                    Required = true,
                    AllowCustomChoice = true,
                    Placeholder = "Select tenant ID",
                    DynamicLoading = new InputLoadOptions
                    {
                        LoadCallback = async (context) =>
                        {
                            // Simulate fetching tenants from Azure.
                            await Task.Delay(1500, context.CancellationToken);
                            context.Input.Options = [
                                KeyValuePair.Create("11111111-1111-1111-1111-111111111111", "Contoso Corp (11111111-1111-1111-1111-111111111111)"),
                                KeyValuePair.Create("22222222-2222-2222-2222-222222222222", "Fabrikam Inc (22222222-2222-2222-2222-222222222222)"),
                                KeyValuePair.Create("33333333-3333-3333-3333-333333333333", "Northwind Traders (33333333-3333-3333-3333-333333333333)")
                            ];
                        }
                    }
                };

                var subscriptionInput = new InteractionInput
                {
                    Name = "SubscriptionId",
                    InputType = InputType.Choice,
                    Label = "Subscription ID",
                    Required = true,
                    AllowCustomChoice = true,
                    Placeholder = "Select subscription ID",
                    Disabled = true,
                    DynamicLoading = new InputLoadOptions
                    {
                        LoadCallback = async (context) =>
                        {
                            // Simulate fetching subscriptions for the selected tenant.
                            await Task.Delay(2000, context.CancellationToken);

                            var tenantId = context.AllInputs["Tenant"].Value ?? string.Empty;
                            context.Input.Disabled = false;

                            if (tenantId.StartsWith("1", StringComparison.Ordinal))
                            {
                                context.Input.Options = [
                                    KeyValuePair.Create("aaaa1111-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Dev/Test Subscription (aaaa1111)"),
                                    KeyValuePair.Create("bbbb2222-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Production Subscription (bbbb2222)")
                                ];
                            }
                            else
                            {
                                context.Input.Options = [
                                    KeyValuePair.Create("cccc3333-cccc-cccc-cccc-cccccccccccc", "Sandbox Subscription (cccc3333)"),
                                    KeyValuePair.Create("dddd4444-dddd-dddd-dddd-dddddddddddd", "Staging Subscription (dddd4444)"),
                                    KeyValuePair.Create("eeee5555-eeee-eeee-eeee-eeeeeeeeeeee", "Production Subscription (eeee5555)")
                                ];
                            }
                        },
                        DependsOnInputs = ["Tenant"]
                    }
                };

                var resourceGroupInput = new InteractionInput
                {
                    Name = "ResourceGroup",
                    InputType = InputType.Choice,
                    Label = "Resource group",
                    Placeholder = "Select or enter resource group",
                    AllowCustomChoice = true,
                    Disabled = true,
                    DynamicLoading = new InputLoadOptions
                    {
                        LoadCallback = async (context) =>
                        {
                            // Simulate fetching resource groups for the selected subscription.
                            await Task.Delay(1000, context.CancellationToken);

                            context.Input.Options = [
                                KeyValuePair.Create("rg-aspire-dev", "rg-aspire-dev"),
                                KeyValuePair.Create("rg-myapp-prod", "rg-myapp-prod"),
                                KeyValuePair.Create("rg-shared-services", "rg-shared-services")
                            ];
                            context.Input.Disabled = false;

                            if (string.IsNullOrEmpty(context.Input.Value))
                            {
                                context.Input.Value = "rg-aspire-stress";
                            }
                        },
                        DependsOnInputs = ["SubscriptionId"]
                    }
                };

                var locationInput = new InteractionInput
                {
                    Name = "Location",
                    InputType = InputType.Choice,
                    Label = "Location",
                    Placeholder = "Select location",
                    Required = true,
                    Disabled = true,
                    DynamicLoading = new InputLoadOptions
                    {
                        LoadCallback = async (context) =>
                        {
                            var resourceGroupName = context.AllInputs["ResourceGroup"].Value ?? string.Empty;

                            // If an existing resource group is selected, lock the location.
                            if (resourceGroupName is "rg-aspire-dev" or "rg-myapp-prod" or "rg-shared-services")
                            {
                                await Task.Delay(6000, context.CancellationToken);
                                var existingLocation = resourceGroupName switch
                                {
                                    "rg-aspire-dev" => "westus2-rg",
                                    "rg-myapp-prod" => "eastus-rg",
                                    "rg-shared-services" => "centralus-rg",
                                    _ => "westus2-rg"
                                };
                                context.Input.Options = [KeyValuePair.Create(existingLocation, existingLocation)];
                                context.Input.Value = existingLocation;
                                context.Input.Disabled = true;
                                return;
                            }

                            // For new resource groups, simulate loading all available locations.
                            await Task.Delay(3000, context.CancellationToken);
                            context.Input.Options = [
                                KeyValuePair.Create("eastus", "East US"),
                                KeyValuePair.Create("eastus2", "East US 2"),
                                KeyValuePair.Create("westus2", "West US 2"),
                                KeyValuePair.Create("westus3", "West US 3"),
                                KeyValuePair.Create("centralus", "Central US"),
                                KeyValuePair.Create("northeurope", "North Europe"),
                                KeyValuePair.Create("westeurope", "West Europe"),
                                KeyValuePair.Create("southeastasia", "Southeast Asia"),
                                KeyValuePair.Create("australiaeast", "Australia East"),
                                KeyValuePair.Create("japaneast", "Japan East")
                            ];
                            context.Input.Disabled = false;
                        },
                        DependsOnInputs = ["SubscriptionId", "ResourceGroup"]
                    }
                };

                var inputs = new List<InteractionInput> { tenantInput, subscriptionInput, resourceGroupInput, locationInput };

                var result = await interactionService.PromptInputsAsync(
                    "Azure provisioning",
                    "The model contains Azure resources that require an Azure Subscription.\n\nTo learn more, see the [Azure provisioning docs](https://aka.ms/dotnet/aspire/azure/provisioning).",
                    inputs,
                    new InputsDialogInteractionOptions
                    {
                        EnableMessageMarkdown = true,
                        ValidationCallback = (validationContext) =>
                        {
                            var tenant = validationContext.Inputs["Tenant"];
                            if (!string.IsNullOrWhiteSpace(tenant.Value) && !Guid.TryParse(tenant.Value, out _))
                            {
                                validationContext.AddValidationError(tenant, "Tenant ID must be a valid GUID.");
                            }

                            var subscription = validationContext.Inputs["SubscriptionId"];
                            if (!string.IsNullOrWhiteSpace(subscription.Value) && !Guid.TryParse(subscription.Value, out _))
                            {
                                validationContext.AddValidationError(subscription, "Subscription ID must be a valid GUID.");
                            }

                            return Task.CompletedTask;
                        }
                    },
                    commandContext.CancellationToken);

                if (result.Canceled)
                {
                    return CommandResults.Canceled();
                }

                var resourceLoggerService = commandContext.ServiceProvider.GetRequiredService<ResourceLoggerService>();
                var logger = resourceLoggerService.GetLogger(commandContext.ResourceName);

                foreach (var input in result.Data)
                {
                    logger.LogInformation("Azure provisioning: {Name} = {Value}", input.Name, input.Value);
                }

                return CommandResults.Success();
            }, new CommandOptions
            {
                Description = "Simulates the Azure provisioning interaction inputs prompt with pretend data.",
                IconName = "CloudArrowUp",
                IconVariant = IconVariant.Filled
            });

        return resource;
    }

    private static void RunInteractionWithDismissValues(string title, Func<bool?, string, Task> action)
    {
        // Don't wait for interactions to complete, i.e. await tasks.
        _ = action(null, $"{title} - ShowDismiss = null");
        _ = action(true, $"{title} - ShowDismiss = true");
        _ = action(false, $"{title} - ShowDismiss = false");
    }
}

#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
