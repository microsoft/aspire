// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

internal interface IBrowserLogsSessionManager
{
    Task StartSessionAsync(BrowserAutomationResource resource, BrowserConfiguration configuration, string resourceName, Uri url, CancellationToken cancellationToken);

    Task<BrowserLogsScreenshotCaptureResult> CaptureScreenshotAsync(string resourceName, BrowserScreenshotCaptureOptions options, CancellationToken cancellationToken);

    Task<string> GetPageSnapshotAsync(string resourceName, int maxElements, int maxTextLength, CancellationToken cancellationToken);

    Task<string> GetAsync(string resourceName, string property, string? selector, string? name, CancellationToken cancellationToken);

    Task<string> IsAsync(string resourceName, string state, string selector, CancellationToken cancellationToken);

    Task<string> FindAsync(string resourceName, string kind, string value, string? name, int index, CancellationToken cancellationToken);

    Task<string> HighlightAsync(string resourceName, string selector, CancellationToken cancellationToken);

    Task<string> EvaluateAsync(string resourceName, string expression, CancellationToken cancellationToken);

    Task<string> CookiesAsync(string resourceName, string action, string? name, string? value, string? domain, string? path, CancellationToken cancellationToken);

    Task<string> StorageAsync(string resourceName, string area, string action, string? key, string? value, CancellationToken cancellationToken);

    Task<string> StateAsync(string resourceName, string action, string? state, bool clearExisting, CancellationToken cancellationToken);

    Task<string> CdpAsync(string resourceName, string method, string? parametersJson, string session, CancellationToken cancellationToken);

    Task<string> TabsAsync(string resourceName, string action, string? url, string? targetId, CancellationToken cancellationToken);

    Task<string> FramesAsync(string resourceName, CancellationToken cancellationToken);

    Task<string> DialogAsync(string resourceName, string action, string? promptText, CancellationToken cancellationToken);

    Task<string> DownloadsAsync(string resourceName, string behavior, string? downloadPath, bool eventsEnabled, CancellationToken cancellationToken);

    Task<string> UploadAsync(string resourceName, string selector, string files, CancellationToken cancellationToken);

    Task<string> GetUrlAsync(string resourceName, CancellationToken cancellationToken);

    Task<string> GoBackAsync(string resourceName, CancellationToken cancellationToken);

    Task<string> GoForwardAsync(string resourceName, CancellationToken cancellationToken);

    Task<string> ReloadAsync(string resourceName, CancellationToken cancellationToken);

    Task<string> NavigateAsync(BrowserAutomationResource resource, string resourceName, Uri url, CancellationToken cancellationToken);

    Task<string> ClickAsync(string resourceName, string selector, CancellationToken cancellationToken);

    Task<string> DoubleClickAsync(string resourceName, string selector, CancellationToken cancellationToken);

    Task<string> FillAsync(string resourceName, string selector, string value, CancellationToken cancellationToken);

    Task<string> CheckAsync(string resourceName, string selector, CancellationToken cancellationToken);

    Task<string> UncheckAsync(string resourceName, string selector, CancellationToken cancellationToken);

    Task<string> FocusAsync(string resourceName, string selector, CancellationToken cancellationToken);

    Task<string> TypeAsync(string resourceName, string selector, string text, CancellationToken cancellationToken);

    Task<string> PressAsync(string resourceName, string? selector, string key, CancellationToken cancellationToken);

    Task<string> HoverAsync(string resourceName, string selector, CancellationToken cancellationToken);

    Task<string> SelectAsync(string resourceName, string selector, string value, CancellationToken cancellationToken);

    Task<string> ScrollAsync(string resourceName, string? selector, int deltaX, int deltaY, CancellationToken cancellationToken);

    Task<string> ScrollIntoViewAsync(string resourceName, string selector, CancellationToken cancellationToken);

    Task<string> KeyDownAsync(string resourceName, string? selector, string key, CancellationToken cancellationToken);

    Task<string> KeyUpAsync(string resourceName, string? selector, string key, CancellationToken cancellationToken);

    Task<string> MouseAsync(string resourceName, string action, int x, int y, string? button, int deltaX, int deltaY, CancellationToken cancellationToken);

    Task<string> WaitForAsync(string resourceName, string? selector, string? text, int timeoutMilliseconds, CancellationToken cancellationToken);

    Task<string> WaitForUrlAsync(string resourceName, string url, string match, int timeoutMilliseconds, CancellationToken cancellationToken);

    Task<string> WaitForLoadStateAsync(string resourceName, string state, int timeoutMilliseconds, CancellationToken cancellationToken);

    Task<string> WaitForElementStateAsync(string resourceName, string selector, string state, int timeoutMilliseconds, CancellationToken cancellationToken);

    Task<string> WaitForFunctionAsync(string resourceName, string function, int timeoutMilliseconds, CancellationToken cancellationToken);

    Task<string> CloseActiveSessionAsync(string resourceName, CancellationToken cancellationToken);
}

internal sealed record BrowserLogsScreenshotCaptureResult(
    string SessionId,
    string Browser,
    string BrowserExecutable,
    string BrowserHostOwnership,
    int? ProcessId,
    string TargetId,
    Uri TargetUrl,
    BrowserLogsArtifact Artifact);
