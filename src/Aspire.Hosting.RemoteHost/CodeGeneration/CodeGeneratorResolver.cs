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
    private readonly Lazy<DiscoveryResult> _discovery;
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
        _discovery = new Lazy<DiscoveryResult>(
            () => DiscoverGenerators(serviceProvider, assembliesProvider()));
    }

    /// <summary>
    /// Gets a code generator for the specified language.
    /// </summary>
    /// <param name="language">The target language (e.g., "TypeScript", "Python").</param>
    /// <returns>The code generator, or null if not found.</returns>
    public ICodeGenerator? GetCodeGenerator(string language)
    {
        _discovery.Value.Generators.TryGetValue(language, out var generator);
        return generator;
    }

    /// <summary>
    /// Gets the languages of all discovered code generators.
    /// </summary>
    /// <returns>The set of supported language identifiers.</returns>
    public IReadOnlyCollection<string> GetSupportedLanguages()
    {
        return _discovery.Value.Generators.Keys.ToArray();
    }

    /// <summary>
    /// Gets the result of generator discovery: the resolved generators and any
    /// <see cref="ReflectionTypeLoadException"/>s swallowed while probing assemblies. A non-empty
    /// <see cref="DiscoveryResult.LoadFailures"/> almost always means a code generator was silently
    /// dropped because of a binary mismatch (typically a diverged <c>Aspire.TypeSystem</c> version
    /// between the bundled apphost server and the restored SDK assemblies); callers use it to turn an
    /// otherwise-opaque "no generator found" failure into an actionable incompatible-SDK diagnostic.
    /// Exposing the whole result (rather than a dedicated accessor) also lets unit tests observe the
    /// swallowed failures, which discovery otherwise hides; see <c>ResolverDiagnosticsTests</c>.
    /// </summary>
    internal DiscoveryResult Discovery => _discovery.Value;

    private DiscoveryResult DiscoverGenerators(
        IServiceProvider serviceProvider,
        IReadOnlyList<Assembly> assemblies)
    {
        var generators = new Dictionary<string, ICodeGenerator>(StringComparer.OrdinalIgnoreCase);
        var loadFailures = new List<ReflectionTypeLoadException>();

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
                // Remember the load failure so a downstream "no generator found" error can be
                // re-cast as an actionable incompatible-SDK diagnostic instead of a cryptic
                // ArgumentException (see Discovery).
                loadFailures.Add(ex);
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

        return new DiscoveryResult(generators, loadFailures);
    }

    private static bool LooksLikeCodeGeneratorAssembly(string? assemblyName)
        => assemblyName is not null
           && assemblyName.StartsWith("Aspire.Hosting.CodeGeneration.", StringComparison.OrdinalIgnoreCase);

    internal sealed record DiscoveryResult(
        Dictionary<string, ICodeGenerator> Generators,
        IReadOnlyList<ReflectionTypeLoadException> LoadFailures);
}
