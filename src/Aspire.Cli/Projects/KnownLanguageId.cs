// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

/// <summary>
/// Known language identifiers for AppHost projects.
/// </summary>
internal static class KnownLanguageId
{
    /// <summary>
    /// The language ID for C# (.NET) AppHost projects.
    /// </summary>
    public const string CSharp = "csharp";

    /// <summary>
    /// The display name for C# (.NET) AppHost projects.
    /// </summary>
    public const string CSharpDisplayName = "C# (.NET)";

    /// <summary>
    /// The language ID for TypeScript (Node.js) AppHost projects.
    /// Format: {language}/{runtime} to support multiple runtimes.
    /// </summary>
    public const string TypeScript = "typescript/nodejs";

    /// <summary>
    /// Short alias for TypeScript that can be used on the command line.
    /// </summary>
    public const string TypeScriptAlias = "typescript";

    /// <summary>
    /// The language ID for Python AppHost projects.
    /// </summary>
    public const string Python = "python";

    /// <summary>
    /// The display name for Python AppHost projects.
    /// </summary>
    public const string PythonDisplayName = "Python";

    /// <summary>
    /// The language ID for Go AppHost projects.
    /// </summary>
    public const string Go = "go";

    /// <summary>
    /// The display name for Go AppHost projects.
    /// </summary>
    public const string GoDisplayName = "Go";

    /// <summary>
    /// The language ID for Java AppHost projects.
    /// </summary>
    public const string Java = "java";

    /// <summary>
    /// The display name for Java AppHost projects.
    /// </summary>
    public const string JavaDisplayName = "Java";

    /// <summary>
    /// The language ID for Rust AppHost projects.
    /// </summary>
    public const string Rust = "rust";

    /// <summary>
    /// The display name for Rust AppHost projects.
    /// </summary>
    public const string RustDisplayName = "Rust";
}
