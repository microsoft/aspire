// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class BrowserPageSessionTests
{
    [Fact]
    public void TrySelectReusableStartupPageTargetId_PrefersUnattachedBlankPage()
    {
        var targetId = BrowserPageSession.TrySelectReusableStartupPageTargetId(
        [
            new BrowserLogsTargetInfo { TargetId = "restored-page", Type = "page", Url = "https://example.com", Attached = false },
            new BrowserLogsTargetInfo { TargetId = "service-worker", Type = "service_worker", Url = "https://example.com/sw.js", Attached = false },
            new BrowserLogsTargetInfo { TargetId = "launcher-page", Type = "page", Url = "about:blank", Attached = false }
        ]);

        Assert.Equal("launcher-page", targetId);
    }

    [Fact]
    public void TrySelectReusableStartupPageTargetId_FallsBackToFirstUnattachedPage()
    {
        var targetId = BrowserPageSession.TrySelectReusableStartupPageTargetId(
        [
            new BrowserLogsTargetInfo { TargetId = "attached-page", Type = "page", Url = "about:blank", Attached = true },
            new BrowserLogsTargetInfo { TargetId = "fallback-page", Type = "page", Url = "chrome://newtab/", Attached = false }
        ]);

        Assert.Equal("fallback-page", targetId);
    }
}
