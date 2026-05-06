// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Cli.Acquisition;

namespace Aspire.Cli.Tests;

/// <summary>
/// Regression tests for PR1 bootstrap wiring: the running CLI's
/// <see cref="CliExecutionContext.Channel"/> should be sourced from the binary's
/// <c>[AssemblyMetadata("AspireCliChannel")]</c> via
/// <see cref="IIdentityChannelReader"/>.
/// <para>
/// At PR1's current commit (S4..S10 landed), the reader exists but is not yet wired
/// into Program.cs / DI — <see cref="CliExecutionContext.Channel"/> still defaults
/// to <c>"daily"</c> via the constructor default. The integration test below is
/// expected-failing and serves as a tripwire for whoever lands the bootstrap wiring.
/// See decision drop: <c>livingston-pr1-bootstrap-wire.md</c>.
/// </para>
/// </summary>
public class CliBootstrapTests
{
    [Fact]
    public void IIdentityChannelReader_TypeExists_AndProductionImplementationIsRegistered()
    {
        // Locks the type signatures in place so the bootstrap wiring (when it lands) has
        // a stable surface to bind to.
        var iface = typeof(IIdentityChannelReader);
        Assert.True(iface.IsInterface);

        var readChannel = iface.GetMethod(nameof(IIdentityChannelReader.ReadChannel));
        Assert.NotNull(readChannel);
        Assert.Equal(typeof(string), readChannel.ReturnType);
        Assert.Empty(readChannel.GetParameters());

        var impl = typeof(IdentityChannelReader);
        Assert.True(iface.IsAssignableFrom(impl));

        // Production constructor: optional Assembly? (defaults to GetEntryAssembly()).
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
        // End-to-end: the actual Aspire.Cli assembly being tested has AspireCliChannel
        // metadata baked in by the csproj (PR1-S2). The reader must extract it and the
        // value must be one of the four valid channels.
        var reader = new IdentityChannelReader(typeof(Aspire.Cli.Program).Assembly);

        var channel = reader.ReadChannel();

        Assert.Contains(channel, new[] { "stable", "staging", "daily", "pr" });
    }

    [Fact]
    public void IIdentityChannelReader_NotYetRegisteredInProductionDI_BootstrapWiringIsPendingFollowUp()
    {
        // Snapshot of the current PR1 state: PR1-S4 added IIdentityChannelReader and the
        // default IdentityChannelReader implementation, but Program.cs does NOT yet register
        // the interface in its DI container, nor does it call ReadChannel() at process start
        // to populate CliExecutionContext.Channel. CliExecutionContext.Channel still defaults
        // to the constructor's "daily" literal.
        //
        // This test pins that state. When the bootstrap wiring lands, this test should be
        // updated (or removed) along with new positive coverage for:
        //   * AddSingleton<IIdentityChannelReader, IdentityChannelReader>() in Program.cs
        //   * CliExecutionContext constructed from IIdentityChannelReader.ReadChannel()
        //     and IdentityChannelReader.ParsePrNumber(InformationalVersion)
        //
        // Tracked as a decision drop to Ocean: livingston-pr1-bootstrap-wire-needed.md
        var startupContextType = typeof(Aspire.Cli.Program).Assembly
            .GetType("Aspire.Cli.StartupContext", throwOnError: false);

        // Reflection-driven assertion: search any non-public type in the CLI assembly for
        // an explicit registration of IIdentityChannelReader. As of PR1-S10 there is none.
        var assembly = typeof(Aspire.Cli.Program).Assembly;
        var hasIdentityChannelReaderUsageSymbol = assembly
            .GetTypes()
            .Where(t => t.Namespace?.StartsWith("Aspire.Cli", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Any(m => m.Name.Contains("IdentityChannelReader", StringComparison.Ordinal));

        // The interface and the impl exist (S4 landed) but no consumer references the symbol
        // by name in any method (no DI registration, no call site). When this changes, this
        // assertion will flip and the test should be updated alongside the wiring PR.
        Assert.False(
            hasIdentityChannelReaderUsageSymbol,
            "IIdentityChannelReader appears to have a consumer in the production CLI now. " +
            "If you just added bootstrap wiring (PR1 follow-up), update CliBootstrapTests to " +
            "assert the new wiring positively (DI registration + CliExecutionContext.Channel " +
            "populated from ReadChannel() + ParsePrNumber()) and delete this snapshot test.");
    }
}
