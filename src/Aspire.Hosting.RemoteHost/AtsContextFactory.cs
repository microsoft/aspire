// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.TypeSystem;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteHost;

/// <summary>
/// Factory for creating a shared <see cref="AtsContext"/> from loaded assemblies.
/// </summary>
internal sealed class AtsContextFactory
{
    private readonly Lazy<AtsContext> _context;

    public AtsContextFactory(AssemblyLoader assemblyLoader, ILogger<AtsContextFactory> logger)
    {
        _context = new Lazy<AtsContext>(() => Create(assemblyLoader.GetAssemblies(), logger));
    }

    /// <summary>
    /// Gets or creates the <see cref="AtsContext"/> by scanning the loaded assemblies.
    /// </summary>
    /// <returns>The scanned ATS context.</returns>
    public AtsContext GetContext() => _context.Value;

    private static AtsContext Create(IReadOnlyList<Assembly> assemblies, ILogger logger)
    {
        logger.LogDebug("Creating AtsContext from {AssemblyCount} assemblies...", assemblies.Count);

        // Scan all assemblies using multi-pass scanning
        var result = AtsCapabilityScanner.ScanAssemblies(assemblies);

        // Log diagnostics
        foreach (var diagnostic in result.Diagnostics)
        {
            if (diagnostic.Severity == AtsDiagnosticSeverity.Error)
            {
                logger.LogError("[ATS] {Message} at {Location}", diagnostic.Message, diagnostic.Location);
            }
            else if (diagnostic.Severity == AtsDiagnosticSeverity.Info)
            {
                logger.LogDebug("[ATS] {Message} at {Location}", diagnostic.Message, diagnostic.Location);
            }
            else
            {
                logger.LogWarning("[ATS] {Message} at {Location}", diagnostic.Message, diagnostic.Location);
            }
        }

        logger.LogDebug("Scanned {CapabilityCount} capabilities, {HandleTypeCount} handle types, {DtoCount} DTOs, {EnumCount} enums, {ExportedValueCount} exported values",
            result.Capabilities.Count, result.HandleTypes.Count, result.DtoTypes.Count, result.EnumTypes.Count, result.ExportedValues.Count);

        return result.ToAtsContext();
    }
}
