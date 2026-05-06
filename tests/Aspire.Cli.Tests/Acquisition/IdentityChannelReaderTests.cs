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
    [InlineData("pr")]
    public void ReadChannel_AssemblyHasMetadataForKnownChannel_ReturnsValue(string channel)
    {
        var assembly = BuildFakeAssemblyWithChannelMetadata($"FakeCli_{channel}", channel);

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

    [Fact]
    public void ReadChannel_ChannelMetadataValueIsUnknownString_ReturnedVerbatim()
    {
        // The reader does not validate the value — invalid values are caught at build time
        // by AssemblyMetadataChannelTests (the smoke test). Document the
        // intentional "trust the build" behavior here.
        var assembly = BuildFakeAssemblyWithChannelMetadata("FakeCli_Foobar", "foobar");

        var reader = new IdentityChannelReader(assembly);

        Assert.Equal("foobar", reader.ReadChannel());
    }

    [Fact]
    public void ReadChannel_AssemblyHasMultipleChannelMetadataAttributes_ReturnsFirstNonEmpty()
    {
        // MSBuild misconfiguration could conceivably emit two AspireCliChannel entries.
        // The reader uses FirstOrDefault, so the first attribute encountered wins. Document
        // this so future changes don't silently flip ordering.
        var assembly = BuildFakeAssemblyWithChannelMetadata(
            "FakeCli_DualChannel",
            channelValues: ["staging", "pr"]);

        var reader = new IdentityChannelReader(assembly);

        Assert.Equal("staging", reader.ReadChannel());
    }

    [Fact]
    public void ReadChannel_ChannelMetadataValueIsWhitespaceOnly_ReturnedVerbatim()
    {
        // Production reader treats only null/empty as "missing" (string.IsNullOrEmpty), not
        // string.IsNullOrWhiteSpace. A whitespace-only value is therefore returned. This is
        // a known but low-risk gap — the build-time smoke test catches it. Documenting the
        // current behavior here so any future tightening is a deliberate decision.
        var assembly = BuildFakeAssemblyWithChannelMetadata("FakeCli_Whitespace", "   ");

        var reader = new IdentityChannelReader(assembly);

        Assert.Equal("   ", reader.ReadChannel());
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

    // Spec-derived: GitHub Actions PR builds emit `InformationalVersion` of the form
    // `<base>-pr.<N>.g<SHA>(+<sha>)?` (see `.github/workflows/ci.yml` /
    // `/p:VersionSuffix=pr.$PR_NUMBER.g$SHORT_SHA`). The parser MUST accept that real
    // shape and extract <N>. The legacy no-separator `-pr<N>.<sha>` form is also
    // accepted for back-compat with assemblies built from local dev shells.
    [Theory]
    [InlineData("0.0.0-pr12345.deadbeef", 12345)]               // legacy no-separator
    [InlineData("0.0.0-pr.5", 5)]                                // CI shape, short
    [InlineData("0.0.0-pr.12345.gabcd1234", 12345)]              // real GH Actions shape
    [InlineData("0.0.0-pr.12345.gabcd1234+abc", 12345)]          // with build metadata
    [InlineData("1.2.3-preview.5", null)]                        // -preview must NOT match
    [InlineData(null, null)]
    [InlineData("0.0.0-pr0", 0)]
    [InlineData("", null)]
    [InlineData("0.0.0", null)]
    [InlineData("0.0.0-pr", null)]                               // marker but no digits
    [InlineData("0.0.0-pr-12345", null)]                         // hyphen separator NOT accepted
    [InlineData("0.0.0-pr12345abc", 12345)]
    [InlineData("0.0.0-pr2147483647", int.MaxValue)]
    [InlineData("0.0.0-pr2147483648", null)]                     // overflow → null
    [InlineData("0.0.0-prabc.def", null)]                        // marker followed by non-digits
    [InlineData("1.0.0-rc.1.pr12345", null)]                     // `.pr` (no leading `-`) must NOT match
    public void ParsePrNumber_ReturnsExpected(string? input, int? expected)
    {
        Assert.Equal(expected, IdentityChannelReader.ParsePrNumber(input));
    }

    [Fact]
    public void ParsePrNumber_RealCliInformationalVersion_DoesNotThrow()
    {
        // Defensive smoke: whatever the test-host CLI assembly's InformationalVersion looks
        // like today, ParsePrNumber must never throw. It either returns a positive number
        // (when the host happens to be a PR build) or null.
        var infoVersion = typeof(Aspire.Cli.Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        var prNumber = IdentityChannelReader.ParsePrNumber(infoVersion);

        Assert.True(prNumber is null or >= 0);
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
