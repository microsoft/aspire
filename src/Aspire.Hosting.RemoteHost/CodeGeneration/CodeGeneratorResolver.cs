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
    private readonly ILogger<CodeGeneratorResolver> _logger;

    public CodeGeneratorResolver(
        IServiceProvider serviceProvider,
        AssemblyLoader assemblyLoader,
        ILogger<CodeGeneratorResolver> logger)
    {
        _logger = logger;
        _generators = new Lazy<Dictionary<string, ICodeGenerator>>(
            () => DiscoverGenerators(serviceProvider, assemblyLoader.GetAssemblies()));
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

    private Dictionary<string, ICodeGenerator> DiscoverGenerators(
        IServiceProvider serviceProvider,
        IReadOnlyList<Assembly> assemblies)
    {
        var generators = new Dictionary<string, ICodeGenerator>(StringComparer.OrdinalIgnoreCase);

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
                    typeof(ICodeGenerator).IsAssignableFrom(type))
                {
                    try
                    {
                        var generator = (ICodeGenerator)ActivatorUtilities.CreateInstance(serviceProvider, type);
                        generators[generator.Language] = generator;
                        _logger.LogDebug("Discovered code generator: {TypeName} for language '{Language}'", type.Name, generator.Language);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to instantiate code generator '{TypeName}'", type.Name);
                    }
                }
            }
        }

        return generators;
    }
}
