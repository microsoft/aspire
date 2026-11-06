// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Utils;
using Aspire.Cli.Watch;
using Aspire.Shared;

namespace Aspire.Cli.Tests;

public class DotnetWatchResourceProxyTests
{
    [Fact]
    public async Task MissingInfrastructureConfigurationReturnsACliFailureInsteadOfCrashing()
    {
        if (Environment.GetEnvironmentVariable(DotnetWatchConfiguration.ResourcePipe) is not null)
        {
            Assert.Skip("The CLI failure boundary requires an unconfigured infrastructure process.");
        }

        Assert.Equal(1, await DotnetWatchResourceProxy.RunAsync([]));
    }

    [Theory]
    [InlineData("aspire", "aspire", false)]
    [InlineData("aspire.exe", "aspire.exe", false)]
    [InlineData("dotnet", "Aspire.Cli.dll", true)]
    [InlineData("dotnet.exe", "Aspire.Cli.dll", true)]
    public void SelfInvocationIncludesOnlyTheManagedEntrypoint(string processPath, string entryPoint, bool managed)
    {
        var invocation = CliSelfInvocation.Create(processPath, entryPoint);

        Assert.Equal(processPath, invocation.ExecutablePath);
        Assert.Equal(managed ? [Path.GetFullPath(entryPoint)] : [], invocation.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("a\u03c0\ud83d\udd25")]
    public async Task StringFramingMatchesBinaryWriter(string text)
    {
        using var expected = new MemoryStream();
        using (var writer = new BinaryWriter(expected, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(text);
        }
        using var actual = new MemoryStream();

        await DotnetWatchProtocol.WriteStringAsync(actual, text, TestContext.Current.CancellationToken);

        Assert.Equal(expected.ToArray(), actual.ToArray());
        expected.Position = 0;
        Assert.Equal(text, await DotnetWatchProtocol.ReadStringAsync(expected, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff })]
    [InlineData(new byte[] { 0x81, 0x80, 0x40 })]
    public async Task InvalidAndOversizedLengthsAreRejectedBeforeAllocatingPayload(byte[] prefix)
    {
        using var stream = new MemoryStream(prefix);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => DotnetWatchProtocol.ReadStringAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TruncatedPayloadIsNotSuccessfulCompletion()
    {
        using var stream = new MemoryStream(new byte[] { 4, (byte)'a' });

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => DotnetWatchProtocol.ReadStringAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidUtf8IsRejected()
    {
        using var stream = new MemoryStream(new byte[] { 1, 0xff });

        await Assert.ThrowsAsync<DecoderFallbackException>(
            () => DotnetWatchProtocol.ReadStringAsync(stream, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, null)]
    [InlineData(false, "https")]
    public async Task LaunchRequestPreservesProfileStatesAndProtectsSdkLikeApplicationOptions(bool excludeProfile, string? profile)
    {
        var request = DotnetWatchProtocol.CreateLaunchRequest(
            "/service/Service.csproj",
            ["--configuration", "application-only", "two words"],
            new Dictionary<string, string> { ["INTENDED"] = "value", ["EMPTY"] = "" },
            excludeProfile,
            profile);
        await Verify(request, "json").UseParameters(excludeProfile, profile ?? "default");
    }

    [Fact]
    public async Task ProxyForwardsOnlyInventoriedEnvironmentAndKeepsOutputStreamsSeparate()
    {
        var pipeName = "aw-" + Guid.NewGuid().ToString("N")[..10];
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var environment = new Dictionary<string, string>
        {
            [DotnetWatchConfiguration.ResourcePipe] = pipeName,
            [DotnetWatchConfiguration.EntryPoint] = "/service/Service.csproj",
            [DotnetWatchConfiguration.EnvironmentNames] = """["INTENDED","EMPTY"]""",
            [DotnetWatchConfiguration.ExcludeProfile] = "true",
            ["INTENDED"] = "value",
            ["EMPTY"] = "",
            ["UNRELATED_AMBIENT"] = "not forwarded"
        };
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var proxy = DotnetWatchResourceProxy.RunAsync(
            ["--application", "two words"], name => environment.GetValueOrDefault(name),
            standardOutput, standardError, TestContext.Current.CancellationToken);
        await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        using var reader = new BinaryReader(server, Encoding.UTF8, leaveOpen: true);
        Assert.Equal(1, reader.ReadByte());
        var request = reader.ReadString();
        using (var writer = new BinaryWriter(server, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1);
            writer.Write((byte)1);
            writer.Write("stdout \u03c0");
            writer.Write((byte)2);
            writer.Write("stderr");
            writer.Flush();
        }
        await server.DisposeAsync();
        await Assert.ThrowsAsync<EndOfStreamException>(() => proxy);

        using var json = JsonDocument.Parse(request);
        Assert.Equal(
            ["INTENDED", "EMPTY"],
            json.RootElement.GetProperty("EnvironmentVariables").EnumerateObject().Select(property => property.Name));
        Assert.Equal("stdout \u03c0" + Environment.NewLine, standardOutput.ToString());
        Assert.Equal("stderr" + Environment.NewLine, standardError.ToString());
    }

    [Fact]
    public async Task MissingInventoryDoesNotConnectOrForwardAmbientEnvironment()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var environment = new Dictionary<string, string>
        {
            [DotnetWatchConfiguration.ResourcePipe] = "unused",
            [DotnetWatchConfiguration.EntryPoint] = "/service/Service.csproj"
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => DotnetWatchResourceProxy.RunAsync(
            [], name => environment.GetValueOrDefault(name), standardOutput, standardError,
            TestContext.Current.CancellationToken));
        Assert.Equal("", standardOutput.ToString());
        Assert.Equal("", standardError.ToString());
    }

    [Fact]
    public async Task CancelingDuringHandshakeClosesTheResourceConnection()
    {
        var pipeName = "aw-" + Guid.NewGuid().ToString("N")[..10];
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var environment = new Dictionary<string, string>
        {
            [DotnetWatchConfiguration.ResourcePipe] = pipeName,
            [DotnetWatchConfiguration.EntryPoint] = "/service/Service.csproj",
            [DotnetWatchConfiguration.EnvironmentNames] = "[]"
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var proxy = DotnetWatchResourceProxy.RunAsync(
            [], name => environment.GetValueOrDefault(name), standardOutput, standardError, cancellation.Token);
        await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        using var reader = new BinaryReader(server, Encoding.UTF8, leaveOpen: true);
        Assert.Equal(1, reader.ReadByte());
        using var request = JsonDocument.Parse(reader.ReadString());
        Assert.Equal("/service/Service.csproj", request.RootElement.GetProperty("EntryPoint").GetString());

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => proxy);
        var eof = await server.ReadAsync(new byte[1], TestContext.Current.CancellationToken);
        Assert.Equal(0, eof);
        Assert.Equal("", standardOutput.ToString());
        Assert.Equal("", standardError.ToString());
    }
}
