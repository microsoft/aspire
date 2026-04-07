// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class AutomaticNpmInstallWarningTests
{
    [Fact]
    public void IsMatch_WhenNpmIsMissing_ReturnsTrue()
    {
        var lines = new[]
        {
            (OutputLineStream.StdErr, "npm is not installed or not found in PATH. Please install Node.js and try again.")
        };

        Assert.True(AutomaticNpmInstallWarning.IsMatch(lines));
    }

    [Fact]
    public void IsMatch_WhenNpxIsMissing_ReturnsTrue()
    {
        var lines = new[]
        {
            (OutputLineStream.StdErr, "npx is not installed or not found in PATH. Please install Node.js and try again.")
        };

        Assert.True(AutomaticNpmInstallWarning.IsMatch(lines));
    }

    [Fact]
    public void IsMatch_WhenOutputIsUnrelated_ReturnsFalse()
    {
        var lines = new[]
        {
            (OutputLineStream.StdErr, "npm ERR! code E401"),
            (OutputLineStream.StdOut, "Installing packages...")
        };

        Assert.False(AutomaticNpmInstallWarning.IsMatch(lines));
    }
}
