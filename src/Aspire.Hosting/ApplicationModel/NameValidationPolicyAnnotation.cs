// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an annotation that customizes the name validation rules applied to a resource when
/// it is added to the application model.
/// </summary>
/// <remarks>
/// By default, resource names must be 1–64 ASCII characters long, start with a letter, contain only letters, digits,
/// and hyphens, and not contain consecutive or trailing hyphens. Use this annotation to relax individual rules.
/// The <see cref="None"/> policy disables every rule, which is useful for internal resources (such as installers
/// or rebuilders) that append suffixes to user-provided resource names and are never deployed.
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}")]
public sealed class NameValidationPolicyAnnotation : IResourceAnnotation
{
    /// <summary>
    /// The default policy that enforces all standard name validation rules.
    /// </summary>
    public static readonly NameValidationPolicyAnnotation Default = new();

    /// <summary>
    /// A policy that disables all name validation rules.
    /// </summary>
    public static readonly NameValidationPolicyAnnotation None = new()
    {
        MaxLength = null,
        ValidateStartsWithLetter = false,
        ValidateAllowedCharacters = false,
        ValidateNoConsecutiveHyphens = false,
        ValidateNoTrailingHyphen = false
    };

    /// <summary>
    /// Gets the maximum allowed length for the resource name, or <see langword="null"/> to disable length validation.
    /// Defaults to <see cref="ModelName.DefaultMaxLength"/>.
    /// </summary>
    public int? MaxLength { get; init; } = ModelName.DefaultMaxLength;

    /// <summary>
    /// Gets a value indicating whether to validate that the name starts with an ASCII letter.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool ValidateStartsWithLetter { get; init; } = ModelName.DefaultValidateStartsWithLetter;

    /// <summary>
    /// Gets a value indicating whether to validate that the name contains only ASCII letters, digits, and hyphens.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool ValidateAllowedCharacters { get; init; } = ModelName.DefaultValidateAllowedCharacters;

    /// <summary>
    /// Gets a value indicating whether to validate that the name does not contain consecutive hyphens.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool ValidateNoConsecutiveHyphens { get; init; } = ModelName.DefaultValidateNoConsecutiveHyphens;

    /// <summary>
    /// Gets a value indicating whether to validate that the name does not end with a hyphen.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool ValidateNoTrailingHyphen { get; init; } = ModelName.DefaultValidateNoTrailingHyphen;
}
