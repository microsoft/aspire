// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Tests;

/// <summary>
/// Integration tests for PR1 bootstrap wiring: the running CLI's
/// <see cref="CliExecutionContext.Channel"/> is sourced from the binary's
/// <c>[AssemblyMetadata("AspireCliChannel")]</c> value via
/// <see cref="IIdentityChannelReader"/>, registered in DI by
/// <see cref="Aspire.Cli.Program.BuildApplicationAsync"/> (PR1-S12).
/// </summary>
public class CliBootstrapTests
{
    private static readonly string[] s_validChannels = ["stable", "staging", "daily", "pr"];

    private static async Task<IHost> BuildHostAsync()
    {
        var loggingOptions = Program.ParseLoggingOptions([]);
        var errorWriter = new TestStartupErrorWriter();
        var (loggerFactory, fileLoggerProvider) = Program.CreateLoggerFactory([], loggingOptions, errorWriter);
        var startupContext = new Program.CliStartupContext(loggingOptions, errorWriter, loggerFactory, fileLoggerProvider, loggerFactory.CreateLogger<Program>());
        return await Program.BuildApplicationAsync([], startupContext);
    }

    [Fact]
    public void IIdentityChannelReader_TypeExists_AndProductionImplementationShape()
    {
        // Locks the type signatures in place so the bootstrap wiring stays bound to a stable
        // contract. If the interface or default implementation shape changes, the production
        // factory delegate in Program.BuildApplicationAsync needs to change in lockstep.
        var iface = typeof(IIdentityChannelReader);
        Assert.True(iface.IsInterface);

        var readChannel = iface.GetMethod(nameof(IIdentityChannelReader.ReadChannel));
        Assert.NotNull(readChannel);
        Assert.Equal(typeof(string), readChannel.ReturnType);
        Assert.Empty(readChannel.GetParameters());

        var impl = typeof(IdentityChannelReader);
        Assert.True(iface.IsAssignableFrom(impl));

        var ctor = impl.GetConstructors().Single();
        var parameters = ctor.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(Assembly), parameters[0].ParameterType);
        Assert.True(parameters[0].HasDefaultValue);
        Assert.Null(parameters[0].DefaultValue);
    }

    [Fact]
    public void IdentityChannelReader_OnRunningCliAssembly_ReturnsKnownChannel()
    {
        var reader = new IdentityChannelReader(typeof(Aspire.Cli.Program).Assembly);

        var channel = reader.ReadChannel();

        Assert.Contains(channel, s_validChannels);
    }

    [Fact]
    public async Task BuildApplication_RegistersIIdentityChannelReader_AsIdentityChannelReaderInstance()
    {
        // PR1-S12 contract: Program.BuildApplicationAsync registers IIdentityChannelReader
        // as a singleton, backed by the default IdentityChannelReader (which reads from
        // Assembly.GetEntryAssembly()).
        using var host = await BuildHostAsync();

        var reader = host.Services.GetRequiredService<IIdentityChannelReader>();

        Assert.NotNull(reader);
        Assert.IsType<IdentityChannelReader>(reader);
    }

    [Fact]
    public async Task BuildApplication_PopulatesCliExecutionContextChannel_FromIdentityChannelReader()
    {
        // PR1-S12 contract: the CliExecutionContext factory delegate must source Channel
        // from IIdentityChannelReader.ReadChannel() rather than the constructor default.
        // Without this wiring, the entire PR1-S10 reseed chain would write "daily" for
        // every CLI build regardless of the baked AspireCliChannel.
        using var host = await BuildHostAsync();

        var reader = host.Services.GetRequiredService<IIdentityChannelReader>();
        var context = host.Services.GetRequiredService<CliExecutionContext>();

        Assert.Equal(reader.ReadChannel(), context.Channel);
    }

    [Fact]
    public async Task BuildApplication_LocallyBuiltCli_HasDailyChannelAndNullPrNumber()
    {
        // The Aspire.Cli.csproj defaults AspireCliChannel to "daily" when not overridden
        // by CI (no /p:AspireCliChannel=...), so a locally-built CLI assembly must expose
        // Channel == "daily" and PrNumber == null through the bootstrapped context.
        using var host = await BuildHostAsync();

        var context = host.Services.GetRequiredService<CliExecutionContext>();

        Assert.Equal("daily", context.Channel);
        Assert.Null(context.PrNumber);
    }

    [Fact]
    public async Task BuildApplication_CliExecutionContextChannel_MatchesAssemblyMetadataAttribute()
    {
        // End-to-end coherence: the channel flowing through the DI container must equal the
        // value baked into the entry assembly by [AssemblyMetadata("AspireCliChannel", "...")].
        // The bootstrap registers the default IdentityChannelReader, which reads from
        // Assembly.GetEntryAssembly(); under `dotnet test` that's the test host (which mirrors
        // the production "daily" via the test csproj's AssemblyMetadata item).
        var entryAssembly = Assembly.GetEntryAssembly();
        Assert.NotNull(entryAssembly);
        var bakedChannel = entryAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => string.Equals(a.Key, "AspireCliChannel", StringComparison.Ordinal))
            .Value;
        Assert.False(string.IsNullOrEmpty(bakedChannel));

        using var host = await BuildHostAsync();

        var context = host.Services.GetRequiredService<CliExecutionContext>();

        Assert.Equal(bakedChannel, context.Channel);
    }
}
