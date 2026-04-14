// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Rust;

/// <summary>
/// Generates a Rust SDK using the ATS (Aspire Type System) capability-based API.
/// Produces wrapper structs that proxy capabilities via JSON-RPC.
/// </summary>
internal sealed class AtsRustCodeGenerator : ICodeGenerator
{
    private static readonly HashSet<string> s_rustKeywords = new(StringComparer.Ordinal)
    {
        "as", "async", "await", "break", "const", "continue", "crate", "dyn", "else",
        "enum", "extern", "false", "fn", "for", "if", "impl", "in", "let", "loop",
        "match", "mod", "move", "mut", "pub", "ref", "return", "self", "Self",
        "static", "struct", "super", "trait", "true", "type", "unsafe", "use",
        "where", "while", "abstract", "become", "box", "do", "final", "macro",
        "override", "priv", "try", "typeof", "unsized", "virtual", "yield"
    };

    private TextWriter _writer = null!;
    private readonly Dictionary<string, string> _structNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _dtoNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _enumNames = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Language => "Rust";

    /// <inheritdoc />
    public Dictionary<string, string> GenerateDistributedApplication(AtsContext context)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mod.rs"] = """
                //! Aspire Rust SDK
                //! GENERATED CODE - DO NOT EDIT

                pub mod transport;
                pub mod base;
                pub mod aspire;

