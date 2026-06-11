// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model.Serialization;
using Google.Protobuf;
using Xunit;

namespace Aspire.Dashboard.Tests.Model.Serialization;

public class OtlpJsonProtobufConverterTests
{
    [Fact]
    public void HexToByteString_EmptyString_ReturnsEmptyByteString()
    {
        var result = OtlpJsonToProtobufConverter.HexToByteString(string.Empty);

        Assert.Equal(ByteString.Empty, result);
    }

    [Fact]
    public void HexToByteString_NullString_ReturnsEmptyByteString()
    {
        var result = OtlpJsonToProtobufConverter.HexToByteString(null!);

        Assert.Equal(ByteString.Empty, result);
    }

    [Theory]
    [InlineData("00", new byte[] { 0x00 })]
    [InlineData("ff", new byte[] { 0xff })]
    [InlineData("FF", new byte[] { 0xff })]
    [InlineData("0123456789abcdef", new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef })]
    [InlineData("ABCDEF", new byte[] { 0xab, 0xcd, 0xef })]
    public void HexToByteString_ValidHex_ReturnsExpectedBytes(string hex, byte[] expected)
    {
        var result = OtlpJsonToProtobufConverter.HexToByteString(hex);

        Assert.Equal(expected, result.ToByteArray());
    }

    [Fact]
    public void HexToByteString_TraceId_ReturnsCorrectBytes()
    {
        // 16-byte trace ID (32 hex chars)
        var traceIdHex = "0af7651916cd43dd8448eb211c80319c";

        var result = OtlpJsonToProtobufConverter.HexToByteString(traceIdHex);

        Assert.Equal(16, result.Length);
        Assert.Equal(0x0a, result[0]);
        Assert.Equal(0xf7, result[1]);
        Assert.Equal(0x9c, result[15]);
    }

    [Fact]
    public void HexToByteString_SpanId_ReturnsCorrectBytes()
    {
        // 8-byte span ID (16 hex chars)
        var spanIdHex = "00f067aa0ba902b7";

        var result = OtlpJsonToProtobufConverter.HexToByteString(spanIdHex);

        Assert.Equal(8, result.Length);
        Assert.Equal(0x00, result[0]);
        Assert.Equal(0xf0, result[1]);
        Assert.Equal(0xb7, result[7]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("12345")]
    public void HexToByteString_OddLengthString_ThrowsArgumentException(string hex)
    {
        var exception = Assert.Throws<ArgumentException>(() => OtlpJsonToProtobufConverter.HexToByteString(hex));

        Assert.Equal("hex", exception.ParamName);
        Assert.Contains("even length", exception.Message);
    }

    [Theory]
    [InlineData("gg")]
    [InlineData("zz")]
    [InlineData("0x12")]
    public void HexToByteString_InvalidHexCharacters_ThrowsFormatException(string hex)
    {
        Assert.Throws<FormatException>(() => OtlpJsonToProtobufConverter.HexToByteString(hex));
    }
}
