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
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding browser automation resources to browser-based application resources.
/// </summary>
[Experimental("ASPIREBROWSERAUTOMATION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static partial class BrowserAutomationBuilderExtensions
{
    internal const string BrowserResourceType = "Browser";
    internal const string BrowserConfigurationSectionName = "Aspire:Hosting:Browser";
    internal const string LegacyBrowserConfigurationSectionName = "Aspire:Hosting:BrowserAutomation";
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
    /// <c>Aspire:Hosting:Browser</c> and otherwise prefers an installed <c>"msedge"</c> browser in shared user data
    /// mode, an installed <c>"chrome"</c> browser in isolated user data mode, and finally falls back to <c>"chrome"</c>.
    /// Supported values include logical
    /// browser names such as <c>"msedge"</c> and <c>"chrome"</c>, or an explicit browser executable path.
    /// </param>
    /// <param name="profile">
    /// Optional Chromium profile name or directory name to use. Only valid when the effective user data mode
    /// is <see cref="BrowserUserDataMode.Shared"/>. When not specified, the tracked browser uses the
    /// configured value from <c>Aspire:Hosting:Browser</c> if present.
    /// </param>
    /// <param name="userDataMode">
    /// Optional <see cref="BrowserUserDataMode"/> that selects whether the tracked browser launches against
    /// a persistent Aspire-managed user data directory shared across all AppHosts on the machine
    /// (<see cref="BrowserUserDataMode.Shared"/>, the default) or a per-AppHost persistent user data directory
    /// (<see cref="BrowserUserDataMode.Isolated"/>). Both modes use Aspire-managed paths under
    /// <c>%LocalAppData%\Aspire\BrowserData</c> on Windows (or platform equivalents); the user's normal browser
    /// profile is never used. When not specified, the tracked browser uses the configured value from
    /// <c>Aspire:Hosting:Browser</c> and otherwise defaults to <see cref="BrowserUserDataMode.Shared"/>.
    /// </param>
    /// <returns>A reference to the original <see cref="IResourceBuilder{T}"/> for further chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method adds a child browser resource beneath the parent resource represented by <paramref name="builder"/>.
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
    /// using <c>Aspire:Hosting:Browser:Browser</c>, <c>Aspire:Hosting:Browser:Profile</c>,
    /// and <c>Aspire:Hosting:Browser:UserDataMode</c>, or scoped to a specific resource with
    /// <c>Aspire:Hosting:Browser:{ResourceName}:Browser</c>,
    /// <c>Aspire:Hosting:Browser:{ResourceName}:Profile</c>, and
    /// <c>Aspire:Hosting:Browser:{ResourceName}:UserDataMode</c>. Explicit method arguments override configuration.
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
    [AspireExport(Description = "Adds browser automation commands that open tracked browser sessions, capture browser diagnostics, automate browser interactions, and capture screenshots.")]
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

        builder.ApplicationBuilder.Services.TryAddSingleton<IBrowserSessionManager, BrowserSessionManager>();
        builder.ApplicationBuilder.Services.TryAddSingleton<BrowserConfigurationStore>();
        builder.ApplicationBuilder.Services.TryAddSingleton<BrowserConfigurationManager>();

        var parentResource = builder.Resource;
        var explicitConfigurationValues = new BrowserConfigurationExplicitValues(browser, profile, userDataMode);
        var initialConfiguration = BrowserConfiguration.Resolve(builder.ApplicationBuilder.Configuration, parentResource.Name, explicitConfigurationValues);
        var browserResource = new BrowserResource(
            $"{parentResource.Name}-browser",
            parentResource,
            initialConfiguration,
            explicitConfigurationValues);
        browserResource.Annotations.Add(NameValidationPolicyAnnotation.None);

        var browserBuilder = builder.ApplicationBuilder.AddResource(browserResource)
            .WithParentRelationship(parentResource)
            .ExcludeFromManifest()
            .WithIconName("GlobeDesktop")
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = BrowserResourceType,
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.NotStarted,
                Properties = CreateInitialProperties(parentResource.Name, initialConfiguration)
            });

        AddBrowserCommands(builder, browserBuilder, browserResource, parentResource);

        return builder;
    }

    private static ImmutableArray<ResourcePropertySnapshot> CreateInitialProperties(string resourceName, BrowserConfiguration configuration)
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

    private static void ThrowIfBlankWhenSpecified(string? value, string paramName)
    {
        if (value is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        }
    }

    private static CommandOptions CreateBrowserCommandOptions(
        string description,
        string iconName,
        IReadOnlyList<InteractionInput> argumentInputs,
        Func<InputsDialogValidationContext, Task>? validateArguments = null)
    {
        return new CommandOptions
        {
            Description = description,
            IconName = iconName,
            IconVariant = IconVariant.Regular,
            Arguments = argumentInputs,
            ValidateArguments = validateArguments,
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

    private static async Task<string> AddSnapshotAfterAsync(IBrowserSessionManager sessionManager, string resourceName, string resultJson, bool snapshotAfter, CancellationToken cancellationToken)
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

    private static string GetRequiredStringArgument(InteractionInputCollection arguments, string name, bool allowEmpty = false)
    {
        if (!TryGetStringArgument(arguments, name, out var value) || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
        {
            throw new InvalidOperationException($"Browser command argument '{name}' is required.");
        }

        return value ?? string.Empty;
    }

    private static string? GetOptionalStringArgument(InteractionInputCollection arguments, string name)
    {
        if (!TryGetStringArgument(arguments, name, out var value))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool TryGetStringArgument(InteractionInputCollection arguments, string name, out string? result)
    {
        if (!arguments.TryGetByName(name, out var input) || input.Value is null)
        {
            result = null;
            return false;
        }

        result = input.Value;
        return true;
    }

    private static int GetOptionalIntegerArgument(InteractionInputCollection arguments, string name, int defaultValue, int minimum, int maximum)
    {
        if (!TryGetStringArgument(arguments, name, out var value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new InvalidOperationException($"Browser command argument '{name}' must be an integer.");
        }

        if (result < minimum || result > maximum)
        {
            throw new InvalidOperationException($"Browser command argument '{name}' must be between {minimum} and {maximum}.");
        }

        return result;
    }

    private static int? GetOptionalNullableIntegerArgument(InteractionInputCollection arguments, string name, int minimum, int maximum)
    {
        if (!TryGetStringArgument(arguments, name, out var value))
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new InvalidOperationException($"Browser command argument '{name}' must be an integer.");
        }

        if (result < minimum || result > maximum)
        {
            throw new InvalidOperationException($"Browser command argument '{name}' must be between {minimum} and {maximum}.");
        }

        return result;
    }

    private static bool GetOptionalBooleanArgument(InteractionInputCollection arguments, string name, bool defaultValue)
    {
        if (!TryGetStringArgument(arguments, name, out var value))
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var result)
            ? result
            : throw new InvalidOperationException($"Browser command argument '{name}' must be a boolean.");
    }

    private static Task ValidateInspectBrowserArguments(InputsDialogValidationContext context)
    {
        ValidateOptionalIntegerArgument(context, "maxElements", 1, 500);
        ValidateOptionalIntegerArgument(context, "maxTextLength", 100, 50_000);

        return Task.CompletedTask;
    }

    private static Task ValidateGetArguments(InputsDialogValidationContext context)
    {
        var property = GetValidationValue(context, "property");
        switch (property)
        {
            case "value":
            case "count":
            case "box":
            case "styles":
                ValidateRequiredArgument(context, "selector", $"The '{property}' property requires a selector.");
                break;
            case "attr":
                ValidateRequiredArgument(context, "selector", "The 'attr' property requires a selector.");
                ValidateRequiredArgument(context, "name", "The 'attr' property requires a name.");
                break;
        }

        return Task.CompletedTask;
    }

    private static Task ValidateFindArguments(InputsDialogValidationContext context)
    {
        ValidateOptionalIntegerArgument(context, "index", 1, 10_000);

        return Task.CompletedTask;
    }

    private static Task ValidateCookiesArguments(InputsDialogValidationContext context)
    {
        if (GetValidationValue(context, "action") == "set")
        {
            ValidateRequiredArgument(context, "name", "Cookie name is required when action is 'set'.");
            ValidateRequiredArgument(context, "value", allowEmpty: true, errorMessage: "Cookie value is required when action is 'set'.");
        }

        return Task.CompletedTask;
    }

    private static Task ValidateStorageArguments(InputsDialogValidationContext context)
    {
        if (GetValidationValue(context, "action") == "set")
        {
            ValidateRequiredArgument(context, "key", "Storage key is required when action is 'set'.");
            ValidateRequiredArgument(context, "value", allowEmpty: true, errorMessage: "Storage value is required when action is 'set'.");
        }

        return Task.CompletedTask;
    }

    private static Task ValidateStateArguments(InputsDialogValidationContext context)
    {
        if (GetValidationValue(context, "action") == "set")
        {
            ValidateRequiredArgument(context, "state", "State JSON is required when action is 'set'.");
            ValidateJsonObjectArgument(context, "state", "State must be a JSON object.");
        }

        return Task.CompletedTask;
    }

    private static Task ValidateCdpArguments(InputsDialogValidationContext context)
    {
        ValidateJsonObjectArgument(context, "params", "CDP params must be a JSON object.");

        return Task.CompletedTask;
    }

    private static Task ValidateTabsArguments(InputsDialogValidationContext context)
    {
        switch (GetValidationValue(context, "action"))
        {
            case "open":
                ValidateRequiredArgument(context, "url", "A URL is required when action is 'open'.");
                break;
            case "close":
                ValidateRequiredArgument(context, "targetId", "A target id is required when action is 'close'.");
                break;
        }

        return Task.CompletedTask;
    }

    private static Task ValidateDownloadsArguments(InputsDialogValidationContext context)
    {
        if (GetValidationValue(context, "behavior") is "allow" or "allowAndName")
        {
            ValidateRequiredArgument(context, "downloadPath", "A download path is required when behavior allows downloads.");
        }

        return Task.CompletedTask;
    }

    private static Task ValidateUploadArguments(InputsDialogValidationContext context)
    {
        var files = GetValidationValue(context, "files");
        if (string.IsNullOrWhiteSpace(files))
        {
            return Task.CompletedTask;
        }

        var trimmed = files.Trim();
        if (!trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        try
        {
            var filePaths = JsonSerializer.Deserialize<string[]>(trimmed);
            if (filePaths is not { Length: > 0 } || filePaths.Any(string.IsNullOrWhiteSpace))
            {
                AddValidationError(context, "files", "Files JSON must be a non-empty array of file paths.");
            }
        }
        catch (JsonException)
        {
            AddValidationError(context, "files", "Files must be a file path or a JSON array of file paths.");
        }

        return Task.CompletedTask;
    }

    private static Task ValidateNavigateArguments(InputsDialogValidationContext context)
    {
        var url = GetValidationValue(context, "url");
        if (!string.IsNullOrWhiteSpace(url) && !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            AddValidationError(context, "url", "URL must be an absolute URI.");
        }

        return Task.CompletedTask;
    }

    private static Task ValidateScrollArguments(InputsDialogValidationContext context)
    {
        ValidateOptionalIntegerArgument(context, "deltaX", -100_000, 100_000);
        ValidateOptionalIntegerArgument(context, "deltaY", -100_000, 100_000);

        return Task.CompletedTask;
    }

    private static Task ValidateMouseArguments(InputsDialogValidationContext context)
    {
        ValidateOptionalIntegerArgument(context, "x", -100_000, 100_000);
        ValidateOptionalIntegerArgument(context, "y", -100_000, 100_000);
        ValidateOptionalIntegerArgument(context, "deltaX", -100_000, 100_000);
        ValidateOptionalIntegerArgument(context, "deltaY", -100_000, 100_000);

        return Task.CompletedTask;
    }

    private static Task ValidateWaitForArguments(InputsDialogValidationContext context)
    {
        ValidateAtLeastOneArgument(context, ["selector", "text"], "Provide a selector, text, or both when waiting in the browser.");
        ValidateOptionalIntegerArgument(context, "timeoutMilliseconds", MinimumBrowserCommandTimeoutMilliseconds, MaximumBrowserCommandTimeoutMilliseconds);

        return Task.CompletedTask;
    }

    private static Task ValidateWaitArguments(InputsDialogValidationContext context)
    {
        ValidateAtLeastOneArgument(
            context,
            ["selector", "text", "urlContains", "url", "loadState", "elementState", "function"],
            "Provide at least one wait condition.");

        if (!string.IsNullOrWhiteSpace(GetValidationValue(context, "elementState")))
        {
            ValidateRequiredArgument(context, "selector", "Selector is required when elementState is specified.");
        }

        ValidateOptionalIntegerArgument(context, "timeoutMilliseconds", MinimumBrowserCommandTimeoutMilliseconds, MaximumBrowserCommandTimeoutMilliseconds);

        return Task.CompletedTask;
    }

    private static Task ValidateWaitTimeoutArguments(InputsDialogValidationContext context)
    {
        ValidateOptionalIntegerArgument(context, "timeoutMilliseconds", MinimumBrowserCommandTimeoutMilliseconds, MaximumBrowserCommandTimeoutMilliseconds);

        return Task.CompletedTask;
    }

    private static Task ValidateScreenshotArguments(InputsDialogValidationContext context)
    {
        ValidateOptionalIntegerArgument(context, "quality", 0, 100);

        return Task.CompletedTask;
    }

    private static void ValidateOptionalIntegerArgument(InputsDialogValidationContext context, string name, int minimum, int maximum)
    {
        var value = GetValidationValue(context, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integerValue))
        {
            AddValidationError(context, name, "Value must be an integer.");
            return;
        }

        if (integerValue < minimum || integerValue > maximum)
        {
            AddValidationError(context, name, $"Value must be between {minimum} and {maximum}.");
        }
    }

    private static void ValidateJsonObjectArgument(InputsDialogValidationContext context, string name, string errorMessage)
    {
        var value = GetValidationValue(context, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                AddValidationError(context, name, errorMessage);
            }
        }
        catch (JsonException)
        {
            AddValidationError(context, name, errorMessage);
        }
    }

    private static void ValidateAtLeastOneArgument(InputsDialogValidationContext context, IReadOnlyList<string> names, string errorMessage)
    {
        if (names.Any(name => !string.IsNullOrWhiteSpace(GetValidationValue(context, name))))
        {
            return;
        }

        AddValidationError(context, names[0], errorMessage);
    }

    private static void ValidateRequiredArgument(InputsDialogValidationContext context, string name, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(GetValidationValue(context, name)))
        {
            AddValidationError(context, name, errorMessage);
        }
    }

    private static void ValidateRequiredArgument(InputsDialogValidationContext context, string name, bool allowEmpty, string errorMessage)
    {
        var value = GetValidationValue(context, name);
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
        {
            AddValidationError(context, name, errorMessage);
        }
    }

    private static string? GetValidationValue(InputsDialogValidationContext context, string name)
    {
        return context.Inputs.TryGetByName(name, out var input) ? input.Value : null;
    }

    private static void AddValidationError(InputsDialogValidationContext context, string name, string errorMessage)
    {
        if (context.Inputs.TryGetByName(name, out var input))
        {
            context.AddValidationError(input, errorMessage);
        }
    }

    private sealed record BrowserScreenshotCommandResult(
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
