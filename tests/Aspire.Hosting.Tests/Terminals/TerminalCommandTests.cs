// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Terminals;

#pragma warning disable ASPIRETERMINAL002 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Tests.Terminals;

/// <summary>
/// Guards validation on <see cref="TerminalCommand"/>. Everything here fails at the assignment that caused it
/// rather than later, inside terminal creation: the command is not translated into a process until
/// <see cref="TerminalService.CreateTerminal(TerminalLaunchOptions)"/> runs, which is far enough away from the
/// property set that an unvalidated value would surface as an unattributed failure from the terminal library.
/// </summary>
[Trait("Partition", "2")]
public class TerminalCommandTests
{
    [Fact]
    public void Constructor_NullExecutable_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TerminalCommand(null!));
    }

    [Fact]
    public void Constructor_EmptyExecutable_Throws()
    {
        Assert.Throws<ArgumentException>(() => new TerminalCommand(string.Empty));
    }

    [Fact]
    public void Constructor_OnlyRequiresExecutable()
    {
        var command = new TerminalCommand("bash");

        Assert.Equal("bash", command.Executable);
        Assert.Empty(command.Arguments);
        Assert.Empty(command.EnvironmentVariables);
        Assert.Null(command.WorkingDirectory);
    }

    [Fact]
    public void Arguments_Null_Throws()
    {
        var command = new TerminalCommand("bash");

        var ex = Assert.Throws<ArgumentNullException>(() => command.Arguments = null!);
        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void Arguments_SupportsSpreadAssignment()
    {
        string[] shell = ["/bin/sh"];
        var command = new TerminalCommand("docker")
        {
            Arguments = ["exec", "-it", "my-container", .. shell]
        };

        Assert.Equal(["exec", "-it", "my-container", "/bin/sh"], command.Arguments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Columns_NotPositive_Throws(int value)
    {
        var command = new TerminalCommand("bash");

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => command.Columns = value);
        Assert.Equal("value", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rows_NotPositive_Throws(int value)
    {
        var command = new TerminalCommand("bash");

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => command.Rows = value);
        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void Dimensions_DefaultToAModernGrid()
    {
        var command = new TerminalCommand("bash");

        Assert.Equal(120, command.Columns);
        Assert.Equal(32, command.Rows);
    }

    [Fact]
    public void Dimensions_AcceptPositiveValues()
    {
        var command = new TerminalCommand("bash") { Columns = 80, Rows = 24 };

        Assert.Equal(80, command.Columns);
        Assert.Equal(24, command.Rows);
    }
}
