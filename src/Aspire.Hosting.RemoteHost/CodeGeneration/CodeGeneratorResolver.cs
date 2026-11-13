// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteHost.CodeGeneration;

/// <summary>
/// Resolves code generators by language, discovering them from loaded assemblies.
/// </summary>
internal sealed class CodeGeneratorResolver
{
    private readonly Lazy<Dictionary<string, ICodeGenerator>> _generators;
    private readonly Lazy<Dictionary<string, IApiReferenceExporter>> _exporters;
    private readonly ILogger<CodeGeneratorResolver> _logger;

    public CodeGeneratorResolver(
        IServiceProvider serviceProvider,
        AssemblyLoader assemblyLoader,
        ILogger<CodeGeneratorResolver> logger)
        : this(serviceProvider, assemblyLoader.GetAssemblies, logger)
    {
    }

    // Test-only seam: lets unit tests inject a synthetic assembly set without going
    // through the AssemblyLoader (which is sealed and probes the file system).
    internal CodeGeneratorResolver(
        IServiceProvider serviceProvider,
        Func<IReadOnlyList<Assembly>> assembliesProvider,
        ILogger<CodeGeneratorResolver> logger)
    {
        _logger = logger;
        _generators = new Lazy<Dictionary<string, ICodeGenerator>>(
            () => DiscoverGenerators(serviceProvider, assembliesProvider()));
        _exporters = new Lazy<Dictionary<string, IApiReferenceExporter>>(
            () => DiscoverExporters(serviceProvider, assembliesProvider()));
    }

    /// <summary>
    /// Gets a code generator for the specified language.
    /// </summary>
    /// <param name="language">The target language (e.g., "TypeScript", "Python").</param>
    /// <returns>The code generator, or null if not found.</returns>
    public ICodeGenerator? GetCodeGenerator(string language)
    {
        _generators.Value.TryGetValue(language, out var generator);
        return generator;
    }

    /// <summary>
    /// Gets the API reference exporter for the specified language, if the language supports API export.
    /// </summary>
    /// <param name="language">The target language (e.g., "TypeScript", "Python").</param>
    /// <returns>
    /// The exporter, or <see langword="null"/> when no generator is registered for the language or
    /// the language provides no <see cref="IApiReferenceExporter"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// An exporter is never reachable for a language whose code generator is not: a documented API
    /// that no generator produces would be worse than no documentation at all. That is why the
    /// generator lookup gates the result even though exporters are discovered independently.
    /// </para>
    /// <para>
    /// Exporters are discovered as their own types rather than read off the generator so that a
    /// language provider can add export support without changing the generator type's interface
    /// list. <c>Aspire.TypeSystem</c> is force-shared from the default load context, so a generator
    /// implementing a newly added shared interface fails to load entirely under a CLI that predates
    /// it (see <c>AtsTypeScriptApiReferenceExporter</c>). A generator that implements the interface
    /// itself is still honored, so a provider that keeps both roles on one type keeps working.
    /// </para>
    /// </remarks>
    public IApiReferenceExporter? GetApiReferenceExporter(string language)
    {
        if (GetCodeGenerator(language) is not { } generator)
        {
            return null;
        }

        if (generator is IApiReferenceExporter selfExporter)
        {
            return selfExporter;
        }

        _exporters.Value.TryGetValue(language, out var exporter);
        return exporter;
    }

    /// <summary>
    /// Gets the languages of all discovered code generators.
    /// </summary>
    /// <returns>The set of supported language identifiers.</returns>
    public IReadOnlyCollection<string> GetSupportedLanguages()
    {
        return _generators.Value.Keys.ToArray();
    }

