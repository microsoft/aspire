// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TypeSystem;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Hosting.RemoteHost.CodeGeneration;

/// <summary>
/// JSON-RPC service for generating language-specific SDK code.
/// </summary>
internal sealed class CodeGenerationService
{
    private readonly AtsContextFactory _atsContextFactory;
    private readonly CodeGeneratorResolver _resolver;
    private readonly ILogger<CodeGenerationService> _logger;

    public CodeGenerationService(
        AtsContextFactory atsContextFactory,
        CodeGeneratorResolver resolver,
        ILogger<CodeGenerationService> logger)
    {
        _atsContextFactory = atsContextFactory;
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// Gets the ATS capabilities, types, and diagnostics.
    /// </summary>
    /// <returns>The capabilities information.</returns>
    [JsonRpcMethod("getCapabilities")]
    public CapabilitiesResponse GetCapabilities()
    {
        _logger.LogDebug(">> getCapabilities()");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var context = _atsContextFactory.GetContext();

            var response = new CapabilitiesResponse
            {
                Capabilities = context.Capabilities.Select(MapCapability).ToList(),
                HandleTypes = context.HandleTypes.Select(MapHandleType).ToList(),
                DtoTypes = context.DtoTypes.Select(MapDtoType).ToList(),
                EnumTypes = context.EnumTypes.Select(MapEnumType).ToList(),
                Diagnostics = context.Diagnostics.Select(MapDiagnostic).ToList()
            };

            _logger.LogDebug("<< getCapabilities() completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "<< getCapabilities() failed");
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
            Type = MapTypeRef(cp.Type)
        }).ToList(),
        CallbackReturnType = p.CallbackReturnType != null ? MapTypeRef(p.CallbackReturnType) : null,
        DefaultValue = p.DefaultValue?.ToString()
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
        ImplementedInterfaces = t.ImplementedInterfaces.Select(MapTypeRef).ToList(),
        BaseTypeHierarchy = t.BaseTypeHierarchy.Select(MapTypeRef).ToList()
    };

    private static DtoTypeResponse MapDtoType(AtsDtoTypeInfo t) => new()
    {
        TypeId = t.TypeId,
        Name = t.Name,
        Description = t.Description,
        Properties = t.Properties.Select(p => new DtoPropertyResponse
        {
            Name = p.Name,
            Type = MapTypeRef(p.Type),
            IsOptional = p.IsOptional,
            Description = p.Description
        }).ToList()
    };

    private static EnumTypeResponse MapEnumType(AtsEnumTypeInfo t) => new()
    {
        TypeId = t.TypeId,
        Name = t.Name,
        Values = t.Values.ToList()
    };

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
    /// <returns>A dictionary of file paths to file contents.</returns>
    [JsonRpcMethod("generateCode")]
    public Dictionary<string, string> GenerateCode(string language)
    {
        _logger.LogDebug(">> generateCode({Language})", language);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var generator = _resolver.GetCodeGenerator(language);
            if (generator == null)
            {
                throw new ArgumentException($"No code generator found for language: {language}");
            }

            var files = generator.GenerateDistributedApplication(_atsContextFactory.GetContext());

            _logger.LogDebug("<< generateCode({Language}) completed in {ElapsedMs}ms, generated {FileCount} files", language, sw.ElapsedMilliseconds, files.Count);
            return files;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "<< generateCode({Language}) failed", language);
            throw;
        }
    }
}

#region Response DTOs (Full Fidelity)

internal sealed class CapabilitiesResponse
{
    public List<CapabilityResponse> Capabilities { get; set; } = [];
    public List<HandleTypeResponse> HandleTypes { get; set; } = [];
    public List<DtoTypeResponse> DtoTypes { get; set; } = [];
    public List<EnumTypeResponse> EnumTypes { get; set; } = [];
    public List<DiagnosticResponse> Diagnostics { get; set; } = [];
}

internal sealed class CapabilityResponse
{
    public string CapabilityId { get; set; } = "";
    public string MethodName { get; set; } = "";
    public string? OwningTypeName { get; set; }
    public string QualifiedMethodName { get; set; } = "";
    public string? Description { get; set; }
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
}

internal sealed class CallbackParameterResponse
{
    public string Name { get; set; } = "";
    public TypeRefResponse? Type { get; set; }
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
    public List<TypeRefResponse> ImplementedInterfaces { get; set; } = [];
    public List<TypeRefResponse> BaseTypeHierarchy { get; set; } = [];
}

internal sealed class DtoTypeResponse
{
    public string TypeId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<DtoPropertyResponse> Properties { get; set; } = [];
}

internal sealed class DtoPropertyResponse
{
    public string Name { get; set; } = "";
    public TypeRefResponse? Type { get; set; }
    public bool IsOptional { get; set; }
    public string? Description { get; set; }
}

internal sealed class EnumTypeResponse
{
    public string TypeId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Values { get; set; } = [];
}

internal sealed class DiagnosticResponse
{
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Location { get; set; }
}

#endregion
