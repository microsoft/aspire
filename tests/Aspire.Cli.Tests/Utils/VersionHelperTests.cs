// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class VersionHelperTests
{
    [Fact]
    public void TryGetCurrentCliVersionMatch_WithPrHivesAndNoChannel_ReturnsCurrentCliVersion()
    {
        var cliVersion = VersionHelper.GetDefaultSdkVersion();
        var candidates = new[]
        {
            "99.0.0",
            cliVersion,
        };

        var result = VersionHelper.TryGetCurrentCliVersionMatch(
            candidates,
            version => version,
            out var match,
            channelName: null,
            hasPrHives: true);

        Assert.True(result);
        Assert.Equal(cliVersion, match);
    }
}
