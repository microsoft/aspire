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
    /// The template ID for the CLI Python empty AppHost template.
    /// </summary>
    public const string PythonEmptyAppHost = "aspire-py-empty";

    /// <summary>
    /// The template ID for the dotnet empty AppHost template.
    /// </summary>
    public const string DotNetEmptyAppHost = "aspire";

    /// <summary>
    /// The template ID for the TypeScript starter template.
    /// </summary>
    public const string TypeScriptStarter = "aspire-ts-starter";

    /// <summary>
    /// The template ID for the grouped integration test template.
    /// </summary>
    public const string IntegrationTest = "aspire-test";

    /// <summary>
    /// The template ID for the MSTest integration test template.
    /// </summary>
    public const string MSTest = "aspire-mstest";

    /// <summary>
    /// The template ID for the NUnit integration test template.
    /// </summary>
    public const string NUnit = "aspire-nunit";

    /// <summary>
    /// The template ID for the xUnit integration test template.
    /// </summary>
    public const string XUnit = "aspire-xunit";

    /// <summary>
    /// The template ID for the CLI Java empty AppHost template.
    /// </summary>
    public const string JavaEmptyAppHost = "aspire-java-empty";

    /// <summary>
    /// The template ID for the Python starter template.
    /// </summary>
    public const string PythonStarter = "aspire-py-starter";

    /// <summary>
    /// The template ID for the CLI Go empty AppHost template.
    /// </summary>
    public const string GoEmptyAppHost = "aspire-go-empty";

    /// <summary>
    /// The template ID for the CLI Rust empty AppHost template.
    /// </summary>
    public const string RustEmptyAppHost = "aspire-rust-empty";

    /// <summary>
    /// The template ID for the Go starter template (Redis + Go HTTP API).
    /// </summary>
    public const string GoStarter = "aspire-go-starter";

    /// <summary>
    /// The template ID for the Java starter template (Express API + React frontend, Java AppHost).
    /// </summary>
    public const string JavaStarter = "aspire-java-starter";
}