    private Dictionary<string, ICodeGenerator> DiscoverGenerators(
        IServiceProvider serviceProvider,
        IReadOnlyList<Assembly> assemblies)
    {
        var generators = new Dictionary<string, ICodeGenerator>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in assemblies)
        {
            var assemblyName = assembly.GetName().Name;
            var types = GetLoadableTypes(assembly, assemblyName, out var hadTypeLoadFailure);

            var discoveredInAssembly = 0;
            foreach (var type in types)
            {
                if (!type.IsAbstract &&
                    !type.IsInterface &&
                    typeof(ICodeGenerator).IsAssignableFrom(type))
                {
                    try
                    {
                        var generator = (ICodeGenerator)ActivatorUtilities.CreateInstance(serviceProvider, type);
                        generators[generator.Language] = generator;
                        discoveredInAssembly++;
                        _logger.LogDebug("Discovered code generator: {TypeName} for language '{Language}'", type.Name, generator.Language);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to instantiate code generator '{TypeName}'", type.Name);
                    }
                }
            }

            // If an assembly named like a code-generation contributor produced zero generators,
            // that is almost certainly a silent type-load failure rather than an intentional
            // design. Log a Warning so the user can see it.
            if (discoveredInAssembly == 0 && LooksLikeCodeGeneratorAssembly(assemblyName))
            {
                _logger.LogWarning(
                    "Assembly '{AssemblyName}' was loaded but did not contribute any {Interface} implementations. {Hint}",
                    assemblyName,
                    nameof(ICodeGenerator),
                    hadTypeLoadFailure
                        ? "This is likely caused by a binary mismatch between the bundled and probed assemblies (see preceding LoaderExceptions)."
                        : "Verify the assembly contains a non-abstract type that implements " + typeof(ICodeGenerator).FullName + ".");
            }
        }

        return generators;
    }

    private Dictionary<string, IApiReferenceExporter> DiscoverExporters(
        IServiceProvider serviceProvider,
        IReadOnlyList<Assembly> assemblies)
    {
        var exporters = new Dictionary<string, IApiReferenceExporter>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in assemblies)
        {
            var assemblyName = assembly.GetName().Name;

            // An assembly with no exporter is the normal case (most languages generate code they
            // cannot yet describe), so unlike generator discovery this pass never warns about
            // finding nothing. A type-load failure was already reported by DiscoverGenerators.
            foreach (var type in GetLoadableTypes(assembly, assemblyName, out _))
            {
                if (type.IsAbstract || type.IsInterface || !typeof(IApiReferenceExporter).IsAssignableFrom(type))
                {
                    continue;
                }

                try
                {
                    var exporter = (IApiReferenceExporter)ActivatorUtilities.CreateInstance(serviceProvider, type);
                    exporters[exporter.Language] = exporter;
                    _logger.LogDebug("Discovered API reference exporter: {TypeName} for language '{Language}'", type.Name, exporter.Language);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to instantiate API reference exporter '{TypeName}'", type.Name);
                }
            }
        }

        return exporters;
    }

    /// <summary>
    /// Returns the types an assembly can actually load, keeping the ones that bound when others did
    /// not. Dropping the whole assembly on a single unloadable type would take every generator in it
    /// down with that type.
    /// </summary>
    private Type[] GetLoadableTypes(Assembly assembly, string? assemblyName, out bool hadTypeLoadFailure)
    {
        hadTypeLoadFailure = false;

        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            hadTypeLoadFailure = true;
            // Surface loader binding failures at Warning level. These typically indicate
            // a binary mismatch between the bundled runtime assemblies and the integration
            // assemblies loaded from disk (for example, when Aspire.TypeSystem versions
            // diverge). Including the LoaderExceptions in the log is essential for
            // diagnosing these failures, which previously disappeared into Debug-level
            // output that the apphost server never wrote to disk.
            var loaderMessages = ex.LoaderExceptions is { Length: > 0 } loaders
                ? string.Join("; ", loaders.Where(e => e is not null).Select(e => e!.Message).Distinct())
                : "(no LoaderExceptions captured)";
            _logger.LogWarning(
                ex,
                "Some types in assembly '{AssemblyName}' could not be loaded; {LoadedCount} of {TotalCount} types are available. LoaderExceptions: {LoaderExceptions}",
                assemblyName,
                ex.Types.Count(t => t is not null),
                ex.Types.Length,
                loaderMessages);
            return ex.Types.Where(t => t is not null).ToArray()!;
        }
    }

    private static bool LooksLikeCodeGeneratorAssembly(string? assemblyName)
        => assemblyName is not null
           && assemblyName.StartsWith("Aspire.Hosting.CodeGeneration.", StringComparison.OrdinalIgnoreCase);
}
