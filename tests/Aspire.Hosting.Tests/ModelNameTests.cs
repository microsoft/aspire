// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class ModelNameTests
{
    [Fact]
    public void ValidateName_Null_Error()
    {
        // Arrange & Act
        var exception = Assert.Throws<ArgumentNullException>(() => ModelName.ValidateName(nameof(Resource), null!));

        // Assert
        Assert.Equal("Value cannot be null. (Parameter 'name')", exception.Message);
    }

    [Fact]
    public void ValidateName_Empty_Error()
    {
        // Arrange & Act
        var exception = Assert.Throws<ArgumentException>(() => ModelName.ValidateName(nameof(Resource), ""));

        // Assert
        Assert.Equal($"Resource name '' is invalid. Name must be between 1 and 64 characters long. (Parameter 'name')", exception.Message);
    }

    [Fact]
    public void ValidateName_LongString_Error()
    {
        // Arrange & Act
        var name = new string('a', 65);
        var exception = Assert.Throws<ArgumentException>(() => ModelName.ValidateName(nameof(Resource), name));

        // Assert
        Assert.Equal($"Resource name '{name}' is invalid. Name must be between 1 and 64 characters long. (Parameter 'name')", exception.Message);
    }

    [Fact]
    public void ValidateName_Whitespace_Error()
    {
        // Arrange & Act
        var exception = Assert.Throws<ArgumentException>(() => ModelName.ValidateName(nameof(Resource), " "));

        // Assert
        Assert.Equal("Resource name ' ' is invalid. Name must contain only ASCII letters, digits, and hyphens. (Parameter 'name')", exception.Message);
    }

    [Fact]
    public void ValidateName_Underscore_Error()
    {
        // Arrange & Act
        var exception = Assert.Throws<ArgumentException>(() => ModelName.ValidateName(nameof(Resource), "test_name"));

        // Assert
        Assert.Equal("Resource name 'test_name' is invalid. Name must contain only ASCII letters, digits, and hyphens. (Parameter 'name')", exception.Message);
    }

    [Fact]
    public void ValidateName_StartHyphen_Error()
    {
        // Arrange & Act
        var exception = Assert.Throws<ArgumentException>(() => ModelName.ValidateName(nameof(Resource), "-abc"));

        // Assert
        Assert.Equal("Resource name '-abc' is invalid. Name must start with an ASCII letter. (Parameter 'name')", exception.Message);
    }

    [Fact]
    public void ValidateName_ConsecutiveHyphens_Error()
    {
        // Arrange & Act
        var exception = Assert.Throws<ArgumentException>(() => ModelName.ValidateName(nameof(Resource), "test--name"));

        // Assert
        Assert.Equal("Resource name 'test--name' is invalid. Name cannot contain consecutive hyphens. (Parameter 'name')", exception.Message);
    }

    [Fact]
    public void ValidateName_StartNumber_Error()
    {
        // Arrange & Act
        var exception = Assert.Throws<ArgumentException>(() => ModelName.ValidateName(nameof(Resource), "1abc"));

        // Assert
        Assert.Equal("Resource name '1abc' is invalid. Name must start with an ASCII letter. (Parameter 'name')", exception.Message);
    }

    [Fact]
    public void ValidateName_EndHyphen_Error()
    {
        // Arrange & Act
        var exception = Assert.Throws<ArgumentException>(() => ModelName.ValidateName(nameof(Resource), "abc-"));

        // Assert
        Assert.Equal("Resource name 'abc-' is invalid. Name cannot end with a hyphen. (Parameter 'name')", exception.Message);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    [InlineData("abc123")]
    [InlineData("abc-123")]
    [InlineData("a-b-c-1-2-3")]
    [InlineData("ABC")]
    public void ValidateName_ValidNames_Success(string name)
    {
        ModelName.ValidateName(nameof(Resource), name);
    }
}
