// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding tracked browser log resources to browser-based application resources.
/// </summary>
public static class BrowserLogsBuilderExtensions
{
    internal const string BrowserResourceType = "BrowserLogs";
    internal const string BrowserLogsConfigurationSectionName = "Aspire:Hosting:BrowserLogs";
    internal const string BrowserConfigurationKey = "Browser";
    internal const string BrowserPropertyName = "Browser";
    internal const string BrowserExecutablePropertyName = "Browser executable";
    internal const string BrowserHostOwnershipPropertyName = "Browser host ownership";
    internal const string ProfileConfigurationKey = "Profile";
    internal const string ProfilePropertyName = "Profile";
    internal const string UserDataModeConfigurationKey = "UserDataMode";
    internal const string UserDataModePropertyName = "User data mode";
    internal const BrowserUserDataMode DefaultUserDataMode = BrowserConfiguration.DefaultUserDataMode;
    internal const string BrowserProcessLifetimeConfigurationKey = "BrowserProcessLifetime";
    internal const string BrowserProcessLifetimePropertyName = "Browser process lifetime";
    internal const BrowserProcessLifetime DefaultBrowserProcessLifetime = BrowserConfiguration.DefaultBrowserProcessLifetime;
    internal const string TargetUrlPropertyName = "Target URL";
    internal const string ActiveSessionsPropertyName = "Active sessions";
    internal const string BrowserSessionsPropertyName = "Browser sessions";
    internal const string ActiveSessionCountPropertyName = "Active session count";
    internal const string TotalSessionsLaunchedPropertyName = "Total sessions launched";
    internal const string LastErrorPropertyName = "Last error";
    internal const string LastSessionPropertyName = "Last session";
    internal const string OpenTrackedBrowserCommandName = "open-tracked-browser";
    internal const string ConfigureTrackedBrowserCommandName = "configure-tracked-browser";
    internal const string CaptureScreenshotCommandName = "capture-screenshot";
    private static readonly JsonSerializerOptions s_commandResultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>
    /// Adds a child resource that can open the application's primary browser endpoint in a tracked browser session,
    /// surface browser diagnostics, and capture screenshots.
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="browser">
    /// The browser to launch. When not specified, the tracked browser uses the configured value from
    /// <c>Aspire:Hosting:BrowserLogs</c> and otherwise prefers an installed <c>"msedge"</c> browser in shared user data
    /// mode, an installed <c>"chrome"</c> browser in isolated user data mode, and finally falls back to <c>"chrome"</c>.
    /// Supported values include logical
    /// browser names such as <c>"msedge"</c> and <c>"chrome"</c>, or an explicit browser executable path.
    /// </param>
    /// <param name="profile">
    /// Optional Chromium profile name or directory name to use. Only valid when the effective user data mode
    /// is <see cref="BrowserUserDataMode.Shared"/>. When not specified, the tracked browser uses the
    /// configured value from <c>Aspire:Hosting:BrowserLogs</c> if present.
    /// </param>
    /// <param name="userDataMode">
    /// Optional <see cref="BrowserUserDataMode"/> that selects whether the tracked browser launches against
    /// a persistent Aspire-managed user data directory shared across all AppHosts on the machine
    /// (<see cref="BrowserUserDataMode.Shared"/>, the default) or a per-AppHost persistent user data directory
    /// (<see cref="BrowserUserDataMode.Isolated"/>). Both modes use Aspire-managed paths under
    /// <c>%LocalAppData%\Aspire\BrowserData</c> on Windows (or platform equivalents); the user's normal browser
    /// profile is never used. When not specified, the tracked browser uses the configured value from
    /// <c>Aspire:Hosting:BrowserLogs</c> and otherwise defaults to <see cref="BrowserUserDataMode.Shared"/>.
    /// </param>
    /// <param name="browserProcessLifetime">
    /// Optional <see cref="BrowserProcessLifetime"/> that selects whether the pipe-created tracked browser process is
    /// session-scoped and terminated with the AppHost (<see cref="BrowserProcessLifetime.Session"/>, the default), or
    /// persistent and left running after the AppHost stops (<see cref="BrowserProcessLifetime.Persistent"/>). Persistent
    /// browsers cannot continue streaming logs after the AppHost exits and cannot be reattached by a later AppHost
    /// because the CDP pipe is private.
    /// </param>
    /// <returns>A reference to the original <see cref="IResourceBuilder{T}"/> for further chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method adds a child browser logs resource beneath the parent resource represented by <paramref name="builder"/>.
    /// The child resource exposes a dashboard command that launches a Chromium-based browser in a tracked mode, attaches to
    /// the browser's debugging protocol, forwards browser console, error, exception, and network output to the child
    /// resource's console log stream, and can capture screenshots as command artifacts.
    /// </para>
    /// <para>
    /// The tracked browser session uses the <a href="https://chromedevtools.github.io/devtools-protocol/">Chrome DevTools
    /// Protocol (CDP)</a> to subscribe to browser runtime, log, page, and network events.
    /// </para>
    /// <para>
    /// The parent resource must expose at least one HTTP or HTTPS endpoint. HTTPS endpoints are preferred over HTTP
    /// endpoints when selecting the browser target URL.
    /// </para>
    /// <para>
    /// Browser, profile, user data mode, and browser process lifetime settings can also be supplied from configuration
    /// using <c>Aspire:Hosting:BrowserLogs:Browser</c>, <c>Aspire:Hosting:BrowserLogs:Profile</c>,
    /// <c>Aspire:Hosting:BrowserLogs:UserDataMode</c>, and
    /// <c>Aspire:Hosting:BrowserLogs:BrowserProcessLifetime</c>, or scoped to a specific resource with
    /// <c>Aspire:Hosting:BrowserLogs:{ResourceName}:Browser</c>,
    /// <c>Aspire:Hosting:BrowserLogs:{ResourceName}:Profile</c>, and
    /// <c>Aspire:Hosting:BrowserLogs:{ResourceName}:UserDataMode</c>, and
    /// <c>Aspire:Hosting:BrowserLogs:{ResourceName}:BrowserProcessLifetime</c>. Explicit method arguments
    /// override configuration.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add tracked browser logs for a web front end:
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddProject&lt;Projects.WebFrontend&gt;("web")
    ///     .WithExternalHttpEndpoints()
    ///     .WithBrowserLogs();
    /// </code>
    /// </example>
    [Experimental("ASPIREBROWSERLOGS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport(Description = "Adds a child browser logs resource that opens tracked browser sessions, captures browser logs, and captures screenshots.")]
    public static IResourceBuilder<T> WithBrowserLogs<T>(
        this IResourceBuilder<T> builder,
        string? browser = null,
        string? profile = null,
        BrowserUserDataMode? userDataMode = null,
        BrowserProcessLifetime? browserProcessLifetime = null)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ThrowIfBlankWhenSpecified(browser, nameof(browser));
        ThrowIfBlankWhenSpecified(profile, nameof(profile));

        builder.ApplicationBuilder.Services.TryAddSingleton<IBrowserLogsSessionManager, BrowserLogsSessionManager>();
        builder.ApplicationBuilder.Services.TryAddSingleton<BrowserLogsConfigurationStore>();
        builder.ApplicationBuilder.Services.TryAddSingleton<BrowserLogsConfigurationManager>();

        var parentResource = builder.Resource;
        var explicitConfigurationValues = new BrowserConfigurationExplicitValues(browser, profile, userDataMode, browserProcessLifetime);
        var initialConfiguration = BrowserConfiguration.Resolve(builder.ApplicationBuilder.Configuration, parentResource.Name, explicitConfigurationValues);
        var browserLogsResource = new BrowserLogsResource(
            $"{parentResource.Name}-browser-logs",
            parentResource,
            initialConfiguration,
            explicitConfigurationValues);
        browserLogsResource.Annotations.Add(NameValidationPolicyAnnotation.None);

        builder.ApplicationBuilder.AddResource(browserLogsResource)
            .WithParentRelationship(parentResource)
            .ExcludeFromManifest()
            .WithIconName("GlobeDesktop")
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = BrowserResourceType,
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.NotStarted,
                Properties = CreateInitialProperties(parentResource.Name, initialConfiguration)
            })
            .WithCommand(
                OpenTrackedBrowserCommandName,
                CommandStrings.OpenTrackedBrowserName,
                async context =>
                {
                    try
                    {
                        var configuration = context.ServiceProvider.GetRequiredService<IConfiguration>();
                        var configurationStore = context.ServiceProvider.GetRequiredService<BrowserLogsConfigurationStore>();
                        var currentConfiguration = browserLogsResource.ResolveCurrentConfiguration(configuration, configurationStore);
                        var url = ResolveBrowserUrl(parentResource);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        await sessionManager.StartSessionAsync(browserLogsResource, currentConfiguration, context.ResourceName, url, context.CancellationToken).ConfigureAwait(false);
                        return CommandResults.Success();
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                new CommandOptions
                {
                    Description = CommandStrings.OpenTrackedBrowserDescription,
                    IconName = "Open",
                    IconVariant = IconVariant.Regular,
                    IsHighlighted = true,
                    UpdateState = context =>
                    {
                        var childState = context.ResourceSnapshot.State?.Text;
                        if (childState == KnownResourceStates.Starting)
                        {
                            return ResourceCommandState.Disabled;
                        }

                        var resourceNotifications = context.ServiceProvider.GetRequiredService<ResourceNotificationService>();
                        foreach (var resourceName in parentResource.GetResolvedResourceNames())
                        {
                            if (resourceNotifications.TryGetCurrentState(resourceName, out var resourceEvent))
                            {
                                var parentState = resourceEvent.Snapshot.State?.Text;
                                if (parentState == KnownResourceStates.Running || parentState == KnownResourceStates.RuntimeUnhealthy)
                                {
                                    return ResourceCommandState.Enabled;
                                }
                            }
                        }

                        return ResourceCommandState.Disabled;
                    }
                })
            .WithCommand(
                ConfigureTrackedBrowserCommandName,
                CommandStrings.ConfigureTrackedBrowserName,
                async context =>
                {
                    try
                    {
                        var configurationManager = context.ServiceProvider.GetRequiredService<BrowserLogsConfigurationManager>();
                        return await configurationManager.ConfigureAsync(browserLogsResource, context.CancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                new CommandOptions
                {
                    Description = CommandStrings.ConfigureTrackedBrowserDescription,
                    IconName = "Settings",
                    IconVariant = IconVariant.Regular,
                    UpdateState = context =>
                    {
                        var interactionService = context.ServiceProvider.GetRequiredService<IInteractionService>();
                        return interactionService.IsAvailable
                            ? ResourceCommandState.Enabled
                            : ResourceCommandState.Disabled;
                    }
                })
            .WithCommand(
                CaptureScreenshotCommandName,
                CommandStrings.CaptureScreenshotName,
                async context =>
                {
                    try
                    {
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var result = await sessionManager.CaptureScreenshotAsync(context.ResourceName, context.CancellationToken).ConfigureAwait(false);
                        var resultJson = JsonSerializer.Serialize(
                            new BrowserLogsScreenshotCommandResult(
                                result.Artifact.ResourceName,
                                result.SessionId,
                                result.Browser,
                                result.BrowserExecutable,
                                result.BrowserHostOwnership,
                                result.ProcessId,
                                result.TargetId,
                                result.TargetUrl.ToString(),
                                result.Artifact.FilePath,
                                result.Artifact.MimeType,
                                result.Artifact.SizeBytes,
                                result.Artifact.CreatedAt),
                            s_commandResultJsonOptions);

                        return CommandResults.Success(
                            $"Captured screenshot to '{result.Artifact.FilePath}'.",
                            new CommandResultData
                            {
                                Value = resultJson,
                                Format = CommandResultFormat.Json,
                                DisplayImmediately = true
                            });
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                new CommandOptions
                {
                    Description = CommandStrings.CaptureScreenshotDescription,
                    IconName = "Camera",
                    IconVariant = IconVariant.Regular,
                    UpdateState = context =>
                    {
                        var childState = context.ResourceSnapshot.State?.Text;
                        return childState == KnownResourceStates.Running
                            ? ResourceCommandState.Enabled
                            : ResourceCommandState.Disabled;
                    }
                });

        builder.OnBeforeResourceStarted((_, @event, _) => RefreshBrowserLogsResourceAsync(@event.Services.GetRequiredService<ResourceNotificationService>()))
               .OnResourceReady((_, @event, _) => RefreshBrowserLogsResourceAsync(@event.Services.GetRequiredService<ResourceNotificationService>()))
               .OnResourceStopped((_, @event, _) => RefreshBrowserLogsResourceAsync(@event.Services.GetRequiredService<ResourceNotificationService>()));

        return builder;

        Task RefreshBrowserLogsResourceAsync(ResourceNotificationService notifications) =>
            notifications.PublishUpdateAsync(browserLogsResource, snapshot => snapshot);

        static ImmutableArray<ResourcePropertySnapshot> CreateInitialProperties(string resourceName, BrowserConfiguration configuration)
        {
            List<ResourcePropertySnapshot> properties =
            [
                new(CustomResourceKnownProperties.Source, resourceName),
                new(BrowserPropertyName, configuration.Browser),
                new(UserDataModePropertyName, configuration.UserDataMode.ToString()),
                new(BrowserProcessLifetimePropertyName, configuration.BrowserProcessLifetime.ToString())
            ];

            if (configuration.Profile is { } profile)
            {
                properties.Add(new ResourcePropertySnapshot(ProfilePropertyName, profile));
            }

            properties.AddRange(
            [
                new ResourcePropertySnapshot(ActiveSessionCountPropertyName, 0),
                new ResourcePropertySnapshot(ActiveSessionsPropertyName, "None"),
                new ResourcePropertySnapshot(BrowserSessionsPropertyName, "[]"),
                new ResourcePropertySnapshot(TotalSessionsLaunchedPropertyName, 0)
            ]);

            return [.. properties];
        }

        static Uri ResolveBrowserUrl(T resource)
        {
            EndpointAnnotation? endpointAnnotation = null;
            if (resource.TryGetAnnotationsOfType<EndpointAnnotation>(out var endpoints))
            {
                endpointAnnotation = endpoints.FirstOrDefault(e => e.UriScheme == "https")
                    ?? endpoints.FirstOrDefault(e => e.UriScheme == "http");
            }

            if (endpointAnnotation is null)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, MessageStrings.BrowserLogsResourceMissingHttpEndpoint, resource.Name));
            }

            var endpointReference = resource.GetEndpoint(endpointAnnotation.Name);
            if (!endpointReference.IsAllocated)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, MessageStrings.BrowserLogsEndpointNotAllocated, endpointAnnotation.Name, resource.Name));
            }

            return new Uri(endpointReference.Url, UriKind.Absolute);
        }

        static void ThrowIfBlankWhenSpecified(string? value, string paramName)
        {
            if (value is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
            }
        }
    }

    private sealed record BrowserLogsScreenshotCommandResult(
        string ResourceName,
        string SessionId,
        string Browser,
        string BrowserExecutable,
        string BrowserHostOwnership,
        int? ProcessId,
        string TargetId,
        string TargetUrl,
        string Path,
        string MimeType,
        long SizeBytes,
        DateTimeOffset CreatedAt);
}
