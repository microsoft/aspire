// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TypeSystem;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Hosting.RemoteHost.CodeGeneration;

/// <summary>
/// JSON-RPC service for generating language-specific SDK code.
/// </summary>
internal sealed class CodeGenerationService
{
    private const string GetCapabilitiesMethodName = "getCapabilities";
    private const string GenerateCodeMethodName = "generateCode";

    private readonly JsonRpcAuthenticationState _authenticationState;
    private readonly AtsContextFactory _atsContextFactory;
    private readonly CodeGeneratorResolver _resolver;
    private readonly AssemblyLoader _assemblyLoader;
    private readonly ILogger<CodeGenerationService> _logger;
    private readonly RemoteHostProfilingTelemetry _profilingTelemetry;

    public CodeGenerationService(
        JsonRpcAuthenticationState authenticationState,
        AtsContextFactory atsContextFactory,
        CodeGeneratorResolver resolver,
        AssemblyLoader assemblyLoader,
        ILogger<CodeGenerationService> logger,
        RemoteHostProfilingTelemetry profilingTelemetry)
    {
        _authenticationState = authenticationState;
        _atsContextFactory = atsContextFactory;
        _resolver = resolver;
        _assemblyLoader = assemblyLoader;
        _logger = logger;
        _profilingTelemetry = profilingTelemetry;
    }

