// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Collections.Immutable;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;

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
    internal const string ProfileConfigurationKey = "Profile";
    internal const string ProfilePropertyName = "Profile";
    internal const string TargetUrlPropertyName = "Target URL";
    internal const string ActiveSessionsPropertyName = "Active sessions";
    internal const string BrowserSessionsPropertyName = "Browser sessions";
    internal const string ActiveSessionCountPropertyName = "Active session count";
    internal const string TotalSessionsLaunchedPropertyName = "Total sessions launched";
    internal const string LastSessionPropertyName = "Last session";
    internal const string OpenTrackedBrowserCommandName = "open-tracked-browser";

    /// <summary>
    /// Adds a child resource that can open the application's primary browser endpoint in a tracked browser session and
    /// surface browser console output in the dashboard console logs.
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="browser">
    /// The browser to launch. When not specified, the tracked browser uses the configured value from
    /// <c>Aspire:Hosting:BrowserLogs</c> and otherwise prefers an installed <c>"chrome"</c> browser, then an installed
    /// <c>"msedge"</c> browser, before finally falling back to <c>"chrome"</c>. Supported values include logical
    /// browser names such as <c>"msedge"</c> and <c>"chrome"</c>, or an explicit browser executable path.
    /// </param>
    /// <param name="profile">
    /// Optional Chromium profile directory name to use. When not specified, the tracked browser uses the configured
    /// value from <c>Aspire:Hosting:BrowserLogs</c> if present.
    /// </param>
    /// <returns>A reference to the original <see cref="IResourceBuilder{T}"/> for further chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method adds a child browser logs resource beneath the parent resource represented by <paramref name="builder"/>.
    /// The child resource exposes a dashboard command that launches a Chromium-based browser in a tracked mode, attaches to
    /// the browser's debugging protocol, and forwards browser console, error, and exception output to the child resource's
    /// console log stream.
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
    /// Browser and profile settings can also be supplied from configuration using
    /// <c>Aspire:Hosting:BrowserLogs:Browser</c> and <c>Aspire:Hosting:BrowserLogs:Profile</c>, or scoped to a
    /// specific resource with <c>Aspire:Hosting:BrowserLogs:{ResourceName}:Browser</c> and
    /// <c>Aspire:Hosting:BrowserLogs:{ResourceName}:Profile</c>. Explicit method arguments override configuration.
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
    [AspireExport(Description = "Adds a child browser logs resource that opens tracked browser sessions and captures browser logs.")]
    public static IResourceBuilder<T> WithBrowserLogs<T>(this IResourceBuilder<T> builder, string? browser = null, string? profile = null)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ThrowIfBlankWhenSpecified(browser, nameof(browser));
        ThrowIfBlankWhenSpecified(profile, nameof(profile));

        builder.ApplicationBuilder.Services.TryAddSingleton<IBrowserLogsSessionManager, BrowserLogsSessionManager>();

        var parentResource = builder.Resource;
        var settings = ResolveSettings(builder.ApplicationBuilder.Configuration, parentResource.Name, browser, profile);
        var browserLogsResource = new BrowserLogsResource($"{parentResource.Name}-browser-logs", parentResource, settings, browser, profile);
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
                Properties = CreateInitialProperties(parentResource.Name, settings)
            })
            .WithCommand(
                OpenTrackedBrowserCommandName,
                CommandStrings.OpenTrackedBrowserName,
                async context =>
                {
                    try
                    {
                        var configuration = context.ServiceProvider.GetRequiredService<IConfiguration>();
                        var currentSettings = browserLogsResource.ResolveCurrentSettings(configuration);
                        var url = ResolveBrowserUrl(parentResource);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        await sessionManager.StartSessionAsync(browserLogsResource, currentSettings, context.ResourceName, url, context.CancellationToken).ConfigureAwait(false);
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
                });

        builder.OnBeforeResourceStarted((_, @event, _) => RefreshBrowserLogsResourceAsync(@event.Services.GetRequiredService<ResourceNotificationService>()))
               .OnResourceReady((_, @event, _) => RefreshBrowserLogsResourceAsync(@event.Services.GetRequiredService<ResourceNotificationService>()))
               .OnResourceStopped((_, @event, _) => RefreshBrowserLogsResourceAsync(@event.Services.GetRequiredService<ResourceNotificationService>()));

        return builder;

        Task RefreshBrowserLogsResourceAsync(ResourceNotificationService notifications) =>
            notifications.PublishUpdateAsync(browserLogsResource, snapshot => snapshot);

        static ImmutableArray<ResourcePropertySnapshot> CreateInitialProperties(string resourceName, BrowserLogsSettings settings)
        {
            List<ResourcePropertySnapshot> properties =
            [
                new(CustomResourceKnownProperties.Source, resourceName),
                new(BrowserPropertyName, settings.Browser)
            ];

            if (settings.Profile is { } profile)
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
                throw new InvalidOperationException($"Resource '{resource.Name}' does not have an HTTP or HTTPS endpoint. Browser logs require an endpoint to navigate to.");
            }

            var endpointReference = resource.GetEndpoint(endpointAnnotation.Name);
            if (!endpointReference.IsAllocated)
            {
                throw new InvalidOperationException($"Endpoint '{endpointAnnotation.Name}' for resource '{resource.Name}' has not been allocated yet.");
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

    internal static BrowserLogsSettings ResolveSettings(IConfiguration configuration, string resourceName, string? browser, string? profile)
    {
        var browserLogsSection = configuration.GetSection(BrowserLogsConfigurationSectionName);
        var resourceSection = browserLogsSection.GetSection(resourceName);

        var resolvedBrowser = browser
            ?? resourceSection[BrowserConfigurationKey]
            ?? browserLogsSection[BrowserConfigurationKey]
            ?? GetDefaultBrowser();
        var resolvedProfile = profile
            ?? resourceSection[ProfileConfigurationKey]
            ?? browserLogsSection[ProfileConfigurationKey];

        if (string.IsNullOrWhiteSpace(resolvedBrowser))
        {
            throw new InvalidOperationException("Tracked browser configuration resolved an empty browser value.");
        }

        if (resolvedProfile is not null && string.IsNullOrWhiteSpace(resolvedProfile))
        {
            throw new InvalidOperationException("Tracked browser configuration resolved an empty profile value.");
        }

        return new BrowserLogsSettings(resolvedBrowser, resolvedProfile);
    }

    internal static string GetDefaultBrowser(Func<string, string?> resolveBrowserExecutable)
    {
        if (resolveBrowserExecutable("chrome") is not null)
        {
            return "chrome";
        }

        if (resolveBrowserExecutable("msedge") is not null)
        {
            return "msedge";
        }

        return "chrome";
    }

    private static string GetDefaultBrowser() => GetDefaultBrowser(BrowserLogsRunningSession.TryResolveBrowserExecutable);
}
