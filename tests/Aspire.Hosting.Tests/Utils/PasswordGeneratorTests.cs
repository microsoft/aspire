// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using static Aspire.Hosting.Utils.PasswordGenerator;

namespace Aspire.Hosting.Tests.Utils;

[Trait("Partition", "4")]
public class PasswordGeneratorTests
{
    [Fact]
    public void ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Generate(-1, true, true, true, true, 0, 0, 0, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => Generate(10, true, true, true, true, -1, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Generate(10, true, true, true, true, 0, -1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Generate(10, true, true, true, true, 0, 0, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Generate(10, true, true, true, true, 0, 0, 0, -1));
    }

    [Fact]
    public void ThrowsArgumentException()
    {
        // can't have a minimum requirement when that type is disabled
        Assert.Throws<ArgumentException>(() => Generate(10, false, true, true, true, 1, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => Generate(10, true, false, true, true, 0, 1, 0, 0));
        Assert.Throws<ArgumentException>(() => Generate(10, true, true, false, true, 0, 0, 1, 0));
        Assert.Throws<ArgumentException>(() => Generate(10, true, true, true, false, 0, 0, 0, 1));

        Assert.Throws<ArgumentException>(() => Generate(10, false, false, false, false, 0, 0, 0, 0));
    }

    [Fact]
    public void ThrowsOverflowException()
    {
        Assert.Throws<OverflowException>(() => Generate(10, true, true, true, true, int.MaxValue, 1, 0, 0));
    }

    [Theory]
    [InlineData(true, true, true, true, LowerCaseChars + UpperCaseChars + NumericChars + SpecialChars, null)]
    [InlineData(true, true, true, false, LowerCaseChars + UpperCaseChars + NumericChars, SpecialChars)]
    [InlineData(true, true, false, true, LowerCaseChars + UpperCaseChars + SpecialChars, NumericChars)]
    [InlineData(true, true, false, false, LowerCaseChars + UpperCaseChars, NumericChars + SpecialChars)]
    [InlineData(true, false, true, true, LowerCaseChars + NumericChars + SpecialChars, UpperCaseChars)]
    [InlineData(true, false, true, false, LowerCaseChars + NumericChars, UpperCaseChars + SpecialChars)]
    [InlineData(true, false, false, true, LowerCaseChars + SpecialChars, UpperCaseChars + NumericChars)]
    [InlineData(true, false, false, false, LowerCaseChars, UpperCaseChars + NumericChars + SpecialChars)]
    [InlineData(false, true, true, true, UpperCaseChars + NumericChars + SpecialChars, LowerCaseChars)]
    [InlineData(false, true, true, false, UpperCaseChars + NumericChars, LowerCaseChars + SpecialChars)]
    [InlineData(false, true, false, true, UpperCaseChars + SpecialChars, LowerCaseChars + NumericChars)]
    [InlineData(false, true, false, false, UpperCaseChars, LowerCaseChars + NumericChars + SpecialChars)]
    [InlineData(false, false, true, true, NumericChars + SpecialChars, LowerCaseChars + UpperCaseChars)]
    [InlineData(false, false, true, false, NumericChars, LowerCaseChars + UpperCaseChars + SpecialChars)]
    [InlineData(false, false, false, true, SpecialChars, LowerCaseChars + UpperCaseChars + NumericChars)]
    // NOTE: all false throws ArgumentException
    public void TestGenerate(bool lower, bool upper, bool numeric, bool special, string includes, string? excludes)
    {
        var password = Generate(10, lower, upper, numeric, special, 0, 0, 0, 0);

        Assert.Equal(10, password.Length);
        Assert.True(password.All(includes.Contains));

        if (excludes is not null)
        {
            Assert.True(!password.Any(excludes.Contains));
        }
    }

    [Theory]
    [InlineData(1, 0, 0, 0)]
    [InlineData(0, 1, 0, 0)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 2, 1, 0)]
    [InlineData(0, 0, 2, 3)]
    [InlineData(1, 0, 2, 0)]
    [InlineData(5, 1, 1, 1)]
    public void TestGenerateMin(int minLower, int minUpper, int minNumeric, int minSpecial)
    {
        var password = Generate(10, true, true, true, true, minLower, minUpper, minNumeric, minSpecial);

        Assert.Equal(10, password.Length);

        if (minLower > 0)
        {
            Assert.True(password.Count(LowerCaseChars.Contains) >= minLower);
        }
        if (minUpper > 0)
        {
            Assert.True(password.Count(UpperCaseChars.Contains) >= minUpper);
        }
        if (minNumeric > 0)
        {
            Assert.True(password.Count(NumericChars.Contains) >= minNumeric);
        }
        if (minSpecial > 0)
        {
            Assert.True(password.Count(SpecialChars.Contains) >= minSpecial);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(22)]
    public void ValidUriCharacters(int minLength)
    {
        var password = Generate(minLength, true, true, true, false, 0, 0, 0, 0);
        password += SpecialChars;

        Exception? exception = Record.Exception(() => new Uri($"https://guest:{password}@localhost:12345"));

        Assert.True((exception is null), $"Password contains invalid chars: {password}");
    }

    [Fact]
    public void MinLengthLessThanSumMinTypes()
    {
        var password = Generate(7, true, true, true, true, 2, 2, 2, 2);

        Assert.Equal(8, password.Length);
    }

    [Fact]
    public void WorksWithLargeLengths()
    {
        var password = Generate(1025, true, true, true, true, 0, 0, 0, 0);
        Assert.Equal(1025, password.Length);

        password = Generate(10, true, true, true, true, 1024, 1024, 1024, 1025);
        Assert.Equal(4097, password.Length);
    }
}
