// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREBROWSERAUTOMATION001 // Type is for evaluation purposes only

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

// Resource command implementations. Each method resolves the active tracked browser session for a browser child resource,
// then either evaluates a BrowserScripts-generated page script with Runtime.evaluate or sends a focused CDP command. The
// split keeps the automation command surface reviewable without mixing it with session lifecycle, resource snapshot, and
// disposal mechanics.
internal sealed partial class BrowserSessionManager
{
    private const int WaitCommandEvaluationTimeoutGraceMilliseconds = 5_000;
    private static readonly JsonSerializerOptions s_browserCommandEnvelopeJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds and runs the page snapshot script against the active page.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateSnapshotExpression"/> and <see cref="BrowserRunningSession.EvaluateJsonAsync"/>
    /// so the snapshot is produced from live DOM/accessibility metadata inside the page.
    /// </remarks>
    public async Task<string> GetPageSnapshotAsync(string resourceName, int maxElements, int maxTextLength, CancellationToken cancellationToken)
    {
        var activeSession = await GetActiveSessionAsync(resourceName, "inspect", cancellationToken).ConfigureAwait(false);
        var expression = BrowserScripts.CreateSnapshotExpression(maxElements, maxTextLength);
        return await activeSession.Session.EvaluateJsonAsync(expression, timeout: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a page or element property through a structured browser-side script.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateGetExpression"/> for common state queries so callers do not need the raw
    /// evaluate command for title, URL, text, attributes, counts, bounds, or styles.
    /// </remarks>
    public async Task<string> GetAsync(string resourceName, string property, string? selector, string? name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);

        var activeSession = await GetActiveSessionAsync(resourceName, "get", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateGetExpression(property, selector, name),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks a common element state on the active page.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateIsExpression"/> because visibility, enabled state, and checked state require
    /// DOM/style inspection in the target browser.
    /// </remarks>
    public async Task<string> IsAsync(string resourceName, string state, string selector, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var activeSession = await GetActiveSessionAsync(resourceName, "is", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateIsExpression(state, selector),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a user-facing locator into the concrete element metadata used by follow-up commands.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateFindExpression"/> so role/text/label/snapshot-ref lookup happens against the
    /// live page DOM.
    /// </remarks>
    public async Task<string> FindAsync(string resourceName, string kind, string value, string? name, int index, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var activeSession = await GetActiveSessionAsync(resourceName, "find", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateFindExpression(kind, value, name, index),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a visual highlight to the selected element in the active browser.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateHighlightExpression"/> so a human can see which element the automation flow
    /// selected before continuing.
    /// </remarks>
    public async Task<string> HighlightAsync(string resourceName, string selector, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var activeSession = await GetActiveSessionAsync(resourceName, "highlight", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateHighlightExpression(selector),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs caller-authored JavaScript in the active page and returns a serialized value.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateEvaluateExpression"/> as the explicit escape hatch when structured commands
    /// cannot express a page-specific diagnostic.
    /// </remarks>
    public async Task<string> EvaluateAsync(string resourceName, string expression, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        var activeSession = await GetActiveSessionAsync(resourceName, "evaluate", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateEvaluateExpression(expression),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets, sets, or clears cookies visible to the active page origin.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateCookiesExpression"/> because cookie mutation and inspection are page-origin
    /// scoped browser operations.
    /// </remarks>
    public async Task<string> CookiesAsync(string resourceName, string action, string? name, string? value, string? domain, string? path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var activeSession = await GetActiveSessionAsync(resourceName, "cookies", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateCookiesExpression(action, name, value, domain, path),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets, sets, or clears Web Storage entries for the active page origin.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateStorageExpression"/> to access localStorage/sessionStorage from the page
    /// context where those origin-scoped APIs are available.
    /// </remarks>
    public async Task<string> StorageAsync(string resourceName, string area, string action, string? key, string? value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var activeSession = await GetActiveSessionAsync(resourceName, "storage", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateStorageExpression(area, action, key, value),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures, restores, or clears combined cookie and Web Storage state.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateStateExpression"/> to make repeatable local-dev scenarios possible with one
    /// command instead of several cookie/storage calls.
    /// </remarks>
    public async Task<string> StateAsync(string resourceName, string action, string? state, bool clearExisting, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var activeSession = await GetActiveSessionAsync(resourceName, "state", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateStateExpression(action, state, clearExisting),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a raw Chrome DevTools Protocol command to the selected browser or page session.
    /// </summary>
    /// <remarks>
    /// This bypasses <see cref="BrowserScripts"/> for scenarios already represented by CDP, or diagnostics not yet modeled
    /// as structured resource commands.
    /// </remarks>
    public async Task<string> CdpAsync(string resourceName, string method, string? parametersJson, string session, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(session);

        var activeSession = await GetActiveSessionAsync(resourceName, "send CDP command", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.SendCdpCommandJsonAsync(method, parametersJson, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists, opens, or closes browser targets using the CDP Target domain.
    /// </summary>
    /// <remarks>
    /// Tabs are browser-process state, so this uses Target.getTargets, Target.createTarget, or Target.closeTarget instead
    /// of page JavaScript.
    /// </remarks>
    public async Task<string> TabsAsync(string resourceName, string action, string? url, string? targetId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var activeSession = await GetActiveSessionAsync(resourceName, "manage tabs", cancellationToken).ConfigureAwait(false);
        var resultJson = action switch
        {
            "list" => await activeSession.Session.SendCdpCommandJsonAsync("Target.getTargets", parametersJson: null, "browser", cancellationToken).ConfigureAwait(false),
            "open" => await activeSession.Session.SendCdpCommandJsonAsync(
                "Target.createTarget",
                JsonSerializer.Serialize(new { url = GetRequiredUrlText(url) }),
                "browser",
                cancellationToken).ConfigureAwait(false),
            "close" => await activeSession.Session.SendCdpCommandJsonAsync(
                "Target.closeTarget",
                JsonSerializer.Serialize(new { targetId = GetRequiredTargetId(targetId) }),
                "browser",
                cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Tab action must be 'list', 'open', or 'close'.")
        };

        return CreateBrowserCommandEnvelope("tabs", resultJson);
    }

    /// <summary>
    /// Reads iframe/frame information from the active page.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateFramesExpression"/> because frame metadata needs both the live DOM iframe
    /// elements and the page's current frame tree view.
    /// </remarks>
    public async Task<string> FramesAsync(string resourceName, CancellationToken cancellationToken)
    {
        var activeSession = await GetActiveSessionAsync(resourceName, "list frames", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateFramesExpression(),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts or dismisses the currently displayed JavaScript dialog.
    /// </summary>
    /// <remarks>
    /// Uses Page.handleJavaScriptDialog through CDP because modal browser dialogs are browser-controlled and cannot be
    /// handled by page-local JavaScript after they appear.
    /// </remarks>
    public async Task<string> DialogAsync(string resourceName, string action, string? promptText, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var accept = action switch
        {
            "accept" => true,
            "dismiss" => false,
            _ => throw new InvalidOperationException("Dialog action must be 'accept' or 'dismiss'.")
        };

        var activeSession = await GetActiveSessionAsync(resourceName, "handle dialog", cancellationToken).ConfigureAwait(false);
        var parametersJson = promptText is null
            ? JsonSerializer.Serialize(new { accept })
            : JsonSerializer.Serialize(new { accept, promptText });
        var resultJson = await activeSession.Session.SendCdpCommandJsonAsync("Page.handleJavaScriptDialog", parametersJson, "page", cancellationToken).ConfigureAwait(false);

        return CreateBrowserCommandEnvelope("dialog", resultJson);
    }

    /// <summary>
    /// Configures browser download handling for the active browser process.
    /// </summary>
    /// <remarks>
    /// Uses Browser.setDownloadBehavior through CDP because download destination and event behavior are controlled by the
    /// browser process rather than the page DOM.
    /// </remarks>
    public async Task<string> DownloadsAsync(string resourceName, string behavior, string? downloadPath, bool eventsEnabled, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(behavior);

        if (behavior is not ("allow" or "deny" or "default" or "allowAndName"))
        {
            throw new InvalidOperationException("Download behavior must be 'allow', 'allowAndName', 'deny', or 'default'.");
        }

        if ((behavior is "allow" or "allowAndName") && string.IsNullOrWhiteSpace(downloadPath))
        {
            throw new InvalidOperationException("A download path is required when download behavior is 'allow' or 'allowAndName'.");
        }

        var parametersJson = string.IsNullOrWhiteSpace(downloadPath)
            ? JsonSerializer.Serialize(new { behavior, eventsEnabled })
            : JsonSerializer.Serialize(new { behavior, downloadPath, eventsEnabled });

        var activeSession = await GetActiveSessionAsync(resourceName, "configure downloads", cancellationToken).ConfigureAwait(false);
        var resultJson = await activeSession.Session.SendCdpCommandJsonAsync("Browser.setDownloadBehavior", parametersJson, "browser", cancellationToken).ConfigureAwait(false);

        return CreateBrowserCommandEnvelope("downloads", resultJson);
    }

    /// <summary>
    /// Sets selected files on a file input element.
    /// </summary>
    /// <remarks>
    /// Uses DOM.getDocument, DOM.querySelector, and DOM.setFileInputFiles through CDP because browsers intentionally do not
    /// allow page JavaScript to assign arbitrary host file paths to a file input.
    /// </remarks>
    public async Task<string> UploadAsync(string resourceName, string selector, string files, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ArgumentException.ThrowIfNullOrWhiteSpace(files);

        var filePaths = ParseFilePaths(files);
        if (filePaths.Length == 0)
        {
            throw new InvalidOperationException("At least one file path is required.");
        }

        var activeSession = await GetActiveSessionAsync(resourceName, "upload files", cancellationToken).ConfigureAwait(false);
        var documentJson = await activeSession.Session.SendCdpCommandJsonAsync("DOM.getDocument", parametersJson: null, "page", cancellationToken).ConfigureAwait(false);
        var rootNodeId = GetIntegerProperty(documentJson, "root", "nodeId");
        var queryJson = await activeSession.Session.SendCdpCommandJsonAsync(
            "DOM.querySelector",
            JsonSerializer.Serialize(new { nodeId = rootNodeId, selector }),
            "page",
            cancellationToken).ConfigureAwait(false);
        var nodeId = GetIntegerProperty(queryJson, "nodeId");
        if (nodeId == 0)
        {
            throw new InvalidOperationException($"Element '{selector}' was not found.");
        }

        var resultJson = await activeSession.Session.SendCdpCommandJsonAsync(
            "DOM.setFileInputFiles",
            JsonSerializer.Serialize(new { files = filePaths, nodeId }),
            "page",
            cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            action = "upload",
            selector,
            files = filePaths,
            result = JsonSerializer.Deserialize<JsonElement>(resultJson)
        });
    }

    /// <summary>
    /// Reads the current active page URL.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateUrlExpression"/> so the value comes from location.href in the active page
    /// context.
    /// </remarks>
    public async Task<string> GetUrlAsync(string resourceName, CancellationToken cancellationToken)
    {
        var activeSession = await GetActiveSessionAsync(resourceName, "get URL", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateUrlExpression(),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Navigates the active page one entry back in browser history.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateHistoryNavigationExpression"/> so the page performs the same history operation
    /// a user would trigger from the browser UI.
    /// </remarks>
    public async Task<string> GoBackAsync(string resourceName, CancellationToken cancellationToken)
    {
        var activeSession = await GetActiveSessionAsync(resourceName, "go back", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateHistoryNavigationExpression("back"),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Navigates the active page one entry forward in browser history.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateHistoryNavigationExpression"/> to mirror the browser forward action and return
    /// the resulting page URL.
    /// </remarks>
    public async Task<string> GoForwardAsync(string resourceName, CancellationToken cancellationToken)
    {
        var activeSession = await GetActiveSessionAsync(resourceName, "go forward", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateHistoryNavigationExpression("forward"),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reloads the active page.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateHistoryNavigationExpression"/> so reload is performed from the page context and
    /// returns a consistent command envelope.
    /// </remarks>
    public async Task<string> ReloadAsync(string resourceName, CancellationToken cancellationToken)
    {
        var activeSession = await GetActiveSessionAsync(resourceName, "reload", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateHistoryNavigationExpression("reload"),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Navigates the active page to a new URL and updates Aspire's tracked target URL.
    /// </summary>
    /// <remarks>
    /// Uses Page.navigate through CDP because navigation is a browser protocol operation; after CDP accepts it, the method
    /// updates resource state so dashboard commands and snapshots point at the new URL.
    /// </remarks>
    public async Task<string> NavigateAsync(BrowserResource resource, string resourceName, Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(url);

        var activeSession = await GetActiveSessionAsync(resourceName, "navigate", cancellationToken).ConfigureAwait(false);
        await activeSession.Session.NavigateAsync(url, cancellationToken).ConfigureAwait(false);
        await UpdateActiveSessionTargetUrlAsync(resource, resourceName, activeSession.SessionId, url, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(
            new BrowserNavigateCommandResult(
                activeSession.SessionId,
                activeSession.Browser,
                activeSession.TargetId,
                url.ToString()),
            s_browserSessionPropertyJsonOptions);
    }

    /// <summary>
    /// Clicks a selected element in the active page.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateClickExpression"/> so focus, DOM events, and element-specific click behavior
    /// run inside the page.
    /// </remarks>
    public async Task<string> ClickAsync(string resourceName, string selector, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var activeSession = await GetActiveSessionAsync(resourceName, "click", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateClickExpression(selector),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Double-clicks a selected element in the active page.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateDoubleClickExpression"/> to emit the mouse and dblclick event sequence many UI
    /// components expect.
    /// </remarks>
    public async Task<string> DoubleClickAsync(string resourceName, string selector, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var activeSession = await GetActiveSessionAsync(resourceName, "double click", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateDoubleClickExpression(selector),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets an input-like element to a final value.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateFillExpression"/> so input/change events notify framework bindings after the
    /// value is assigned.
    /// </remarks>
    public async Task<string> FillAsync(string resourceName, string selector, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ArgumentNullException.ThrowIfNull(value);

        var activeSession = await GetActiveSessionAsync(resourceName, "fill", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateFillExpression(selector, value),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks a checkbox or radio-style control.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateCheckExpression"/> to mutate the DOM property and fire form events in the page.
    /// </remarks>
    public async Task<string> CheckAsync(string resourceName, string selector, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var activeSession = await GetActiveSessionAsync(resourceName, "check", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateCheckExpression(selector, isChecked: true),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Unchecks a checkbox-style control.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateCheckExpression"/> with the unchecked state so the same event path is used as
    /// the check command.
    /// </remarks>
    public async Task<string> UncheckAsync(string resourceName, string selector, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var activeSession = await GetActiveSessionAsync(resourceName, "uncheck", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateCheckExpression(selector, isChecked: false),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves browser focus to a selected element.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateFocusExpression"/> so subsequent keyboard commands target the browser's actual
    /// active element.
    /// </remarks>
    public async Task<string> FocusAsync(string resourceName, string selector, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var activeSession = await GetActiveSessionAsync(resourceName, "focus", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateFocusExpression(selector),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Types text into a selected element.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateTypeExpression"/> to dispatch keyboard and input events for each character,
    /// enabling autocomplete and validation scenarios.
    /// </remarks>
    public async Task<string> TypeAsync(string resourceName, string selector, string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ArgumentNullException.ThrowIfNull(text);

        var activeSession = await GetActiveSessionAsync(resourceName, "type text", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateTypeExpression(selector, text),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Presses one key on a selected element or the current focus target.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreatePressExpression"/> for submit-on-enter, shortcuts, and other single-key
    /// behaviors that depend on page event handlers.
    /// </remarks>
    public async Task<string> PressAsync(string resourceName, string? selector, string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var activeSession = await GetActiveSessionAsync(resourceName, "press keys", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreatePressExpression(selector, key),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hovers over a selected element.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateHoverExpression"/> to dispatch pointer and mouse events that trigger menus,
    /// tooltips, and CSS hover behavior.
    /// </remarks>
    public async Task<string> HoverAsync(string resourceName, string selector, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var activeSession = await GetActiveSessionAsync(resourceName, "hover", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateHoverExpression(selector),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Selects an option in a native select element.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateSelectExpression"/> so option resolution and change events happen in the page.
    /// </remarks>
    public async Task<string> SelectAsync(string resourceName, string selector, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ArgumentNullException.ThrowIfNull(value);

        var activeSession = await GetActiveSessionAsync(resourceName, "select", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateSelectExpression(selector, value),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Scrolls the page window or a selected scroll container.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateScrollExpression"/> so viewport-dependent or lazy-loaded content can be
    /// revealed before later commands run.
    /// </remarks>
    public async Task<string> ScrollAsync(string resourceName, string? selector, int deltaX, int deltaY, CancellationToken cancellationToken)
    {
        var activeSession = await GetActiveSessionAsync(resourceName, "scroll", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateScrollExpression(selector, deltaX, deltaY),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Brings a selected element into the viewport.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateScrollIntoViewExpression"/> as a preparation step for mouse, keyboard, or
    /// inspection commands that need the element visible.
    /// </remarks>
    public async Task<string> ScrollIntoViewAsync(string resourceName, string selector, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var activeSession = await GetActiveSessionAsync(resourceName, "scroll into view", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateScrollIntoViewExpression(selector),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatches a keydown event in the active page.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateKeyEventExpression"/> for modifier and held-key scenarios where keydown and
    /// keyup are separate commands.
    /// </remarks>
    public async Task<string> KeyDownAsync(string resourceName, string? selector, string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var activeSession = await GetActiveSessionAsync(resourceName, "key down", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateKeyEventExpression("keydown", selector, key),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatches a keyup event in the active page.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateKeyEventExpression"/> to complete modifier and held-key sequences started by
    /// keydown.
    /// </remarks>
    public async Task<string> KeyUpAsync(string resourceName, string? selector, string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var activeSession = await GetActiveSessionAsync(resourceName, "key up", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateKeyEventExpression("keyup", selector, key),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatches coordinate-based mouse input in the active page.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateMouseExpression"/> for hit-test-sensitive UI such as canvas, wheel, or
    /// coordinate-specific interactions.
    /// </remarks>
    public async Task<string> MouseAsync(string resourceName, string action, int x, int y, string? button, int deltaX, int deltaY, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var activeSession = await GetActiveSessionAsync(resourceName, "mouse input", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateMouseExpression(action, x, y, button, deltaX, deltaY),
            timeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a selector, page text, or both to be present in the active page.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateWaitForExpression"/> with an evaluation timeout so polling happens inside the
    /// page instead of repeatedly round-tripping through CDP.
    /// </remarks>
    public async Task<string> WaitForAsync(string resourceName, string? selector, string? text, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(selector) && string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Provide a selector, text, or both when waiting in the browser.");
        }

        var activeSession = await GetActiveSessionAsync(resourceName, "wait", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateWaitForExpression(selector, text, timeoutMilliseconds),
            CreateEvaluationTimeout(timeoutMilliseconds),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the active page URL to match an expected value.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateWaitForUrlExpression"/> to coordinate redirect and client-side-routing flows
    /// before the next command runs.
    /// </remarks>
    public async Task<string> WaitForUrlAsync(string resourceName, string url, string match, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(match);

        var activeSession = await GetActiveSessionAsync(resourceName, "wait for URL", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateWaitForUrlExpression(url, match, timeoutMilliseconds),
            CreateEvaluationTimeout(timeoutMilliseconds),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for document readiness or the browser automation network-idle approximation.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateWaitForLoadStateExpression"/> because readiness combines document.readyState
    /// and in-page resource timing observations.
    /// </remarks>
    public async Task<string> WaitForLoadStateAsync(string resourceName, string state, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        var activeSession = await GetActiveSessionAsync(resourceName, "wait for load state", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateWaitForLoadStateExpression(state, timeoutMilliseconds),
            CreateEvaluationTimeout(timeoutMilliseconds),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for one element to reach an attachment, visibility, enabled, or checked state.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateWaitForElementStateExpression"/> so live DOM and style state are polled in the
    /// target page until the requested condition is true.
    /// </remarks>
    public async Task<string> WaitForElementStateAsync(string resourceName, string selector, string state, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        var activeSession = await GetActiveSessionAsync(resourceName, "wait for element state", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateWaitForElementStateExpression(selector, state, timeoutMilliseconds),
            CreateEvaluationTimeout(timeoutMilliseconds),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a caller-authored page predicate to return a truthy value.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="BrowserScripts.CreateWaitForFunctionExpression"/> as the readiness escape hatch for app-specific
    /// conditions not covered by the structured wait commands.
    /// </remarks>
    public async Task<string> WaitForFunctionAsync(string resourceName, string function, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(function);

        var activeSession = await GetActiveSessionAsync(resourceName, "wait for function", cancellationToken).ConfigureAwait(false);
        return await activeSession.Session.EvaluateJsonAsync(
            BrowserScripts.CreateWaitForFunctionExpression(function, timeoutMilliseconds),
            CreateEvaluationTimeout(timeoutMilliseconds),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the active tracked browser session and returns the closed-session metadata.
    /// </summary>
    /// <remarks>
    /// Uses the session lifecycle path instead of page JavaScript or raw CDP so owned browser hosts are disposed through
    /// the same cleanup path as resource shutdown.
    /// </remarks>
    public async Task<string> CloseActiveSessionAsync(string resourceName, CancellationToken cancellationToken)
    {
        var activeSession = await GetActiveSessionAsync(resourceName, "close", cancellationToken).ConfigureAwait(false);
        await activeSession.Session.StopAsync(cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(
            new BrowserCloseBrowserCommandResult(
                activeSession.SessionId,
                activeSession.Browser,
                activeSession.BrowserExecutable,
                activeSession.BrowserHostOwnership,
                activeSession.ProcessId,
                activeSession.TargetId,
                activeSession.TargetUrl.ToString()),
            s_browserSessionPropertyJsonOptions);
    }

    private async Task<ActiveBrowserSession> GetActiveSessionAsync(string resourceName, string action, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);

        // Commands always run against the resource's active tracked session. The per-resource lock keeps selection stable
        // while lifecycle events may be starting, closing, or replacing browser targets.
        var resourceState = _resourceStates.GetOrAdd(resourceName, static _ => new ResourceSessionState());
        await resourceState.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);

            var activeSession = SelectActiveSession(resourceState);
            return activeSession ?? throw new InvalidOperationException($"No active tracked browser session is available to {action}.");
        }
        finally
        {
            resourceState.Lock.Release();
        }
    }

    private async Task UpdateActiveSessionTargetUrlAsync(BrowserResource resource, string resourceName, string sessionId, Uri targetUrl, CancellationToken cancellationToken)
    {
        // Navigation accepts through CDP before page load completes; update Aspire's resource snapshot immediately so the
        // dashboard reflects the requested target URL while the browser continues loading.
        var resourceState = _resourceStates.GetOrAdd(resourceName, static _ => new ResourceSessionState());
        await resourceState.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (resourceState.ActiveSessions.TryGetValue(sessionId, out var activeSession))
            {
                resourceState.ActiveSessions[sessionId] = activeSession with { TargetUrl = targetUrl };
                resourceState.LastTargetUrl = targetUrl.ToString();

                await PublishResourceSnapshotAsync(
                    resource,
                    resourceName,
                    resourceState,
                    stateText: KnownResourceStates.Running,
                    stateStyle: KnownResourceStateStyles.Success,
                    pendingSession: null,
                    stopTimeStamp: null,
                    exitCode: null).ConfigureAwait(false);
            }
        }
        finally
        {
            resourceState.Lock.Release();
        }
    }

    private static string GetRequiredUrlText(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("A URL is required for the tab open action.");
        }

        return url;
    }

    private static string GetRequiredTargetId(string? targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new InvalidOperationException("A target id is required for the tab close action.");
        }

        return targetId;
    }

    private static string[] ParseFilePaths(string files)
    {
        // The upload command accepts either a single host path (/tmp/avatar.png) or a JSON array for multi-file inputs:
        // ["/tmp/front.png", "/tmp/back.png"].
        var trimmed = files.Trim();
        if (!trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return [trimmed];
        }

        return JsonSerializer.Deserialize<string[]>(trimmed)
            ?? throw new InvalidOperationException("File paths JSON must be an array of strings.");
    }

    private static int GetIntegerProperty(string json, params string[] propertyPath)
    {
        // Several CDP calls return nested IDs that are required for the next protocol command. For example:
        // - DOM.getDocument returns { "root": { "nodeId": 1, ... } }, so callers ask for ("root", "nodeId").
        // - DOM.querySelector returns { "nodeId": 17 }, so callers ask for ("nodeId").
        // Fail loudly if Chromium changes the shape instead of sending nodeId 0 through the upload path.
        using var document = JsonDocument.Parse(json);
        var current = document.RootElement;
        foreach (var propertyName in propertyPath)
        {
            current = current.TryGetProperty(propertyName, out var property)
                ? property
                : throw new InvalidOperationException($"CDP response did not contain '{string.Join(".", propertyPath)}'.");
        }

        return current.TryGetInt32(out var value)
            ? value
            : throw new InvalidOperationException($"CDP response property '{string.Join(".", propertyPath)}' was not an integer.");
    }

    private static string CreateBrowserCommandEnvelope(string action, string resultJson)
    {
        // Raw CDP commands return protocol-shaped JSON. Parse once, clone the root so it survives the JsonDocument, and
        // wrap it with the browser command action so responses have the same top-level shape as BrowserScripts results.
        using var resultDocument = JsonDocument.Parse(resultJson);
        var envelope = new BrowserCommandEnvelope(action, resultDocument.RootElement.Clone());

        return JsonSerializer.Serialize(envelope, s_browserCommandEnvelopeJsonOptions);
    }

    private static ActiveBrowserSession? SelectActiveSession(ResourceSessionState resourceState)
    {
        // Prefer the last explicitly selected/opened session. If none is recorded, use the newest tracked session so the
        // first command after launch has a deterministic page target.
        return resourceState.LastSessionId is { } lastSessionId &&
            resourceState.ActiveSessions.TryGetValue(lastSessionId, out var lastSession)
                ? lastSession
                : resourceState.ActiveSessions.Count == 0
                    ? null
                    : resourceState.ActiveSessions.Values.MaxBy(static session => session.StartedAt);
    }

    private static TimeSpan CreateEvaluationTimeout(int timeoutMilliseconds)
    {
        // Wait scripts enforce timeoutMilliseconds inside the page. Runtime.evaluate gets a short transport grace period
        // so the browser can return the script's own timeout error instead of Aspire canceling the CDP call at the same
        // instant and hiding the useful page-side message.
        return TimeSpan.FromMilliseconds(timeoutMilliseconds + WaitCommandEvaluationTimeoutGraceMilliseconds);
    }

    private sealed record BrowserCommandEnvelope(string Action, JsonElement Result);
}
