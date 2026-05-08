// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

internal interface IBrowserSessionManager
{
    /// <summary>
    /// Starts the browser process and connects it to the target resource so every later command operates against the same tracked page.
    /// </summary>
    Task StartSessionAsync(BrowserResource resource, BrowserConfiguration configuration, string resourceName, Uri url, CancellationToken cancellationToken);

    /// <summary>
    /// Captures a screenshot artifact for visual debugging, agent reasoning, and issue handoffs.
    /// </summary>
    Task<BrowserScreenshotCaptureResult> CaptureScreenshotAsync(string resourceName, BrowserScreenshotCaptureOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Produces the accessibility-oriented page snapshot that lets automation clients understand the UI before acting.
    /// </summary>
    Task<string> GetPageSnapshotAsync(string resourceName, int maxElements, int maxTextLength, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a targeted page or element property so clients can assert state without dropping to arbitrary JavaScript.
    /// </summary>
    Task<string> GetAsync(string resourceName, string property, string? selector, string? name, CancellationToken cancellationToken);

    /// <summary>
    /// Answers common element state checks such as visibility, enabled state, and checked state.
    /// </summary>
    Task<string> IsAsync(string resourceName, string state, string selector, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves user-facing locators into a stable selector for follow-up actions.
    /// </summary>
    Task<string> FindAsync(string resourceName, string kind, string value, string? name, int index, CancellationToken cancellationToken);

    /// <summary>
    /// Highlights a selector in the live browser so humans can confirm what automation is targeting.
    /// </summary>
    Task<string> HighlightAsync(string resourceName, string selector, CancellationToken cancellationToken);

    /// <summary>
    /// Evaluates page JavaScript as an explicit escape hatch for diagnostics that the structured commands do not cover.
    /// </summary>
    Task<string> EvaluateAsync(string resourceName, string expression, CancellationToken cancellationToken);

    /// <summary>
    /// Manages cookies so local development flows can seed, inspect, or clear browser authentication and personalization state.
    /// </summary>
    Task<string> CookiesAsync(string resourceName, string action, string? name, string? value, string? domain, string? path, CancellationToken cancellationToken);

    /// <summary>
    /// Manages local and session storage so client-side app state can be reproduced between automation runs.
    /// </summary>
    Task<string> StorageAsync(string resourceName, string area, string action, string? key, string? value, CancellationToken cancellationToken);

    /// <summary>
    /// Captures or restores the combined browser state needed to move deterministically between scenarios.
    /// </summary>
    Task<string> StateAsync(string resourceName, string action, string? state, bool clearExisting, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a Chrome DevTools Protocol command for advanced browser diagnostics and capabilities not modeled by first-class commands.
    /// </summary>
    Task<string> CdpAsync(string resourceName, string method, string? parametersJson, string session, CancellationToken cancellationToken);

    /// <summary>
    /// Lists, opens, or closes browser targets so multi-tab flows can be controlled through the resource command surface.
    /// </summary>
    Task<string> TabsAsync(string resourceName, string action, string? url, string? targetId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists frames so callers can detect iframe boundaries before selecting or evaluating content.
    /// </summary>
    Task<string> FramesAsync(string resourceName, CancellationToken cancellationToken);

    /// <summary>
    /// Handles JavaScript dialogs that would otherwise block unattended automation.
    /// </summary>
    Task<string> DialogAsync(string resourceName, string action, string? promptText, CancellationToken cancellationToken);

    /// <summary>
    /// Configures download behavior so local browser flows that create files can be exercised and observed.
    /// </summary>
    Task<string> DownloadsAsync(string resourceName, string behavior, string? downloadPath, bool eventsEnabled, CancellationToken cancellationToken);

    /// <summary>
    /// Sets files on a file input so upload scenarios use the real browser path instead of bypassing the UI.
    /// </summary>
    Task<string> UploadAsync(string resourceName, string selector, string files, CancellationToken cancellationToken);

    /// <summary>
    /// Reports the active page URL and title so callers can confirm redirects and navigation outcomes.
    /// </summary>
    Task<string> GetUrlAsync(string resourceName, CancellationToken cancellationToken);

    /// <summary>
    /// Drives browser history backward to exercise flows that depend on realistic user navigation.
    /// </summary>
    Task<string> GoBackAsync(string resourceName, CancellationToken cancellationToken);

    /// <summary>
    /// Drives browser history forward after a back navigation without restarting the tracked session.
    /// </summary>
    Task<string> GoForwardAsync(string resourceName, CancellationToken cancellationToken);

    /// <summary>
    /// Reloads the active page so refresh behavior, caching, and startup state can be reproduced.
    /// </summary>
    Task<string> ReloadAsync(string resourceName, CancellationToken cancellationToken);

    /// <summary>
    /// Navigates the tracked page to an explicit URL while preserving the session, artifacts, and diagnostics pipeline.
    /// </summary>
    Task<string> NavigateAsync(BrowserResource resource, string resourceName, Uri url, CancellationToken cancellationToken);

    /// <summary>
    /// Clicks a target so buttons, links, and custom controls are exercised through normal browser events.
    /// </summary>
    Task<string> ClickAsync(string resourceName, string selector, CancellationToken cancellationToken);

    /// <summary>
    /// Double-clicks a target for controls whose behavior depends on double-click semantics.
    /// </summary>
    Task<string> DoubleClickAsync(string resourceName, string selector, CancellationToken cancellationToken);

    /// <summary>
    /// Fills an input with a final value when the scenario cares about form state more than individual keystrokes.
    /// </summary>
    Task<string> FillAsync(string resourceName, string selector, string value, CancellationToken cancellationToken);

    /// <summary>
    /// Checks checkbox or radio controls through browser events instead of mutating DOM state directly.
    /// </summary>
    Task<string> CheckAsync(string resourceName, string selector, CancellationToken cancellationToken);

    /// <summary>
    /// Unchecks checkbox or radio controls so opt-out and toggled-off form paths can be tested.
    /// </summary>
    Task<string> UncheckAsync(string resourceName, string selector, CancellationToken cancellationToken);

    /// <summary>
    /// Moves focus to a target so keyboard-only and focus-driven UI behavior can be exercised.
    /// </summary>
    Task<string> FocusAsync(string resourceName, string selector, CancellationToken cancellationToken);

    /// <summary>
    /// Types text through keyboard input so autocomplete, validation, and input-event handlers observe realistic changes.
    /// </summary>
    Task<string> TypeAsync(string resourceName, string selector, string text, CancellationToken cancellationToken);

    /// <summary>
    /// Presses a key for shortcuts, submit-on-enter, and focus navigation scenarios.
    /// </summary>
    Task<string> PressAsync(string resourceName, string? selector, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Triggers hover behavior such as menus, tooltips, and CSS hover states.
    /// </summary>
    Task<string> HoverAsync(string resourceName, string selector, CancellationToken cancellationToken);

    /// <summary>
    /// Selects native select options through the browser event system.
    /// </summary>
    Task<string> SelectAsync(string resourceName, string selector, string value, CancellationToken cancellationToken);

    /// <summary>
    /// Scrolls the page or a container to reveal lazy-loaded or viewport-dependent content.
    /// </summary>
    Task<string> ScrollAsync(string resourceName, string? selector, int deltaX, int deltaY, CancellationToken cancellationToken);

    /// <summary>
    /// Scrolls a target into view before actions that must operate on content below the fold or inside scroll panes.
    /// </summary>
    Task<string> ScrollIntoViewAsync(string resourceName, string selector, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a held key gesture so modifier-key and multi-step keyboard interactions can be represented.
    /// </summary>
    Task<string> KeyDownAsync(string resourceName, string? selector, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Ends a held key gesture so modifier-key and custom keyup handlers complete correctly.
    /// </summary>
    Task<string> KeyUpAsync(string resourceName, string? selector, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Sends coordinate-based mouse gestures for canvas, drag-like, and hit-test-sensitive UI.
    /// </summary>
    Task<string> MouseAsync(string resourceName, string action, int x, int y, string? button, int deltaX, int deltaY, CancellationToken cancellationToken);

    /// <summary>
    /// Waits for selector or text readiness before continuing an automation flow.
    /// </summary>
    Task<string> WaitForAsync(string resourceName, string? selector, string? text, int timeoutMilliseconds, CancellationToken cancellationToken);

    /// <summary>
    /// Waits for redirects or client-side routing before asserting page state.
    /// </summary>
    Task<string> WaitForUrlAsync(string resourceName, string url, string match, int timeoutMilliseconds, CancellationToken cancellationToken);

    /// <summary>
    /// Waits for document, load, or network-idle readiness when UI state depends on page loading.
    /// </summary>
    Task<string> WaitForLoadStateAsync(string resourceName, string state, int timeoutMilliseconds, CancellationToken cancellationToken);

    /// <summary>
    /// Waits for a specific element state so actions do not race rendering, enablement, or disappearance.
    /// </summary>
    Task<string> WaitForElementStateAsync(string resourceName, string selector, string state, int timeoutMilliseconds, CancellationToken cancellationToken);

    /// <summary>
    /// Waits for a custom page predicate when the built-in readiness checks cannot express the condition.
    /// </summary>
    Task<string> WaitForFunctionAsync(string resourceName, string function, int timeoutMilliseconds, CancellationToken cancellationToken);

    /// <summary>
    /// Stops the active tracked browser session so developers can clean up or restart with new configuration.
    /// </summary>
    Task<string> CloseActiveSessionAsync(string resourceName, CancellationToken cancellationToken);
}

internal sealed record BrowserScreenshotCaptureResult(
    string SessionId,
    string Browser,
    string BrowserExecutable,
    string BrowserHostOwnership,
    int? ProcessId,
    string TargetId,
    Uri TargetUrl,
    BrowserArtifact Artifact);
