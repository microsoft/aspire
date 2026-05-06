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

    // PR1-TG1: edge cases for AssemblyMetadata semantics.

    [Fact]
    public void ReadChannel_ChannelMetadataValueIsUnknownString_ReturnedVerbatim()
    {
        // The reader does not validate the value — invalid values are caught at build time
        // by AssemblyMetadataChannelTests (the smoke test from PR1-S5). Document the
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

    [Fact]
    public void ParsePrNumber_PrChannelInformationalVersion_ReturnsPrNumber()
    {
        Assert.Equal(12345, IdentityChannelReader.ParsePrNumber("0.0.0-pr12345.deadbeef"));
    }

    [Fact]
    public void ParsePrNumber_PreviewSuffixWithoutPrMarker_ReturnsNull()
    {
        Assert.Null(IdentityChannelReader.ParsePrNumber("1.2.3-preview.5"));
    }

    [Fact]
    public void ParsePrNumber_PrMarkerWithDotImmediatelyAfter_ReturnsNull()
    {
        // "0.0.0-pr.5" -> after "-pr" we see '.', no digits, so null.
        Assert.Null(IdentityChannelReader.ParsePrNumber("0.0.0-pr.5"));
    }

    [Fact]
    public void ParsePrNumber_NullInput_ReturnsNull()
    {
        Assert.Null(IdentityChannelReader.ParsePrNumber(null));
    }

    [Fact]
    public void ParsePrNumber_PrMarkerWithoutDotSuffix_ReturnsNumber()
    {
        // "0.0.0-pr0" — digits run to end-of-string with no dot delimiter; "0" parses to 0.
        Assert.Equal(0, IdentityChannelReader.ParsePrNumber("0.0.0-pr0"));
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
