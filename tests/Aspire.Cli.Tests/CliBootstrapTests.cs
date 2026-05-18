// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Cli.Tests;

/// <summary>
/// Integration tests for the bootstrap wiring: the running CLI's
/// <see cref="CliExecutionContext.IdentityChannel"/> is sourced from the binary's
/// <c>[AssemblyMetadata("AspireCliChannel")]</c> value via
/// <see cref="IIdentityChannelReader"/>, registered in DI by
/// <see cref="Aspire.Cli.Program.BuildApplicationAsync"/>.
/// </summary>
public class CliBootstrapTests
{
    private static readonly string[] s_fixedChannels = ["stable", "staging", "daily", "local"];

    private static async Task<IHost> BuildHostAsync()
    {
        var loggingOptions = Program.ParseLoggingOptions([]);
        var errorWriter = new TestStartupErrorWriter();
        var (loggerFactory, fileLoggerProvider) = Program.CreateLoggerFactory([], loggingOptions, errorWriter);
        var startupContext = new Program.CliStartupContext(loggingOptions, errorWriter, loggerFactory, fileLoggerProvider, loggerFactory.CreateLogger(Program.RootLoggerName));
        return await Program.BuildApplicationAsync([], startupContext);
    }

    private static string GetBakedEntryAssemblyChannel()
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        Assert.NotNull(entryAssembly);
        var bakedChannel = entryAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => string.Equals(a.Key, "AspireCliChannel", StringComparison.Ordinal))
            .Value;
        Assert.False(string.IsNullOrEmpty(bakedChannel));
        return bakedChannel!;
    }

    [Fact]
    public void IdentityChannelReader_OnRunningCliAssembly_ReturnsKnownChannel()
    {
        var reader = new IdentityChannelReader(typeof(Aspire.Cli.Program).Assembly);

        var channel = reader.ReadChannel();

        // Test host can be built with /p:AspireCliChannel=<anything in the accepted set>;
        // assert shape, not a single literal, so this test stops being an accidental
        // regression for non-default builds (including pr-<N> when the test host is a PR build).
        Assert.True(
            s_fixedChannels.Contains(channel) || channel.StartsWith("pr-", StringComparison.Ordinal),
            $"Unexpected channel '{channel}'; expected one of stable|staging|daily|local|pr-<N>.");
    }

    [Fact]
    public async Task BuildApplication_RegistersIIdentityChannelReader_AsIdentityChannelReaderInstance()
    {
        // Program.BuildApplicationAsync registers IIdentityChannelReader as a singleton,
        // backed by the default IdentityChannelReader (which reads from
        // typeof(Aspire.Cli.Program).Assembly).
        using var host = await BuildHostAsync();

        var reader = host.Services.GetRequiredService<IIdentityChannelReader>();

        Assert.NotNull(reader);
        Assert.IsType<IdentityChannelReader>(reader);
    }

    [Fact]
    public async Task BuildApplication_PopulatesCliExecutionContextChannel_FromIdentityChannelReader()
    {
        // The CliExecutionContext factory delegate must source Channel from
        // IIdentityChannelReader.ReadChannel() rather than the constructor default.
        // Without this wiring, the entire reseed chain would write "daily" for every
        // CLI build regardless of the baked AspireCliChannel.
        using var host = await BuildHostAsync();

        var reader = host.Services.GetRequiredService<IIdentityChannelReader>();
        var context = host.Services.GetRequiredService<CliExecutionContext>();

        Assert.Equal(reader.ReadChannel(), context.IdentityChannel);
    }

    [Fact]
    public async Task BuildApplication_CliExecutionContextChannel_MatchesAssemblyMetadataAttribute()
    {
        // End-to-end coherence: the channel flowing through the DI container must equal the
        // value baked into the entry assembly by [AssemblyMetadata("AspireCliChannel", "...")].
        // IdentityChannelReader reads from typeof(Aspire.Cli.Program).Assembly; this test
        // reads Assembly.GetEntryAssembly() directly and the comparison works because
        // Aspire.Cli.csproj and the test csproj forward the same $(AspireCliChannel) MSBuild
        // property — keeping both assemblies in lockstep regardless of the build configuration
        // (so this test is also correct on /p:AspireCliChannel=stable or pr-<N> CI builds).
        var bakedChannel = GetBakedEntryAssemblyChannel();

        using var host = await BuildHostAsync();

        var context = host.Services.GetRequiredService<CliExecutionContext>();

        Assert.Equal(bakedChannel, context.IdentityChannel);
    }
}

