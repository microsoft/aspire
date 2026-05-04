// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteHost.Language;

/// <summary>
/// Resolves language support implementations by language, discovering them from loaded assemblies.
/// </summary>
internal sealed class LanguageSupportResolver
{
    private readonly Lazy<Dictionary<string, ILanguageSupport>> _languages;
    private readonly ILogger<LanguageSupportResolver> _logger;

    public LanguageSupportResolver(
        IServiceProvider serviceProvider,
        AssemblyLoader assemblyLoader,
        ILogger<LanguageSupportResolver> logger)
        : this(serviceProvider, assemblyLoader.GetAssemblies, logger)
    {
    }

    // Test-only seam: lets unit tests inject a synthetic assembly set without going through
    // the AssemblyLoader (which is sealed and probes the file system).
    internal LanguageSupportResolver(
        IServiceProvider serviceProvider,
        Func<IReadOnlyList<Assembly>> assembliesProvider,
        ILogger<LanguageSupportResolver> logger)
    {
        _logger = logger;
        _languages = new Lazy<Dictionary<string, ILanguageSupport>>(
            () => DiscoverLanguages(serviceProvider, assembliesProvider()));
    }

    /// <summary>
    /// Gets language support for the specified language.
    /// </summary>
    /// <param name="language">The target language (e.g., "TypeScript", "Python").</param>
    /// <returns>The language support, or null if not found.</returns>
    public ILanguageSupport? GetLanguageSupport(string language)
    {
        _languages.Value.TryGetValue(language, out var support);
        return support;
    }

    /// <summary>
    /// Gets all available language support implementations.
    /// </summary>
    /// <returns>All discovered language support implementations.</returns>
    public IEnumerable<ILanguageSupport> GetAllLanguages()
    {
        return _languages.Value.Values;
    }

    private Dictionary<string, ILanguageSupport> DiscoverLanguages(
        IServiceProvider serviceProvider,
        IReadOnlyList<Assembly> assemblies)
    {
        var languages = new Dictionary<string, ILanguageSupport>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in assemblies)
        {
            Type[] types;
            var assemblyName = assembly.GetName().Name;
            var hadTypeLoadFailure = false;
            try
            {
                types = assembly.GetTypes();
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
                // Use the types that were successfully loaded
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }

            var discoveredInAssembly = 0;
            foreach (var type in types)
            {
                if (!type.IsAbstract &&
                    !type.IsInterface &&
                    typeof(ILanguageSupport).IsAssignableFrom(type))
                {
                    try
                    {
                        var language = (ILanguageSupport)ActivatorUtilities.CreateInstance(serviceProvider, type);
                        languages[language.Language] = language;
                        discoveredInAssembly++;
                        _logger.LogDebug("Discovered language support: {TypeName} for language '{Language}'", type.Name, language.Language);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to instantiate language support '{TypeName}'", type.Name);
                    }
                }
            }

            // If an assembly named like a code-generation/language-support contributor produced
            // zero implementations, that is almost certainly a silent type-load failure rather
            // than an intentional design. Log a Warning so the user can see it.
            if (discoveredInAssembly == 0 && LooksLikeLanguageSupportAssembly(assemblyName))
            {
                _logger.LogWarning(
                    "Assembly '{AssemblyName}' was loaded but did not contribute any {Interface} implementations. {Hint}",
                    assemblyName,
                    nameof(ILanguageSupport),
                    hadTypeLoadFailure
                        ? "This is likely caused by a binary mismatch between the bundled and probed assemblies (see preceding LoaderExceptions)."
                        : "Verify the assembly contains a non-abstract type that implements " + typeof(ILanguageSupport).FullName + ".");
            }
        }

        return languages;
    }

    private static bool LooksLikeLanguageSupportAssembly(string? assemblyName)
        => assemblyName is not null
           && assemblyName.StartsWith("Aspire.Hosting.CodeGeneration.", StringComparison.OrdinalIgnoreCase);
}
