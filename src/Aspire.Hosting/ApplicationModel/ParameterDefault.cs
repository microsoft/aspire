// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Publishing;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents how a default value should be retrieved.
/// </summary>
public abstract class ParameterDefault
{
    /// <summary>
    /// Writes the current <see cref="ParameterDefault"/> to the manifest context.
    /// </summary>
    /// <param name="context">The context for the manifest publishing operation.</param>
    public abstract void WriteToManifest(ManifestPublishingContext context);

    /// <summary>
    /// Generates a value for the parameter.
    /// </summary>
    /// <returns>The generated string value.</returns>
    public abstract string GetDefaultValue();
}

/// <summary>
/// Represents that a default value should be generated.
/// </summary>
/// <remarks>
/// The recommended minimum bits of entropy for a generated password is 128 bits.
///
/// <para>
/// The general calculation of bits of entropy is:
/// </para>
///
/// <c>log base 2 (numberPossibleOutputs)</c>
///
/// <para>
/// This generator uses 23 upper case, 23 lower case (excludes i,l,o,I,L,O to prevent confusion),
/// 10 numeric, and 11 special characters. So a total of 67 possible characters.
/// </para>
/// 
/// <para>
/// When all character sets are enabled, the number of possible outputs is <c>(67 ^ length)</c>.
/// The minimum password length for 128 bits of entropy is 22 characters: <c>log base 2 (67 ^ 22)</c>.
/// </para>
///
/// <para>
/// When character sets are disabled, it lowers the number of possible outputs and thus the bits of entropy.
/// </para>
///
/// <para>
/// Using MinLower, MinUpper, MinNumeric, and MinSpecial also lowers the number of possible outputs and thus the bits of entropy.
/// </para>
/// 
/// <para>
/// A generalized lower-bound formula for the number of possible outputs is to consider a string of the form:
/// </para>
///
/// <code lang="csharp">
/// {nonRequiredCharacters}{requiredCharacters}
///
/// let a = MinLower, b = MinUpper, c = MinNumeric, d = MinSpecial
/// let x = length - (a + b + c + d)
///
/// nonRequiredPossibilities = 67^x
/// requiredPossibilities = 23^a * 23^b * 10^c * 11^d * (a + b + c + d)! / (a! * b! * c! * d!)
/// 
/// lower-bound of total possibilities = nonRequiredPossibilities * requiredPossibilities
/// </code>
///
/// Putting it all together, the lower-bound bits of entropy calculation is:
///
/// <code lang="csharp">
/// log base 2 [67^x * 23^a * 23^b * 10^c * 11^d * (a + b + c + d)! / (a! * b! * c! * d!)]
/// </code>
/// </remarks>
[AspireDto]
public sealed class GenerateParameterDefault : ParameterDefault
{
    /// <summary>
    /// Gets or sets the minimum length of the generated value.
    /// </summary>
    public int MinLength { get; set; } = 22;

    /// <summary>
    /// Gets or sets a value indicating whether to include lowercase alphabet characters in the result.
    /// </summary>
    public bool Lower { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to include uppercase alphabet characters in the result.
    /// </summary>
    public bool Upper { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to include numeric characters in the result.
    /// </summary>
    public bool Numeric { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to include special characters in the result.
    /// </summary>
    public bool Special { get; set; } = true;

    /// <summary>
    /// Gets or sets the minimum number of lowercase characters in the result.
    /// </summary>
    public int MinLower { get; set; }

    /// <summary>
    /// Gets or sets the minimum number of uppercase characters in the result.
    /// </summary>
    public int MinUpper { get; set; }

    /// <summary>
    /// Gets or sets the minimum number of numeric characters in the result.
    /// </summary>
    public int MinNumeric { get; set; }

    /// <summary>
    /// Gets or sets the minimum number of special characters in the result.
    /// </summary>
    public int MinSpecial { get; set; }

    /// <inheritdoc/>
    public override void WriteToManifest(ManifestPublishingContext context)
    {
        context.Writer.WriteStartObject("generate");
        context.Writer.WriteNumber("minLength", MinLength);

        static void WriteBoolIfNotTrue(ManifestPublishingContext context, string propertyName, bool value)
        {
            if (value != true)
            {
                context.Writer.WriteBoolean(propertyName, value);
            }
        }

        WriteBoolIfNotTrue(context, "lower", Lower);
        WriteBoolIfNotTrue(context, "upper", Upper);
        WriteBoolIfNotTrue(context, "numeric", Numeric);
        WriteBoolIfNotTrue(context, "special", Special);

        static void WriteIntIfNotZero(ManifestPublishingContext context, string propertyName, int value)
        {
            if (value != 0)
            {
                context.Writer.WriteNumber(propertyName, value);
            }
        }

        WriteIntIfNotZero(context, "minLower", MinLower);
        WriteIntIfNotZero(context, "minUpper", MinUpper);
        WriteIntIfNotZero(context, "minNumeric", MinNumeric);
        WriteIntIfNotZero(context, "minSpecial", MinSpecial);

        context.Writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public override string GetDefaultValue() =>
        PasswordGenerator.Generate(MinLength, Lower, Upper, Numeric, Special, MinLower, MinUpper, MinNumeric, MinSpecial);
}

// Simple parameter default that just returns a constant value, at both runtime and publish time.
class ConstantParameterDefault(Func<string> valueGetter) : ParameterDefault
{
    private string? _value;
    private bool _hasValue;

    public override string GetDefaultValue()
    {
        if (!_hasValue)
        {
            _value = valueGetter();
            _hasValue = true;
        }
        return _value!;
    }

    public override void WriteToManifest(ManifestPublishingContext context)
    {
        context.Writer.WriteString("value", GetDefaultValue());
    }
}
