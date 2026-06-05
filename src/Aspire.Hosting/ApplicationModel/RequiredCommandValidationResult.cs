// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents the result of validating a required command.
/// </summary>
[Experimental("ASPIRECOMMAND001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class RequiredCommandValidationResult
{
    private RequiredCommandValidationResult(bool isValid, string? validationMessage)
    {
        if (!isValid && validationMessage is null)
        {
            throw new ArgumentException("A validation message must be provided for a failed validation.", nameof(validationMessage));
        }

        IsValid = isValid;
        ValidationMessage = validationMessage;
    }

    /// <summary>
    /// Gets a value indicating whether the command validation succeeded.
    /// </summary>
    [MemberNotNullWhen(false, nameof(ValidationMessage))]
    public bool IsValid { get; }

    /// <summary>
    /// Gets an optional validation message describing why validation failed.
    /// </summary>
    public string? ValidationMessage { get; }

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    /// <returns>A successful validation result.</returns>
    public static RequiredCommandValidationResult Success() => new(true, null);

    /// <summary>
    /// Creates a failed validation result with the specified message.
    /// </summary>
    /// <param name="message">A message describing why validation failed.</param>
    /// <returns>A failed validation result.</returns>
    public static RequiredCommandValidationResult Failure(string message) => new(false, message);
}
