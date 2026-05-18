// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only
#pragma warning disable ASPIREBROWSERAUTOMATION001 // Type is for evaluation purposes only

using Aspire.Hosting.Browsers.Resources;
using System.Globalization;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding browser automation resources to browser-based application resources.
/// </summary>
public static partial class BrowserAutomationBuilderExtensions
{
    // Browser resource command registration. The order in this method is intentional because resource command order is
    // the order automation clients and dashboard surfaces see when enumerating available commands.
    private static void AddBrowserCommands<T>(
        IResourceBuilder<T> parentBuilder,
        IResourceBuilder<BrowserResource> browserBuilder,
        BrowserResource browserResource,
        T parentResource)
        where T : IResourceWithEndpoints
    {
        browserBuilder
            // Opens the app endpoint in a tracked browser session so logs, screenshots, and automation commands have a live page to operate on.
            .WithCommand(
                OpenTrackedBrowserCommandName,
                BrowserCommandStrings.OpenTrackedBrowserName,
                async context =>
                {
                    try
                    {
                        var configuration = context.ServiceProvider.GetRequiredService<IConfiguration>();
                        var configurationStore = context.ServiceProvider.GetRequiredService<BrowserConfigurationStore>();
                        var currentConfiguration = browserResource.ResolveCurrentConfiguration(configuration, configurationStore);
                        var url = ResolveBrowserUrl(parentResource);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
                        await sessionManager.StartSessionAsync(browserResource, currentConfiguration, context.ResourceName, url, context.CancellationToken).ConfigureAwait(false);
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
            // Lets the developer choose browser, profile, and user-data mode without editing code or restarting the AppHost.
            .WithCommand(
                ConfigureTrackedBrowserCommandName,
                BrowserCommandStrings.ConfigureTrackedBrowserName,
                async context =>
                {
                    try
                    {
                        var configurationManager = context.ServiceProvider.GetRequiredService<BrowserConfigurationManager>();
                        return await configurationManager.ConfigureAsync(browserResource, context.Arguments, context.CancellationToken).ConfigureAwait(false);
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
                    Arguments = BrowserConfigurationManager.CreateArgumentDefinitions(browserResource, parentBuilder.ApplicationBuilder.UserSecretsManager.IsAvailable),
                    ValidateArguments = context =>
                    {
                        var configurationManager = context.Services.GetRequiredService<BrowserConfigurationManager>();
                        return configurationManager.ValidateInputsAsync(browserResource, context);
                    },
                    UpdateState = context =>
                    {
                        var interactionService = context.ServiceProvider.GetRequiredService<IInteractionService>();
                        return interactionService.IsAvailable
                            ? ResourceCommandState.Enabled
                            : ResourceCommandState.Disabled;
                    }
                })
            // Returns a compact page snapshot so agents can understand the current UI before choosing selectors or actions.
            .WithCommand(
                InspectBrowserCommandName,
                BrowserCommandStrings.InspectBrowserName,
                async context =>
                {
                    try
                    {
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateInspectBrowserArguments))
            // Reads a focused bit of page or element state for assertions and handoffs without requiring raw JavaScript.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateGetArguments))
            // Answers common element-state questions such as visibility, enabled state, or checked state.
            .WithCommand(
                IsCommandName,
                BrowserCommandStrings.IsBrowserName,
                async context =>
                {
                    try
                    {
                        var state = GetRequiredStringArgument(context.Arguments, "state");
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Resolves user-facing locators into a stable selector/ref so follow-up commands can target the right element.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateFindArguments))
            // Visually marks an element in the browser to help humans verify what an agent or script is about to operate on.
            .WithCommand(
                HighlightCommandName,
                BrowserCommandStrings.HighlightBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Provides an escape hatch for page-specific inspection when the structured commands are not expressive enough.
            .WithCommand(
                EvaluateCommandName,
                BrowserCommandStrings.EvaluateBrowserName,
                async context =>
                {
                    try
                    {
                        var expression = GetRequiredStringArgument(context.Arguments, "expression");
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Manages cookies so local-dev tests can seed, inspect, or clear authentication and personalization state.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateCookiesArguments))
            // Manages localStorage/sessionStorage so agents can reproduce client-side stateful scenarios.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateStorageArguments))
            // Captures or restores the combined browser state needed to move between scenarios deterministically.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateStateArguments))
            // Exposes raw CDP for advanced diagnostics and browser features not modeled by the first-class commands.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateCdpArguments))
            // Manages browser targets for multi-tab flows and for recovering or inspecting sessions beyond the active page.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateTabsArguments))
            // Lists frames so agents can detect iframe boundaries before selecting or evaluating content.
            .WithCommand(
                FramesCommandName,
                BrowserCommandStrings.FramesBrowserName,
                async context =>
                {
                    try
                    {
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
                        var resultJson = await sessionManager.FramesAsync(context.ResourceName, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.FramesBrowserSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(BrowserCommandStrings.FramesBrowserDescription, "Window", []))
            // Handles JavaScript dialogs that would otherwise block automation progress.
            .WithCommand(
                DialogCommandName,
                BrowserCommandStrings.DialogBrowserName,
                async context =>
                {
                    try
                    {
                        var action = GetRequiredStringArgument(context.Arguments, "action");
                        var promptText = GetOptionalStringArgument(context.Arguments, "promptText");
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Configures browser download behavior so local-dev flows that produce files can be exercised and observed.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateDownloadsArguments))
            // Sets files on a file input so upload workflows can be tested through the real browser path.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateUploadArguments))
            // Reports the active page URL/title so callers can confirm redirects and navigation results.
            .WithCommand(
                BrowserUrlCommandName,
                BrowserCommandStrings.BrowserUrlName,
                async context =>
                {
                    try
                    {
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
                        var resultJson = await sessionManager.GetUrlAsync(context.ResourceName, context.CancellationToken).ConfigureAwait(false);
                        return BrowserJsonCommandSuccess(BrowserCommandStrings.BrowserUrlSucceeded, resultJson);
                    }
                    catch (Exception ex)
                    {
                        return CommandResults.Failure(ex.Message);
                    }
                },
                CreateBrowserCommandOptions(BrowserCommandStrings.BrowserUrlDescription, "Link", []))
            // Drives browser history backward for flows that depend on realistic user navigation.
            .WithCommand(
                BackBrowserCommandName,
                BrowserCommandStrings.BackBrowserName,
                async context =>
                {
                    try
                    {
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Drives browser history forward after a back navigation without resetting the page session.
            .WithCommand(
                ForwardBrowserCommandName,
                BrowserCommandStrings.ForwardBrowserName,
                async context =>
                {
                    try
                    {
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Reloads the active page to reproduce refresh behavior, cached assets, and app startup state.
            .WithCommand(
                ReloadBrowserCommandName,
                BrowserCommandStrings.ReloadBrowserName,
                async context =>
                {
                    try
                    {
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Navigates the tracked page to an explicit URL while preserving the session and diagnostics pipeline.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
                        var resultJson = await sessionManager.NavigateAsync(browserResource, context.ResourceName, url, context.CancellationToken).ConfigureAwait(false);
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
                    ],
                    ValidateNavigateArguments))
            // Performs the most common user interaction for buttons, links, and custom clickable controls.
            .WithCommand(
                ClickBrowserCommandName,
                BrowserCommandStrings.ClickBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Supports controls whose behavior is specifically tied to double-click rather than single-click.
            .WithCommand(
                DoubleClickBrowserCommandName,
                BrowserCommandStrings.DoubleClickBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Replaces an input value in one step for form scenarios where final field state matters more than key events.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Checks checkbox/radio-style controls through the page event system instead of mutating DOM state directly.
            .WithCommand(
                CheckBrowserCommandName,
                BrowserCommandStrings.CheckBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Unchecks checkbox/radio-style controls for form flows that exercise opt-out or toggled-off paths.
            .WithCommand(
                UncheckBrowserCommandName,
                BrowserCommandStrings.UncheckBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Moves focus to an element so keyboard-only flows and focus-driven UI behavior can be tested.
            .WithCommand(
                FocusBrowserElementCommandName,
                BrowserCommandStrings.FocusBrowserElementName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Types text as key input so autocomplete, validation, and input-event handlers see realistic user typing.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Sends a complete key press for keyboard shortcuts, submit-on-enter, and focus navigation scenarios.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Starts a held key gesture, enabling modifier-key and multi-step keyboard interactions.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Ends a held key gesture so modifier-key and custom keyup handlers can be exercised correctly.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Triggers hover behavior such as menus, tooltips, and CSS hover states.
            .WithCommand(
                HoverBrowserElementCommandName,
                BrowserCommandStrings.HoverBrowserElementName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Selects options in native select controls through normal browser events.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Scrolls containers or the page to reveal lazy-loaded content and exercise viewport-dependent UI.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateScrollArguments))
            // Brings a specific element into view before interacting with controls below the fold or inside scroll panes.
            .WithCommand(
                ScrollIntoViewBrowserCommandName,
                BrowserCommandStrings.ScrollIntoViewBrowserName,
                async context =>
                {
                    try
                    {
                        var selector = GetRequiredStringArgument(context.Arguments, "selector");
                        var snapshotAfter = GetOptionalBooleanArgument(context.Arguments, "snapshotAfter", defaultValue: false);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
            // Sends coordinate-based mouse gestures for canvas, drag-like, or hit-test-sensitive UI.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateMouseArguments))
            // Waits for the common selector/text readiness cases that agents need before the next action.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateWaitForArguments))
            // Provides one wait command that can express the common readiness checks without picking a specialized command.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateWaitArguments))
            // Waits for redirects or client-side routing before asserting or continuing a workflow.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateWaitTimeoutArguments))
            // Waits for document/load/network-idle states when UI readiness is tied to page loading rather than an element.
            .WithCommand(
                WaitForBrowserLoadStateCommandName,
                BrowserCommandStrings.WaitForBrowserLoadStateName,
                async context =>
                {
                    try
                    {
                        var state = GetOptionalStringArgument(context.Arguments, "state") ?? "load";
                        var timeoutMilliseconds = GetOptionalIntegerArgument(context.Arguments, "timeoutMilliseconds", DefaultBrowserCommandTimeoutMilliseconds, MinimumBrowserCommandTimeoutMilliseconds, MaximumBrowserCommandTimeoutMilliseconds);
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateWaitTimeoutArguments))
            // Waits for a particular element state so actions do not race rendering, enablement, or disappearance.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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
                    ],
                    ValidateWaitTimeoutArguments))
            // Captures a durable visual artifact for bug reports, agent reasoning, and human review.
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
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
                        var result = await sessionManager.CaptureScreenshotAsync(context.ResourceName, new BrowserScreenshotCaptureOptions(format, quality, fullPage), context.CancellationToken).ConfigureAwait(false);
                        var resultJson = JsonSerializer.Serialize(
                            new BrowserScreenshotCommandResult(
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
                    Arguments =
                    [
                        CreateChoiceArgument("format", BrowserCommandStrings.ScreenshotFormatArgumentLabel, BrowserCommandStrings.ScreenshotFormatArgumentDescription, "png", required: false, ["png", "jpeg", "webp"]),
                        CreateNumberArgument("quality", BrowserCommandStrings.ScreenshotQualityArgumentLabel, BrowserCommandStrings.ScreenshotQualityArgumentDescription, value: null, required: false),
                        CreateBooleanArgument("fullPage", BrowserCommandStrings.FullPageArgumentLabel, BrowserCommandStrings.FullPageArgumentDescription, value: "false", required: false)
                    ],
                    ValidateArguments = ValidateScreenshotArguments,
                    UpdateState = context =>
                    {
                        var childState = context.ResourceSnapshot.State?.Text;
                        return childState == KnownResourceStates.Running
                            ? ResourceCommandState.Enabled
                            : ResourceCommandState.Disabled;
                    }
                })
            // Stops the tracked browser process so developers can clean up or restart with new configuration.
            .WithCommand(
                CloseTrackedBrowserCommandName,
                BrowserCommandStrings.CloseTrackedBrowserName,
                async context =>
                {
                    try
                    {
                        var sessionManager = context.ServiceProvider.GetRequiredService<IBrowserSessionManager>();
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

        parentBuilder.OnBeforeResourceStarted((_, @event, _) => RefreshBrowserResourceAsync(browserResource, @event.Services.GetRequiredService<ResourceNotificationService>()))
                     .OnResourceReady((_, @event, _) => RefreshBrowserResourceAsync(browserResource, @event.Services.GetRequiredService<ResourceNotificationService>()))
                     .OnResourceStopped((_, @event, _) => RefreshBrowserResourceAsync(browserResource, @event.Services.GetRequiredService<ResourceNotificationService>()));
    }

    private static Task RefreshBrowserResourceAsync(BrowserResource browserResource, ResourceNotificationService notifications) =>
        notifications.PublishUpdateAsync(browserResource, snapshot => snapshot);

    private static Task<string> ExecuteUnifiedWaitAsync(
        IBrowserSessionManager sessionManager,
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

    private static Uri ResolveBrowserUrl<T>(T resource)
        where T : IResourceWithEndpoints
    {
        EndpointAnnotation? endpointAnnotation = null;
        if (resource.TryGetAnnotationsOfType<EndpointAnnotation>(out var endpoints))
        {
            endpointAnnotation = endpoints.FirstOrDefault(e => e.UriScheme == "https")
                ?? endpoints.FirstOrDefault(e => e.UriScheme == "http");
        }

        if (endpointAnnotation is null)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, BrowserMessageStrings.BrowserResourceMissingHttpEndpoint, resource.Name));
        }

        var endpointReference = resource.GetEndpoint(endpointAnnotation.Name);
        if (!endpointReference.IsAllocated)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, BrowserMessageStrings.BrowserEndpointNotAllocated, endpointAnnotation.Name, resource.Name));
        }

        return new Uri(endpointReference.Url, UriKind.Absolute);
    }
}
