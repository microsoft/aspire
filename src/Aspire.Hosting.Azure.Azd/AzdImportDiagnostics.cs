// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Azd;

/// <summary>
/// Indicates the severity of an <see cref="AzdImportDiagnostic"/>.
/// </summary>
public enum AzdImportDiagnosticSeverity
{
    /// <summary>
    /// Informational message about an import decision (for example, that existing infrastructure was preserved).
    /// </summary>
    Information,

    /// <summary>
    /// A non-fatal issue: part of the azd project could not be fully represented and may need manual attention.
    /// </summary>
    Warning,

    /// <summary>
    /// An unsafe-to-deploy condition: the import produced a model that will likely fail or behave
    /// incorrectly at publish/deploy time (for example a service depends on a resource that could not
    /// be referenced) and must be resolved before deploying.
    /// </summary>
    Error,
}

/// <summary>
/// Represents a single observation produced while importing an azd project.
/// </summary>
/// <remarks>
/// Diagnostics make the import non-destructive and transparent: anything the importer cannot map (an
/// unsupported host kind, an unknown resource type, a missing source path) is reported here rather
/// than being silently dropped, so a user migrating from azd can see exactly what needs follow-up.
/// </remarks>
/// <param name="Severity">The severity of the diagnostic.</param>
/// <param name="Message">A human-readable description of the observation.</param>
/// <param name="Target">The name of the azd service or resource the diagnostic relates to, if any.</param>
public sealed record AzdImportDiagnostic(AzdImportDiagnosticSeverity Severity, string Message, string? Target = null)
{
    /// <inheritdoc />
    public override string ToString()
        => Target is null ? $"[{Severity}] {Message}" : $"[{Severity}] ({Target}) {Message}";
}

/// <summary>
/// Collects the <see cref="AzdImportDiagnostic"/> instances produced during an import.
/// </summary>
public sealed class AzdImportDiagnostics
{
    private readonly List<AzdImportDiagnostic> _items = [];

    /// <summary>
    /// Gets the diagnostics produced during the import, in the order they were reported.
    /// </summary>
    public IReadOnlyList<AzdImportDiagnostic> Items => _items;

    /// <summary>
    /// Gets a value indicating whether any <see cref="AzdImportDiagnosticSeverity.Warning"/> diagnostics were produced.
    /// </summary>
    public bool HasWarnings => _items.Any(static d => d.Severity == AzdImportDiagnosticSeverity.Warning);

    /// <summary>
    /// Gets a value indicating whether any <see cref="AzdImportDiagnosticSeverity.Error"/> diagnostics were produced.
    /// An import that has errors produced a model that is unsafe to deploy without manual changes.
    /// </summary>
    public bool HasErrors => _items.Any(static d => d.Severity == AzdImportDiagnosticSeverity.Error);

    internal void Add(AzdImportDiagnostic diagnostic) => _items.Add(diagnostic);

    internal void Information(string message, string? target = null)
        => Add(new AzdImportDiagnostic(AzdImportDiagnosticSeverity.Information, message, target));

    internal void Warning(string message, string? target = null)
        => Add(new AzdImportDiagnostic(AzdImportDiagnosticSeverity.Warning, message, target));

    internal void Error(string message, string? target = null)
        => Add(new AzdImportDiagnostic(AzdImportDiagnosticSeverity.Error, message, target));
}
