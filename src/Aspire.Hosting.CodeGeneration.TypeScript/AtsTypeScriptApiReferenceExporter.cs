// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.TypeScript;

/// <summary>
/// Exports the canonical TypeScript API reference for the surface
/// <see cref="AtsTypeScriptCodeGenerator"/> generates.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately lives on its own type rather than on <see cref="AtsTypeScriptCodeGenerator"/>.
/// <c>Aspire.TypeSystem</c> is force-shared from the apphost server's default
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> (see
/// <c>src/Aspire.Hosting.RemoteHost/IntegrationLoadContext.cs</c>) and freezes its strong-name
/// <c>AssemblyVersion</c> at a constant so an older CLI still binds a newer SDK's codegen assembly.
/// Version binding therefore succeeds, but an older CLI's bundled copy has no
/// <see cref="IApiReferenceExporter"/> in it: the interface is new. A type's interface list is
/// resolved eagerly when the type loads, so putting the interface on the code generator would make
/// the generator itself unloadable under any CLI that predates the interface, and
/// <c>CodeGeneratorResolver</c> would then find no TypeScript generator at all — TypeScript
/// generation, not just export, would stop working.
/// </para>
/// <para>
/// Keeping export on a separate type confines that loss to the feature the older CLI cannot use
/// anyway: <c>CodeGeneratorResolver</c> salvages the loadable types out of the
/// <see cref="System.Reflection.ReflectionTypeLoadException"/>, so the generator survives and only
/// this type disappears.
/// </para>
/// </remarks>
internal sealed class AtsTypeScriptApiReferenceExporter : IApiReferenceExporter
{
    /// <inheritdoc />
    public string Language => "TypeScript";

    /// <inheritdoc />
    public JsonElement ExportApi(
        AtsContext context,
        ApiReferenceExportOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        // Build the projector from the same context the generator would use, so the exported
        // documentation describes the exact signatures generation would emit rather than a
        // second, independently derived reading of the ATS context.
        var projector = new TypeScriptApiProjector(context);
        var model = projector.BuildApiModel(
            new TypeScriptApiPackageIdentity(options.PackageName, options.PackageVersion),
            options.ExportingAssemblyNames,
            cancellationToken);

        // JsonDocument.Parse + Clone rather than JsonSerializer, because this assembly is
        // AOT-compatible and the serializer's reflection-based overloads are not.
        using var document = JsonDocument.Parse(TypeScriptApiExportWriter.WriteToJson(model));
        return document.RootElement.Clone();
    }
}
