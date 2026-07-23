// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class TypeScriptAppHostToolchainTestHelpersTests
{
    [Fact]
    public void GetWatchModeReadyText_WhenToolchainIsDeno_ReturnsTypeScriptCompilerWatchText()
    {
        var watchModeReadyText = TypeScriptAppHostToolchainTestHelpers.GetWatchModeReadyText("deno");

        Assert.Equal("Watching for file changes.", watchModeReadyText);
    }
}
