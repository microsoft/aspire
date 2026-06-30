// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001 // IInteractionService is experimental

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Maui.Utilities;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Maui.Lifecycle;

/// <summary>
/// Validates local run-mode prerequisites before MAUI platform resources start.
/// </summary>
internal sealed class MauiPrerequisiteCheckEventSubscriber(
    IEnumerable<IMauiPrerequisiteChecker> checkers,
    IInteractionService interactionService,
    ResourceLoggerService loggerService,
    ILogger<MauiPrerequisiteCheckEventSubscriber> logger) : IDistributedApplicationEventingSubscriber
{
    private readonly ConcurrentDictionary<string, bool> _successfulChecks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<MauiPrerequisiteCheckResult>>> _inflightChecks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _shownNotifications = new(StringComparer.Ordinal);
    private readonly List<IMauiPrerequisiteChecker> _checkers = checkers.ToList();

    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        if (executionContext.IsRunMode)
        {
            eventing.Subscribe<BeforeResourceStartedEvent>(OnBeforeResourceStartedAsync);
        }

        return Task.CompletedTask;
    }

    private async Task OnBeforeResourceStartedAsync(BeforeResourceStartedEvent @event, CancellationToken cancellationToken)
    {
        if (@event.Resource is not IMauiPlatformResource)
        {
            return;
        }

        var missingPrerequisites = await GetMissingPrerequisitesAsync(@event.Resource, cancellationToken).ConfigureAwait(false);
        if (missingPrerequisites.Count == 0)
        {
            return;
        }

        var resourceLogger = loggerService.GetLogger(@event.Resource);
        foreach (var missing in missingPrerequisites)
        {
            resourceLogger.LogError(
                "{PrerequisiteName} is required before resource '{ResourceName}' can start. {Details} {InstallHint} See {DocumentationUrl}",
                missing.Checker.Name,
                @event.Resource.Name,
                missing.Result.Details,
                missing.Checker.InstallHint,
                missing.Checker.DocumentationUrl);
        }

        ShowNotification(@event.Resource, missingPrerequisites, cancellationToken);

        throw new DistributedApplicationException(BuildErrorMessage(@event.Resource, missingPrerequisites));
    }

    private async Task<List<MissingMauiPrerequisite>> GetMissingPrerequisitesAsync(IResource resource, CancellationToken cancellationToken)
    {
        var missing = new List<MissingMauiPrerequisite>();

        foreach (var checker in _checkers)
        {
            if (!checker.AppliesTo(resource))
            {
                continue;
            }

            var cacheKey = checker.GetCacheKey(resource);
            if (_successfulChecks.ContainsKey(cacheKey))
            {
                continue;
            }

            var result = await GetCheckResultAsync(checker, resource, cacheKey, cancellationToken).ConfigureAwait(false);
            if (result.IsAvailable)
            {
                _successfulChecks[cacheKey] = true;
            }
            else
            {
                missing.Add(new(checker, result));
            }
        }

        return missing;
    }

    private async Task<MauiPrerequisiteCheckResult> GetCheckResultAsync(
        IMauiPrerequisiteChecker checker,
        IResource resource,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var lazyCheck = _inflightChecks.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<MauiPrerequisiteCheckResult>>(
                () => checker.CheckAsync(resource, logger, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            // The probe is shared across matching resources, so a single resource-start cancellation
            // must not cancel the underlying check for other resources. Apply caller cancellation only
            // while awaiting the shared task.
            return await lazyCheck.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inflightChecks.TryRemove(new KeyValuePair<string, Lazy<Task<MauiPrerequisiteCheckResult>>>(cacheKey, lazyCheck));
        }
    }

    private void ShowNotification(IResource resource, IReadOnlyList<MissingMauiPrerequisite> missingPrerequisites, CancellationToken cancellationToken)
    {
        if (!interactionService.IsAvailable)
        {
            return;
        }

        var notificationKey = string.Join("|", missingPrerequisites.Select(static missing => missing.Checker.Name));
        if (!_shownNotifications.TryAdd(notificationKey, 0))
        {
            return;
        }

        var docsPrerequisite = missingPrerequisites[0].Checker;
        _ = ShowNotificationAsync(resource, missingPrerequisites, docsPrerequisite.DocumentationUrl, cancellationToken);
    }

    private async Task ShowNotificationAsync(
        IResource resource,
        IReadOnlyList<MissingMauiPrerequisite> missingPrerequisites,
        string documentationUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            await interactionService.PromptNotificationAsync(
                title: "MAUI prerequisites missing",
                message: BuildNotificationMessage(resource, missingPrerequisites),
                options: new NotificationInteractionOptions
                {
                    Intent = MessageIntent.Error,
                    LinkText = "Installation instructions",
                    LinkUrl = documentationUrl,
                    ShowSecondaryButton = false,
                    ShowDismiss = true,
                    EnableMessageMarkdown = true,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("MAUI prerequisite notification was canceled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to show MAUI prerequisite notification.");
        }
    }

    private static string BuildNotificationMessage(IResource resource, IReadOnlyList<MissingMauiPrerequisite> missingPrerequisites)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"Resource **{resource.Name}** cannot start until the following MAUI prerequisite");
        builder.Append(missingPrerequisites.Count == 1 ? " is installed:\n\n" : "s are installed:\n\n");

        foreach (var missing in missingPrerequisites)
        {
            builder.Append(CultureInfo.InvariantCulture, $"- **{missing.Checker.Name}**: {missing.Result.Details} {missing.Checker.InstallHint}\n");
        }

        return builder.ToString();
    }

    private static string BuildErrorMessage(IResource resource, IReadOnlyList<MissingMauiPrerequisite> missingPrerequisites)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"Resource '{resource.Name}' cannot start because the following MAUI prerequisite");
        builder.Append(missingPrerequisites.Count == 1 ? " is missing: " : "s are missing: ");

        for (var i = 0; i < missingPrerequisites.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(i == missingPrerequisites.Count - 1 ? " and " : ", ");
            }

            var missing = missingPrerequisites[i];
            builder.Append(CultureInfo.InvariantCulture, $"{missing.Checker.Name} ({missing.Result.Details} {missing.Checker.InstallHint})");
        }

        return builder.ToString();
    }

    private sealed record MissingMauiPrerequisite(IMauiPrerequisiteChecker Checker, MauiPrerequisiteCheckResult Result);
}
