// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests.ApplicationModel;

[Trait("Partition", "4")]
public class GenerateParameterDefaultTests
{
    [Fact]
    public void GetDefaultValue_Respects_Length_And_LowerOnly()
    {
        var gd = new GenerateParameterDefault
        {
            MinLength = 10,
            Lower = true,
            Upper = false,
            Numeric = false,
            Special = false
        };

        var value = gd.GetDefaultValue();

        Assert.Equal(10, value.Length);
        Assert.True(value.All(PasswordGenerator.LowerCaseChars.Contains));
    }

    [Fact]
    public void GetDefaultValue_Respects_Minimum_Type_Counts()
    {
        var gd = new GenerateParameterDefault
        {
            MinLength = 12,
            Lower = true,
            Upper = true,
            Numeric = true,
            Special = true,
            MinLower = 2,
            MinUpper = 1,
            MinNumeric = 1,
            MinSpecial = 2
        };

        var value = gd.GetDefaultValue();

        Assert.True(value.Count(PasswordGenerator.LowerCaseChars.Contains) >= gd.MinLower);
        Assert.True(value.Count(PasswordGenerator.UpperCaseChars.Contains) >= gd.MinUpper);
        Assert.True(value.Count(PasswordGenerator.NumericChars.Contains) >= gd.MinNumeric);
        Assert.True(value.Count(PasswordGenerator.SpecialChars.Contains) >= gd.MinSpecial);
        Assert.True(value.Length >= 12);
    }

    [Fact]
    public void GetDefaultValue_Default_HasAtLeast128BitsOfEntropy()
    {
        var defaultGenerator = new GenerateParameterDefault();

        var choiceCount =
            PasswordGenerator.LowerCaseChars.Length +
            PasswordGenerator.UpperCaseChars.Length +
            PasswordGenerator.NumericChars.Length +
            PasswordGenerator.SpecialChars.Length;

        var entropyBits = defaultGenerator.MinLength * Math.Log2(choiceCount);

        Assert.True(entropyBits >= 128, $"Default password entropy ({entropyBits} bits) must be at least 128 bits.");
    }
}