    /// <summary>
    /// Gets the ATS capabilities, types, exported values, and diagnostics.
    /// </summary>
    /// <param name="assemblyNames">
    /// An optional list of assembly names used to scope the returned capabilities and types to those
    /// exported by the specified assemblies and their referenced assemblies. If <c>null</c> or empty,
    /// capabilities are returned for all available assemblies.
    /// </param>
    /// <returns>The capabilities information.</returns>
    [JsonRpcMethod(GetCapabilitiesMethodName)]
    public CapabilitiesResponse GetCapabilities(string[]? assemblyNames = null)
    {
        using var rpcActivity = _profilingTelemetry.StartJsonRpcServerCall(GetCapabilitiesMethodName);
        using var activity = _profilingTelemetry.StartCodeGenerationGetCapabilities();
        try
        {
            _authenticationState.ThrowIfNotAuthenticated();
            _logger.LogDebug(">> getCapabilities()");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var context = _atsContextFactory.GetContext();
            if (assemblyNames is { Length: > 0 })
            {
                context = AtsContextFilter.FilterByExportingAssemblies(context, assemblyNames);
            }
            activity.SetAtsCounts(
                context.Capabilities.Count,
                context.HandleTypes.Count,
                context.DtoTypes.Count,
                context.EnumTypes.Count,
                context.ExportedValues.Count,
                context.Diagnostics.Count);

            var response = new CapabilitiesResponse
            {
                Capabilities = context.Capabilities.Select(MapCapability).ToList(),
                HandleTypes = context.HandleTypes.Select(MapHandleType).ToList(),
                DtoTypes = context.DtoTypes.Select(MapDtoType).ToList(),
                EnumTypes = context.EnumTypes.Select(MapEnumType).ToList(),
                ExportedValues = context.ExportedValues.Select(MapExportedValue).ToList(),
                Diagnostics = context.Diagnostics.Select(MapDiagnostic).ToList()
            };

            _logger.LogDebug("<< getCapabilities() completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            _logger.LogError(ex, "<< getCapabilities() failed");
            var wrapped = CodeGenerationDiagnosticBuilder.TryCreateRpcException(ex, _assemblyLoader, _logger);
            if (wrapped is not null)
            {
                throw wrapped;
            }
            throw;
        }
    }

    private static CapabilityResponse MapCapability(AtsCapabilityInfo c) => new()
    {
        CapabilityId = c.CapabilityId,
        MethodName = c.MethodName,
        OwningTypeName = c.OwningTypeName,
        QualifiedMethodName = c.QualifiedMethodName,
        Description = c.Description,
        Documentation = MapDocumentation(c.Documentation),
        CapabilityKind = c.CapabilityKind.ToString(),
        TargetTypeId = c.TargetTypeId,
        TargetParameterName = c.TargetParameterName,
        ReturnsBuilder = c.ReturnsBuilder,
        Parameters = c.Parameters.Select(MapParameter).ToList(),
        ReturnType = MapTypeRef(c.ReturnType),
        TargetType = c.TargetType != null ? MapTypeRef(c.TargetType) : null,
        ExpandedTargetTypes = c.ExpandedTargetTypes.Select(MapTypeRef).ToList()
    };

    private static ParameterResponse MapParameter(AtsParameterInfo p) => new()
    {
        Name = p.Name,
        Type = p.Type != null ? MapTypeRef(p.Type) : null,
        IsOptional = p.IsOptional,
        IsNullable = p.IsNullable,
        IsCallback = p.IsCallback,
        CallbackParameters = p.CallbackParameters?.Select(cp => new CallbackParameterResponse
        {
            Name = cp.Name,
            Type = MapTypeRef(cp.Type),
            Documentation = MapDocumentation(cp.Documentation)
        }).ToList(),
        CallbackReturnType = p.CallbackReturnType != null ? MapTypeRef(p.CallbackReturnType) : null,
        DefaultValue = p.DefaultValue?.ToString(),
        Documentation = MapDocumentation(p.Documentation)
    };

    private static TypeRefResponse MapTypeRef(AtsTypeRef t) => new()
    {
        TypeId = t.TypeId,
        Category = t.Category.ToString(),
        IsInterface = t.IsInterface,
        IsReadOnly = t.IsReadOnly,
        ElementType = t.ElementType != null ? MapTypeRef(t.ElementType) : null,
        KeyType = t.KeyType != null ? MapTypeRef(t.KeyType) : null,
        ValueType = t.ValueType != null ? MapTypeRef(t.ValueType) : null,
        UnionTypes = t.UnionTypes?.Select(MapTypeRef).ToList()
    };

    private static HandleTypeResponse MapHandleType(AtsTypeInfo t) => new()
    {
        AtsTypeId = t.AtsTypeId,
        IsInterface = t.IsInterface,
        ExposeProperties = t.HasExposeProperties,
        ExposeMethods = t.HasExposeMethods,
        Documentation = MapDocumentation(t.Documentation),
        ImplementedInterfaces = t.ImplementedInterfaces.Select(MapTypeRef).ToList(),
        BaseTypeHierarchy = t.BaseTypeHierarchy.Select(MapTypeRef).ToList()
    };

    private static DtoTypeResponse MapDtoType(AtsDtoTypeInfo t) => new()
    {
        TypeId = t.TypeId,
        Name = t.Name,
        Description = t.Description,
        Documentation = MapDocumentation(t.Documentation),
        Properties = t.Properties.Select(p => new DtoPropertyResponse
        {
            Name = p.Name,
            Type = MapTypeRef(p.Type),
            IsOptional = p.IsOptional,
            Description = p.Description,
            Documentation = MapDocumentation(p.Documentation)
        }).ToList()
    };

    private static EnumTypeResponse MapEnumType(AtsEnumTypeInfo t) => new()
    {
        TypeId = t.TypeId,
        Name = t.Name,
        Values = t.Values.ToList(),
        ValueInfos = t.ValueInfos.Select(value => new EnumValueResponse
        {
            Name = value.Name,
            Documentation = MapDocumentation(value.Documentation)
        }).ToList(),
        Documentation = MapDocumentation(t.Documentation)
    };

    private static ExportedValueResponse MapExportedValue(AtsExportedValueInfo value) => new()
    {
        PathSegments = value.PathSegments.ToList(),
        Type = MapTypeRef(value.Type),
        Value = value.Value?.DeepClone(),
        Description = value.Description,
        Documentation = MapDocumentation(value.Documentation)
    };

    private static DocumentationResponse? MapDocumentation(AtsDocumentationInfo? documentation)
    {
        if (documentation is null)
        {
            return null;
        }

        return new DocumentationResponse
        {
            Summary = documentation.Summary,
            Remarks = documentation.Remarks,
            Returns = documentation.Returns,
            Parameters = documentation.Parameters.Select(parameter => new ParameterDocumentationResponse
            {
                Name = parameter.Name,
                Description = parameter.Description
            }).ToList()
        };
    }

    private static DiagnosticResponse MapDiagnostic(AtsDiagnostic d) => new()
    {
        Severity = d.Severity.ToString(),
        Message = d.Message,
        Location = d.Location
    };

    /// <summary>
    /// Generates SDK code for the specified language.
    /// </summary>
    /// <param name="language">The target language (e.g., "TypeScript", "Python").</param>
    /// <param name="assemblyName">The exporting assembly to scope the generated SDK to, or null to use the full ATS context.</param>
    /// <returns>A dictionary of file paths to file contents.</returns>
    [JsonRpcMethod(GenerateCodeMethodName)]
    public Dictionary<string, string> GenerateCode(string language, string? assemblyName = null)
    {
        using var rpcActivity = _profilingTelemetry.StartJsonRpcServerCall(GenerateCodeMethodName);
        using var activity = _profilingTelemetry.StartCodeGenerationGenerate(language);
        try
        {
            _authenticationState.ThrowIfNotAuthenticated();
            _logger.LogDebug(">> generateCode({Language})", language);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var generator = _resolver.GetCodeGenerator(language);
            if (generator == null)
            {
                throw new ArgumentException(BuildNoCodeGeneratorMessage(language));
            }

            var context = _atsContextFactory.GetContext();
            if (!string.IsNullOrWhiteSpace(assemblyName))
            {
                context = AtsContextFilter.FilterByExportingAssembliesWithReferences(context, [assemblyName]);
            }

            var files = generator.GenerateDistributedApplication(context);
            activity.SetFileCount(files.Count);

            _logger.LogDebug("<< generateCode({Language}) completed in {ElapsedMs}ms, generated {FileCount} files", language, sw.ElapsedMilliseconds, files.Count);
            return files;
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            _logger.LogError(ex, "<< generateCode({Language}) failed", language);
            var wrapped = CodeGenerationDiagnosticBuilder.TryCreateRpcException(ex, _assemblyLoader, _logger);
            if (wrapped is not null)
            {
                throw wrapped;
            }
            throw;
        }
    }

    private string BuildNoCodeGeneratorMessage(string language)
    {
        var available = _resolver.GetSupportedLanguages()
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (available.Length == 0)
        {
            // No generators discovered at all is almost always a binary-mismatch / type-load
            // failure (see CodeGeneratorResolver warnings). Point the user at the apphost
            // server log so they can see the underlying LoaderExceptions.
            return $"No code generator found for language: {language}. " +
                   "No code generators were discovered in any loaded assembly. " +
                   "This usually indicates a binary mismatch between the bundled apphost server and the integration assemblies on disk; " +
                   "check the apphost server log for 'LoaderExceptions' Warnings.";
        }

        return $"No code generator found for language: {language}. Available languages: {string.Join(", ", available)}.";
    }
}

#region Response DTOs (Full Fidelity)

internal sealed class CapabilitiesResponse
{
    public List<CapabilityResponse> Capabilities { get; set; } = [];
    public List<HandleTypeResponse> HandleTypes { get; set; } = [];
    public List<DtoTypeResponse> DtoTypes { get; set; } = [];
    public List<EnumTypeResponse> EnumTypes { get; set; } = [];
    public List<ExportedValueResponse> ExportedValues { get; set; } = [];
    public List<DiagnosticResponse> Diagnostics { get; set; } = [];
}

internal sealed class CapabilityResponse
{
    public string CapabilityId { get; set; } = "";
    public string MethodName { get; set; } = "";
    public string? OwningTypeName { get; set; }
    public string QualifiedMethodName { get; set; } = "";
    public string? Description { get; set; }
    public DocumentationResponse? Documentation { get; set; }
    public string CapabilityKind { get; set; } = "";
    public string? TargetTypeId { get; set; }
    public string? TargetParameterName { get; set; }
    public bool ReturnsBuilder { get; set; }
    public List<ParameterResponse> Parameters { get; set; } = [];
    public TypeRefResponse? ReturnType { get; set; }
    public TypeRefResponse? TargetType { get; set; }
    public List<TypeRefResponse> ExpandedTargetTypes { get; set; } = [];
}

internal sealed class ParameterResponse
{
    public string Name { get; set; } = "";
    public TypeRefResponse? Type { get; set; }
    public bool IsOptional { get; set; }
    public bool IsNullable { get; set; }
    public bool IsCallback { get; set; }
    public List<CallbackParameterResponse>? CallbackParameters { get; set; }
    public TypeRefResponse? CallbackReturnType { get; set; }
    public string? DefaultValue { get; set; }
    public DocumentationResponse? Documentation { get; set; }
}

internal sealed class CallbackParameterResponse
{
    public string Name { get; set; } = "";
    public TypeRefResponse? Type { get; set; }
    public DocumentationResponse? Documentation { get; set; }
}

internal sealed class TypeRefResponse
{
    public string TypeId { get; set; } = "";
    public string Category { get; set; } = "";
    public bool IsInterface { get; set; }
    public bool IsReadOnly { get; set; }
    public TypeRefResponse? ElementType { get; set; }
    public TypeRefResponse? KeyType { get; set; }
    public TypeRefResponse? ValueType { get; set; }
    public List<TypeRefResponse>? UnionTypes { get; set; }
}

internal sealed class HandleTypeResponse
{
    public string AtsTypeId { get; set; } = "";
    public bool IsInterface { get; set; }
    public bool ExposeProperties { get; set; }
    public bool ExposeMethods { get; set; }
    public DocumentationResponse? Documentation { get; set; }
    public List<TypeRefResponse> ImplementedInterfaces { get; set; } = [];
    public List<TypeRefResponse> BaseTypeHierarchy { get; set; } = [];
}

internal sealed class DtoTypeResponse
{
    public string TypeId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DocumentationResponse? Documentation { get; set; }
    public List<DtoPropertyResponse> Properties { get; set; } = [];
}

internal sealed class DtoPropertyResponse
{
    public string Name { get; set; } = "";
    public TypeRefResponse? Type { get; set; }
    public bool IsOptional { get; set; }
    public string? Description { get; set; }
    public DocumentationResponse? Documentation { get; set; }
}

internal sealed class EnumTypeResponse
{
    public string TypeId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Values { get; set; } = [];
    public List<EnumValueResponse> ValueInfos { get; set; } = [];
    public DocumentationResponse? Documentation { get; set; }
}

internal sealed class EnumValueResponse
{
    public string Name { get; set; } = "";
    public DocumentationResponse? Documentation { get; set; }
}

internal sealed class ExportedValueResponse
{
    public List<string> PathSegments { get; set; } = [];
    public TypeRefResponse Type { get; set; } = null!;
    public System.Text.Json.Nodes.JsonNode? Value { get; set; }
    public string? Description { get; set; }
    public DocumentationResponse? Documentation { get; set; }
}

internal sealed class DocumentationResponse
{
    public string? Summary { get; set; }
    public string? Remarks { get; set; }
    public string? Returns { get; set; }
    public List<ParameterDocumentationResponse> Parameters { get; set; } = [];
}

internal sealed class ParameterDocumentationResponse
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

internal sealed class DiagnosticResponse
{
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Location { get; set; }
}

#endregion
