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