                pub use transport::*;
                pub use base::*;
                pub use aspire::*;
                """,
            ["transport.rs"] = GetEmbeddedResource("transport.rs"),
            ["base.rs"] = GetEmbeddedResource("base.rs"),
            ["aspire.rs"] = GenerateAspireSdk(context)
        };
    }

    private static string GetEmbeddedResource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Aspire.Hosting.CodeGeneration.Rust.Resources.{name}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private string GenerateAspireSdk(AtsContext context)
    {
        using var stringWriter = new StringWriter(CultureInfo.InvariantCulture);
        _writer = stringWriter;

        var capabilities = context.Capabilities;
        var dtoTypes = context.DtoTypes;
        var enumTypes = context.EnumTypes;

        _enumNames.Clear();
        foreach (var enumType in enumTypes)
        {
            _enumNames[enumType.TypeId] = SanitizeIdentifier(enumType.Name);
        }

        _dtoNames.Clear();
        foreach (var dto in dtoTypes)
        {
            _dtoNames[dto.TypeId] = SanitizeIdentifier(dto.Name);
        }

        var handleTypes = BuildHandleTypes(context);
        var capabilitiesByTarget = GroupCapabilitiesByTarget(capabilities);
        var listTypeIds = CollectListAndDictTypeIds(capabilities);

        WriteHeader();
        GenerateEnumTypes(enumTypes);
        GenerateDtoTypes(dtoTypes);
        GenerateHandleTypes(handleTypes, capabilitiesByTarget);
        GenerateHandleWrapperRegistrations(handleTypes, listTypeIds);
        GenerateConnectionHelpers();

        return stringWriter.ToString();
    }

    private void WriteHeader()
    {
        WriteLine("//! aspire.rs - Capability-based Aspire SDK");
        WriteLine("//! GENERATED CODE - DO NOT EDIT");
        WriteLine();
        WriteLine("use std::collections::HashMap;");
        WriteLine("use std::sync::Arc;");
        WriteLine();
        WriteLine("use serde::{Deserialize, Serialize};");
        WriteLine("use serde_json::{json, Value};");
        WriteLine();
        WriteLine("use crate::transport::{");
        WriteLine("    AspireClient, CancellationToken, Handle,");
        WriteLine("    register_callback, register_cancellation, serialize_value,");
        WriteLine("};");
        WriteLine("use crate::base::{");
        WriteLine("    HandleWrapperBase, ResourceBuilderBase, ReferenceExpression,");
        WriteLine("    AspireList, AspireDict, serialize_handle, HasHandle,");
        WriteLine("};");
        WriteLine();
    }

    private void GenerateEnumTypes(IReadOnlyList<AtsEnumTypeInfo> enumTypes)
    {
        if (enumTypes.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Enums");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var enumType in enumTypes)
        {
            if (enumType.ClrType is null)
            {
                continue;
            }

            var enumName = _enumNames[enumType.TypeId];
            WriteLine($"/// {enumType.Name}");
            WriteLine("#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]");
            WriteLine($"pub enum {enumName} {{");
            var firstMember = true;
            foreach (var member in Enum.GetNames(enumType.ClrType))
            {
                var memberName = ToPascalCase(member);
                if (firstMember)
                {
                    WriteLine($"    #[default]");
                    firstMember = false;
                }
                WriteLine($"    #[serde(rename = \"{member}\")]");
                WriteLine($"    {memberName},");
            }
            WriteLine("}");
            WriteLine();

            // Generate Display trait
            WriteLine($"impl std::fmt::Display for {enumName} {{");
            WriteLine("    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {");
            WriteLine("        match self {");
            foreach (var member in Enum.GetNames(enumType.ClrType))
            {
                var memberName = ToPascalCase(member);
                WriteLine($"            Self::{memberName} => write!(f, \"{member}\"),");
            }
            WriteLine("        }");
            WriteLine("    }");
            WriteLine("}");
            WriteLine();
        }
    }

    private void GenerateDtoTypes(IReadOnlyList<AtsDtoTypeInfo> dtoTypes)
    {
        if (dtoTypes.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// DTOs");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var dto in dtoTypes)
        {
            // Skip ReferenceExpression - it's defined in base.rs
            if (dto.TypeId == AtsConstants.ReferenceExpressionTypeId)
            {
                continue;
            }

            var dtoName = _dtoNames[dto.TypeId];
            WriteLine($"/// {dto.Name}");
            WriteLine("#[derive(Debug, Clone, Default, Serialize, Deserialize)]");
            WriteLine($"pub struct {dtoName} {{");
            foreach (var property in dto.Properties)
            {
                var propertyName = ToSnakeCase(property.Name);
                var propertyType = MapTypeRefToRustForDto(property.Type, property.IsOptional);
                if (property.IsOptional)
                {
                    WriteLine($"    #[serde(rename = \"{property.Name}\", skip_serializing_if = \"Option::is_none\")]");
                }
                else
                {
                    WriteLine($"    #[serde(rename = \"{property.Name}\")]");
                }
                WriteLine($"    pub {propertyName}: {propertyType},");
            }
            WriteLine("}");
            WriteLine();

            // Generate to_map method
            WriteLine($"impl {dtoName} {{");
            WriteLine("    pub fn to_map(&self) -> HashMap<String, Value> {");
            WriteLine("        let mut map = HashMap::new();");
            foreach (var property in dto.Properties)
            {
                var propertyName = ToSnakeCase(property.Name);
                if (property.IsOptional)
                {
                    WriteLine($"        if let Some(ref v) = self.{propertyName} {{");
                    WriteLine($"            map.insert(\"{property.Name}\".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));");
                    WriteLine("        }");
                }
                else
                {
                    WriteLine($"        map.insert(\"{property.Name}\".to_string(), serde_json::to_value(&self.{propertyName}).unwrap_or(Value::Null));");
                }
            }
            WriteLine("        map");
            WriteLine("    }");
            WriteLine("}");
            WriteLine();
        }
    }

    private void GenerateHandleTypes(
        IReadOnlyList<RustHandleType> handleTypes,
        Dictionary<string, List<AtsCapabilityInfo>> capabilitiesByTarget)
    {
        if (handleTypes.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Handle Wrappers");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var handleType in handleTypes.OrderBy(t => t.StructName, StringComparer.Ordinal))
        {
            WriteLine($"/// Wrapper for {handleType.TypeId}");
            WriteLine($"pub struct {handleType.StructName} {{");
            WriteLine("    handle: Handle,");
            WriteLine("    client: Arc<AspireClient>,");
            WriteLine("}");
            WriteLine();

            // Implement HasHandle trait
            WriteLine($"impl HasHandle for {handleType.StructName} {{");
            WriteLine("    fn handle(&self) -> &Handle {");
            WriteLine("        &self.handle");
            WriteLine("    }");
            WriteLine("}");
            WriteLine();

            // Constructor and methods
            WriteLine($"impl {handleType.StructName} {{");
            WriteLine("    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {");
            WriteLine("        Self { handle, client }");
            WriteLine("    }");
            WriteLine();
            WriteLine("    pub fn handle(&self) -> &Handle {");
            WriteLine("        &self.handle");
            WriteLine("    }");
            WriteLine();
            WriteLine("    pub fn client(&self) -> &Arc<AspireClient> {");
            WriteLine("        &self.client");
            WriteLine("    }");

            if (capabilitiesByTarget.TryGetValue(handleType.TypeId, out var methods))
            {
                foreach (var method in methods)
                {
                    GenerateCapabilityMethod(handleType.StructName, method);
                }
            }

            WriteLine("}");
            WriteLine();
        }
    }

    private void GenerateCapabilityMethod(string structName, AtsCapabilityInfo capability)
    {
        var targetParamName = capability.TargetParameterName ?? "builder";
        var methodName = ToSnakeCase(capability.MethodName);
        var parameters = capability.Parameters
            .Where(p => !string.Equals(p.Name, targetParamName, StringComparison.Ordinal))
            .ToList();

        // Check if this is a List/Dict property getter (no parameters, returns List/Dict)
        if (parameters.Count == 0 && IsListOrDictPropertyGetter(capability.ReturnType))
        {
            GenerateListOrDictProperty(structName, capability, methodName);
            return;
        }

        var returnType = MapTypeRefToRust(capability.ReturnType, false);
        var hasReturn = capability.ReturnType.TypeId != AtsConstants.Void;

        // Build parameter list
        var paramList = new StringBuilder();
        paramList.Append("&self");
        foreach (var parameter in parameters)
        {
            var paramName = ToSnakeCase(parameter.Name);
            string paramType;
            if (parameter.IsCallback)
            {
                paramType = "impl Fn(Vec<Value>) -> Value + Send + Sync + 'static";
            }
            else if (IsCancellationToken(parameter))
            {
                paramType = "Option<&CancellationToken>";
            }
            else if (IsHandleType(parameter.Type))
            {
                // Handle wrappers are passed by reference
                var handleTypeName = MapTypeRefToRust(parameter.Type, false);
                paramType = parameter.IsOptional ? $"Option<&{handleTypeName}>" : $"&{handleTypeName}";
            }
            else
            {
                // Use idiomatic Rust parameter types (e.g., &str instead of String)
                paramType = MapParameterTypeToRust(parameter.Type, parameter.IsOptional);
            }
            paramList.Append(CultureInfo.InvariantCulture, $", {paramName}: {paramType}");
        }

        // Generate doc comment
        if (!string.IsNullOrEmpty(capability.Description))
        {
            WriteLine();
            WriteLine($"    /// {capability.Description}");
        }

        var resultType = hasReturn ? $"Result<{returnType}, Box<dyn std::error::Error>>" : "Result<(), Box<dyn std::error::Error>>";
        WriteLine($"    pub fn {methodName}({paramList}) -> {resultType} {{");
        WriteLine("        let mut args: HashMap<String, Value> = HashMap::new();");
        WriteLine($"        args.insert(\"{targetParamName}\".to_string(), self.handle.to_json());");

        foreach (var parameter in parameters)
        {
            var paramName = ToSnakeCase(parameter.Name);
            if (parameter.IsCallback)
            {
                WriteLine($"        let callback_id = register_callback({paramName});");
                WriteLine($"        args.insert(\"{parameter.Name}\".to_string(), Value::String(callback_id));");
                continue;
            }

            if (IsCancellationToken(parameter))
            {
                WriteLine($"        if let Some(token) = {paramName} {{");
                WriteLine($"            let token_id = register_cancellation(token, self.client.clone());");
                WriteLine($"            args.insert(\"{parameter.Name}\".to_string(), Value::String(token_id));");
                WriteLine("        }");
                continue;
            }

            // Handle wrappers need to be converted to their handle JSON
            if (IsHandleType(parameter.Type))
            {
                if (parameter.IsOptional)
                {
                    WriteLine($"        if let Some(ref v) = {paramName} {{");
                    WriteLine($"            args.insert(\"{parameter.Name}\".to_string(), v.handle().to_json());");
                    WriteLine("        }");
                }
                else
                {
                    WriteLine($"        args.insert(\"{parameter.Name}\".to_string(), {paramName}.handle().to_json());");
                }
                continue;
            }

            // Handle-backed AspireList/AspireDict parameters: serialize via their handle
            if (IsAspireListOrDictParam(parameter.Type))
            {
                if (parameter.IsOptional)
                {
                    WriteLine($"        if let Some(ref v) = {paramName} {{");
                    WriteLine($"            args.insert(\"{parameter.Name}\".to_string(), v.handle().to_json());");
                    WriteLine("        }");
                }
                else
                {
                    WriteLine($"        args.insert(\"{parameter.Name}\".to_string(), {paramName}.handle().to_json());");
                }
                continue;
            }

            // Collections of handle types: serialize each element via its handle
            if (IsCollectionOfHandleTypes(parameter.Type))
            {
                if (parameter.IsOptional)
                {
                    WriteLine($"        if let Some(ref v) = {paramName} {{");
                    WriteLine($"            let handles: Vec<Value> = v.iter().map(|item| item.handle().to_json()).collect();");
                    WriteLine($"            args.insert(\"{parameter.Name}\".to_string(), Value::Array(handles));");
                    WriteLine("        }");
                }
                else
                {
                    WriteLine($"        let handles: Vec<Value> = {paramName}.iter().map(|item| item.handle().to_json()).collect();");
                    WriteLine($"        args.insert(\"{parameter.Name}\".to_string(), Value::Array(handles));");
                }
                continue;
            }

            if (parameter.IsOptional)
            {
                WriteLine($"        if let Some(ref v) = {paramName} {{");
                WriteLine($"            args.insert(\"{parameter.Name}\".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));");
                WriteLine("        }");
            }
            else
            {
                WriteLine($"        args.insert(\"{parameter.Name}\".to_string(), serde_json::to_value(&{paramName}).unwrap_or(Value::Null));");
            }
        }

        WriteLine($"        let result = self.client.invoke_capability(\"{capability.CapabilityId}\", args)?;");

        if (hasReturn)
        {
            var returnTypeRef = capability.ReturnType;

            // Generate conversion based on return type
            if (IsHandleType(returnTypeRef))
            {
                var wrappedType = MapHandleType(returnTypeRef.TypeId);
                WriteLine($"        let handle: Handle = serde_json::from_value(result)?;");
                WriteLine($"        Ok({wrappedType}::new(handle, self.client.clone()))");
            }
            else if (returnTypeRef?.TypeId == AtsConstants.CancellationToken)
            {
                // CancellationToken needs special handling - create from handle
                WriteLine($"        let handle: Handle = serde_json::from_value(result)?;");
                WriteLine($"        Ok(CancellationToken::new(handle, self.client.clone()))");
            }
            else if (returnTypeRef?.Category == AtsTypeCategory.Dict && returnTypeRef.IsReadOnly == false)
            {
                // Handle-backed AspireDict
                WriteLine($"        let handle: Handle = serde_json::from_value(result)?;");
                WriteLine($"        Ok(AspireDict::new(handle, self.client.clone()))");
            }
            else if (returnTypeRef?.Category == AtsTypeCategory.List && returnTypeRef.IsReadOnly == false)
            {
                // Handle-backed AspireList
                WriteLine($"        let handle: Handle = serde_json::from_value(result)?;");
                WriteLine($"        Ok(AspireList::new(handle, self.client.clone()))");
            }
            else if (IsCollectionOfHandleTypes(returnTypeRef))
            {
                // Readonly list of handle types: deserialize as Vec<Handle> and wrap each
                var elementType = MapTypeRefToRust(returnTypeRef!.ElementType, false);
                WriteLine($"        let handles: Vec<Handle> = serde_json::from_value(result)?;");
                WriteLine($"        Ok(handles.into_iter().map(|h| {elementType}::new(h, self.client.clone())).collect())");
            }
            else
            {
                WriteLine($"        Ok(serde_json::from_value(result)?)");
            }
        }
        else
        {
            WriteLine("        Ok(())");
        }

        WriteLine("    }");
    }

    private static bool IsListOrDictPropertyGetter(AtsTypeRef? returnType)
    {
        if (returnType is null)
        {
            return false;
        }

        return returnType.Category == AtsTypeCategory.List || returnType.Category == AtsTypeCategory.Dict;
    }

#pragma warning disable IDE0060 // Remove unused parameter - structName kept for API consistency
    private void GenerateListOrDictProperty(string structName, AtsCapabilityInfo capability, string methodName)
#pragma warning restore IDE0060
    {
        var returnType = capability.ReturnType!;
        var isDict = returnType.Category == AtsTypeCategory.Dict;
        var wrapperType = isDict ? "AspireDict" : "AspireList";

        // Determine type arguments
        string typeArgs;
        if (isDict)
        {
            var keyType = MapTypeRefToRust(returnType.KeyType, false);
            var valueType = MapTypeRefToRust(returnType.ValueType, false);
            typeArgs = $"<{keyType}, {valueType}>";
        }
        else
        {
            var elementType = MapTypeRefToRust(returnType.ElementType, false);
            typeArgs = $"<{elementType}>";
        }

        var fullType = $"{wrapperType}{typeArgs}";

        // Generate doc comment
        if (!string.IsNullOrEmpty(capability.Description))
        {
            WriteLine();
            WriteLine($"    /// {capability.Description}");
        }

        // Generate getter method that creates AspireList/AspireDict with lazy getter
        WriteLine($"    pub fn {methodName}(&self) -> {fullType} {{");
        WriteLine($"        {wrapperType}::with_getter(self.handle.clone(), self.client.clone(), \"{capability.CapabilityId}\")");
        WriteLine("    }");
    }

#pragma warning disable IDE0060 // Remove unused parameter - keeping for API consistency with other generators
    private void GenerateHandleWrapperRegistrations(
        IReadOnlyList<RustHandleType> handleTypes,
        HashSet<string> listTypeIds)
#pragma warning restore IDE0060
    {
        WriteLine("// ============================================================================");
        WriteLine("// Handle wrapper registrations");
        WriteLine("// ============================================================================");
        WriteLine();
        WriteLine("pub fn register_all_wrappers() {");
        WriteLine("    // Handle wrappers are created inline in generated code");
        WriteLine("    // This function is provided for API compatibility");
        WriteLine("}");
        WriteLine();
    }

    private void GenerateConnectionHelpers()
    {
        var builderStructName = _structNames.TryGetValue(AtsConstants.BuilderTypeId, out var name)
            ? name
            : "DistributedApplicationBuilder";

        WriteLine("// ============================================================================");
        WriteLine("// Connection Helpers");
        WriteLine("// ============================================================================");
        WriteLine();
        WriteLine("/// Establishes a connection to the AppHost server.");
        WriteLine("pub fn connect() -> Result<Arc<AspireClient>, Box<dyn std::error::Error>> {");
        WriteLine("    let socket_path = std::env::var(\"REMOTE_APP_HOST_SOCKET_PATH\")");
        WriteLine("        .map_err(|_| \"REMOTE_APP_HOST_SOCKET_PATH environment variable not set. Run this application using `aspire run`\")?;");
        WriteLine("    let client = Arc::new(AspireClient::new(&socket_path));");
        WriteLine("    client.connect()?;");
        WriteLine("    let auth_token = std::env::var(\"ASPIRE_REMOTE_APPHOST_TOKEN\")");
        WriteLine("        .map_err(|_| \"ASPIRE_REMOTE_APPHOST_TOKEN environment variable not set. Run this application using `aspire run`\")?;");
        WriteLine("    client.authenticate(&auth_token)?;");
        WriteLine("    Ok(client)");
        WriteLine("}");
        WriteLine();
        WriteLine($"/// Creates a new distributed application builder.");
        WriteLine($"pub fn create_builder(options: Option<CreateBuilderOptions>) -> Result<{builderStructName}, Box<dyn std::error::Error>> {{");
        WriteLine("    let client = connect()?;");
        WriteLine("    let mut resolved_options: HashMap<String, Value> = HashMap::new();");
        WriteLine("    if let Some(opts) = options {");
        WriteLine("        for (k, v) in opts.to_map() {");
        WriteLine("            resolved_options.insert(k, v);");
        WriteLine("        }");
        WriteLine("    }");
        WriteLine("    if !resolved_options.contains_key(\"Args\") {");
        WriteLine("        let args: Vec<String> = std::env::args().skip(1).collect();");
        WriteLine("        resolved_options.insert(\"Args\".to_string(), serde_json::to_value(args).unwrap_or(Value::Null));");
        WriteLine("    }");
        WriteLine("    if !resolved_options.contains_key(\"ProjectDirectory\") {");
        WriteLine("        if let Ok(pwd) = std::env::current_dir() {");
        WriteLine("            resolved_options.insert(\"ProjectDirectory\".to_string(), Value::String(pwd.to_string_lossy().to_string()));");
        WriteLine("        }");
        WriteLine("    }");
        WriteLine("    let mut args: HashMap<String, Value> = HashMap::new();");
        WriteLine("    args.insert(\"options\".to_string(), serde_json::to_value(resolved_options).unwrap_or(Value::Null));");
        WriteLine("    let result = client.invoke_capability(\"Aspire.Hosting/createBuilderWithOptions\", args)?;");
        WriteLine("    let handle: Handle = serde_json::from_value(result)?;");
        WriteLine($"    Ok({builderStructName}::new(handle, client))");
        WriteLine("}");
        WriteLine();
    }

    private IReadOnlyList<RustHandleType> BuildHandleTypes(AtsContext context)
    {
        var handleTypeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handleType in context.HandleTypes)
        {
            // Skip ReferenceExpression and CancellationToken - they're defined in base.rs/transport.rs
            if (handleType.AtsTypeId == AtsConstants.ReferenceExpressionTypeId
                || IsCancellationTokenTypeId(handleType.AtsTypeId))
            {
                continue;
            }
            handleTypeIds.Add(handleType.AtsTypeId);
        }

        foreach (var capability in context.Capabilities)
        {
            AddHandleTypeIfNeeded(handleTypeIds, capability.TargetType);
            AddHandleTypeIfNeeded(handleTypeIds, capability.ReturnType);
            foreach (var parameter in capability.Parameters)
            {
                AddHandleTypeIfNeeded(handleTypeIds, parameter.Type);
                if (parameter.IsCallback && parameter.CallbackParameters is not null)
                {
                    foreach (var callbackParam in parameter.CallbackParameters)
                    {
                        AddHandleTypeIfNeeded(handleTypeIds, callbackParam.Type);
                    }
                }
            }
            // Also include expanded target types (concrete types discovered via interface expansion)
            foreach (var expandedType in capability.ExpandedTargetTypes)
            {
                AddHandleTypeIfNeeded(handleTypeIds, expandedType);
            }
        }

        _structNames.Clear();
        foreach (var typeId in handleTypeIds)
        {
            _structNames[typeId] = CreateStructName(typeId);
        }

        var handleTypeMap = context.HandleTypes
            .GroupBy(t => t.AtsTypeId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Any(t => t.IsResourceBuilder),
                StringComparer.Ordinal);
        var results = new List<RustHandleType>();
        foreach (var typeId in handleTypeIds)
        {
            var isResourceBuilder = false;
            if (handleTypeMap.TryGetValue(typeId, out var typeInfo))
            {
                isResourceBuilder = typeInfo;
            }

            results.Add(new RustHandleType(typeId, _structNames[typeId], isResourceBuilder));
        }

        return results;
    }

    private static Dictionary<string, List<AtsCapabilityInfo>> GroupCapabilitiesByTarget(
        IReadOnlyList<AtsCapabilityInfo> capabilities)
    {
        var result = new Dictionary<string, List<AtsCapabilityInfo>>(StringComparer.Ordinal);

        foreach (var capability in capabilities)
        {
            if (string.IsNullOrEmpty(capability.TargetTypeId))
            {
                continue;
            }

            var targetTypes = capability.ExpandedTargetTypes.Count > 0
                ? capability.ExpandedTargetTypes
                : capability.TargetType is not null
                    ? [capability.TargetType]
                    : [];

            foreach (var targetType in targetTypes)
            {
                if (targetType.TypeId is null)
                {
                    continue;
                }

                if (!result.TryGetValue(targetType.TypeId, out var list))
                {
                    list = new List<AtsCapabilityInfo>();
                    result[targetType.TypeId] = list;
                }
                list.Add(capability);
            }
        }

        return result;
    }

    private static HashSet<string> CollectListAndDictTypeIds(IReadOnlyList<AtsCapabilityInfo> capabilities)
    {
        var typeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            AddListOrDictTypeIfNeeded(typeIds, capability.TargetType);
            AddListOrDictTypeIfNeeded(typeIds, capability.ReturnType);
            foreach (var parameter in capability.Parameters)
            {
                AddListOrDictTypeIfNeeded(typeIds, parameter.Type);
                if (parameter.IsCallback && parameter.CallbackParameters is not null)
                {
                    foreach (var callbackParam in parameter.CallbackParameters)
                    {
                        AddListOrDictTypeIfNeeded(typeIds, callbackParam.Type);
                    }
                }
            }
        }

        return typeIds;
    }

    private string MapTypeRefToRust(AtsTypeRef? typeRef, bool isOptional)
    {
        if (typeRef is null)
        {
            return "Value";
        }

        if (typeRef.TypeId == AtsConstants.ReferenceExpressionTypeId)
        {
            return isOptional ? "Option<ReferenceExpression>" : "ReferenceExpression";
        }

        var baseType = typeRef.Category switch
        {
            AtsTypeCategory.Primitive => MapPrimitiveType(typeRef.TypeId),
            AtsTypeCategory.Enum => MapEnumType(typeRef.TypeId),
            AtsTypeCategory.Handle => MapHandleType(typeRef.TypeId),
            AtsTypeCategory.Dto => MapDtoType(typeRef.TypeId),
            AtsTypeCategory.Callback => "Box<dyn Fn(Vec<Value>) -> Value + Send + Sync>",
            AtsTypeCategory.Array => $"Vec<{MapTypeRefToRust(typeRef.ElementType, false)}>",
            AtsTypeCategory.List => typeRef.IsReadOnly
                ? $"Vec<{MapTypeRefToRust(typeRef.ElementType, false)}>"
                : $"AspireList<{MapTypeRefToRust(typeRef.ElementType, false)}>",
            AtsTypeCategory.Dict => typeRef.IsReadOnly
                ? $"HashMap<{MapTypeRefToRust(typeRef.KeyType, false)}, {MapTypeRefToRust(typeRef.ValueType, false)}>"
                : $"AspireDict<{MapTypeRefToRust(typeRef.KeyType, false)}, {MapTypeRefToRust(typeRef.ValueType, false)}>",
            AtsTypeCategory.Union => "Value",
            AtsTypeCategory.Unknown => "Value",
            _ => "Value"
        };

        return isOptional ? $"Option<{baseType}>" : baseType;
    }

    /// <summary>
    /// Maps type references to Rust types for use in DTO fields.
    /// Handle types are mapped to Handle (the serializable type) instead of wrapper types.
    /// </summary>
    private string MapTypeRefToRustForDto(AtsTypeRef? typeRef, bool isOptional)
    {
        if (typeRef is null)
        {
            return "Value";
        }

        if (typeRef.TypeId == AtsConstants.ReferenceExpressionTypeId)
        {
            return isOptional ? "Option<ReferenceExpression>" : "ReferenceExpression";
        }

        var baseType = typeRef.Category switch
        {
            AtsTypeCategory.Primitive => MapPrimitiveType(typeRef.TypeId),
            AtsTypeCategory.Enum => MapEnumType(typeRef.TypeId),
            // Use Handle directly for handle types in DTOs since Handle implements Serialize/Deserialize
            AtsTypeCategory.Handle => "Handle",
            AtsTypeCategory.Dto => MapDtoType(typeRef.TypeId),
            AtsTypeCategory.Callback => "Value", // Callbacks can't be serialized in DTOs
            AtsTypeCategory.Array => $"Vec<{MapTypeRefToRustForDto(typeRef.ElementType, false)}>",
            AtsTypeCategory.List => $"Vec<{MapTypeRefToRustForDto(typeRef.ElementType, false)}>",
            AtsTypeCategory.Dict => $"HashMap<{MapTypeRefToRustForDto(typeRef.KeyType, false)}, {MapTypeRefToRustForDto(typeRef.ValueType, false)}>",
            AtsTypeCategory.Union => "Value",
            AtsTypeCategory.Unknown => "Value",
            _ => "Value"
        };

        return isOptional ? $"Option<{baseType}>" : baseType;
    }

    private string MapHandleType(string typeId) =>
        _structNames.TryGetValue(typeId, out var name) ? name : "Handle";

    private string MapDtoType(string typeId) =>
        _dtoNames.TryGetValue(typeId, out var name) ? name : "HashMap<String, Value>";

    private string MapEnumType(string typeId) =>
        _enumNames.TryGetValue(typeId, out var name) ? name : "String";

    private static string MapPrimitiveType(string typeId) => typeId switch
    {
        AtsConstants.String or AtsConstants.Char => "String",
        AtsConstants.Number => "f64",
        AtsConstants.Boolean => "bool",
        AtsConstants.Void => "()",
        AtsConstants.Any => "Value",
        AtsConstants.DateTime or AtsConstants.DateTimeOffset or
        AtsConstants.DateOnly or AtsConstants.TimeOnly => "String",
        AtsConstants.TimeSpan => "f64",
        AtsConstants.Guid or AtsConstants.Uri => "String",
        AtsConstants.CancellationToken => "CancellationToken",
        _ => "Value"
    };

    // Maps parameter types to more idiomatic Rust types (e.g., &str instead of String)
    private string MapParameterTypeToRust(AtsTypeRef? typeRef, bool isOptional)
    {
        if (typeRef is null)
        {
            return "Value";
        }

        // For primitives that are strings, use &str for parameters (more idiomatic)
        if (typeRef.Category == AtsTypeCategory.Primitive)
        {
            var baseType = typeRef.TypeId switch
            {
                AtsConstants.String or AtsConstants.Char => "&str",
                AtsConstants.Number => "f64",
                AtsConstants.Boolean => "bool",
                AtsConstants.Void => "()",
                AtsConstants.Any => "&Value",
                AtsConstants.DateTime or AtsConstants.DateTimeOffset or
                AtsConstants.DateOnly or AtsConstants.TimeOnly => "&str",
                AtsConstants.TimeSpan => "f64",
                AtsConstants.Guid or AtsConstants.Uri => "&str",
                AtsConstants.CancellationToken => "&CancellationToken",
                _ => "&Value"
            };
            return isOptional ? $"Option<{baseType}>" : baseType;
        }

        // For arrays/lists of strings, use Vec<String> since we need owned values
        // For other types, use the standard mapping
        return MapTypeRefToRust(typeRef, isOptional);
    }

    private static bool IsHandleType(AtsTypeRef? typeRef) =>
        typeRef?.Category == AtsTypeCategory.Handle
        && typeRef.TypeId != AtsConstants.ReferenceExpressionTypeId
        && !IsCancellationTokenTypeId(typeRef.TypeId);

    private static bool IsCollectionOfHandleTypes(AtsTypeRef? typeRef) =>
        typeRef is not null
        && (typeRef.Category == AtsTypeCategory.Array || (typeRef.Category == AtsTypeCategory.List && typeRef.IsReadOnly))
        && typeRef.ElementType is not null
        && IsHandleType(typeRef.ElementType);

    private static bool IsAspireListOrDictParam(AtsTypeRef? typeRef) =>
        typeRef is not null
        && ((typeRef.Category == AtsTypeCategory.List && !typeRef.IsReadOnly)
            || (typeRef.Category == AtsTypeCategory.Dict && !typeRef.IsReadOnly));

    private static bool IsCancellationToken(AtsParameterInfo parameter) =>
        IsCancellationTokenTypeId(parameter.Type?.TypeId);

    private static bool IsCancellationTokenTypeId(string? typeId) =>
        string.Equals(typeId, AtsConstants.CancellationToken, StringComparison.Ordinal)
        || (typeId?.EndsWith("/System.Threading.CancellationToken", StringComparison.Ordinal) ?? false);

    private static void AddHandleTypeIfNeeded(HashSet<string> handleTypeIds, AtsTypeRef? typeRef)
    {
        if (typeRef is null)
        {
            return;
        }

        // Skip ReferenceExpression and CancellationToken - they're defined in base.rs/transport.rs
        if (typeRef.TypeId == AtsConstants.ReferenceExpressionTypeId
            || IsCancellationTokenTypeId(typeRef.TypeId))
        {
            return;
        }

        if (typeRef.Category == AtsTypeCategory.Handle)
        {
            handleTypeIds.Add(typeRef.TypeId);
        }
    }

    private static void AddListOrDictTypeIfNeeded(HashSet<string> typeIds, AtsTypeRef? typeRef)
    {
        if (typeRef is null)
        {
            return;
        }

        if (typeRef.Category == AtsTypeCategory.List || typeRef.Category == AtsTypeCategory.Dict)
        {
            if (!typeRef.IsReadOnly)
            {
                typeIds.Add(typeRef.TypeId);
            }
        }
    }

    private string CreateStructName(string typeId)
    {
        var baseName = ExtractTypeName(typeId);
        var name = SanitizeIdentifier(baseName);
        if (_structNames.Values.Contains(name, StringComparer.Ordinal))
        {
            var assemblyName = typeId.Split('/')[0];
            var assemblyPrefix = SanitizeIdentifier(assemblyName);
            name = $"{assemblyPrefix}{name}";
        }

        var counter = 1;
        var candidate = name;
        while (_structNames.Values.Contains(candidate, StringComparer.Ordinal))
        {
            counter++;
            candidate = $"{name}{counter}";
        }

        return candidate;
    }

    private static string ExtractTypeName(string typeId)
    {
        var slashIndex = typeId.IndexOf('/', StringComparison.Ordinal);
        var typeName = slashIndex >= 0 ? typeId[(slashIndex + 1)..] : typeId;
        var lastDot = typeName.LastIndexOf('.');
        var plusIndex = typeName.LastIndexOf('+');
        var delimiterIndex = Math.Max(lastDot, plusIndex);
        return delimiterIndex >= 0 ? typeName[(delimiterIndex + 1)..] : typeName;
    }

    private static string SanitizeIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "_";
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        if (!char.IsLetter(builder[0]) && builder[0] != '_')
        {
            builder.Insert(0, '_');
        }

        var sanitized = builder.ToString();
        return s_rustKeywords.Contains(sanitized) ? $"r#{sanitized}" : sanitized;
    }

    /// <summary>
    /// Converts a name to PascalCase for Rust type names.
    /// </summary>
    private static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }
        if (char.IsUpper(name[0]))
        {
            return name;
        }
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Converts a name to snake_case for Rust identifiers.
    /// </summary>
    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        return SanitizeIdentifier(JsonNamingPolicy.SnakeCaseLower.ConvertName(name));
    }

    private void WriteLine(string value = "")
    {
        _writer.WriteLine(value);
    }

    private sealed record RustHandleType(string TypeId, string StructName, bool IsResourceBuilder);
}
