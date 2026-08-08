// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.TypeSystem;

/// <summary>
/// Describes the package identity and ownership scope of an <see cref="IApiReferenceExporter"/> export.
/// </summary>
/// <remarks>
/// The ATS context handed to an exporter is already filtered to the exporting assemblies, their
/// reference closure, and the reduced member shapes needed to resolve wrappers for referenced handle
/// types. That closure is exactly why <see cref="ExportingAssemblyNames"/> exists: it lets the exporter
/// tell apart symbols the package owns and should document from symbols it merely needs to emit so the
/// output is self-contained. Without it, every package would republish its dependencies' API reference.
/// </remarks>
public sealed class ApiReferenceExportOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ApiReferenceExportOptions"/> class.
    /// </summary>
    /// <param name="packageName">The name of the package being exported.</param>
    /// <param name="packageVersion">The version label to record for the package being exported.</param>
    /// <param name="exportingAssemblyNames">
    /// The assemblies whose symbols this package owns and documents. Symbols outside this set are
    /// present only to complete the reference closure.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="packageName"/>, <paramref name="packageVersion"/>, or
    /// <paramref name="exportingAssemblyNames"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="packageName"/> or <paramref name="packageVersion"/> is empty or
    /// consists only of white-space characters.
    /// </exception>
    public ApiReferenceExportOptions(
        string packageName,
        string packageVersion,
        IReadOnlyCollection<string> exportingAssemblyNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageVersion);
        ArgumentNullException.ThrowIfNull(exportingAssemblyNames);

        PackageName = packageName;
        PackageVersion = packageVersion;
        ExportingAssemblyNames = exportingAssemblyNames;
    }

    /// <summary>
    /// Gets the name of the package being exported.
    /// </summary>
    public string PackageName { get; }

    /// <summary>
    /// Gets the version label recorded for this export, as supplied by the caller.
    /// </summary>
    /// <remarks>
    /// Consumers key published documentation on this value, so callers are expected to pass the
    /// exact version that was restored. Nothing on this type can confirm that: an exporter sees
    /// loaded assemblies, not the package resolution that produced them, so any value — including a
    /// floating or range expression — would be recorded verbatim. Exactness therefore belongs where
    /// the restore is decided. <c>aspire sdk export</c> rejects a floating or range version before
    /// the scanner is built, pins the requested version so an unavailable one fails the restore
    /// instead of resolving upward, and refuses a package a repository checkout would build in place
    /// of the requested one.
    /// </remarks>
    public string PackageVersion { get; }

    /// <summary>
    /// Gets the assemblies whose symbols this package owns and documents.
    /// </summary>
    public IReadOnlyCollection<string> ExportingAssemblyNames { get; }
}
