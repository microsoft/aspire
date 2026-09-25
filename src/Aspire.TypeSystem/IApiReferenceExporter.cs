// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.TypeSystem;

/// <summary>
/// Optional companion to <see cref="ICodeGenerator"/> for languages that can describe their
/// generated surface as a machine-readable API reference.
/// </summary>
/// <remarks>
/// <para>
/// Code generation and API export answer different questions. <see cref="ICodeGenerator"/> produces
/// the source a user compiles against; this interface produces the documentation model that
/// describes that source. Keeping them separate means a language provider can ship runnable code
/// generation long before it can describe it, and documentation tooling can tell the difference
/// instead of publishing a silently empty reference.
/// </para>
/// <para>
/// The payload schema is owned by the language provider. Hosts must pass the returned document
/// through unmodified so language-specific details survive transport.
/// </para>
/// </remarks>
public interface IApiReferenceExporter
{
    /// <summary>
    /// Gets the target language name (for example, "TypeScript"). This must match the
    /// <see cref="ICodeGenerator.Language"/> value of the generator that produces the same surface,
    /// so a host can resolve one from the other.
    /// </summary>
    string Language { get; }

    /// <summary>
    /// Exports the API reference for the surface the generator would produce from the same context.
    /// </summary>
    /// <param name="context">The ATS context containing capabilities, types, and enums.</param>
    /// <param name="options">
    /// The package identity and ownership scope for the export. Assembly ownership matching follows
    /// the case-insensitive contract documented by
    /// <see cref="ApiReferenceExportOptions.ExportingAssemblyNames"/>.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the export between projected items.</param>
    /// <returns>
    /// A language-defined JSON document describing the generated API. The returned element must be
    /// detached from any owning <see cref="JsonDocument"/>, for example by calling
    /// <see cref="JsonElement.Clone"/>.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken"/> requests cancellation.
    /// </exception>
    /// <example>
    /// <code>
    /// using var document = JsonDocument.Parse(json);
    /// return document.RootElement.Clone();
    /// </code>
    /// </example>
    JsonElement ExportApi(
        AtsContext context,
        ApiReferenceExportOptions options,
        CancellationToken cancellationToken);
}
