// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.TypeSystem;

/// <summary>
/// Specifies the runtime execution configuration for a language.
/// </summary>
public sealed class RuntimeSpec
{
    /// <summary>
    /// Gets the language identifier (e.g., "TypeScript", "Python").
    /// </summary>
    public required string Language { get; init; }

    /// <summary>
    /// Gets the display name for the language (e.g., "TypeScript (Node.js)").
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the code generation language identifier for the generateCode RPC.
    /// </summary>
    public required string CodeGenLanguage { get; init; }

    /// <summary>
    /// Gets the file patterns used to detect this language (e.g., ["apphost.ts"]).
    /// </summary>
    public required string[] DetectionPatterns { get; init; }

    /// <summary>
    /// Gets the commands to initialize the project environment (e.g., create a virtual environment
    /// and install dependencies). Runs once during scaffolding. Null if no initialization is needed.
    /// </summary>
    public CommandSpec[]? Initialize { get; init; }

    /// <summary>
    /// Gets the command to install dependencies. Null if no dependencies to install.
    /// </summary>
    public CommandSpec? InstallDependencies { get; init; }

    /// <summary>
    /// Gets the commands to run before executing or publishing the AppHost. Null if no pre-execution validation is needed.
    /// Watch-mode validation should be part of <see cref="WatchExecute" /> when needed.
    /// </summary>
    public CommandSpec[]? PreExecute { get; init; }

    /// <summary>
    /// Gets the command to execute the AppHost for run.
    /// </summary>
    public required CommandSpec Execute { get; init; }

    /// <summary>
    /// Gets the command to execute the AppHost in watch mode. Null if watch mode not supported.
    /// </summary>
    public CommandSpec? WatchExecute { get; init; }

    /// <summary>
    /// Gets the command to execute the AppHost for publish. Null to use Execute with args appended.
    /// </summary>
    public CommandSpec? PublishExecute { get; init; }

    /// <summary>
    /// Gets the extension capability required to launch this language via the VS Code extension.
    /// When set (e.g., "node"), the CLI will use the extension launcher if the extension reports
    /// this capability. When null, the CLI always uses the default process-based launcher.
    /// </summary>
    public string? ExtensionLaunchCapability { get; init; }

    /// <summary>
    /// Gets files that must exist in the project directory before execution.
    /// If a file in this dictionary is missing, the CLI will create it with the provided content.
    /// This supports upgrade scenarios where new runtime requirements are introduced.
    /// </summary>
    public Dictionary<string, string>? MigrationFiles { get; init; }
}

/// <summary>
/// Specifies a command to execute.
/// </summary>
public sealed class CommandSpec
{
    /// <summary>
    /// Gets the command to execute (e.g., "npm", "npx", "python").
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// Gets the arguments for the command.
    /// Supports placeholders: {appHostFile}, {appHostDir}, {args}
    /// </summary>
    public required string[] Args { get; init; }

    /// <summary>
    /// Gets the environment variables to set when executing the command.
    /// These are merged with any environment variables provided by the caller.
    /// </summary>
    public Dictionary<string, string>? EnvironmentVariables { get; init; }
}
