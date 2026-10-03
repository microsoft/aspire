// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.DevTunnels.Tests;

public class DevTunnelOutputParserTests
{
    [Theory]
    [InlineData("Hosting port: 3000", "Connect via browser: https://abc-3000.usw2.devtunnels.ms")]
    [InlineData(" Hosting\t port = 3000 ", "Connect  via browser -> https://abc-3000.usw2.devtunnels.ms/")]
    [InlineData("HOSTING PORT | 3000", "CONNECT VIA BROWSER : <https://abc-3000.usw2.devtunnels.ms/>")]
    [InlineData("\u001b[32mHosting port: 3000\u001b[0m", "\u001b[34mConnect via browser: https://abc-3000.usw2.devtunnels.ms\u001b[0m")]
    public void ParsesPortWithLayoutVariations(string heading, string url)
    {
        var parser = new DevTunnelOutputParser("mytunnel");
        Assert.Equal(default, parser.Parse(heading));
        Assert.Equal(new(DevTunnelOutputParser.OutputKind.Port, 3000, new("https://abc-3000.usw2.devtunnels.ms/")), parser.Parse(url));
    }

    [Fact]
    public void ParsesDocumentedSingleLineFormat()
    {
        var parser = new DevTunnelOutputParser("mytunnel");
        Assert.Equal(new(DevTunnelOutputParser.OutputKind.Port, 3000, new("https://abc-3000.usw2.devtunnels.ms/")),
            parser.Parse("Hosting port 3000 at https://abc-3000.usw2.devtunnels.ms/"));
    }

    [Fact]
    public void PreservesRecognizedFieldsAcrossLines()
    {
        var parser = new DevTunnelOutputParser("mytunnel");
        foreach (var part in new[] { "Hosting", "port:", "", "3000", "Connect via", "browser:" })
        {
            Assert.Equal(default, parser.Parse(part));
        }
        Assert.Equal(new(DevTunnelOutputParser.OutputKind.Port, 3000, new("https://abc-3000.usw2.devtunnels.ms/")),
            parser.Parse("https://abc-3000.usw2.devtunnels.ms/"));
        Assert.Equal(default, parser.Parse("Inspect network activity:"));
        Assert.Equal(default, parser.Parse("https://abc-3000-inspect.usw2.devtunnels.ms/"));
    }

    [Theory]
    [InlineData("Ready to accept connections for tunnel: mytunnel.usw2")]
    [InlineData(" READY  TO ACCEPT CONNECTIONS FOR TUNNEL = mytunnel.usw2 ")]
    [InlineData("Ready to accept connections for tunnel - mytunnel.usw2!")]
    [InlineData("Ready to accept connections.")]
    public void RecognizesReadiness(string line)
    {
        Assert.Equal(new(DevTunnelOutputParser.OutputKind.Ready), new DevTunnelOutputParser("mytunnel").Parse(line));
    }

    [Fact]
    public void WaitsForWrappedTunnelId()
    {
        var parser = new DevTunnelOutputParser("mytunnel");
        Assert.Equal(default, parser.Parse("Ready to accept connections for tunnel:"));
        Assert.Equal(new(DevTunnelOutputParser.OutputKind.Ready), parser.Parse("mytunnel.usw2"));
    }

    [Fact]
    public void RecognizesWrappedSingleLineFormatAndReadySuffix()
    {
        var parser = new DevTunnelOutputParser("mytunnel");
        Assert.Equal(default, parser.Parse("Hosting port 3000"));
        Assert.Equal(new(DevTunnelOutputParser.OutputKind.Port, 3000, new("https://abc-3000.usw2.devtunnels.ms/")),
            parser.Parse("at https://abc-3000.usw2.devtunnels.ms/"));
        Assert.Equal(new(DevTunnelOutputParser.OutputKind.Ready), parser.Parse("Ready to accept connections"));
        Assert.Equal(new(DevTunnelOutputParser.OutputKind.Ready), parser.Parse("for tunnel: mytunnel.usw2"));
    }

    [Theory]
    [InlineData("Connection to host tunnel relay restored.", DevTunnelOutputParser.OutputKind.Connected)]
    [InlineData("Connection to host tunnel relay closed. Another host for the tunnel has connected.", DevTunnelOutputParser.OutputKind.Disconnected)]
    [InlineData("Connection to host tunnel relay lost; reconnecting.", DevTunnelOutputParser.OutputKind.Disconnected)]
    [InlineData("Unexpected future CLI output", DevTunnelOutputParser.OutputKind.Unrecognized)]
    [InlineData("Ready to accept connections for tunnel: other.usw2", DevTunnelOutputParser.OutputKind.Unrecognized)]
    [InlineData("Hosting port: 65536", DevTunnelOutputParser.OutputKind.Unrecognized)]
    public void ClassifiesLifecycleMessages(string text, int kind)
    {
        Assert.Equal(new((DevTunnelOutputParser.OutputKind)kind), new DevTunnelOutputParser("mytunnel").Parse(text));
    }

    [Theory]
    [InlineData("http://abc-3000.usw2.devtunnels.ms")]
    [InlineData("https://abc-3001.usw2.devtunnels.ms")]
    [InlineData("https://abc-3000.usw2.devtunnels.ms.evil.example")]
    [InlineData("https://user@abc-3000.usw2.devtunnels.ms")]
    [InlineData("https://abc-3000.usw2.devtunnels.ms/path")]
    [InlineData("https://abc-3000.usw2.devtunnels.ms?secret=value")]
    public void RejectsUnexpectedPortUris(string url)
    {
        var parser = new DevTunnelOutputParser("mytunnel");
        parser.Parse("Hosting port: 3000");
        Assert.Equal(new(DevTunnelOutputParser.OutputKind.Unrecognized), parser.Parse("Connect via browser: " + url));
    }

    [Fact]
    public void UnknownOutputCannotJoinUnrelatedFields()
    {
        var parser = new DevTunnelOutputParser("mytunnel");
        parser.Parse("Hosting port: 3000");
        Assert.Equal(new(DevTunnelOutputParser.OutputKind.Unrecognized), parser.Parse("Unknown message"));
        Assert.Equal(new(DevTunnelOutputParser.OutputKind.Unrecognized),
            parser.Parse("Connect via browser: https://abc-3000.usw2.devtunnels.ms"));
    }

    [Fact]
    public void BoundsBufferedOutput()
    {
        var parser = new DevTunnelOutputParser("mytunnel");
        Assert.Equal(new(DevTunnelOutputParser.OutputKind.Unrecognized), parser.Parse("Hosting port" + new string(':', 5000)));
        Assert.Equal(new(DevTunnelOutputParser.OutputKind.Ready), parser.Parse("Ready to accept connections."));
    }
}
