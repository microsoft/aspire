// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Interaction;

/// <summary>
/// Thrown when a required value cannot be resolved in non-interactive mode.
/// Caught centrally by BaseCommand to return a standard exit code.
/// </summary>
internal sealed class NonInteractiveException : Exception
{
    public NonInteractiveException(string symbolDisplayName)
        : base(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NonInteractiveRequiredInputMissing, symbolDisplayName))
    {
        SymbolDisplayName = symbolDisplayName;
    }

    public string SymbolDisplayName { get; }
}
