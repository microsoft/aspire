// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Aspire.Hosting.Utils;

internal static class PasswordGenerator
{
    // Some chars are excluded:
    // - prevent potential confusions, e.g., 0,o,O and i,I,l
    // - exclude special chars which could interfere with command line arguments, URL (rfc3986 gen-delims), or connection strings, e.g. =,$,...

    internal const string LowerCaseChars = "abcdefghjkmnpqrstuvwxyz"; // exclude i,l,o
    internal const string UpperCaseChars = "ABCDEFGHJKMNPQRSTUVWXYZ"; // exclude I,L,O
    internal const string NumericChars = "0123456789";
    internal const string SpecialChars = "-_.{}~()*+!"; // exclude &<>=;,`'^%$#@/:[]

    /// <summary>
    /// Creates a cryptographically random string.
    /// </summary>
    public static string Generate(int minLength,
        bool lower, bool upper, bool numeric, bool special,
        int minLower, int minUpper, int minNumeric, int minSpecial)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minLength);
        ArgumentOutOfRangeException.ThrowIfNegative(minLower);
        ArgumentOutOfRangeException.ThrowIfNegative(minUpper);
        ArgumentOutOfRangeException.ThrowIfNegative(minNumeric);
        ArgumentOutOfRangeException.ThrowIfNegative(minSpecial);
        CheckMinZeroWhenDisabled(lower, minLower);
        CheckMinZeroWhenDisabled(upper, minUpper);
        CheckMinZeroWhenDisabled(numeric, minNumeric);
        CheckMinZeroWhenDisabled(special, minSpecial);

        var requiredMinLength = checked(minLower + minUpper + minNumeric + minSpecial);
        var length = Math.Max(minLength, requiredMinLength);

        Span<char> chars = length <= 128 ? stackalloc char[length] : new char[length];

        // fill the required characters first
        var currentChars = chars;
        GenerateRequiredValues(ref currentChars, minLower, LowerCaseChars);
        GenerateRequiredValues(ref currentChars, minUpper, UpperCaseChars);
        GenerateRequiredValues(ref currentChars, minNumeric, NumericChars);
        GenerateRequiredValues(ref currentChars, minSpecial, SpecialChars);

        // fill the rest of the password with random characters from all the available choices
        var choices = GetChoices(lower, upper, numeric, special);
        RandomNumberGenerator.GetItems(choices, currentChars);

        RandomNumberGenerator.Shuffle(chars);

        var result = new string(chars);

        // clear the buffer so the password isn't in memory in multiple places
        chars.Clear();

        return result;
    }

    private static void CheckMinZeroWhenDisabled(
        bool enabled,
        int minValue,
        [CallerArgumentExpression(nameof(enabled))] string? enabledParamName = null,
        [CallerArgumentExpression(nameof(minValue))] string? minValueParamName = null)
    {
        if (!enabled && minValue > 0)
        {
            ThrowArgumentException();
        }

        void ThrowArgumentException() => throw new ArgumentException($"'{minValueParamName}' must be 0 if '{enabledParamName}' is disabled.");
    }

    private static void GenerateRequiredValues(ref Span<char> destination, int minValues, string choices)
    {
        Debug.Assert(destination.Length >= minValues);

        if (minValues > 0)
        {
            RandomNumberGenerator.GetItems(choices, destination.Slice(0, minValues));
            destination = destination.Slice(minValues);
        }
    }

    private static string GetChoices(bool lower, bool upper, bool numeric, bool special) =>
        (lower, upper, numeric, special) switch
        {
            (true, true, true, true) => LowerCaseChars + UpperCaseChars + NumericChars + SpecialChars,
            (true, true, true, false) => LowerCaseChars + UpperCaseChars + NumericChars,
            (true, true, false, true) => LowerCaseChars + UpperCaseChars + SpecialChars,
            (true, true, false, false) => LowerCaseChars + UpperCaseChars,

            (true, false, true, true) => LowerCaseChars + NumericChars + SpecialChars,
            (true, false, true, false) => LowerCaseChars + NumericChars,
            (true, false, false, true) => LowerCaseChars + SpecialChars,
            (true, false, false, false) => LowerCaseChars,

            (false, true, true, true) => UpperCaseChars + NumericChars + SpecialChars,
            (false, true, true, false) => UpperCaseChars + NumericChars,
            (false, true, false, true) => UpperCaseChars + SpecialChars,
            (false, true, false, false) => UpperCaseChars,

            (false, false, true, true) => NumericChars + SpecialChars,
            (false, false, true, false) => NumericChars,
            (false, false, false, true) => SpecialChars,
            (false, false, false, false) => throw new ArgumentException("At least one character type must be enabled.")
        };
}
