// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

internal interface IBrowserLogsSessionManager
{
    Task StartSessionAsync(BrowserLogsResource resource, BrowserConfiguration configuration, string resourceName, Uri url, CancellationToken cancellationToken);

    Task<BrowserLogsScreenshotCaptureResult> CaptureScreenshotAsync(string resourceName, CancellationToken cancellationToken);
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
