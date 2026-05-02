// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

internal interface IBrowserLogsSessionManager
{
    Task StartSessionAsync(BrowserLogsResource resource, BrowserConfiguration configuration, string resourceName, Uri url, CancellationToken cancellationToken);

    Task<BrowserLogsScreenshotCaptureResult> CaptureScreenshotAsync(string resourceName, CancellationToken cancellationToken);

    Task<string> GetPageSnapshotAsync(string resourceName, int maxElements, int maxTextLength, CancellationToken cancellationToken);

    Task<string> NavigateAsync(BrowserLogsResource resource, string resourceName, Uri url, CancellationToken cancellationToken);

    Task<string> ClickAsync(string resourceName, string selector, CancellationToken cancellationToken);

    Task<string> FillAsync(string resourceName, string selector, string value, CancellationToken cancellationToken);

    Task<string> FocusAsync(string resourceName, string selector, CancellationToken cancellationToken);

    Task<string> TypeAsync(string resourceName, string selector, string text, CancellationToken cancellationToken);

    Task<string> PressAsync(string resourceName, string? selector, string key, CancellationToken cancellationToken);

    Task<string> HoverAsync(string resourceName, string selector, CancellationToken cancellationToken);

    Task<string> SelectAsync(string resourceName, string selector, string value, CancellationToken cancellationToken);

    Task<string> ScrollAsync(string resourceName, string? selector, int deltaX, int deltaY, CancellationToken cancellationToken);

    Task<string> WaitForAsync(string resourceName, string? selector, string? text, int timeoutMilliseconds, CancellationToken cancellationToken);

    Task<string> WaitForUrlAsync(string resourceName, string url, string match, int timeoutMilliseconds, CancellationToken cancellationToken);

    Task<string> WaitForLoadStateAsync(string resourceName, string state, int timeoutMilliseconds, CancellationToken cancellationToken);

    Task<string> WaitForElementStateAsync(string resourceName, string selector, string state, int timeoutMilliseconds, CancellationToken cancellationToken);

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
