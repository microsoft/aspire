// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Declares a named output produced by a pipeline step.
/// </summary>
/// <remarks>
/// Relative paths are resolved from the AppHost project directory. Pipeline steps must write
/// artifacts to the path returned by <see cref="IPipelineOutputResolver.Resolve"/>.
/// </remarks>
/// <example>
/// This example declares and resolves an inventory directory:
/// <code>
/// var inventoryDefinition = new PipelineOutputDefinition(
///     "inventory",
///     ".configgen",
///     PipelineOutputKind.Directory);
///
/// var step = new PipelineStep
/// {
///     Name = "generate-inventory",
///     Outputs = [inventoryDefinition],
///     SupportsOutputPathRelocation = true,
///     Action = context =>
///     {
///         var inventory = context.Outputs.Resolve(inventoryDefinition);
///         Directory.CreateDirectory(inventory.OutputPath);
///         File.WriteAllText(Path.Combine(inventory.OutputPath, "inventory.txt"), "generated");
///         return Task.CompletedTask;
///     }
/// };
/// </code>
/// </example>
[Experimental("ASPIREPIPELINES004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class PipelineOutputDefinition
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineOutputDefinition"/> class.
    /// </summary>
    /// <param name="name">The name that uniquely identifies this output within its pipeline step.</param>
    /// <param name="defaultPath">The default logical target path.</param>
    /// <param name="kind">The kind of output.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="name"/> or <paramref name="defaultPath"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="name"/> is empty, contains a colon, or when
    /// <paramref name="defaultPath"/> is empty or rooted.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="kind"/> is not a defined <see cref="PipelineOutputKind"/> value.
    /// </exception>
    public PipelineOutputDefinition(string name, string defaultPath, PipelineOutputKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultPath);

        if (name.Contains(':'))
        {
            throw new ArgumentException("Pipeline output names cannot contain ':'.", nameof(name));
        }

        if (Path.IsPathRooted(defaultPath))
        {
            throw new ArgumentException(
                "The default pipeline output path must be relative to the AppHost directory.",
                nameof(defaultPath));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Name = name;
        DefaultPath = defaultPath;
        Kind = kind;
    }

    /// <summary>
    /// Gets the name that uniquely identifies this output within its pipeline step.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the default logical target path.
    /// </summary>
    /// <remarks>
    /// Relative paths are resolved from <see cref="IPipelineOutputResolver.AppHostDirectory"/>.
    /// The configured path can be overridden through the
    /// <c>Pipeline:Outputs:&lt;step-name&gt;:&lt;output-name&gt;:Path</c> configuration key.
    /// </remarks>
    public string DefaultPath { get; }

    /// <summary>
    /// Gets the kind of output.
    /// </summary>
    public PipelineOutputKind Kind { get; }
}
