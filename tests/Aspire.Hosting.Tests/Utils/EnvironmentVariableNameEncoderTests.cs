// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests.Utils;

[Trait("Partition", "4")]
public class EnvironmentVariableNameEncoderTests
{
    [Theory]
    [InlineData("resource")]
    [InlineData("RESOURCE_123")]
    [InlineData("Already_Valid_Name")]
    public void Encode_WhenNameAlreadyValid_ReturnsOriginalValue(string name)
    {
        var result = EnvironmentVariableNameEncoder.Encode(name);

        Assert.Equal(name, result);
    }

    [Theory]
    [InlineData("service-name", "service_name")]
    [InlineData("service.name", "service_name")]
    [InlineData("multi--segment", "multi__segment")]
    public void Encode_ReplacesInvalidCharactersWithUnderscore(string name, string expected)
    {
        var result = EnvironmentVariableNameEncoder.Encode(name);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("1service", "_1service")]
    [InlineData("9-service", "_9_service")]
    public void Encode_WhenNameStartsWithDigit_PrependsUnderscore(string name, string expected)
    {
        var result = EnvironmentVariableNameEncoder.Encode(name);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Encode_WhenNameIsEmpty_ReturnsEmptyString()
    {
        Assert.Equal("", EnvironmentVariableNameEncoder.Encode(string.Empty));
    }

    [Fact]
    public void Encode_WhenNameIsNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => EnvironmentVariableNameEncoder.Encode(null!));
    }
}
