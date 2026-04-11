// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.DashboardService.Proto.V1;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static Aspire.Hosting.Interaction;

namespace Aspire.Hosting.Dashboard;

/// <summary>
/// Implements a gRPC service that a dashboard can consume.
/// </summary>
/// <remarks>
/// An instance of this type is created for every gRPC service call, so it may not hold onto any state
/// required beyond a single request. Longer-scoped data is stored in <see cref="DashboardServiceData"/>.
/// </remarks>
[Authorize(Policy = ResourceServiceApiKeyAuthorization.PolicyName)]
internal sealed partial class DashboardService(DashboardServiceData serviceData, IHostEnvironment hostEnvironment, IHostApplicationLifetime hostApplicationLifetime, IConfiguration configuration, ILogger<DashboardService> logger)
    : Aspire.DashboardService.Proto.V1.DashboardService.DashboardServiceBase
{
    // gRPC has a maximum receive size of 4MB. Force logs into batches to avoid exceeding receive size.
    // Protobuf sends strings as UTF8. Be conservative and assume the average character byte size is 2.
    public const int LogMaxBatchCharacters = 1024 * 1024 * 2;

    // Calls that consume or produce streams must create a linked cancellation token
    // with IHostApplicationLifetime.ApplicationStopping to ensure eager cancellation
    // of pending connections during shutdown.

    [GeneratedRegex("""^(?<name>.+?)\.?AppHost$""", RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationNameRegex();

    public override Task<ApplicationInformationResponse> GetApplicationInformation(
        ApplicationInformationRequest request,
        ServerCallContext context)
    {
        // Read the application name from configuration if available, otherwise fall back to the environment
        var applicationName = configuration["AppHost:DashboardApplicationName"] ?? hostEnvironment.ApplicationName;
        
        return Task.FromResult(new ApplicationInformationResponse
        {
            ApplicationName = ComputeApplicationName(applicationName)
        });

        static string ComputeApplicationName(string applicationName)
        {
            return ApplicationNameRegex().Match(applicationName) switch
            {
                Match { Success: true } match => match.Groups["name"].Value,
                _ => applicationName
            };
        }
    }

    public override async Task WatchInteractions(IAsyncStreamReader<WatchInteractionsRequestUpdate> requestStream, IServerStreamWriter<WatchInteractionsResponseUpdate> responseStream, ServerCallContext context)
    {
        await ExecuteAsync(
            WatchInteractionsInternal,
            context).ConfigureAwait(false);

        async Task WatchInteractionsInternal(CancellationToken cancellationToken)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var updates = serviceData.SubscribeInteractionUpdates();

            // Send
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var interaction in updates.WithCancellation(cts.Token).ConfigureAwait(false))
                    {
                        var change = new WatchInteractionsResponseUpdate();
                        change.InteractionId = interaction.InteractionId;
                        change.Title = interaction.Title;
                        if (interaction.Message != null)
                        {
                            change.Message = interaction.Message;
                        }
                        if (interaction.Options.PrimaryButtonText != null)
                        {
                            change.PrimaryButtonText = interaction.Options.PrimaryButtonText;
                        }
                        if (interaction.Options.SecondaryButtonText != null)
                        {
                            change.SecondaryButtonText = interaction.Options.SecondaryButtonText;
                        }
                        change.ShowDismiss = interaction.Options.ShowDismiss ?? true;
                        change.ShowSecondaryButton = interaction.Options.ShowSecondaryButton ?? true;
                        change.EnableMessageMarkdown = interaction.Options.EnableMessageMarkdown ?? false;

                        if (interaction.State == InteractionState.Complete)
                        {
                            change.Complete = new InteractionComplete();
                        }
                        else if (interaction.InteractionInfo is MessageBoxInteractionInfo messageBox)
                        {
                            change.MessageBox = new InteractionMessageBox();
                            change.MessageBox.Intent = MapMessageIntent(messageBox.Intent);
                        }
                        else if (interaction.InteractionInfo is NotificationInteractionInfo notification)
                        {
                            change.Notification = new InteractionNotification();
                            change.Notification.Intent = MapMessageIntent(notification.Intent);
                            if (notification.LinkText != null)
                            {
                                change.Notification.LinkText = notification.LinkText;
                            }
                            if (notification.LinkUrl != null)
                            {
                                change.Notification.LinkUrl = notification.LinkUrl;
                            }
                        }
                        else if (interaction.InteractionInfo is InputsInteractionInfo inputs)
                        {
                            change.InputsDialog = new InteractionInputsDialog();

                            // Find all the inputs that are depended on.
                            // These inputs value changing will cause the interaction to be sent to the server.
                            var updateStateOnChangeInputs = inputs.Inputs
                                .SelectMany(i => i.DynamicLoading?.DependsOnInputs ?? [])
                                .ToList();

                            var inputInstances = inputs.Inputs.Select(input =>
                            {
                                var updateStateOnChange = updateStateOnChangeInputs.Any(i => string.Equals(i, input.Name, StringComparisons.InteractionInputName));

                                var dto = new Aspire.DashboardService.Proto.V1.InteractionInput
                                {
                                    Name = input.Name,
                                    InputType = MapInputType(input.InputType),
                                    Required = input.Required,
                                    AllowCustomChoice = input.AllowCustomChoice,
                                    UpdateStateOnChange = updateStateOnChange,
                                    Disabled = input.Disabled
                                };
                                if (input.EffectiveLabel != null)
                                {
                                    dto.Label = input.EffectiveLabel;
                                }
                                if (input.Description != null)
                                {
                                    dto.Description = input.Description;
                                    dto.EnableDescriptionMarkdown = input.EnableDescriptionMarkdown;
                                }
                                if (input.Placeholder != null)
                                {
                                    dto.Placeholder = input.Placeholder;
                                }
                                if (input.Value != null)
                                {
                                    dto.Value = input.Value;
                                }
                                if (input.Options != null)
                                {
                                    dto.Options.Add(input.Options.ToDictionary());
                                }
                                if (input.DynamicLoadingState is { } providerState)
                                {
                                    dto.Loading = providerState.Loading;
                                }
                                if (input.MaxLength != null)
                                {
                                    dto.MaxLength = input.MaxLength.Value;
                                }
                                dto.ValidationErrors.AddRange(input.ValidationErrors);
                                return dto;
                            }).ToList();
                            change.InputsDialog.InputItems.AddRange(inputInstances);
                        }

                        await responseStream.WriteAsync(change, cts.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error while watching interactions.");
                }
                finally
                {
                    cts.Cancel();
                }
            }, cts.Token);

            // Receive
            try
            {
                await foreach (var request in requestStream.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    await serviceData.SendInteractionRequestAsync(request, cts.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                // Ensure the write task is cancelled if we exit the loop.
                cts.Cancel();
            }
        }
    }

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    private static Aspire.DashboardService.Proto.V1.MessageIntent MapMessageIntent(Aspire.Hosting.MessageIntent? intent)
    {
        if (intent is null)
        {
            return Aspire.DashboardService.Proto.V1.MessageIntent.None;
        }

        return intent.Value switch
        {
            Aspire.Hosting.MessageIntent.Success => Aspire.DashboardService.Proto.V1.MessageIntent.Success,
            Aspire.Hosting.MessageIntent.Warning => Aspire.DashboardService.Proto.V1.MessageIntent.Warning,
            Aspire.Hosting.MessageIntent.Error => Aspire.DashboardService.Proto.V1.MessageIntent.Error,
            Aspire.Hosting.MessageIntent.Information => Aspire.DashboardService.Proto.V1.MessageIntent.Information,
            Aspire.Hosting.MessageIntent.Confirmation => Aspire.DashboardService.Proto.V1.MessageIntent.Confirmation,
            _ => Aspire.DashboardService.Proto.V1.MessageIntent.None,
        };
    }

    private static Aspire.DashboardService.Proto.V1.InputType MapInputType(Aspire.Hosting.InputType inputType)
    {
        return inputType switch
        {
            Aspire.Hosting.InputType.Text => Aspire.DashboardService.Proto.V1.InputType.Text,
            Aspire.Hosting.InputType.SecretText => Aspire.DashboardService.Proto.V1.InputType.SecretText,
            Aspire.Hosting.InputType.Choice => Aspire.DashboardService.Proto.V1.InputType.Choice,
            Aspire.Hosting.InputType.Boolean => Aspire.DashboardService.Proto.V1.InputType.Boolean,
            Aspire.Hosting.InputType.Number => Aspire.DashboardService.Proto.V1.InputType.Number,
            _ => throw new InvalidOperationException($"Unexpected input type: {inputType}"),
        };
    }

    public static Aspire.Hosting.InputType MapInputType(Aspire.DashboardService.Proto.V1.InputType inputType)
    {
        return inputType switch
        {
            Aspire.DashboardService.Proto.V1.InputType.Text => InputType.Text,
            Aspire.DashboardService.Proto.V1.InputType.SecretText => InputType.SecretText,
            Aspire.DashboardService.Proto.V1.InputType.Choice => InputType.Choice,
            Aspire.DashboardService.Proto.V1.InputType.Boolean => InputType.Boolean,
            Aspire.DashboardService.Proto.V1.InputType.Number => InputType.Number,
            _ => throw new InvalidOperationException($"Unexpected input type: {inputType}"),
        };
    }

#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

    public override async Task WatchResources(
        WatchResourcesRequest request,
        IServerStreamWriter<WatchResourcesUpdate> responseStream,
        ServerCallContext context)
    {
        await ExecuteAsync(
            WatchResourcesInternal,
            context).ConfigureAwait(false);

        async Task WatchResourcesInternal(CancellationToken cancellationToken)
        {
            var (initialData, updates) = serviceData.SubscribeResources();

            var data = new InitialResourceData();

            foreach (var resource in initialData)
            {
                data.Resources.Add(Resource.FromSnapshot(resource));
            }

            await responseStream.WriteAsync(new() { InitialData = data }, cancellationToken).ConfigureAwait(false);

            await foreach (var batch in updates.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var changes = new WatchResourcesChanges();

                foreach (var update in batch)
                {
                    var change = new WatchResourcesChange();

                    if (update.ChangeType is ResourceSnapshotChangeType.Upsert)
                    {
                        change.Upsert = Resource.FromSnapshot(update.Resource);
                    }
                    else if (update.ChangeType is ResourceSnapshotChangeType.Delete)
                    {
                        change.Delete = new() { ResourceName = update.Resource.Name, ResourceType = update.Resource.ResourceType };
                    }
                    else
                    {
                        throw new FormatException($"Unexpected {nameof(ResourceSnapshotChange)} type: {update.ChangeType}");
                    }

                    changes.Value.Add(change);
                }

                await responseStream.WriteAsync(new() { Changes = changes }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public override async Task WatchResourceConsoleLogs(
        WatchResourceConsoleLogsRequest request,
        IServerStreamWriter<WatchResourceConsoleLogsUpdate> responseStream,
        ServerCallContext context)
    {
        await ExecuteAsync(
            cancellationToken => WatchResourceConsoleLogsInternal(request.SuppressFollow, cancellationToken),
            context).ConfigureAwait(false);

        async Task WatchResourceConsoleLogsInternal(bool suppressFollow, CancellationToken cancellationToken)
        {
            var enumerable = suppressFollow
                ? serviceData.GetConsoleLogs(request.ResourceName)
                : serviceData.SubscribeConsoleLogs(request.ResourceName);

            if (enumerable is null)
            {
                return;
            }

            await foreach (var group in enumerable.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var sentLines = 0;

                while (sentLines < group.Count)
                {
                    var update = new WatchResourceConsoleLogsUpdate();
                    var currentChars = 0;

                    foreach (var (lineNumber, content, isErrorMessage) in group.Skip(sentLines))
                    {
                        // Truncate excessively long lines.
                        var resolvedContent = content.Length > LogMaxBatchCharacters
                            ? content[..LogMaxBatchCharacters]
                            : content;

                        // Count number of characters to figure out if batch exceeds the limit.
                        // We could calculate byte size here with UTF8 encoding, but getting the exact size of the text and message
                        // would be a bit more complicated. Character count plus a conservative limit should be fine.
                        currentChars += resolvedContent.Length;

                        if (currentChars <= LogMaxBatchCharacters)
                        {
                            update.LogLines.Add(new ConsoleLogLine() { LineNumber = lineNumber, Text = resolvedContent, IsStdErr = isErrorMessage });
                            sentLines++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    await responseStream.WriteAsync(update, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    public override async Task<ResourceCommandResponse> ExecuteResourceCommand(ResourceCommandRequest request, ServerCallContext context)
    {
        var (result, message, value) = await serviceData.ExecuteCommandAsync(request.ResourceName, request.CommandName, context.CancellationToken).ConfigureAwait(false);
        var responseKind = result switch
        {
            ExecuteCommandResultType.Success => ResourceCommandResponseKind.Succeeded,
            ExecuteCommandResultType.Canceled => ResourceCommandResponseKind.Cancelled,
            ExecuteCommandResultType.Failure => ResourceCommandResponseKind.Failed,
            _ => ResourceCommandResponseKind.Undefined
        };

        var response = new ResourceCommandResponse
        {
            Kind = responseKind,
            Message = message ?? string.Empty,
        };

#pragma warning disable CS0612 // Type or member is obsolete
        response.ErrorMessage = message ?? string.Empty;
#pragma warning restore CS0612 // Type or member is obsolete

        if (value is not null)
        {
            static Aspire.DashboardService.Proto.V1.CommandResultFormat MapFormat(ApplicationModel.CommandResultFormat format) => format switch
            {
                ApplicationModel.CommandResultFormat.Text => Aspire.DashboardService.Proto.V1.CommandResultFormat.Text,
                ApplicationModel.CommandResultFormat.Json => Aspire.DashboardService.Proto.V1.CommandResultFormat.Json,
                ApplicationModel.CommandResultFormat.Markdown => Aspire.DashboardService.Proto.V1.CommandResultFormat.Markdown,
                _ => Aspire.DashboardService.Proto.V1.CommandResultFormat.None
            };

            response.Result = new ResourceCommandResult
            {
                Value = value.Value,
                Format = MapFormat(value.Format),
                DisplayImmediately = value.DisplayImmediately
            };
        }

        return response;
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> execute, ServerCallContext serverCallContext)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hostApplicationLifetime.ApplicationStopping, serverCallContext.CancellationToken);

        try
        {
            await execute(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            // Ignore cancellation and just return.
        }
        catch (IOException) when (cts.Token.IsCancellationRequested)
        {
            // Ignore cancellation and just return. Cancelled writes throw IOException.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing service method '{Method}'.", serverCallContext.Method);
            throw;
        }
    }
}
