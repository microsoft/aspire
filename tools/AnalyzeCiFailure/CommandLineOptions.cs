// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;

namespace AnalyzeCiFailure;

/// <summary>Creates analyzer options with consistent single-value and non-repetition validation.</summary>
internal static class CommandLineOptions
{
    internal static Option<string?> CreateSingleUseStringOption(string name, string description)
    {
        var option = new Option<string?>(name)
        {
            Description = description,
            Arity = ArgumentArity.ExactlyOne,
        };
        // Arity alone does not reject a valueless repeat, such as "--repo one --repo".
        option.Validators.Add(result =>
        {
            if (result.IdentifierTokenCount > 1)
            {
                result.AddError($"Option '{name}' may only be specified once.");
            }
        });

        return option;
    }
}
