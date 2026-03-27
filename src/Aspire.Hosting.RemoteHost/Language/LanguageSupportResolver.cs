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
    {
        _logger = logger;
        _languages = new Lazy<Dictionary<string, ILanguageSupport>>(
            () => DiscoverLanguages(serviceProvider, assemblyLoader.GetAssemblies()));
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
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                _logger.LogDebug(ex, "Some types in assembly '{AssemblyName}' could not be loaded", assembly.GetName().Name);
                // Use the types that were successfully loaded
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }

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
                        _logger.LogDebug("Discovered language support: {TypeName} for language '{Language}'", type.Name, language.Language);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to instantiate language support '{TypeName}'", type.Name);
                    }
                }
            }
        }

        return languages;
    }
}
