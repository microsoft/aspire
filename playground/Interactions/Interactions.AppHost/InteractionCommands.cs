// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

internal static class InteractionCommands
{
    public static IResourceBuilder<T> WithInteractionCommands<T>(this IResourceBuilder<T> resource) where T : IResource
    {
        resource
            .WithCommand("filechooser-interaction", "File chooser", executeCommand: async commandContext =>
            {
                var interactionService = commandContext.ServiceProvider.GetRequiredService<IInteractionService>();
                var resourceLoggerService = commandContext.ServiceProvider.GetRequiredService<ResourceLoggerService>();
                var logger = resourceLoggerService.GetLogger(commandContext.ResourceName);

                var result = await interactionService.PromptInputAsync(
                    "Select a file",
                    "Choose a file to process.",
                    new InteractionInput
                    {
                        Name = "file",
                        Label = "File",
                        InputType = InputType.FileChooser,
                        Required = true,
                        Description = "Select a file using the file picker."
                    },
                    cancellationToken: commandContext.CancellationToken);

                if (result.Canceled)
                {
                    return CommandResults.Failure("Canceled");
                }

                logger.LogInformation("Selected file: {Value}", result.Data.Value);

                _ = interactionService.PromptMessageBoxAsync(
                    "File selected",
                    $"You selected: {result.Data.Value}",
                    new MessageBoxInteractionOptions { Intent = MessageIntent.Success });

                return CommandResults.Success();
            })
            .WithCommand("filechooser-multi-interaction", "File chooser with other inputs", executeCommand: async commandContext =>
            {
                var interactionService = commandContext.ServiceProvider.GetRequiredService<IInteractionService>();
                var resourceLoggerService = commandContext.ServiceProvider.GetRequiredService<ResourceLoggerService>();
                var logger = resourceLoggerService.GetLogger(commandContext.ResourceName);

                var result = await interactionService.PromptInputsAsync(
                    "Import configuration",
                    "Select a configuration file and provide a name.",
                    [
                        new InteractionInput
                        {
                            Name = "config_name",
                            Label = "Configuration name",
                            InputType = InputType.Text,
                            Required = true,
                            Placeholder = "Enter a name for this configuration"
                        },
                        new InteractionInput
                        {
                            Name = "config_file",
                            Label = "Configuration file",
                            InputType = InputType.FileChooser,
                            Required = true,
                            Description = "Select the configuration file to import."
                        },
                        new InteractionInput
                        {
                            Name = "overwrite",
                            Label = "Overwrite existing",
                            InputType = InputType.Boolean,
                            Description = "Overwrite if a configuration with the same name already exists."
                        }
                    ],
                    cancellationToken: commandContext.CancellationToken);

                if (result.Canceled)
                {
                    return CommandResults.Failure("Canceled");
                }

                foreach (var input in result.Data)
                {
                    logger.LogInformation("Input: {Name} = {Value}", input.Name, input.Value);
                }

                return CommandResults.Success();
            })
            .WithCommand("filechooser-optional", "File chooser (optional)", executeCommand: async commandContext =>
            {
                var interactionService = commandContext.ServiceProvider.GetRequiredService<IInteractionService>();
                var resourceLoggerService = commandContext.ServiceProvider.GetRequiredService<ResourceLoggerService>();
                var logger = resourceLoggerService.GetLogger(commandContext.ResourceName);

                var result = await interactionService.PromptInputAsync(
                    "Select a file (optional)",
                    "Optionally choose a file. You can leave this empty.",
                    new InteractionInput
                    {
                        Name = "optional_file",
                        Label = "File (optional)",
                        InputType = InputType.FileChooser,
                        Required = false,
                        Placeholder = "No file selected"
                    },
                    cancellationToken: commandContext.CancellationToken);

                if (result.Canceled)
                {
                    return CommandResults.Failure("Canceled");
                }

                if (string.IsNullOrEmpty(result.Data.Value))
                {
                    logger.LogInformation("No file was selected.");
                }
                else
                {
                    logger.LogInformation("Selected file: {Value}", result.Data.Value);
                }

                _ = interactionService.PromptMessageBoxAsync(
                    "Result",
                    string.IsNullOrEmpty(result.Data.Value)
                        ? "No file was selected."
                        : $"You selected: {result.Data.Value}",
                    new MessageBoxInteractionOptions { Intent = MessageIntent.Information });

                return CommandResults.Success();
            })
            .WithCommand("filechooser-display-content", "File chooser (display content)", executeCommand: async commandContext =>
            {
                var interactionService = commandContext.ServiceProvider.GetRequiredService<IInteractionService>();
                var resourceLoggerService = commandContext.ServiceProvider.GetRequiredService<ResourceLoggerService>();
                var logger = resourceLoggerService.GetLogger(commandContext.ResourceName);

                var result = await interactionService.PromptInputAsync(
                    "View file content",
                    "Select a text file to display its content.",
                    new InteractionInput
                    {
                        Name = "file",
                        Label = "Text file",
                        InputType = InputType.FileChooser,
                        Required = true,
                        Description = "Select a text file (e.g. .txt, .json, .xml, .cs)."
                    },
                    cancellationToken: commandContext.CancellationToken);

                if (result.Canceled)
                {
                    return CommandResults.Failure("Canceled");
                }

                var content = result.Data.Value ?? string.Empty;

                logger.LogInformation("File content ({Length} characters):\n{Content}", content.Length, content);

                // Show a truncated preview in a message box.
                const int maxPreviewLength = 2000;
                var preview = content.Length > maxPreviewLength
                    ? content[..maxPreviewLength] + "\n\n... (truncated)"
                    : content;

                _ = interactionService.PromptMessageBoxAsync(
                    "File content",
                    $"```\n{preview}\n```",
                    new MessageBoxInteractionOptions { Intent = MessageIntent.Information, EnableMessageMarkdown = true });

                return CommandResults.Success();
            });

        return resource;
    }
}

#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
