// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class EnvironmentVariableExtensionsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData("2", false)]
    [InlineData("1", true)]
    [InlineData("TrUe", true)]
    public void IsFlagEnabledAcceptsOneOrTrue(string? value, bool expected)
    {
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["FLAG"] = value
        });

        Assert.Equal(expected, environment.IsFlagEnabled("FLAG"));
    }
}
