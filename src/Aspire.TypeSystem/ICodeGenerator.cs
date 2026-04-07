// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.TypeSystem;

/// <summary>
/// Interface for generating language-specific code from ATS capabilities.
/// </summary>
public interface ICodeGenerator
{
    /// <summary>
    /// Gets the target language name (e.g., "TypeScript", "Python").
    /// </summary>
    string Language { get; }

    /// <summary>
    /// Generates the distributed application SDK code from the ATS context.
    /// </summary>
    /// <param name="context">The ATS context containing capabilities, types, and enums.</param>
    /// <returns>A dictionary of file paths to file contents.</returns>
    Dictionary<string, string> GenerateDistributedApplication(AtsContext context);
}
