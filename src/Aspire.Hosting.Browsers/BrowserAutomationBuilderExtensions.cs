// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only
#pragma warning disable ASPIREBROWSERAUTOMATION001 // Type is for evaluation purposes only

using System.Collections.Immutable;
using Aspire.Hosting.Browsers.Resources;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding browser automation resources to browser-based application resources.
/// </summary>
[Experimental("ASPIREBROWSERAUTOMATION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class BrowserAutomationBuilderExtensions
{
    internal const string BrowserResourceType = "BrowserAutomation";
    internal const string BrowserAutomationConfigurationSectionName = "Aspire:Hosting:BrowserAutomation";
    internal const string LegacyBrowserLogsConfigurationSectionName = "Aspire:Hosting:BrowserLogs";
    internal const string BrowserConfigurationKey = "Browser";
    internal const string BrowserPropertyName = "Browser";
    internal const string BrowserExecutablePropertyName = "Browser executable";
    internal const string BrowserHostOwnershipPropertyName = "Browser host ownership";
    internal const string ProfileConfigurationKey = "Profile";
    internal const string ProfilePropertyName = "Profile";
    internal const string UserDataModeConfigurationKey = "UserDataMode";
    internal const string UserDataModePropertyName = "User data mode";
    internal const BrowserUserDataMode DefaultUserDataMode = BrowserConfiguration.DefaultUserDataMode;
    internal const string TargetUrlPropertyName = "Target URL";
    internal const string ActiveSessionsPropertyName = "Active sessions";
    internal const string BrowserSessionsPropertyName = "Browser sessions";
    internal const string ActiveSessionCountPropertyName = "Active session count";
    internal const string TotalSessionsLaunchedPropertyName = "Total sessions launched";
    internal const string LastErrorPropertyName = "Last error";
    internal const string LastSessionPropertyName = "Last session";
    internal const string OpenTrackedBrowserCommandName = "open-tracked-browser";
    internal const string ConfigureTrackedBrowserCommandName = "configure-tracked-browser";
    internal const string InspectBrowserCommandName = "inspect-browser";
    internal const string GetCommandName = "get";
    internal const string IsCommandName = "is";
    internal const string FindCommandName = "find";
    internal const string HighlightCommandName = "highlight";
    internal const string EvaluateCommandName = "eval";
    internal const string CookiesCommandName = "cookies";
    internal const string StorageCommandName = "storage";
    internal const string StateCommandName = "state";
    internal const string CdpCommandName = "cdp";
    internal const string TabsCommandName = "tabs";
    internal const string FramesCommandName = "frames";
    internal const string DialogCommandName = "dialog";
    internal const string DownloadsCommandName = "downloads";
    internal const string UploadCommandName = "upload";
    internal const string BrowserUrlCommandName = "url";
    internal const string BackBrowserCommandName = "back";
    internal const string ForwardBrowserCommandName = "forward";
    internal const string ReloadBrowserCommandName = "reload";
    internal const string NavigateBrowserCommandName = "navigate-browser";
    internal const string ClickBrowserCommandName = "click-browser";
    internal const string DoubleClickBrowserCommandName = "dblclick-browser";
    internal const string FillBrowserCommandName = "fill-browser";
    internal const string CheckBrowserCommandName = "check-browser";
    internal const string UncheckBrowserCommandName = "uncheck-browser";
    internal const string FocusBrowserElementCommandName = "focus-browser-element";
    internal const string TypeBrowserTextCommandName = "type-browser-text";
    internal const string PressBrowserKeyCommandName = "press-browser-key";
    internal const string KeyDownBrowserCommandName = "keydown-browser";
    internal const string KeyUpBrowserCommandName = "keyup-browser";
    internal const string HoverBrowserElementCommandName = "hover-browser-element";
    internal const string SelectBrowserOptionCommandName = "select-browser-option";
    internal const string ScrollBrowserCommandName = "scroll-browser";
    internal const string ScrollIntoViewBrowserCommandName = "scroll-into-view-browser";
    internal const string MouseBrowserCommandName = "mouse";
    internal const string WaitCommandName = "wait";
    internal const string WaitForBrowserCommandName = "wait-for-browser";
    internal const string WaitForBrowserUrlCommandName = "wait-for-browser-url";
    internal const string WaitForBrowserLoadStateCommandName = "wait-for-browser-load-state";
    internal const string WaitForBrowserElementStateCommandName = "wait-for-browser-element-state";
    internal const string CaptureScreenshotCommandName = "capture-screenshot";
    internal const string CloseTrackedBrowserCommandName = "close-tracked-browser";
    private const int DefaultSnapshotMaxElements = 80;
    private const int DefaultSnapshotMaxTextLength = 8_000;
    private const int DefaultBrowserCommandTimeoutMilliseconds = 10_000;
    private const int MinimumBrowserCommandTimeoutMilliseconds = 100;
    private const int MaximumBrowserCommandTimeoutMilliseconds = 60_000;
    private static readonly JsonSerializerOptions s_commandResultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>
    /// Adds a child resource that can open the application's primary browser endpoint in a tracked browser session,
    /// surface browser diagnostics, automate browser interactions, and capture screenshots.
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="browser">
    /// The browser to launch. When not specified, the tracked browser uses the configured value from
    /// <c>Aspire:Hosting:BrowserAutomation</c> and otherwise prefers an installed <c>"msedge"</c> browser in shared user data
    /// mode, an installed <c>"chrome"</c> browser in isolated user data mode, and finally falls back to <c>"chrome"</c>.
    /// Supported values include logical
    /// browser names such as <c>"msedge"</c> and <c>"chrome"</c>, or an explicit browser executable path.
    /// </param>
    /// <param name="profile">
    /// Optional Chromium profile name or directory name to use. Only valid when the effective user data mode
    /// is <see cref="BrowserUserDataMode.Shared"/>. When not specified, the tracked browser uses the
    /// configured value from <c>Aspire:Hosting:BrowserAutomation</c> if present.
    /// </param>
    /// <param name="userDataMode">
    /// Optional <see cref="BrowserUserDataMode"/> that selects whether the tracked browser launches against
    /// a persistent Aspire-managed user data directory shared across all AppHosts on the machine
    /// (<see cref="BrowserUserDataMode.Shared"/>, the default) or a per-AppHost persistent user data directory
    /// (<see cref="BrowserUserDataMode.Isolated"/>). Both modes use Aspire-managed paths under
    /// <c>%LocalAppData%\Aspire\BrowserData</c> on Windows (or platform equivalents); the user's normal browser
    /// profile is never used. When not specified, the tracked browser uses the configured value from
    /// <c>Aspire:Hosting:BrowserAutomation</c> and otherwise defaults to <see cref="BrowserUserDataMode.Shared"/>.
    /// </param>
    /// <returns>A reference to the original <see cref="IResourceBuilder{T}"/> for further chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method adds a child browser automation resource beneath the parent resource represented by <paramref name="builder"/>.
    /// The child resource exposes dashboard commands that launch a Chromium-based browser in a tracked mode, attach to
    /// the browser's debugging protocol, forward browser console, error, exception, and network output to the child
    /// resource's console log stream, automate browser interactions, and capture screenshots as command artifacts.
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
    /// Browser, profile, and user data mode settings can also be supplied from configuration
    /// using <c>Aspire:Hosting:BrowserAutomation:Browser</c>, <c>Aspire:Hosting:BrowserAutomation:Profile</c>,
    /// and <c>Aspire:Hosting:BrowserAutomation:UserDataMode</c>, or scoped to a specific resource with
    /// <c>Aspire:Hosting:BrowserAutomation:{ResourceName}:Browser</c>,
    /// <c>Aspire:Hosting:BrowserAutomation:{ResourceName}:Profile</c>, and
    /// <c>Aspire:Hosting:BrowserAutomation:{ResourceName}:UserDataMode</c>. Explicit method arguments override configuration.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add browser automation for a web front end:
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddProject&lt;Projects.WebFrontend&gt;("web")
    ///     .WithExternalHttpEndpoints()
    ///     .WithBrowserAutomation();
    /// </code>
    /// </example>
    [Experimental("ASPIREBROWSERAUTOMATION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport(Description = "Adds a child browser automation resource that opens tracked browser sessions, captures browser diagnostics, automates browser interactions, and captures screenshots.")]
    public static IResourceBuilder<T> WithBrowserAutomation<T>(
        this IResourceBuilder<T> builder,
        string? browser = null,
        string? profile = null,
        BrowserUserDataMode? userDataMode = null)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ThrowIfBlankWhenSpecified(browser, nameof(browser));
        ThrowIfBlankWhenSpecified(profile, nameof(profile));

        builder.ApplicationBuilder.Services.TryAddSingleton<IBrowserLogsSessionManager, BrowserLogsSessionManager>();
        builder.ApplicationBuilder.Services.TryAddSingleton<BrowserLogsConfigurationStore>();
        builder.ApplicationBuilder.Services.TryAddSingleton<BrowserLogsConfigurationManager>();

        var parentResource = builder.Resource;
        var explicitConfigurationValues = new BrowserConfigurationExplicitValues(browser, profile, userDataMode);
        var initialConfiguration = BrowserConfiguration.Resolve(builder.ApplicationBuilder.Configuration, parentResource.Name, explicitConfigurationValues);
        var browserAutomationResource = new BrowserAutomationResource(
            $"{parentResource.Name}-browser-automation",
            parentResource,
            initialConfiguration,
            explicitConfigurationValues);
        browserAutomationResource.Annotations.Add(NameValidationPolicyAnnotation.None);

        builder.ApplicationBuilder.AddResource(browserAutomationResource)
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
                BrowserCommandStrings.OpenTrackedBrowserName,
                async context =>
                {
                    try
                    {
                        var configuration = context.ServiceProvider.GetRequiredService<IConfiguration>();
                        var configurationStore = context.ServiceProvider.GetRequiredService<BrowserLogsConfigurationStore>();
                        var currentConfiguration = browserAutomationResource.ResolveCurrentConfiguration(configuration, configurationStore);
                        var url = ResolveBrowserUrl(parentResource);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        await sessionManager.StartSessionAsync(browserAutomationResource, currentConfiguration, context.ResourceName, url, context.CancellationToken).ConfigureAwait(false);
                        return CommandResults.Success();
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                new CommandOptions
                {
                    Description = BrowserCommandStrings.OpenTrackedBrowserDescription,
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
                        if (resourceNotifications.TryGetCurrentState(parentResource.Name, out var resourceEvent))
                        {
                            var parentState = resourceEvent.Snapshot.State?.Text;
                            if (parentState == KnownResourceStates.Running || parentState == KnownResourceStates.RuntimeUnhealthy)
                            {
                                return ResourceCommandState.Enabled;
                            }
                        }

                        return ResourceCommandState.Disabled;
                    }
                })
            .WithCommand(
                ConfigureTrackedBrowserCommandName,
                BrowserCommandStrings.ConfigureTrackedBrowserName,
                async context =>
                {
                    try
                    {
                        var configurationManager = context.ServiceProvider.GetRequiredService<BrowserLogsConfigurationManager>();
                        return await configurationManager.ConfigureAsync(browserAutomationResource, context.CancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                new CommandOptions
                {
                    Description = BrowserCommandStrings.ConfigureTrackedBrowserDescription,
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
                InspectBrowserCommandName,
                BrowserCommandStrings.InspectBrowserName,
                async context =>
                {
                    try
                    {
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var maxElements = GetOptionalIntegerArgument(context.Arguments, "maxElements", DefaultSnapshotMaxElements, 1, 500);
                        var maxTextLength = GetOptionalIntegerArgument(context.Arguments, "maxTextLength", DefaultSnapshotMaxTextLength, 100, 50_000);
                        var resultJson = await sessionManager.GetPageSnapshotAsync(context.ResourceName, maxElements, maxTextLength, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.InspectBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.InspectBrowserDescription,
                    "DocumentSearch",
                    [
                        CreateNumberArgument("maxElements", BrowserCommandStrings.MaxElementsArgumentLabel, BrowserCommandStrings.MaxElementsArgumentDescription, DefaultSnapshotMaxElements.ToString(CultureInfo.InvariantCulture), required: false),
                        CreateNumberArgument("maxTextLength", BrowserCommandStrings.MaxTextLengthArgumentLabel, BrowserCommandStrings.MaxTextLengthArgumentDescription, DefaultSnapshotMaxTextLength.ToString(CultureInfo.InvariantCulture), required: false)
                    ]))
            .WithCommand(
                GetCommandName,
                BrowserCommandStrings.GetBrowserName,
                async context =>
                {
                    try
                    {
                        var property = GetRequiredStringArgument(context.Arguments, "property");
                        var selector = GetOptionalStringArgument(context.Arguments, "selector");
                        var name = GetOptionalStringArgument(context.Arguments, "name");
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.GetAsync(context.ResourceName, property, selector, name, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.GetBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.GetBrowserDescription,
                    "Code",
                    [
                        CreateChoiceArgument("property", BrowserCommandStrings.PropertyArgumentLabel, BrowserCommandStrings.PropertyArgumentDescription, "text", required: true, ["title", "url", "text", "html", "value", "attr", "count", "box", "styles"]),
                        CreateSelectorArgument(required: false),
                        CreateTextArgument("name", BrowserCommandStrings.NameArgumentLabel, BrowserCommandStrings.NameArgumentDescription, required: false, placeholder: "href")
                    ]))
            .WithCommand(
                IsCommandName,
                BrowserCommandStrings.IsBrowserName,
                async context =>
                {
                    try
                    {
                        var state = GetRequiredStringArgument(context.Arguments, "state");
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.IsAsync(context.ResourceName, state, selector, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.IsBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.IsBrowserDescription,
                    "CheckmarkCircle",
                    [
                        CreateChoiceArgument("state", BrowserCommandStrings.StateArgumentLabel, BrowserCommandStrings.StateArgumentDescription, "visible", required: true, ["visible", "enabled", "checked"]),
                        CreateSelectorArgument()
                    ]))
            .WithCommand(
                FindCommandName,
                BrowserCommandStrings.FindBrowserName,
                async context =>
                {
                    try
                    {
                        var kind = GetRequiredStringArgument(context.Arguments, "kind");
                        var value = GetRequiredStringArgument(context.Arguments, "value");
                        var name = GetOptionalStringArgument(context.Arguments, "name");
                        var index = GetOptionalIntegerArgument(context.Arguments, "index", defaultValue: 1, minimum: 1, maximum: 10_000);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.FindAsync(context.ResourceName, kind, value, name, index, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.FindBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.FindBrowserDescription,
                    "Search",
                    [
                        CreateChoiceArgument("kind", BrowserCommandStrings.KindArgumentLabel, BrowserCommandStrings.KindArgumentDescription, "text", required: true, ["role", "text", "label", "placeholder", "alt", "title", "testid", "first", "last", "nth"]),
                        CreateTextArgument("value", BrowserCommandStrings.FindValueArgumentLabel, BrowserCommandStrings.FindValueArgumentDescription, required: true, placeholder: "Submit"),
                        CreateTextArgument("name", BrowserCommandStrings.NameArgumentLabel, BrowserCommandStrings.NameArgumentDescription, required: false, placeholder: "Save"),
                        CreateNumberArgument("index", BrowserCommandStrings.IndexArgumentLabel, BrowserCommandStrings.IndexArgumentDescription, "1", required: false)
                    ]))
            .WithCommand(
                HighlightCommandName,
                BrowserCommandStrings.HighlightBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.HighlightAsync(context.ResourceName, selector, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.HighlightBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.HighlightBrowserDescription,
                    "Highlight",
                    [CreateSelectorArgument()]))
            .WithCommand(
                EvaluateCommandName,
                BrowserCommandStrings.EvaluateBrowserName,
                async context =>
                {
                    try
                    {
                        var expression = GetRequiredStringArgument(context.Arguments, "expression");
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.EvaluateAsync(context.ResourceName, expression, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.EvaluateBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.EvaluateBrowserDescription,
                    "DeveloperBoard",
                    [CreateTextArgument("expression", BrowserCommandStrings.ExpressionArgumentLabel, BrowserCommandStrings.ExpressionArgumentDescription, required: true, placeholder: "document.title")]))
            .WithCommand(
                CookiesCommandName,
                BrowserCommandStrings.CookiesBrowserName,
                async context =>
                {
                    try
                    {
                        var action = GetRequiredStringArgument(context.Arguments, "action");
                        var name = GetOptionalStringArgument(context.Arguments, "name");
                        var value = action == "set" ? GetRequiredStringArgument(context.Arguments, "value", allowEmpty: true) : GetOptionalStringArgument(context.Arguments, "value");
                        var domain = GetOptionalStringArgument(context.Arguments, "domain");
                        var path = GetOptionalStringArgument(context.Arguments, "path");
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.CookiesAsync(context.ResourceName, action, name, value, domain, path, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.CookiesBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.CookiesBrowserDescription,
                    "Cookies",
                    [
                        CreateChoiceArgument("action", BrowserCommandStrings.ActionArgumentLabel, BrowserCommandStrings.CookiesActionArgumentDescription, "get", required: true, ["get", "set", "clear"]),
                        CreateTextArgument("name", BrowserCommandStrings.CookieNameArgumentLabel, BrowserCommandStrings.CookieNameArgumentDescription, required: false, placeholder: "session_id"),
                        CreateTextArgument("value", BrowserCommandStrings.CookieValueArgumentLabel, BrowserCommandStrings.CookieValueArgumentDescription, required: false),
                        CreateTextArgument("domain", BrowserCommandStrings.CookieDomainArgumentLabel, BrowserCommandStrings.CookieDomainArgumentDescription, required: false, placeholder: "example.com"),
                        CreateTextArgument("path", BrowserCommandStrings.CookiePathArgumentLabel, BrowserCommandStrings.CookiePathArgumentDescription, required: false, placeholder: "/")
                    ]))
            .WithCommand(
                StorageCommandName,
                BrowserCommandStrings.StorageBrowserName,
                async context =>
                {
                    try
                    {
                        var area = GetRequiredStringArgument(context.Arguments, "area");
                        var action = GetRequiredStringArgument(context.Arguments, "action");
                        var key = GetOptionalStringArgument(context.Arguments, "key");
                        var value = action == "set" ? GetRequiredStringArgument(context.Arguments, "value", allowEmpty: true) : GetOptionalStringArgument(context.Arguments, "value");
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.StorageAsync(context.ResourceName, area, action, key, value, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.StorageBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.StorageBrowserDescription,
                    "Database",
                    [
                        CreateChoiceArgument("area", BrowserCommandStrings.StorageAreaArgumentLabel, BrowserCommandStrings.StorageAreaArgumentDescription, "local", required: true, ["local", "session"]),
                        CreateChoiceArgument("action", BrowserCommandStrings.ActionArgumentLabel, BrowserCommandStrings.StorageActionArgumentDescription, "get", required: true, ["get", "set", "clear"]),
                        CreateTextArgument("key", BrowserCommandStrings.StorageKeyArgumentLabel, BrowserCommandStrings.StorageKeyArgumentDescription, required: false, placeholder: "theme"),
                        CreateTextArgument("value", BrowserCommandStrings.StorageValueArgumentLabel, BrowserCommandStrings.StorageValueArgumentDescription, required: false)
                    ]))
            .WithCommand(
                StateCommandName,
                BrowserCommandStrings.StateBrowserName,
                async context =>
                {
                    try
                    {
                        var action = GetRequiredStringArgument(context.Arguments, "action");
                        var state = action == "set" ? GetRequiredStringArgument(context.Arguments, "state") : GetOptionalStringArgument(context.Arguments, "state");
                        var clearExisting = GetOptionalBooleanArgument(context.Arguments, "clearExisting", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.StateAsync(context.ResourceName, action, state, clearExisting, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.StateBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.StateBrowserDescription,
                    "Save",
                    [
                        CreateChoiceArgument("action", BrowserCommandStrings.ActionArgumentLabel, BrowserCommandStrings.StateActionArgumentDescription, "get", required: true, ["get", "set", "clear"]),
                        CreateTextArgument("state", BrowserCommandStrings.StateJsonArgumentLabel, BrowserCommandStrings.StateJsonArgumentDescription, required: false, placeholder: """{"cookies":[],"localStorage":{},"sessionStorage":{}}"""),
                        CreateBooleanArgument("clearExisting", BrowserCommandStrings.ClearExistingArgumentLabel, BrowserCommandStrings.ClearExistingArgumentDescription, value: "false", required: false)
                    ]))
            .WithCommand(
                CdpCommandName,
                BrowserCommandStrings.CdpBrowserName,
                async context =>
                {
                    try
                    {
                        var method = GetRequiredStringArgument(context.Arguments, "method");
                        var parametersJson = GetOptionalStringArgument(context.Arguments, "params");
                        var session = GetOptionalStringArgument(context.Arguments, "session") ?? "page";
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.CdpAsync(context.ResourceName, method, parametersJson, session, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.CdpBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.CdpBrowserDescription,
                    "DeveloperBoard",
                    [
                        CreateTextArgument("method", BrowserCommandStrings.CdpMethodArgumentLabel, BrowserCommandStrings.CdpMethodArgumentDescription, required: true, placeholder: "Runtime.evaluate"),
                        CreateTextArgument("params", BrowserCommandStrings.CdpParamsArgumentLabel, BrowserCommandStrings.CdpParamsArgumentDescription, required: false, placeholder: """{"expression":"document.title","returnByValue":true}"""),
                        CreateChoiceArgument("session", BrowserCommandStrings.CdpSessionArgumentLabel, BrowserCommandStrings.CdpSessionArgumentDescription, "page", required: false, ["page", "browser"])
                    ]))
            .WithCommand(
                TabsCommandName,
                BrowserCommandStrings.TabsBrowserName,
                async context =>
                {
                    try
                    {
                        var action = GetRequiredStringArgument(context.Arguments, "action");
                        var url = GetOptionalStringArgument(context.Arguments, "url");
                        var targetId = GetOptionalStringArgument(context.Arguments, "targetId");
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.TabsAsync(context.ResourceName, action, url, targetId, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.TabsBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.TabsBrowserDescription,
                    "Tab",
                    [
                        CreateChoiceArgument("action", BrowserCommandStrings.ActionArgumentLabel, BrowserCommandStrings.TabsActionArgumentDescription, "list", required: true, ["list", "open", "close"]),
                        CreateTextArgument("url", BrowserCommandStrings.UrlArgumentLabel, BrowserCommandStrings.TabUrlArgumentDescription, required: false, placeholder: "https://example.com/"),
                        CreateTextArgument("targetId", BrowserCommandStrings.TargetIdArgumentLabel, BrowserCommandStrings.TargetIdArgumentDescription, required: false, placeholder: "target-id")
                    ]))
            .WithCommand(
                FramesCommandName,
                BrowserCommandStrings.FramesBrowserName,
                async context =>
                {
                    try
                    {
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.FramesAsync(context.ResourceName, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.FramesBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(BrowserCommandStrings.FramesBrowserDescription, "Window", []))
            .WithCommand(
                DialogCommandName,
                BrowserCommandStrings.DialogBrowserName,
                async context =>
                {
                    try
                    {
                        var action = GetRequiredStringArgument(context.Arguments, "action");
                        var promptText = GetOptionalStringArgument(context.Arguments, "promptText");
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.DialogAsync(context.ResourceName, action, promptText, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.DialogBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.DialogBrowserDescription,
                    "WindowAd",
                    [
                        CreateChoiceArgument("action", BrowserCommandStrings.ActionArgumentLabel, BrowserCommandStrings.DialogActionArgumentDescription, "accept", required: true, ["accept", "dismiss"]),
                        CreateTextArgument("promptText", BrowserCommandStrings.PromptTextArgumentLabel, BrowserCommandStrings.PromptTextArgumentDescription, required: false)
                    ]))
            .WithCommand(
                DownloadsCommandName,
                BrowserCommandStrings.DownloadsBrowserName,
                async context =>
                {
                    try
                    {
                        var behavior = GetRequiredStringArgument(context.Arguments, "behavior");
                        var downloadPath = GetOptionalStringArgument(context.Arguments, "downloadPath");
                        var eventsEnabled = GetOptionalBooleanArgument(context.Arguments, "eventsEnabled", defaultValue: true);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.DownloadsAsync(context.ResourceName, behavior, downloadPath, eventsEnabled, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.DownloadsBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.DownloadsBrowserDescription,
                    "ArrowDownload",
                    [
                        CreateChoiceArgument("behavior", BrowserCommandStrings.DownloadBehaviorArgumentLabel, BrowserCommandStrings.DownloadBehaviorArgumentDescription, "allow", required: true, ["allow", "allowAndName", "deny", "default"]),
                        CreateTextArgument("downloadPath", BrowserCommandStrings.DownloadPathArgumentLabel, BrowserCommandStrings.DownloadPathArgumentDescription, required: false),
                        CreateBooleanArgument("eventsEnabled", BrowserCommandStrings.DownloadEventsEnabledArgumentLabel, BrowserCommandStrings.DownloadEventsEnabledArgumentDescription, value: "true", required: false)
                    ]))
            .WithCommand(
                UploadCommandName,
                BrowserCommandStrings.UploadBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var files = GetRequiredStringArgument(context.Arguments, "files");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.UploadAsync(context.ResourceName, selector, files, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.UploadBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.UploadBrowserDescription,
                    "Attach",
                    [
                        CreateSelectorArgument(),
                        CreateTextArgument("files", BrowserCommandStrings.FilesArgumentLabel, BrowserCommandStrings.FilesArgumentDescription, required: true, placeholder: """["/tmp/file.txt"]"""),
                        CreateSnapshotAfterArgument()
                    ]))
            .WithCommand(
                BrowserUrlCommandName,
                BrowserCommandStrings.BrowserUrlName,
                async context =>
                {
                    try
                    {
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.GetUrlAsync(context.ResourceName, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.BrowserUrlSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(BrowserCommandStrings.BrowserUrlDescription, "Link", []))
            .WithCommand(
                BackBrowserCommandName,
                BrowserCommandStrings.BackBrowserName,
                async context =>
                {
                    try
                    {
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.GoBackAsync(context.ResourceName, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.BackBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(BrowserCommandStrings.BackBrowserDescription, "ArrowLeft", [CreateSnapshotAfterArgument()]))
            .WithCommand(
                ForwardBrowserCommandName,
                BrowserCommandStrings.ForwardBrowserName,
                async context =>
                {
                    try
                    {
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.GoForwardAsync(context.ResourceName, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.ForwardBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(BrowserCommandStrings.ForwardBrowserDescription, "ArrowRight", [CreateSnapshotAfterArgument()]))
            .WithCommand(
                ReloadBrowserCommandName,
                BrowserCommandStrings.ReloadBrowserName,
                async context =>
                {
                    try
                    {
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.ReloadAsync(context.ResourceName, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.ReloadBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(BrowserCommandStrings.ReloadBrowserDescription, "ArrowClockwise", [CreateSnapshotAfterArgument()]))
            .WithCommand(
                NavigateBrowserCommandName,
                BrowserCommandStrings.NavigateBrowserName,
                async context =>
                {
                    try
                    {
                        var urlText = GetRequiredStringArgument(context.Arguments, "url");
                        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url))
                        {
                            throw new InvalidOperationException("The browser navigation URL must be an absolute URI.");
                        }

                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.NavigateAsync(browserAutomationResource, context.ResourceName, url, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.NavigateBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.NavigateBrowserDescription,
                    "Navigation",
                    [
                        CreateTextArgument("url", BrowserCommandStrings.UrlArgumentLabel, BrowserCommandStrings.UrlArgumentDescription, required: true, placeholder: "https://example.com/"),
                        CreateSnapshotAfterArgument()
                    ]))
            .WithCommand(
                ClickBrowserCommandName,
                BrowserCommandStrings.ClickBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.ClickAsync(context.ResourceName, selector, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.ClickBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.ClickBrowserDescription,
                    "CursorClick",
                    [CreateSelectorArgument(), CreateSnapshotAfterArgument()]))
            .WithCommand(
                DoubleClickBrowserCommandName,
                BrowserCommandStrings.DoubleClickBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.DoubleClickAsync(context.ResourceName, selector, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.DoubleClickBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.DoubleClickBrowserDescription,
                    "CursorClick",
                    [CreateSelectorArgument(), CreateSnapshotAfterArgument()]))
            .WithCommand(
                FillBrowserCommandName,
                BrowserCommandStrings.FillBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var value = GetRequiredStringArgument(context.Arguments, "value", allowEmpty: true);
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.FillAsync(context.ResourceName, selector, value, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.FillBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.FillBrowserDescription,
                    "TextEdit",
                    [
                        CreateSelectorArgument(),
                        CreateTextArgument("value", BrowserCommandStrings.ValueArgumentLabel, BrowserCommandStrings.ValueArgumentDescription, required: true),
                        CreateSnapshotAfterArgument()
                    ]))
            .WithCommand(
                CheckBrowserCommandName,
                BrowserCommandStrings.CheckBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.CheckAsync(context.ResourceName, selector, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.CheckBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.CheckBrowserDescription,
                    "CheckboxChecked",
                    [CreateSelectorArgument(), CreateSnapshotAfterArgument()]))
            .WithCommand(
                UncheckBrowserCommandName,
                BrowserCommandStrings.UncheckBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.UncheckAsync(context.ResourceName, selector, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.UncheckBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.UncheckBrowserDescription,
                    "CheckboxUnchecked",
                    [CreateSelectorArgument(), CreateSnapshotAfterArgument()]))
            .WithCommand(
                FocusBrowserElementCommandName,
                BrowserCommandStrings.FocusBrowserElementName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.FocusAsync(context.ResourceName, selector, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.FocusBrowserElementSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.FocusBrowserElementDescription,
                    "Cursor",
                    [CreateSelectorArgument(), CreateSnapshotAfterArgument()]))
            .WithCommand(
                TypeBrowserTextCommandName,
                BrowserCommandStrings.TypeBrowserTextName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var text = GetRequiredStringArgument(context.Arguments, "text", allowEmpty: true);
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.TypeAsync(context.ResourceName, selector, text, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.TypeBrowserTextSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.TypeBrowserTextDescription,
                    "TextEdit",
                    [
                        CreateSelectorArgument(),
                        CreateTextArgument("text", BrowserCommandStrings.TextArgumentLabel, BrowserCommandStrings.TypeTextArgumentDescription, required: true),
                        CreateSnapshotAfterArgument()
                    ]))
            .WithCommand(
                PressBrowserKeyCommandName,
                BrowserCommandStrings.PressBrowserKeyName,
                async context =>
                {
                    try
                    {
                        var selector = GetOptionalStringArgument(context.Arguments, "selector");
                        var key = GetRequiredStringArgument(context.Arguments, "key");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.PressAsync(context.ResourceName, selector, key, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.PressBrowserKeySucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.PressBrowserKeyDescription,
                    "Keyboard",
                    [
                        CreateTextArgument("key", BrowserCommandStrings.KeyArgumentLabel, BrowserCommandStrings.KeyArgumentDescription, required: true, placeholder: "Enter"),
                        CreateSelectorArgument(required: false),
                        CreateSnapshotAfterArgument()
                    ]))
            .WithCommand(
                KeyDownBrowserCommandName,
                BrowserCommandStrings.KeyDownBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetOptionalStringArgument(context.Arguments, "selector");
                        var key = GetRequiredStringArgument(context.Arguments, "key");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.KeyDownAsync(context.ResourceName, selector, key, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.KeyDownBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.KeyDownBrowserDescription,
                    "Keyboard",
                    [
                        CreateTextArgument("key", BrowserCommandStrings.KeyArgumentLabel, BrowserCommandStrings.KeyArgumentDescription, required: true, placeholder: "Shift"),
                        CreateSelectorArgument(required: false),
                        CreateSnapshotAfterArgument()
                    ]))
            .WithCommand(
                KeyUpBrowserCommandName,
                BrowserCommandStrings.KeyUpBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetOptionalStringArgument(context.Arguments, "selector");
                        var key = GetRequiredStringArgument(context.Arguments, "key");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.KeyUpAsync(context.ResourceName, selector, key, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.KeyUpBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.KeyUpBrowserDescription,
                    "Keyboard",
                    [
                        CreateTextArgument("key", BrowserCommandStrings.KeyArgumentLabel, BrowserCommandStrings.KeyArgumentDescription, required: true, placeholder: "Shift"),
                        CreateSelectorArgument(required: false),
                        CreateSnapshotAfterArgument()
                    ]))
            .WithCommand(
                HoverBrowserElementCommandName,
                BrowserCommandStrings.HoverBrowserElementName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.HoverAsync(context.ResourceName, selector, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.HoverBrowserElementSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.HoverBrowserElementDescription,
                    "CursorHover",
                    [CreateSelectorArgument(), CreateSnapshotAfterArgument()]))
            .WithCommand(
                SelectBrowserOptionCommandName,
                BrowserCommandStrings.SelectBrowserOptionName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var value = GetRequiredStringArgument(context.Arguments, "value", allowEmpty: true);
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.SelectAsync(context.ResourceName, selector, value, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.SelectBrowserOptionSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.SelectBrowserOptionDescription,
                    "CheckboxChecked",
                    [
                        CreateSelectorArgument(),
                        CreateTextArgument("value", BrowserCommandStrings.ValueArgumentLabel, BrowserCommandStrings.SelectValueArgumentDescription, required: true),
                        CreateSnapshotAfterArgument()
                    ]))
            .WithCommand(
                ScrollBrowserCommandName,
                BrowserCommandStrings.ScrollBrowserName,
                async context =>
                {
                    try
                    {
                        var deltaY = GetOptionalIntegerArgument(context.Arguments, "deltaY", 600, -100_000, 100_000);
                        var deltaX = GetOptionalIntegerArgument(context.Arguments, "deltaX", 0, -100_000, 100_000);
                        var selector = GetOptionalStringArgument(context.Arguments, "selector");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.ScrollAsync(context.ResourceName, selector, deltaX, deltaY, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.ScrollBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.ScrollBrowserDescription,
                    "ArrowDown",
                    [
                        CreateNumberArgument("deltaY", BrowserCommandStrings.DeltaYArgumentLabel, BrowserCommandStrings.DeltaYArgumentDescription, "600", required: false),
                        CreateNumberArgument("deltaX", BrowserCommandStrings.DeltaXArgumentLabel, BrowserCommandStrings.DeltaXArgumentDescription, "0", required: false),
                        CreateSelectorArgument(required: false),
                        CreateSnapshotAfterArgument()
                    ]))
            .WithCommand(
                ScrollIntoViewBrowserCommandName,
                BrowserCommandStrings.ScrollIntoViewBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.ScrollIntoViewAsync(context.ResourceName, selector, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.ScrollIntoViewBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.ScrollIntoViewBrowserDescription,
                    "ArrowDown",
                    [CreateSelectorArgument(), CreateSnapshotAfterArgument()]))
            .WithCommand(
                MouseBrowserCommandName,
                BrowserCommandStrings.MouseBrowserName,
                async context =>
                {
                    try
                    {
                        var action = GetRequiredStringArgument(context.Arguments, "action");
                        var x = GetOptionalIntegerArgument(context.Arguments, "x", 0, -100_000, 100_000);
                        var y = GetOptionalIntegerArgument(context.Arguments, "y", 0, -100_000, 100_000);
                        var button = GetOptionalStringArgument(context.Arguments, "button");
                        var deltaX = GetOptionalIntegerArgument(context.Arguments, "deltaX", 0, -100_000, 100_000);
                        var deltaY = GetOptionalIntegerArgument(context.Arguments, "deltaY", 0, -100_000, 100_000);
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.MouseAsync(context.ResourceName, action, x, y, button, deltaX, deltaY, context.CancellationToken).ConfigureAwait(false);
                        resultJson = await AddSnapshotAfterAsync(sessionManager, context.ResourceName, resultJson, snapshotAfter, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.MouseBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.MouseBrowserDescription,
                    "Cursor",
                    [
                        CreateChoiceArgument("action", BrowserCommandStrings.ActionArgumentLabel, BrowserCommandStrings.MouseActionArgumentDescription, "move", required: true, ["move", "down", "up", "click", "wheel"]),
                        CreateNumberArgument("x", BrowserCommandStrings.XCoordinateArgumentLabel, BrowserCommandStrings.XCoordinateArgumentDescription, "0", required: false),
                        CreateNumberArgument("y", BrowserCommandStrings.YCoordinateArgumentLabel, BrowserCommandStrings.YCoordinateArgumentDescription, "0", required: false),
                        CreateChoiceArgument("button", BrowserCommandStrings.MouseButtonArgumentLabel, BrowserCommandStrings.MouseButtonArgumentDescription, "left", required: false, ["left", "middle", "right"]),
                        CreateNumberArgument("deltaX", BrowserCommandStrings.DeltaXArgumentLabel, BrowserCommandStrings.DeltaXArgumentDescription, "0", required: false),
                        CreateNumberArgument("deltaY", BrowserCommandStrings.DeltaYArgumentLabel, BrowserCommandStrings.DeltaYArgumentDescription, "0", required: false),
                        CreateSnapshotAfterArgument()
                    ]))
            .WithCommand(
                WaitForBrowserCommandName,
                BrowserCommandStrings.WaitForBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetOptionalStringArgument(context.Arguments, "selector");
                        var text = GetOptionalStringArgument(context.Arguments, "text");
                        var timeoutMilliseconds = GetOptionalIntegerArgument(context.Arguments, "timeoutMilliseconds", DefaultBrowserCommandTimeoutMilliseconds, MinimumBrowserCommandTimeoutMilliseconds, MaximumBrowserCommandTimeoutMilliseconds);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.WaitForAsync(context.ResourceName, selector, text, timeoutMilliseconds, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.WaitForBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.WaitForBrowserDescription,
                    "Timer",
                    [
                        CreateSelectorArgument(required: false),
                        CreateTextArgument("text", BrowserCommandStrings.TextArgumentLabel, BrowserCommandStrings.TextArgumentDescription, required: false),
                        CreateNumberArgument("timeoutMilliseconds", BrowserCommandStrings.TimeoutMillisecondsArgumentLabel, BrowserCommandStrings.TimeoutMillisecondsArgumentDescription, DefaultBrowserCommandTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture), required: false)
                    ]))
            .WithCommand(
                WaitCommandName,
                BrowserCommandStrings.WaitBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetOptionalStringArgument(context.Arguments, "selector");
                        var text = GetOptionalStringArgument(context.Arguments, "text");
                        var urlContains = GetOptionalStringArgument(context.Arguments, "urlContains");
                        var url = GetOptionalStringArgument(context.Arguments, "url");
                        var match = GetOptionalStringArgument(context.Arguments, "match") ?? "contains";
                        var loadState = GetOptionalStringArgument(context.Arguments, "loadState");
                        var elementState = GetOptionalStringArgument(context.Arguments, "elementState");
                        var function = GetOptionalStringArgument(context.Arguments, "function");
                        var timeoutMilliseconds = GetOptionalIntegerArgument(context.Arguments, "timeoutMilliseconds", DefaultBrowserCommandTimeoutMilliseconds, MinimumBrowserCommandTimeoutMilliseconds, MaximumBrowserCommandTimeoutMilliseconds);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await ExecuteUnifiedWaitAsync(
                            sessionManager,
                            context.ResourceName,
                            selector,
                            text,
                            urlContains,
                            url,
                            match,
                            loadState,
                            elementState,
                            function,
                            timeoutMilliseconds,
                            context.CancellationToken).ConfigureAwait(false);

                        return BrowserJsonCommandSuccess(BrowserCommandStrings.WaitBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.WaitBrowserDescription,
                    "Timer",
                    [
                        CreateSelectorArgument(required: false),
                        CreateTextArgument("text", BrowserCommandStrings.TextArgumentLabel, BrowserCommandStrings.TextArgumentDescription, required: false),
                        CreateTextArgument("urlContains", BrowserCommandStrings.UrlContainsArgumentLabel, BrowserCommandStrings.UrlContainsArgumentDescription, required: false, placeholder: "/dashboard"),
                        CreateTextArgument("url", BrowserCommandStrings.UrlArgumentLabel, BrowserCommandStrings.WaitUrlArgumentDescription, required: false, placeholder: "/orders"),
                        CreateChoiceArgument("match", BrowserCommandStrings.MatchArgumentLabel, BrowserCommandStrings.MatchArgumentDescription, "contains", required: false, ["contains", "exact", "regex"]),
                        CreateChoiceArgument("loadState", BrowserCommandStrings.LoadStateArgumentLabel, BrowserCommandStrings.LoadStateArgumentDescription, value: null, required: false, ["domcontentloaded", "load", "complete", "networkidle"]),
                        CreateChoiceArgument("elementState", BrowserCommandStrings.ElementStateArgumentLabel, BrowserCommandStrings.ElementStateArgumentDescription, value: null, required: false, ["attached", "detached", "visible", "hidden", "enabled", "disabled", "checked", "unchecked"]),
                        CreateTextArgument("function", BrowserCommandStrings.FunctionArgumentLabel, BrowserCommandStrings.FunctionArgumentDescription, required: false, placeholder: "window.__appReady === true"),
                        CreateNumberArgument("timeoutMilliseconds", BrowserCommandStrings.TimeoutMillisecondsArgumentLabel, BrowserCommandStrings.TimeoutMillisecondsArgumentDescription, DefaultBrowserCommandTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture), required: false)
                    ]))
            .WithCommand(
                WaitForBrowserUrlCommandName,
                BrowserCommandStrings.WaitForBrowserUrlName,
                async context =>
                {
                    try
                    {
                        var url = GetRequiredStringArgument(context.Arguments, "url");
                        var match = GetOptionalStringArgument(context.Arguments, "match") ?? "contains";
                        var timeoutMilliseconds = GetOptionalIntegerArgument(context.Arguments, "timeoutMilliseconds", DefaultBrowserCommandTimeoutMilliseconds, MinimumBrowserCommandTimeoutMilliseconds, MaximumBrowserCommandTimeoutMilliseconds);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.WaitForUrlAsync(context.ResourceName, url, match, timeoutMilliseconds, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.WaitForBrowserUrlSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.WaitForBrowserUrlDescription,
                    "Link",
                    [
                        CreateTextArgument("url", BrowserCommandStrings.UrlArgumentLabel, BrowserCommandStrings.WaitUrlArgumentDescription, required: true, placeholder: "/orders"),
                        CreateChoiceArgument("match", BrowserCommandStrings.MatchArgumentLabel, BrowserCommandStrings.MatchArgumentDescription, "contains", required: false, ["contains", "exact", "regex"]),
                        CreateNumberArgument("timeoutMilliseconds", BrowserCommandStrings.TimeoutMillisecondsArgumentLabel, BrowserCommandStrings.TimeoutMillisecondsArgumentDescription, DefaultBrowserCommandTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture), required: false)
                    ]))
            .WithCommand(
                WaitForBrowserLoadStateCommandName,
                BrowserCommandStrings.WaitForBrowserLoadStateName,
                async context =>
                {
                    try
                    {
                        var state = GetOptionalStringArgument(context.Arguments, "state") ?? "load";
                        var timeoutMilliseconds = GetOptionalIntegerArgument(context.Arguments, "timeoutMilliseconds", DefaultBrowserCommandTimeoutMilliseconds, MinimumBrowserCommandTimeoutMilliseconds, MaximumBrowserCommandTimeoutMilliseconds);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.WaitForLoadStateAsync(context.ResourceName, state, timeoutMilliseconds, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.WaitForBrowserLoadStateSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.WaitForBrowserLoadStateDescription,
                    "ArrowSync",
                    [
                        CreateChoiceArgument("state", BrowserCommandStrings.StateArgumentLabel, BrowserCommandStrings.LoadStateArgumentDescription, "load", required: false, ["domcontentloaded", "load", "networkidle"]),
                        CreateNumberArgument("timeoutMilliseconds", BrowserCommandStrings.TimeoutMillisecondsArgumentLabel, BrowserCommandStrings.TimeoutMillisecondsArgumentDescription, DefaultBrowserCommandTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture), required: false)
                    ]))
            .WithCommand(
                WaitForBrowserElementStateCommandName,
                BrowserCommandStrings.WaitForBrowserElementStateName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var state = GetOptionalStringArgument(context.Arguments, "state") ?? "visible";
                        var timeoutMilliseconds = GetOptionalIntegerArgument(context.Arguments, "timeoutMilliseconds", DefaultBrowserCommandTimeoutMilliseconds, MinimumBrowserCommandTimeoutMilliseconds, MaximumBrowserCommandTimeoutMilliseconds);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.WaitForElementStateAsync(context.ResourceName, selector, state, timeoutMilliseconds, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.WaitForBrowserElementStateSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(
                    BrowserCommandStrings.WaitForBrowserElementStateDescription,
                    "Timer",
                    [
                        CreateSelectorArgument(),
                        CreateChoiceArgument("state", BrowserCommandStrings.StateArgumentLabel, BrowserCommandStrings.ElementStateArgumentDescription, "visible", required: false, ["attached", "detached", "visible", "hidden", "enabled", "disabled", "checked", "unchecked"]),
                        CreateNumberArgument("timeoutMilliseconds", BrowserCommandStrings.TimeoutMillisecondsArgumentLabel, BrowserCommandStrings.TimeoutMillisecondsArgumentDescription, DefaultBrowserCommandTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture), required: false)
                    ]))
            .WithCommand(
                CaptureScreenshotCommandName,
                BrowserCommandStrings.CaptureScreenshotName,
                async context =>
                {
                    try
                    {
                        var format = GetOptionalStringArgument(context.Arguments, "format") ?? "png";
                        if (format is not ("png" or "jpeg" or "webp"))
                        {
                            throw new InvalidOperationException("Screenshot format must be 'png', 'jpeg', or 'webp'.");
                        }

                        var quality = GetOptionalNullableIntegerArgument(context.Arguments, "quality", 0, 100);
                        var fullPage = GetOptionalBooleanArgument(context.Arguments, "fullPage", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var result = await sessionManager.CaptureScreenshotAsync(context.ResourceName, new BrowserScreenshotCaptureOptions(format, quality, fullPage), context.CancellationToken).ConfigureAwait(false);
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
                    Description = BrowserCommandStrings.CaptureScreenshotDescription,
                    IconName = "Camera",
                    IconVariant = IconVariant.Regular,
                    ArgumentInputs =
                    [
                        CreateChoiceArgument("format", BrowserCommandStrings.ScreenshotFormatArgumentLabel, BrowserCommandStrings.ScreenshotFormatArgumentDescription, "png", required: false, ["png", "jpeg", "webp"]),
                        CreateNumberArgument("quality", BrowserCommandStrings.ScreenshotQualityArgumentLabel, BrowserCommandStrings.ScreenshotQualityArgumentDescription, value: null, required: false),
                        CreateBooleanArgument("fullPage", BrowserCommandStrings.FullPageArgumentLabel, BrowserCommandStrings.FullPageArgumentDescription, value: "false", required: false)
                    ],
                    UpdateState = context =>
                    {
                        var childState = context.ResourceSnapshot.State?.Text;
                        return childState == KnownResourceStates.Running
                            ? ResourceCommandState.Enabled
                            : ResourceCommandState.Disabled;
                    }
                })
            .WithCommand(
                CloseTrackedBrowserCommandName,
                BrowserCommandStrings.CloseTrackedBrowserName,
                async context =>
                {
                    try
                    {
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserLogsSessionManager>();
                        var resultJson = await sessionManager.CloseActiveSessionAsync(context.ResourceName, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.CloseTrackedBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                new CommandOptions
                {
                    Description = BrowserCommandStrings.CloseTrackedBrowserDescription,
                    IconName = "Dismiss",
                    IconVariant = IconVariant.Regular,
                    UpdateState = BrowserSessionCommandState
                });

        builder.OnBeforeResourceStarted((_, @event, _) => RefreshBrowserAutomationResourceAsync(@event.Services.GetRequiredService<ResourceNotificationService>()))
               .OnResourceReady((_, @event, _) => RefreshBrowserAutomationResourceAsync(@event.Services.GetRequiredService<ResourceNotificationService>()))
               .OnResourceStopped((_, @event, _) => RefreshBrowserAutomationResourceAsync(@event.Services.GetRequiredService<ResourceNotificationService>()));

        return builder;

        Task RefreshBrowserAutomationResourceAsync(ResourceNotificationService notifications) =>
            notifications.PublishUpdateAsync(browserAutomationResource, snapshot => snapshot);

        static Task<string> ExecuteUnifiedWaitAsync(
            IBrowserLogsSessionManager sessionManager,
            string resourceName,
            string? selector,
            string? text,
            string? urlContains,
            string? url,
            string match,
            string? loadState,
            string? elementState,
            string? function,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(function))
            {
                return sessionManager.WaitForFunctionAsync(resourceName, function, timeoutMilliseconds, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(loadState))
            {
                return sessionManager.WaitForLoadStateAsync(resourceName, loadState, timeoutMilliseconds, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(urlContains))
            {
                return sessionManager.WaitForUrlAsync(resourceName, urlContains, "contains", timeoutMilliseconds, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                return sessionManager.WaitForUrlAsync(resourceName, url, match, timeoutMilliseconds, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(elementState))
            {
                if (string.IsNullOrWhiteSpace(selector))
                {
                    throw new InvalidOperationException("Browser command argument 'selector' is required when 'elementState' is specified.");
                }

                return sessionManager.WaitForElementStateAsync(resourceName, selector, elementState, timeoutMilliseconds, cancellationToken);
            }

            return sessionManager.WaitForAsync(resourceName, selector, text, timeoutMilliseconds, cancellationToken);
        }

        static ImmutableArray<ResourcePropertySnapshot> CreateInitialProperties(string resourceName, BrowserConfiguration configuration)
        {
            List<ResourcePropertySnapshot> properties =
            [
                new(CustomResourceKnownProperties.Source, resourceName),
                new(BrowserPropertyName, configuration.Browser),
                new(UserDataModePropertyName, configuration.UserDataMode.ToString())
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
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, BrowserMessageStrings.BrowserAutomationResourceMissingHttpEndpoint, resource.Name));
            }

            var endpointReference = resource.GetEndpoint(endpointAnnotation.Name);
            if (!endpointReference.IsAllocated)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, BrowserMessageStrings.BrowserLogsEndpointNotAllocated, endpointAnnotation.Name, resource.Name));
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

    /// <summary>
    /// Adds a child resource that can open the application's primary browser endpoint in a tracked browser session,
    /// surface browser diagnostics, automate browser interactions, and capture screenshots.
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="browser">The browser to launch.</param>
    /// <param name="profile">Optional Chromium profile name or directory name to use.</param>
    /// <param name="userDataMode">Optional <see cref="BrowserUserDataMode"/> that selects the tracked browser user data directory mode.</param>
    /// <returns>A reference to the original <see cref="IResourceBuilder{T}"/> for further chaining.</returns>
    [Obsolete("Use WithBrowserAutomation instead.")]
    [Experimental("ASPIREBROWSERAUTOMATION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> WithBrowserLogs<T>(
        this IResourceBuilder<T> builder,
        string? browser = null,
        string? profile = null,
        BrowserUserDataMode? userDataMode = null)
        where T : IResourceWithEndpoints
    {
        return builder.WithBrowserAutomation(browser, profile, userDataMode);
    }

    private static CommandOptions CreateBrowserCommandOptions(string description, string iconName, IReadOnlyList<InteractionInput> argumentInputs)
    {
        return new CommandOptions
        {
            Description = description,
            IconName = iconName,
            IconVariant = IconVariant.Regular,
            ArgumentInputs = argumentInputs,
            Visibility = ResourceCommandVisibility.Api,
            UpdateState = BrowserSessionCommandState
        };
    }

    private static ResourceCommandState BrowserSessionCommandState(UpdateCommandStateContext context)
    {
        return context.ResourceSnapshot.State?.Text == KnownResourceStates.Running
            ? ResourceCommandState.Enabled
            : ResourceCommandState.Disabled;
    }

    private static ExecuteCommandResult BrowserJsonCommandSuccess(string message, string resultJson)
    {
        return CommandResults.Success(
            message,
            new CommandResultData
            {
                Value = resultJson,
                Format = CommandResultFormat.Json,
                DisplayImmediately = true
            });
    }

    private static async Task<string> AddSnapshotAfterAsync(IBrowserLogsSessionManager sessionManager, string resourceName, string resultJson, bool snapshotAfter, CancellationToken cancellationToken)
    {
        if (!snapshotAfter)
        {
            return resultJson;
        }

        var snapshotJson = await sessionManager.GetPageSnapshotAsync(resourceName, DefaultSnapshotMaxElements, DefaultSnapshotMaxTextLength, cancellationToken).ConfigureAwait(false);

        // Browser action and snapshot command results are JSON objects shaped like:
        // { "action": "click", ... } and { "action": "snapshot", "elements": [ ... ] }.
        var result = JsonNode.Parse(resultJson)?.AsObject()
            ?? throw new InvalidOperationException("Browser command returned an invalid JSON object.");
        var snapshot = JsonNode.Parse(snapshotJson)?.AsObject()
            ?? throw new InvalidOperationException("Browser snapshot command returned an invalid JSON object.");

        result["snapshotAfter"] = true;
        result["snapshot"] = snapshot;

        return result.ToJsonString(s_commandResultJsonOptions);
    }

    private static InteractionInput CreateSelectorArgument(bool required = true)
    {
        return CreateTextArgument(
            "selector",
            BrowserCommandStrings.SelectorArgumentLabel,
            BrowserCommandStrings.SelectorArgumentDescription,
            required,
            placeholder: "#submit");
    }

    private static InteractionInput CreateSnapshotAfterArgument()
    {
        return CreateBooleanArgument(
            "snapshotAfter",
            BrowserCommandStrings.SnapshotAfterArgumentLabel,
            BrowserCommandStrings.SnapshotAfterArgumentDescription,
            value: "false",
            required: false);
    }

    private static InteractionInput CreateTextArgument(string name, string label, string description, bool required, string? placeholder = null)
    {
        return new InteractionInput
        {
            Name = name,
            Label = label,
            Description = description,
            InputType = InputType.Text,
            Required = required,
            Placeholder = placeholder,
            MaxLength = 50_000
        };
    }

    private static InteractionInput CreateBooleanArgument(string name, string label, string description, string value, bool required)
    {
        return new InteractionInput
        {
            Name = name,
            Label = label,
            Description = description,
            InputType = InputType.Boolean,
            Required = required,
            Value = value
        };
    }

    private static InteractionInput CreateNumberArgument(string name, string label, string description, string? value, bool required)
    {
        return new InteractionInput
        {
            Name = name,
            Label = label,
            Description = description,
            InputType = InputType.Number,
            Required = required,
            Value = value
        };
    }

    private static InteractionInput CreateChoiceArgument(string name, string label, string description, string? value, bool required, IReadOnlyList<string> options)
    {
        return new InteractionInput
        {
            Name = name,
            Label = label,
            Description = description,
            InputType = InputType.Choice,
            Required = required,
            Value = value,
            Options = options.Select(option => new KeyValuePair<string, string>(option, option)).ToArray()
        };
    }

    private static string GetRequiredStringArgument(JsonElement? arguments, string name, bool allowEmpty = false)
    {
        if (!TryGetStringArgument(arguments, name, out var value) || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
        {
            throw new InvalidOperationException($"Browser command argument '{name}' is required.");
        }

        return value ?? string.Empty;
    }

    private static string? GetOptionalStringArgument(JsonElement? arguments, string name)
    {
        if (!TryGetStringArgument(arguments, name, out var value))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool TryGetStringArgument(JsonElement? arguments, string name, out string? result)
    {
        if (!TryGetArgumentObject(arguments, out var argumentObject) || !argumentObject.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            result = null;
            return false;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Browser command argument '{name}' must be a string.");
        }

        result = value.GetString();
        return true;
    }

    private static int GetOptionalIntegerArgument(JsonElement? arguments, string name, int defaultValue, int minimum, int maximum)
    {
        if (!TryGetArgumentObject(arguments, out var argumentObject) || !argumentObject.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return defaultValue;
        }

        int result;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            result = number;
        }
        else if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringNumber))
        {
            result = stringNumber;
        }
        else
        {
            throw new InvalidOperationException($"Browser command argument '{name}' must be an integer.");
        }

        if (result < minimum || result > maximum)
        {
            throw new InvalidOperationException($"Browser command argument '{name}' must be between {minimum} and {maximum}.");
        }

        return result;
    }

    private static int? GetOptionalNullableIntegerArgument(JsonElement? arguments, string name, int minimum, int maximum)
    {
        if (!TryGetArgumentObject(arguments, out var argumentObject) || !argumentObject.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        int result;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            result = number;
        }
        else if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringNumber))
        {
            result = stringNumber;
        }
        else
        {
            throw new InvalidOperationException($"Browser command argument '{name}' must be an integer.");
        }

        if (result < minimum || result > maximum)
        {
            throw new InvalidOperationException($"Browser command argument '{name}' must be between {minimum} and {maximum}.");
        }

        return result;
    }

    private static bool GetOptionalBooleanArgument(JsonElement? arguments, string name, bool defaultValue)
    {
        if (!TryGetArgumentObject(arguments, out var argumentObject) || !argumentObject.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var result) => result,
            _ => throw new InvalidOperationException($"Browser command argument '{name}' must be a boolean.")
        };
    }

    private static bool TryGetArgumentObject(JsonElement? arguments, out JsonElement argumentObject)
    {
        // Browser command arguments are JSON objects shaped like:
        // { "selector": "...", "value": "...", "timeoutMilliseconds": 10000 }
        if (arguments is not { } value || value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            argumentObject = default;
            return false;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Browser command arguments must be a JSON object.");
        }

        argumentObject = value;
        return true;
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
