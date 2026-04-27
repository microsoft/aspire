// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Note this file is included in the Aspire.Hosting.Analyzers project which targets netstandard2.0
/// </summary>
internal static class ModelName
{
    public const int DefaultMaxLength = 64;
    public const bool DefaultValidateStartsWithLetter = true;
    public const bool DefaultValidateAllowedCharacters = true;
    public const bool DefaultValidateNoConsecutiveHyphens = true;
    public const bool DefaultValidateNoTrailingHyphen = true;

    internal static bool IsValidName(string target, string name) => TryValidateName(target, name, DefaultMaxLength, DefaultValidateStartsWithLetter, DefaultValidateAllowedCharacters, DefaultValidateNoConsecutiveHyphens, DefaultValidateNoTrailingHyphen, out _);

    internal static void ValidateName(string target, string name)
    {
#pragma warning disable CA1510 // Use ArgumentNullException throw helper
        // This file is included in projects targeting netstandard2.0
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }
#pragma warning restore CA1510

        if (!TryValidateName(target, name, DefaultMaxLength, DefaultValidateStartsWithLetter, DefaultValidateAllowedCharacters, DefaultValidateNoConsecutiveHyphens, DefaultValidateNoTrailingHyphen, out var validationMessage))
        {
            throw new ArgumentException(validationMessage, nameof(name));
        }
    }

    internal static void ValidateName(string target, string name, int? maxLength, bool validateStartsWithLetter, bool validateAllowedCharacters, bool validateNoConsecutiveHyphens, bool validateNoTrailingHyphen)
    {
#pragma warning disable CA1510 // Use ArgumentNullException throw helper
        // This file is included in projects targeting netstandard2.0
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }
#pragma warning restore CA1510

        if (!TryValidateName(target, name, maxLength, validateStartsWithLetter, validateAllowedCharacters, validateNoConsecutiveHyphens, validateNoTrailingHyphen, out var validationMessage))
        {
            throw new ArgumentException(validationMessage, nameof(name));
        }
    }

    /// <summary>
    /// Validate that a model name is valid using the default validation rules.
    /// </summary>
    internal static bool TryValidateName(string target, string name, out string? validationMessage)
    {
        return TryValidateName(target, name, DefaultMaxLength, DefaultValidateStartsWithLetter, DefaultValidateAllowedCharacters, DefaultValidateNoConsecutiveHyphens, DefaultValidateNoTrailingHyphen, out validationMessage);
    }

    /// <summary>
    /// Validate that a model name is valid using the specified validation rules.
    /// </summary>
    internal static bool TryValidateName(string target, string name, int? maxLength, bool validateStartsWithLetter, bool validateAllowedCharacters, bool validateNoConsecutiveHyphens, bool validateNoTrailingHyphen, out string? validationMessage)
    {
        validationMessage = null;

        if (maxLength is not null && (name.Length < 1 || name.Length > maxLength.Value))
        {
            validationMessage = $"{target} name '{name}' is invalid. Name must be between 1 and {maxLength.Value} characters long.";
            return false;
        }

        if (validateAllowedCharacters || validateNoConsecutiveHyphens)
        {
            var lastCharacterHyphen = false;
            for (var i = 0; i < name.Length; i++)
            {
                if (name[i] == '-')
                {
                    if (validateNoConsecutiveHyphens && lastCharacterHyphen)
                    {
                        validationMessage = $"{target} name '{name}' is invalid. Name cannot contain consecutive hyphens.";
                        return false;
                    }
                    lastCharacterHyphen = true;
                }
                else if (validateAllowedCharacters && !IsAsciiLetterOrDigit(name[i]))
                {
                    validationMessage = $"{target} name '{name}' is invalid. Name must contain only ASCII letters, digits, and hyphens.";
                    return false;
                }
                else
                {
                    lastCharacterHyphen = false;
                }
            }
        }

        if (validateStartsWithLetter && name.Length > 0 && !IsAsciiLetter(name[0]))
        {
            validationMessage = $"{target} name '{name}' is invalid. Name must start with an ASCII letter.";
            return false;
        }

        if (validateNoTrailingHyphen && name.Length > 0 && name[name.Length - 1] == '-')
        {
            validationMessage = $"{target} name '{name}' is invalid. Name cannot end with a hyphen.";
            return false;
        }

        return true;
    }

    private static bool IsAsciiLetter(char c)
    {
#if NET8_0_OR_GREATER
        return char.IsAsciiLetter(c);
#else
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
#endif
    }

    private static bool IsAsciiLetterOrDigit(char c)
    {
#if NET8_0_OR_GREATER
        return char.IsAsciiLetterOrDigit(c);
#else
        return IsAsciiLetter(c) || char.IsDigit(c);
#endif
    }
}
