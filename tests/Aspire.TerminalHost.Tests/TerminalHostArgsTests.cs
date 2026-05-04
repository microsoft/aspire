// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.TerminalHost.Tests;

public class TerminalHostArgsTests
{
    [Fact]
    public void ParseAllRequiredArgsSucceeds()
    {
        var args = TerminalHostArgs.Parse([
            "--replica-count", "2",
            "--producer-uds", "/tmp/p0.sock",
            "--producer-uds", "/tmp/p1.sock",
            "--consumer-uds", "/tmp/c0.sock",
            "--consumer-uds", "/tmp/c1.sock",
            "--control-uds", "/tmp/ctrl.sock",
        ]);

        Assert.Equal(2, args.ReplicaCount);
        Assert.Equal(["/tmp/p0.sock", "/tmp/p1.sock"], args.ProducerUdsPaths);
        Assert.Equal(["/tmp/c0.sock", "/tmp/c1.sock"], args.ConsumerUdsPaths);
        Assert.Equal("/tmp/ctrl.sock", args.ControlUdsPath);
        Assert.Equal(120, args.Columns);
        Assert.Equal(30, args.Rows);
        Assert.Null(args.Shell);
    }

    [Fact]
    public void ParseAcceptsOptionalDimensionsAndShell()
    {
        var args = TerminalHostArgs.Parse([
            "--replica-count", "1",
            "--producer-uds", "/tmp/p.sock",
            "--consumer-uds", "/tmp/c.sock",
            "--control-uds", "/tmp/ctrl.sock",
            "--columns", "200",
            "--rows", "50",
            "--shell", "/bin/bash",
        ]);

        Assert.Equal(200, args.Columns);
        Assert.Equal(50, args.Rows);
        Assert.Equal("/bin/bash", args.Shell);
    }

    [Fact]
    public void ParseMissingReplicaCountThrows()
    {
        var ex = Assert.Throws<TerminalHostArgsException>(() => TerminalHostArgs.Parse([
            "--producer-uds", "/tmp/p.sock",
            "--consumer-uds", "/tmp/c.sock",
            "--control-uds", "/tmp/ctrl.sock",
        ]));

        Assert.Contains("--replica-count", ex.Message);
    }

    [Fact]
    public void ParseMissingControlUdsThrows()
    {
        var ex = Assert.Throws<TerminalHostArgsException>(() => TerminalHostArgs.Parse([
            "--replica-count", "1",
            "--producer-uds", "/tmp/p.sock",
            "--consumer-uds", "/tmp/c.sock",
        ]));

        Assert.Contains("--control-uds", ex.Message);
    }

    [Fact]
    public void ParseProducerCountMismatchThrows()
    {
        var ex = Assert.Throws<TerminalHostArgsException>(() => TerminalHostArgs.Parse([
            "--replica-count", "2",
            "--producer-uds", "/tmp/p0.sock",
            "--consumer-uds", "/tmp/c0.sock",
            "--consumer-uds", "/tmp/c1.sock",
            "--control-uds", "/tmp/ctrl.sock",
        ]));

        Assert.Contains("--producer-uds", ex.Message);
    }

    [Fact]
    public void ParseConsumerCountMismatchThrows()
    {
        var ex = Assert.Throws<TerminalHostArgsException>(() => TerminalHostArgs.Parse([
            "--replica-count", "2",
            "--producer-uds", "/tmp/p0.sock",
            "--producer-uds", "/tmp/p1.sock",
            "--consumer-uds", "/tmp/c0.sock",
            "--control-uds", "/tmp/ctrl.sock",
        ]));

        Assert.Contains("--consumer-uds", ex.Message);
    }

    [Fact]
    public void ParseZeroReplicaCountThrows()
    {
        var ex = Assert.Throws<TerminalHostArgsException>(() => TerminalHostArgs.Parse([
            "--replica-count", "0",
            "--control-uds", "/tmp/ctrl.sock",
        ]));

        Assert.Contains("--replica-count", ex.Message);
    }

    [Fact]
    public void ParseNegativeColumnsThrows()
    {
        var ex = Assert.Throws<TerminalHostArgsException>(() => TerminalHostArgs.Parse([
            "--replica-count", "1",
            "--producer-uds", "/tmp/p.sock",
            "--consumer-uds", "/tmp/c.sock",
            "--control-uds", "/tmp/ctrl.sock",
            "--columns", "-5",
        ]));

        Assert.Contains("--columns", ex.Message);
    }

    [Fact]
    public void ParseUnknownArgumentThrows()
    {
        var ex = Assert.Throws<TerminalHostArgsException>(() => TerminalHostArgs.Parse([
            "--replica-count", "1",
            "--producer-uds", "/tmp/p.sock",
            "--consumer-uds", "/tmp/c.sock",
            "--control-uds", "/tmp/ctrl.sock",
            "--bogus",
        ]));

        Assert.Contains("--bogus", ex.Message);
    }

    [Fact]
    public void ParseMissingValueForArgumentThrows()
    {
        var ex = Assert.Throws<TerminalHostArgsException>(() => TerminalHostArgs.Parse([
            "--replica-count",
        ]));

        Assert.Contains("--replica-count", ex.Message);
    }

    [Fact]
    public void ParseNonIntegerForReplicaCountThrows()
    {
        var ex = Assert.Throws<TerminalHostArgsException>(() => TerminalHostArgs.Parse([
            "--replica-count", "abc",
            "--control-uds", "/tmp/ctrl.sock",
        ]));

        Assert.Contains("--replica-count", ex.Message);
    }

    [Fact]
    public void ParseNullArgsThrows()
    {
        Assert.Throws<ArgumentNullException>(() => TerminalHostArgs.Parse(null!));
    }
}
