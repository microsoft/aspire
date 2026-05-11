// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Reflection.Emit;
using Aspire.Cli.Acquisition;

namespace Aspire.Cli.Tests.Acquisition;

public class IdentityChannelReaderTests
{
    private const string ChannelMetadataKey = "AspireCliChannel";

    [Theory]
    [InlineData("stable")]
    [InlineData("staging")]
    [InlineData("daily")]
    [InlineData("local")]
    [InlineData("pr-1")]
    [InlineData("pr-12345")]
    [InlineData("pr-2147483647")]
    public void ReadChannel_AssemblyHasMetadataForValidChannel_ReturnsValue(string channel)
    {
        var assembly = BuildFakeAssemblyWithChannelMetadata($"FakeCli_{channel.Replace('-', '_')}", channel);

        var reader = new IdentityChannelReader(assembly);

        Assert.Equal(channel, reader.ReadChannel());
    }

    [Fact]
    public void ReadChannel_AssemblyMissingChannelMetadata_ThrowsWithAssemblyName()
    {
        const string assemblyName = "FakeCli_NoChannel";
        var assembly = BuildFakeAssemblyWithChannelMetadata(assemblyName, channelValue: null);

        var reader = new IdentityChannelReader(assembly);

        var ex = Assert.Throws<InvalidOperationException>(reader.ReadChannel);
        Assert.Contains(ChannelMetadataKey, ex.Message, StringComparison.Ordinal);
        Assert.Contains(assemblyName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadChannel_AssemblyHasEmptyChannelMetadata_Throws()
    {
        var assembly = BuildFakeAssemblyWithChannelMetadata("FakeCli_EmptyChannel", channelValue: string.Empty);

        var reader = new IdentityChannelReader(assembly);

        var ex = Assert.Throws<InvalidOperationException>(reader.ReadChannel);
        Assert.Contains(ChannelMetadataKey, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("foobar")]                       // free-form garbage
    [InlineData("pr")]                           // legacy literal "pr" without "-<N>" suffix
    [InlineData("pr-")]                          // prefix with no digits
    [InlineData("pr-abc")]                       // non-digits after prefix
    [InlineData("pr-12a")]                       // mixed digits + non-digits
    [InlineData("pr-12.34")]                     // dot inside the number
    [InlineData("PR-12345")]                     // wrong case for the prefix
    [InlineData("   ")]                          // whitespace-only
    [InlineData(" pr-12345")]                    // leading whitespace
    [InlineData("pr-12345 ")]                    // trailing whitespace
    public void ReadChannel_InvalidChannelValue_Throws(string channelValue)
    {
        // The reader is the single gate on the channel shape. The runtime
        // must fail loudly here rather than letting an unrecognised value
        // become a hive label and silently misroute packages.
        var assembly = BuildFakeAssemblyWithChannelMetadata($"FakeCli_Invalid_{Math.Abs(channelValue.GetHashCode())}", channelValue);

        var reader = new IdentityChannelReader(assembly);

        var ex = Assert.Throws<InvalidOperationException>(reader.ReadChannel);
        Assert.Contains(ChannelMetadataKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadChannel_AssemblyHasMultipleChannelMetadataAttributes_ReturnsFirstNonEmpty()
    {
        // MSBuild misconfiguration could conceivably emit two AspireCliChannel entries.
        // The reader uses FirstOrDefault, so the first attribute encountered wins. Document
        // this so future changes don't silently flip ordering.
        var assembly = BuildFakeAssemblyWithChannelMetadata(
            "FakeCli_DualChannel",
            channelValues: ["staging", "pr-12345"]);

        var reader = new IdentityChannelReader(assembly);

        Assert.Equal("staging", reader.ReadChannel());
    }

    [Fact]
    public void ReadChannel_KeyLookupIsCaseSensitive_DifferentCaseTreatedAsMissing()
    {
        var assembly = BuildFakeAssemblyWithMetadata(
            "FakeCli_CaseMismatch",
            metadata: [(Key: "aspirecliChannel", Value: "stable")]);

        var reader = new IdentityChannelReader(assembly);

        var ex = Assert.Throws<InvalidOperationException>(reader.ReadChannel);
        Assert.Contains(ChannelMetadataKey, ex.Message, StringComparison.Ordinal);
    }

    // IsValidChannel is the gate for the baked value. We assert its exact
    // truth table here so the reader's contract is testable without round-
    // tripping through a fake assembly. The Throws-based tests above cover
    // the integration through ResolveChannel.
    [Theory]
    [InlineData("stable", true)]
    [InlineData("staging", true)]
    [InlineData("daily", true)]
    [InlineData("local", true)]
    [InlineData("pr-1", true)]
    [InlineData("pr-12345", true)]
    [InlineData("pr-0", true)]                                  // zero is permitted; build pipeline never emits it but the reader does not gate on positivity
    [InlineData("pr-99999999999999999999", true)]               // arbitrarily long digit run is accepted; range check is not the reader's job
    [InlineData("pr-0123", true)]                               // leading zeros are permitted; locks in the "any ASCII-digit run" contract
    [InlineData("pr", false)]                                   // legacy literal — the change this test guards against regressing
    [InlineData("pr-", false)]
    [InlineData("pr-abc", false)]
    [InlineData("pr-12a", false)]
    [InlineData("pr-12.34", false)]
    [InlineData("pr-+1", false)]                                // sign chars are not ASCII digits — guards against a "support signed numerics" regression
    [InlineData("pr--1", false)]
    [InlineData("pr-1 2", false)]                               // embedded space inside the digit portion
    [InlineData("pr-1\n", false)]                               // trailing control char inside the digit portion
    [InlineData("pr-١", false)]                                 // Arabic-Indic digit U+0661 — ASCII-only contract, not Unicode-digit
    [InlineData("Pr-12345", false)]                             // case-sensitive prefix
    [InlineData("PR-12345", false)]
    [InlineData("Stable", false)]                               // literals are case-sensitive (ordinal)
    [InlineData("STABLE", false)]
    [InlineData("Local", false)]
    [InlineData(" stable", false)]                              // leading whitespace on a literal — symmetric with the existing pr-12345 whitespace cases
    [InlineData("stable ", false)]
    [InlineData("local-foo", false)]                            // literal-as-prefix must not match; only "pr-" is a recognised prefix
    [InlineData("foobar", false)]
    [InlineData("default", false)]                              // PackageChannelNames.Default is for runtime PSM, never baked
    [InlineData("   ", false)]
    [InlineData("", false)]
    [InlineData(" pr-12345", false)]
    [InlineData("pr-12345 ", false)]
    public void IsValidChannel_MatchesExpectedTruthTable(string value, bool expected)
    {
        Assert.Equal(expected, IdentityChannelReader.IsValidChannel(value));
    }

    private static Assembly BuildFakeAssemblyWithChannelMetadata(string assemblyName, string? channelValue)
    {
        if (channelValue is null)
        {
            return BuildFakeAssemblyWithMetadata(assemblyName, metadata: []);
        }

        return BuildFakeAssemblyWithMetadata(
            assemblyName,
            metadata: [(Key: ChannelMetadataKey, Value: channelValue)]);
    }

    private static Assembly BuildFakeAssemblyWithChannelMetadata(string assemblyName, string[] channelValues)
    {
        var metadata = channelValues
            .Select(v => (Key: ChannelMetadataKey, Value: v))
            .ToArray();
        return BuildFakeAssemblyWithMetadata(assemblyName, metadata);
    }

    private static Assembly BuildFakeAssemblyWithMetadata(string assemblyName, (string Key, string Value)[] metadata)
    {
        var name = new AssemblyName(assemblyName);
        var builder = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);

        var attributeCtor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        foreach (var (key, value) in metadata)
        {
            builder.SetCustomAttribute(new CustomAttributeBuilder(attributeCtor, [key, value]));
        }

        return builder;
    }
}
