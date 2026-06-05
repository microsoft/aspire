// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Commands;

namespace Aspire.Cli.Tests.Commands;

public class OptionWithLegacyTests
{
    [Fact]
    public void GetValue_PrefersInnerOption_WhenBothAreProvided()
    {
        var option = new OptionWithLegacy<string>("--new-name", "--old-name", "Test option");

        var command = new Command("test");
        command.Options.Add(option);

        var result = command.Parse("test --new-name primary-value --old-name legacy-value");

        var value = result.GetValue(option);

        Assert.Equal("primary-value", value);
    }

    [Fact]
    public void GetValue_FallsBackToLegacyOption_WhenInnerOptionIsNotProvided()
    {
        var option = new OptionWithLegacy<string>("--new-name", "--old-name", "Test option");

        var command = new Command("test");
        command.Options.Add(option);

        var result = command.Parse("test --old-name legacy-value");

        var value = result.GetValue(option);

        Assert.Equal("legacy-value", value);
    }
}
