// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Templating;

/// <summary>
/// Known template identifiers.
/// </summary>
internal static class KnownTemplateId
{
    /// <summary>
    /// The template ID for the CLI C# empty AppHost template.
    /// </summary>
    public const string CSharpEmptyAppHost = "aspire-empty";

    /// <summary>
    /// The template ID for the CLI TypeScript empty AppHost template.
    /// </summary>
    public const string TypeScriptEmptyAppHost = "aspire-ts-empty";

    /// <summary>
    /// The template ID for the dotnet empty AppHost template.
    /// </summary>
    public const string DotNetEmptyAppHost = "aspire";

    /// <summary>
    /// The template ID for the TypeScript starter template.
    /// </summary>
    public const string TypeScriptStarter = "aspire-ts-starter";
}
