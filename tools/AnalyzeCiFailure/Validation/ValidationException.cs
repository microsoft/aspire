// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AnalyzeCiFailure.Common;

namespace AnalyzeCiFailure.Validation;

/// <summary>
/// A validation failure that is reported as a single GitHub Actions <c>::error::</c> annotation.
/// Messages must embed untrusted values only through <see cref="WorkflowCommands.Display"/>.
/// </summary>
internal sealed class ValidationException(string message) : Exception(message)
{
    /// <summary>Fails validation with <paramref name="message"/> unless <paramref name="condition"/> holds.</summary>
    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ValidationException(message);
        }
    }
}
