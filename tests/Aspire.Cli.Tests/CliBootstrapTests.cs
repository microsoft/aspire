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
/// Integration tests for the bootstrap wiring: the running CLI's
/// <see cref="CliExecutionContext.Channel"/> is sourced from the binary's
/// <c>[AssemblyMetadata("AspireCliChannel")]</c> value via
/// <see cref="IIdentityChannelReader"/>, registered in DI by
/// <see cref="Aspire.Cli.Program.BuildApplicationAsync"/>.
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

        // Spec (PR1 follow-through): the ctor MUST require an explicit assembly. The default
        // null parameter was a footgun under RemoteExecutor / plugin-loader scenarios where
        // Assembly.GetEntryAssembly() returns the wrong assembly. Callers must decide.
        Assert.False(parameters[0].HasDefaultValue,
            "IdentityChannelReader ctor must NOT have a default parameter — see PR1 follow-through removing the Assembly? = null footgun.");
    }

    [Fact]
    public void IdentityChannelReader_NullAssembly_ThrowsArgumentNullException()
    {
        // Spec (PR1 follow-through): explicit null produces an immediate, descriptive
        // ArgumentNullException so misuse is caught at construction time rather than
        // surfacing later as the cryptic "metadata missing on '?'" exception.
        Assert.Throws<ArgumentNullException>(() => new IdentityChannelReader(null!));
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
        // Program.BuildApplicationAsync registers IIdentityChannelReader as a singleton,
        // backed by the default IdentityChannelReader (which reads from
        // Assembly.GetEntryAssembly()).
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

        Assert.Equal(reader.ReadChannel(), context.Channel);
    }

    [Fact]
    public async Task BuildApplication_LocallyBuiltCli_ChannelMatchesTestHostAssemblyMetadata()
    {
        // The Aspire.Cli.csproj defaults AspireCliChannel to "daily" when not overridden by
        // CI; the test csproj forwards $(AspireCliChannel) the same way (see csproj comment).
        // The test host and production assembly therefore stay in lockstep regardless of
        // whether the build runs under /p:AspireCliChannel=stable or unspecified — both pick
        // up the same value. We assert the bootstrapped context's channel matches the
        // *test host's* baked metadata, NOT a hard-coded literal, so the test stops being
        // an accidental regression for any non-default build.
        using var host = await BuildHostAsync();

        var entryAssembly = Assembly.GetEntryAssembly();
        Assert.NotNull(entryAssembly);
        var bakedChannel = entryAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => string.Equals(a.Key, "AspireCliChannel", StringComparison.Ordinal))
            .Value;
        Assert.False(string.IsNullOrEmpty(bakedChannel));

        var context = host.Services.GetRequiredService<CliExecutionContext>();

        Assert.Equal(bakedChannel, context.Channel);
        // PrNumber is non-null only when the test host is itself a PR build. In a local
        // dev build (and the default CI path) it should be null. We assert the contract:
        // PrNumber.HasValue iff the channel resolved to "pr-<N>".
        if (context.IdentityChannel == "pr")
        {
            Assert.NotNull(context.PrNumber);
        }
        else
        {
            Assert.Null(context.PrNumber);
        }
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
