// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRETERMINAL001 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Tests.Terminals;

/// <summary>
/// Guards launch option defaults and validation before process creation.
/// </summary>
[Trait("Partition", "2")]
public class TerminalLaunchOptionsTests
{
    [Fact]
    public void Executable_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>("value", () => new TerminalLaunchOptions { Title = "Shell", Executable = null! });
    }

    [Fact]
    public void Executable_Empty_Throws()
    {
        Assert.Throws<ArgumentException>("value", () => new TerminalLaunchOptions { Title = "Shell", Executable = string.Empty });
    }

    [Fact]
    public void Defaults_OnlyRequireTitleAndExecutable()
    {
        var options = new TerminalLaunchOptions { Title = "Shell", Executable = "bash" };

        Assert.Equal("Shell", options.Title);
        Assert.Equal("bash", options.Executable);
        Assert.Empty(options.Arguments);
        Assert.Empty(options.EnvironmentVariables);
        Assert.Null(options.WorkingDirectory);
        Assert.Equal(TerminalPlacement.Dock, options.Placement);
    }

    [Fact]
    public void Executable_InvalidAssignment_PreservesPreviousValue()
    {
        var options = new TerminalLaunchOptions { Title = "Shell", Executable = "bash" };

        Assert.Throws<ArgumentNullException>("value", () => options.Executable = null!);
        Assert.Throws<ArgumentException>("value", () => options.Executable = string.Empty);

        Assert.Equal("bash", options.Executable);
    }

    [Fact]
    public void Arguments_Null_Throws()
    {
        var options = new TerminalLaunchOptions { Title = "Shell", Executable = "bash" };

        var ex = Assert.Throws<ArgumentNullException>(() => options.Arguments = null!);
        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void Arguments_SupportsSpreadAssignment()
    {
        string[] shell = ["/bin/sh"];
        var options = new TerminalLaunchOptions
        {
            Title = "Container shell",
            Executable = "docker",
            Arguments = ["exec", "-it", "my-container", .. shell]
        };

        Assert.Equal(["exec", "-it", "my-container", "/bin/sh"], options.Arguments);
    }

    [Fact]
    public void EnvironmentVariables_SupportsCollectionInitializerAndOverrides()
    {
        var options = new TerminalLaunchOptions
        {
            Title = "Shell",
            Executable = "bash",
            EnvironmentVariables =
            {
                ["TERM"] = "xterm-256color",
                ["MY_SETTING"] = "initial"
            }
        };
        options.EnvironmentVariables["MY_SETTING"] = "updated";

        Assert.Equal("xterm-256color", options.EnvironmentVariables["TERM"]);
        Assert.Equal("updated", options.EnvironmentVariables["MY_SETTING"]);
        Assert.Equal(2, options.EnvironmentVariables.Count);
    }

    [Fact]
    public void Collections_AreNotSharedBetweenOptions()
    {
        var first = new TerminalLaunchOptions { Title = "First", Executable = "bash" };
        var second = new TerminalLaunchOptions { Title = "Second", Executable = "bash" };
        first.Arguments.Add("-i");
        first.EnvironmentVariables["MY_SETTING"] = "value";

        Assert.Empty(second.Arguments);
        Assert.Empty(second.EnvironmentVariables);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Columns_NotPositive_Throws(int value)
    {
        var options = new TerminalLaunchOptions { Title = "Shell", Executable = "bash" };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Columns = value);
        Assert.Equal("value", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rows_NotPositive_Throws(int value)
    {
        var options = new TerminalLaunchOptions { Title = "Shell", Executable = "bash" };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Rows = value);
        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void Dimensions_DefaultTo80ColumnsAnd24Rows()
    {
        var options = new TerminalLaunchOptions { Title = "Shell", Executable = "bash" };

        Assert.Equal(80, options.Columns);
        Assert.Equal(24, options.Rows);
    }

    [Fact]
    public void Dimensions_AcceptPositiveValues()
    {
        var options = new TerminalLaunchOptions { Title = "Shell", Executable = "bash", Columns = 80, Rows = 24 };

        Assert.Equal(80, options.Columns);
        Assert.Equal(24, options.Rows);
    }
}
