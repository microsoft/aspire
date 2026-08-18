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
    /// Gets the environment variable that accepts an additional PEM certificate bundle when running an AppHost for this language.
    /// </summary>
    /// <remarks>
    /// When set, the CLI assigns this environment variable a certificate bundle containing the
    /// ASP.NET Core development certificate before launching the AppHost in run mode. The variable
    /// is not set when publishing the AppHost. The runtime uses the bundle as additional trusted roots
    /// for the entire AppHost process, affecting all outbound TLS connections, including connections
    /// unrelated to Aspire-managed resources. For example:
    /// <code>
    /// CertificateBundleEnvironmentVariable = "NODE_EXTRA_CA_CERTS";
    /// </code>
    /// </remarks>
    public string? CertificateBundleEnvironmentVariable { get; init; }

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

    /// <summary>
    /// Gets an optional incremental-build check. When set, the command is skipped if its stamp file
    /// is newer than every declared input. Null means the command always runs.
    /// </summary>
    public CommandUpToDateCheck? UpToDateCheck { get; init; }
}

/// <summary>
/// Declares the inputs and stamp file that let the CLI skip a command whose work is already done.
/// </summary>
/// <remarks>
/// This exists for compilers that have no incremental mode of their own. <c>javac</c> given an
/// explicit list of source files recompiles all of them every time, so an AppHost that has not
/// changed still pays a full compile of the generated SDK on every launch. Toolchains that are
/// already incremental (<c>cargo</c>, for instance) do not need this.
/// </remarks>
public sealed class CommandUpToDateCheck
{
    /// <summary>
    /// Gets the inputs to compare against the stamp file, relative to the working directory unless
    /// absolute. Supports the same placeholders as <see cref="CommandSpec.Args" />.
    /// </summary>
    /// <remarks>
    /// An entry is either a file, a directory, or a directory suffixed with <c>/**</c>. A plain
    /// directory is scanned one level deep; only the <c>/**</c> form recurses. That distinction is
    /// what keeps the check cheap: the AppHost directory can be declared as an input for the sources
    /// that sit beside the AppHost without walking sibling trees such as <c>node_modules</c>.
    /// Entries that do not exist are ignored, so a spec can name a path that only some layouts have.
    /// </remarks>
    public required string[] Inputs { get; init; }

    /// <summary>
    /// Gets the file extensions, including the leading dot, that identify input files.
    /// Null or empty means every file found under <see cref="Inputs" /> counts.
    /// </summary>
    /// <remarks>
    /// Restricting by extension is what keeps a command's own outputs from invalidating it when they
    /// land beside its inputs — <c>.class</c> files written next to the <c>.java</c> files they were
    /// compiled from, for example.
    /// </remarks>
    public string[]? FileExtensions { get; init; }

    /// <summary>
    /// Gets the file, relative to the working directory unless absolute, written after the command
    /// succeeds and compared against the inputs on the next launch.
    /// </summary>
    /// <remarks>
    /// Place this with the command's outputs so that deleting them — <c>mvn clean</c>, or removing the
    /// class output directory — also invalidates the check.
    /// </remarks>
    public required string StampFile { get; init; }
}
