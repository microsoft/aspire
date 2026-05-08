// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Aspire.TypeSystem;

namespace Aspire.Hosting.RemoteHost.Ats;

/// <summary>
/// Shared marshalling logic for converting between .NET objects and JSON in the ATS type system.
/// </summary>
internal sealed class AtsMarshaller
{
    private readonly HandleRegistry _handles;
    private readonly AtsContext _context;
    private readonly CancellationTokenRegistry _cancellationTokenRegistry;
    private readonly Lazy<AtsCallbackProxyFactory> _callbackProxyFactory;

    /// <summary>
    /// Creates a new marshaller instance.
    /// </summary>
    /// <param name="handles">The handle registry for marshalling handles.</param>
    /// <param name="context">The ATS context for type classification.</param>
    /// <param name="cancellationTokenRegistry">The cancellation token registry.</param>
    /// <param name="callbackProxyFactory">Lazy callback proxy factory to break circular dependency.</param>
    public AtsMarshaller(
        HandleRegistry handles,
        AtsContext context,
        CancellationTokenRegistry cancellationTokenRegistry,
        Lazy<AtsCallbackProxyFactory> callbackProxyFactory)
    {
        _handles = handles;
        _context = context;
        _cancellationTokenRegistry = cancellationTokenRegistry;
        _callbackProxyFactory = callbackProxyFactory;
    }

    /// <summary>
    /// Context for unmarshalling operations, providing error context info.
    /// </summary>
    internal sealed class UnmarshalContext
    {
        public string? CapabilityId { get; init; }
        public string? ParameterName { get; init; }
    }

    // NOTE: ReferenceHandler.IgnoreCycles prevents JsonException when serializing DTOs
    // with circular references (e.g. parent/child back-references). Cycles are broken by
    // writing null for back-edges. This means the TypeScript side will receive null for
    // cycle-broken properties. During DTO writeback, those null values will overwrite the
    // original non-null references on the C# side — this is intentional, since TypeScript
    // cannot represent object cycles and the writeback reflects what TypeScript observed.
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>
    /// Simple types that can be serialized directly as JSON primitives.
    /// </summary>
    private static readonly HashSet<Type> s_simpleTypes =
    [
        typeof(string),
        typeof(bool),
        typeof(int),
        typeof(long),
        typeof(double),
        typeof(float),
        typeof(decimal),
        typeof(byte),
        typeof(short),
        typeof(uint),
        typeof(ulong),
        typeof(ushort),
        typeof(sbyte),
        typeof(char),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(DateOnly),
        typeof(TimeOnly),
        typeof(TimeSpan),
        typeof(Guid),
        typeof(Uri)
    ];

    /// <summary>
    /// Gets the shared JSON serializer options.
    /// </summary>
    public static JsonSerializerOptions JsonOptions => s_jsonOptions;

    /// <summary>
    /// Checks if a type is a simple/primitive type that can be serialized directly.
    /// </summary>
    public static bool IsSimpleType(Type type)
    {
        if (s_simpleTypes.Contains(type))
        {
            return true;
        }

        // Enums are serialized as strings
        if (type.IsEnum)
        {
            return true;
        }

        // Nullable<T> where T is simple
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return IsSimpleType(underlying);
        }

        return false;
    }

    /// <summary>
    /// Marshals a .NET object to JSON for sending to the guest using type metadata.
    /// Uses the scanner's type classification instead of runtime type inspection.
    /// </summary>
    /// <param name="value">The value to marshal.</param>
    /// <param name="typeRef">The type metadata from the scanner.</param>
    /// <returns>The JSON representation, or null if the value is null.</returns>
    public JsonNode? MarshalToJson(object? value, AtsTypeRef typeRef)
    {
        if (value == null)
        {
            return null;
        }

        if (typeRef.TypeId == AtsConstants.CancellationToken && value is CancellationToken cancellationToken)
        {
            return SerializeCancellationToken(cancellationToken);
        }

        // Handle 'any' type - fall back to runtime type inspection
        if (typeRef.TypeId == TypeSystem.AtsConstants.Any)
        {
            return MarshalToJson(value);
        }

        return typeRef.Category switch
        {
            AtsTypeCategory.Handle => _handles.Marshal(value, typeRef.TypeId),
            AtsTypeCategory.Primitive => SerializePrimitive(value),
            AtsTypeCategory.Enum => JsonValue.Create(value.ToString()),
            AtsTypeCategory.Dto => SerializeDto(value),
            AtsTypeCategory.Array => SerializeArray(value, typeRef.ElementType),
            AtsTypeCategory.List => _handles.Marshal(value, typeRef.TypeId),
            AtsTypeCategory.Dict => _handles.Marshal(value, typeRef.TypeId),
            _ => throw new InvalidOperationException($"Unknown type category: {typeRef.Category}")
        };
    }

    /// <summary>
    /// Marshals a .NET object to JSON for sending to the guest using a declared CLR type.
    /// </summary>
    /// <param name="value">The value to marshal.</param>
    /// <param name="declaredType">The declared type that should be exposed to the guest.</param>
    /// <returns>The JSON representation, or null if the value is null.</returns>
    public JsonNode? MarshalToJson(object? value, Type declaredType)
    {
        if (value == null)
        {
            return null;
        }

        if (declaredType == typeof(object))
        {
            return MarshalToJson(value);
        }

        if (declaredType == typeof(CancellationToken))
        {
            return SerializeCancellationToken((CancellationToken)value);
        }

        var typeId = AtsTypeMapping.DeriveTypeId(declaredType);
        var category = _context.GetCategory(declaredType);

        return category switch
        {
            AtsTypeCategory.Primitive => SerializePrimitive(value),
            AtsTypeCategory.Enum => JsonValue.Create(value.ToString()),
            AtsTypeCategory.Dto => SerializeDto(value),
            AtsTypeCategory.Array => SerializeArray(value, CreateElementTypeRef(declaredType)),
            AtsTypeCategory.List => _handles.Marshal(value, typeId),
            AtsTypeCategory.Dict => _handles.Marshal(value, typeId),
            AtsTypeCategory.Handle => _handles.Marshal(value, typeId),
            _ => _handles.Marshal(value, typeId)
        };
    }

    private static JsonNode? SerializePrimitive(object value)
    {
        var type = value.GetType();

        // TimeSpan is serialized as total milliseconds for easy JS interop
        if (type == typeof(TimeSpan))
        {
            return JsonValue.Create(((TimeSpan)value).TotalMilliseconds);
        }

        // DateOnly is serialized as ISO date string (yyyy-MM-dd)
        if (type == typeof(DateOnly))
        {
            return JsonValue.Create(((DateOnly)value).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }

        // TimeOnly is serialized as ISO time string (HH:mm:ss.fffffff)
        if (type == typeof(TimeOnly))
        {
            return JsonValue.Create(((TimeOnly)value).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }

        return JsonValue.Create(value);
    }

    private static JsonNode? SerializeDto(object value)
    {
        var json = JsonSerializer.Serialize(value, s_jsonOptions);
        return JsonNode.Parse(json);
    }

    private JsonNode? SerializeArray(object value, AtsTypeRef? elementType)
    {
        var jsonArray = new JsonArray();
        foreach (var item in (IEnumerable)value)
        {
            if (elementType != null)
            {
                jsonArray.Add(MarshalToJson(item, elementType));
            }
            else
            {
                jsonArray.Add(MarshalToJson(item));
            }
        }
        return jsonArray;
    }

    private AtsTypeRef? CreateElementTypeRef(Type declaredType)
    {
        if (declaredType.IsArray)
        {
            return CreateTypeRef(declaredType.GetElementType()!);
        }

        if (declaredType.IsGenericType)
        {
            var genericTypeDefinition = declaredType.GetGenericTypeDefinition();
            if (genericTypeDefinition == typeof(IReadOnlyList<>)
                || genericTypeDefinition == typeof(IReadOnlyCollection<>)
                || genericTypeDefinition == typeof(IEnumerable<>))
            {
                return CreateTypeRef(declaredType.GetGenericArguments()[0]);
            }
        }

        return null;
    }

    private AtsTypeRef CreateTypeRef(Type type)
    {
        return new AtsTypeRef
        {
            TypeId = AtsTypeMapping.DeriveTypeId(type),
            ClrType = type,
            Category = _context.GetCategory(type)
        };
    }

    /// <summary>
    /// Marshals a .NET object to JSON for sending to the guest.
    /// Uses runtime type inspection based on scanned AtsContext.
    /// </summary>
    /// <param name="value">The value to marshal.</param>
    /// <returns>The JSON representation, or null if the value is null.</returns>
    public JsonNode? MarshalToJson(object? value)
    {
        if (value == null)
        {
            return null;
        }

        var type = value.GetType();
        if (type == typeof(CancellationToken))
        {
            return SerializeCancellationToken((CancellationToken)value);
        }

        var category = _context.GetCategory(type);

        return category switch
        {
            AtsTypeCategory.Primitive => SerializePrimitive(value),
            AtsTypeCategory.Enum => JsonValue.Create(value.ToString()),
            AtsTypeCategory.Dto => SerializeDto(value),
            AtsTypeCategory.Array => SerializeArrayRuntime(value),
            AtsTypeCategory.List => MarshalListHandle(value, type),
            AtsTypeCategory.Dict => MarshalDictHandle(value, type),
            AtsTypeCategory.Handle => _handles.Marshal(value, AtsTypeMapping.DeriveTypeId(type)),
            _ => _handles.Marshal(value, AtsTypeMapping.DeriveTypeId(type))
        };
    }

    private JsonNode? SerializeArrayRuntime(object value)
    {
        var jsonArray = new JsonArray();
        foreach (var item in (IEnumerable)value)
        {
            jsonArray.Add(MarshalToJson(item));
        }
        return jsonArray;
    }

    private JsonNode? SerializeCancellationToken(CancellationToken cancellationToken)
    {
        if (cancellationToken == CancellationToken.None)
        {
            return null;
        }

        var (tokenId, _) = _cancellationTokenRegistry.CreateLinked(cancellationToken);
        return JsonValue.Create(tokenId);
    }

    private JsonNode? MarshalListHandle(object value, Type type)
    {
        if (type.IsGenericType)
        {
            var genericArgs = type.GetGenericArguments();
            if (genericArgs.Length == 1)
            {
                var typeId = $"Aspire.Hosting/List<{genericArgs[0].Name}>";
                return _handles.Marshal(value, typeId);
            }
        }
        // Fallback for non-generic lists
        return _handles.Marshal(value, AtsTypeMapping.DeriveTypeId(type));
    }

    private JsonNode? MarshalDictHandle(object value, Type type)
    {
        if (type.IsGenericType)
        {
            var genericArgs = type.GetGenericArguments();
            if (genericArgs.Length == 2)
            {
                var typeId = $"Aspire.Hosting/Dict<{genericArgs[0].Name},{genericArgs[1].Name}>";
                return _handles.Marshal(value, typeId);
            }
        }
        // Fallback for non-generic dicts
        return _handles.Marshal(value, AtsTypeMapping.DeriveTypeId(type));
    }

    /// <summary>
    /// Unmarshals a JSON node to a .NET object of the specified type.
    /// Handles: primitives, arrays, lists, dictionaries, [AspireDto] types, and delegate callbacks.
    /// </summary>
    /// <param name="node">The JSON node to unmarshal.</param>
    /// <param name="targetType">The target .NET type.</param>
    /// <param name="context">The unmarshalling context with registries and error info.</param>
    /// <returns>The unmarshalled .NET object.</returns>
    public object? UnmarshalFromJson(JsonNode? node, Type targetType, UnmarshalContext context)
    {
        if (node == null)
        {
            if (targetType == typeof(CancellationToken))
            {
                return CancellationToken.None;
            }

            return null;
        }

        var capabilityId = context.CapabilityId ?? "unknown";
        var paramName = context.ParameterName ?? "unknown";

        // Check for handle reference
        var handleRef = HandleRef.FromJsonNode(node);
        if (handleRef != null)
        {
            if (!_handles.TryGet(handleRef.HandleId, out var handleObj, out _))
            {
                throw CapabilityException.HandleNotFound(handleRef.HandleId, capabilityId);
            }
            return handleObj;
        }

        // Check for reference expression (value or conditional)
        // Value format: { "$expr": { "format": "...", "valueProviders": [...] } }
        // Conditional format: { "$expr": { "condition": <handle>, "whenTrue": <$expr>, "whenFalse": <$expr> } }
        var exprRef = ReferenceExpressionRef.FromJsonNode(node);
        if (exprRef != null)
        {
            return exprRef.ToReferenceExpression(_handles, capabilityId, paramName);
        }

        // Handle callbacks - any delegate type is treated as a callback
        if (typeof(Delegate).IsAssignableFrom(targetType))
        {
            // Callback ID is passed as a string
            if (node is JsonValue callbackValue && callbackValue.TryGetValue<string>(out var callbackId))
            {
                Delegate? proxy;
                try
                {
                    proxy = _callbackProxyFactory.Value.CreateProxy(callbackId, targetType);
                }
                catch (Exception ex) when (ex is not CapabilityException)
                {
                    throw CapabilityException.InvalidArgument(
                        capabilityId, paramName,
                        $"Callback proxy factory not available: {ex.Message}");
                }
                if (proxy == null)
                {
                    throw CapabilityException.InvalidArgument(
                        capabilityId, paramName,
                        $"Failed to create callback proxy for type '{targetType.Name}'");
                }
                return proxy;
            }
            else
            {
                throw CapabilityException.InvalidArgument(
                    capabilityId, paramName,
                    "Callback parameter must be a string callback ID");
            }
        }

        // Handle CancellationToken - token ID is passed as a string, or null/missing for CancellationToken.None
        if (targetType == typeof(CancellationToken))
        {
            // Token ID as string - get or create a token for this ID
            if (node is JsonValue tokenValue && tokenValue.TryGetValue<string>(out var tokenId) && !string.IsNullOrEmpty(tokenId))
            {
                // Get or create a CancellationToken for this guest-provided ID
                // The guest can later cancel this token by calling cancelToken RPC
                return _cancellationTokenRegistry.GetOrCreate(tokenId);
            }
            // null, empty, or not a string means no cancellation
            return CancellationToken.None;
        }

        try
        {
            // Handle primitives
            if (node is JsonValue value)
            {
                return ConvertPrimitive(value, targetType);
            }

            // Handle JsonArray -> Array or List<T>
            if (node is JsonArray array)
            {
                // T[] arrays
                if (targetType.IsArray)
                {
                    var elementType = targetType.GetElementType()!;
                    var converted = Array.CreateInstance(elementType, array.Count);
                    for (int i = 0; i < array.Count; i++)
                    {
                        var elementContext = new UnmarshalContext
                        {
                            CapabilityId = context.CapabilityId,
                            ParameterName = $"{paramName}[{i}]"
                        };
                        converted.SetValue(UnmarshalFromJson(array[i], elementType, elementContext), i);
                    }
                    return converted;
                }

                // List<T> or IList<T>
                if (targetType.IsGenericType)
                {
                    var genericDef = targetType.GetGenericTypeDefinition();
                    if (genericDef == typeof(List<>) || genericDef == typeof(IList<>) || genericDef == typeof(IEnumerable<>) || genericDef == typeof(ICollection<>) || genericDef == typeof(IReadOnlyList<>) || genericDef == typeof(IReadOnlyCollection<>))
                    {
                        var elementType = targetType.GetGenericArguments()[0];
                        var listType = typeof(List<>).MakeGenericType(elementType);
                        var list = (IList)Activator.CreateInstance(listType)!;
                        for (int i = 0; i < array.Count; i++)
                        {
                            var elementContext = new UnmarshalContext
                            {
                                CapabilityId = context.CapabilityId,
                                ParameterName = $"{paramName}[{i}]"
                            };
                            list.Add(UnmarshalFromJson(array[i], elementType, elementContext));
                        }
                        return list;
                    }
                }
            }

            // Handle JsonObject -> Dictionary<string, T> or DTO
            if (node is JsonObject jsonObj)
            {
                // Dictionary<string, T> or IDictionary<string, T>
                if (targetType.IsGenericType)
                {
                    var genericDef = targetType.GetGenericTypeDefinition();
                    if (genericDef == typeof(Dictionary<,>) || genericDef == typeof(IDictionary<,>) || genericDef == typeof(IReadOnlyDictionary<,>))
                    {
                        var genericArgs = targetType.GetGenericArguments();
                        if (genericArgs[0] == typeof(string))
                        {
                            var valueType = genericArgs[1];
                            var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType);
                            var dict = (IDictionary)Activator.CreateInstance(dictType)!;
                            foreach (var prop in jsonObj)
                            {
                                var valueContext = new UnmarshalContext
                                {
                                    CapabilityId = context.CapabilityId,
                                    ParameterName = $"{paramName}[{prop.Key}]"
                                };
                                dict[prop.Key] = UnmarshalFromJson(prop.Value, valueType, valueContext);
                            }
                            return dict;
                        }
                    }
                }

                // DTOs - must have [AspireDto] attribute
                if (!AttributeDataReader.HasAspireDtoData(targetType))
                {
                    throw CapabilityException.InvalidArgument(
                        capabilityId, paramName,
                        $"Parameter type '{targetType.Name}' must have [AspireDto] attribute to be deserialized from JSON");
                }
                return JsonSerializer.Deserialize(jsonObj.ToJsonString(), targetType, s_jsonOptions);
            }

            return null;
        }
        catch (CapabilityException)
        {
            throw;
        }
        catch (Exception ex) when (IsTypeMismatchException(ex))
        {
            throw CapabilityException.TypeMismatch(
                capabilityId, paramName, DescribeType(targetType), DescribeJsonNode(node));
        }
        catch (JsonException ex)
        {
            throw CapabilityException.InvalidArgument(
                capabilityId, paramName, $"Failed to deserialize '{paramName}' as {DescribeType(targetType)}: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if an exception indicates a type mismatch.
    /// </summary>
    private static bool IsTypeMismatchException(Exception ex)
    {
        // Check for common type mismatch patterns in exception messages
        var message = ex.Message;
        return message.Contains("cannot be converted") ||
            message.Contains("could not be converted") ||
            message.Contains("is not assignable") ||
            message.Contains("type mismatch", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Describes a JSON node's type for error reporting.
    /// </summary>
    private static string DescribeJsonNode(JsonNode? node)
    {
        return node switch
        {
            null => "null",
            JsonValue v when v.TryGetValue<string>(out _) => "string",
            JsonValue v when v.TryGetValue<bool>(out _) => "bool",
            JsonValue v when v.TryGetValue<long>(out _) => "number",
            JsonValue v when v.TryGetValue<double>(out _) => "number",
            JsonValue => "value",
            JsonArray => "array",
            JsonObject obj when obj.ContainsKey("$handle") => "handle",
            JsonObject => "object",
            _ => node.GetType().Name
        };
    }

    /// <summary>
    /// Describes a .NET type in a human-readable way for error messages.
    /// Formats Nullable&lt;T&gt; as the underlying type.
    /// </summary>
    private static string DescribeType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            return underlying.ToString();
        }
        return type.ToString();
    }

    // IEEE 754 doubles can exactly represent integers only through 2^53 - 1.
    private const double MaxSafeIntegerDouble = 9_007_199_254_740_991d;

    private static decimal ReadDecimalValue(JsonValue value, Type targetType)
    {
        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        // Prefer decimal here because this helper is only used for decimal targets.
        if (value.TryGetValue<decimal>(out var decimalValue))
        {
            return decimalValue;
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }

        if (value.TryGetValue<ulong>(out var ulongValue))
        {
            return ulongValue;
        }

        if (value.TryGetValue<double>(out var doubleValue) && double.IsFinite(doubleValue))
        {
            try
            {
                return (decimal)doubleValue;
            }
            catch (OverflowException)
            {
                throw new InvalidCastException($"Value cannot be converted to {targetType.Name}.");
            }
        }

        throw new InvalidCastException($"Value cannot be converted to {targetType.Name}.");
    }

    private static decimal ReadIntegralNumericValue(JsonValue value, Type targetType, decimal minValue, decimal maxValue)
    {
        if (value.TryGetValue<int>(out var intValue))
        {
            return ValidateIntegralNumericValue(intValue, targetType, minValue, maxValue);
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return ValidateIntegralNumericValue(longValue, targetType, minValue, maxValue);
        }

        if (value.TryGetValue<ulong>(out var ulongValue))
        {
            return ValidateIntegralNumericValue(ulongValue, targetType, minValue, maxValue);
        }

        if (value.TryGetValue<decimal>(out var decimalValue))
        {
            return ValidateIntegralNumericValue(decimalValue, targetType, minValue, maxValue);
        }

        if (value.TryGetValue<double>(out var doubleValue) && double.IsFinite(doubleValue))
        {
            if (Math.Abs(doubleValue) > MaxSafeIntegerDouble)
            {
                throw new InvalidCastException($"Value cannot be converted to {targetType.Name}.");
            }

            return ValidateIntegralNumericValue((decimal)doubleValue, targetType, minValue, maxValue);
        }

        throw new InvalidCastException($"Value cannot be converted to {targetType.Name}.");
    }

    private static decimal ValidateIntegralNumericValue(decimal numericValue, Type targetType, decimal minValue, decimal maxValue)
    {
        if (decimal.Truncate(numericValue) != numericValue || numericValue < minValue || numericValue > maxValue)
        {
            throw new InvalidCastException($"Value cannot be converted to {targetType.Name}.");
        }

        return numericValue;
    }

    /// <summary>
    /// Converts a JSON primitive to the target .NET type.
    /// </summary>
    public static object? ConvertPrimitive(JsonValue value, Type targetType)
    {
        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // When target type is object (union parameter), infer from JSON value
        if (underlyingType == typeof(object))
        {
            if (value.TryGetValue<string>(out var s))
            {
                return s;
            }
            if (value.TryGetValue<bool>(out var b))
            {
                return b;
            }
            if (value.TryGetValue<long>(out var l))
            {
                return l;
            }
            if (value.TryGetValue<double>(out var d))
            {
                return d;
            }
            return value.ToJsonString();
        }

        // Handle enums - they come as string names
        if (underlyingType.IsEnum)
        {
            if (value.TryGetValue<string>(out var enumName))
            {
                return Enum.Parse(underlyingType, enumName, ignoreCase: true);
            }
            // Also support numeric enum values
            if (value.TryGetValue<int>(out var enumValue))
            {
                return Enum.ToObject(underlyingType, enumValue);
            }
        }

        if (underlyingType == typeof(string))
        {
            return value.GetValue<string>();
        }

        if (underlyingType == typeof(bool))
        {
            return value.GetValue<bool>();
        }

        if (underlyingType == typeof(int))
        {
            return decimal.ToInt32(ReadIntegralNumericValue(value, underlyingType, int.MinValue, int.MaxValue));
        }

        if (underlyingType == typeof(long))
        {
            return decimal.ToInt64(ReadIntegralNumericValue(value, underlyingType, long.MinValue, long.MaxValue));
        }

        if (underlyingType == typeof(byte))
        {
            return decimal.ToByte(ReadIntegralNumericValue(value, underlyingType, byte.MinValue, byte.MaxValue));
        }

        if (underlyingType == typeof(short))
        {
            return decimal.ToInt16(ReadIntegralNumericValue(value, underlyingType, short.MinValue, short.MaxValue));
        }

        if (underlyingType == typeof(uint))
        {
            return decimal.ToUInt32(ReadIntegralNumericValue(value, underlyingType, uint.MinValue, uint.MaxValue));
        }

        if (underlyingType == typeof(ulong))
        {
            return decimal.ToUInt64(ReadIntegralNumericValue(value, underlyingType, ulong.MinValue, ulong.MaxValue));
        }

        if (underlyingType == typeof(ushort))
        {
            return decimal.ToUInt16(ReadIntegralNumericValue(value, underlyingType, ushort.MinValue, ushort.MaxValue));
        }

        if (underlyingType == typeof(sbyte))
        {
            return decimal.ToSByte(ReadIntegralNumericValue(value, underlyingType, sbyte.MinValue, sbyte.MaxValue));
        }

        if (underlyingType == typeof(double))
        {
            return value.GetValue<double>();
        }

        if (underlyingType == typeof(float))
        {
            return value.TryGetValue<float>(out var floatValue) ? floatValue : (float)value.GetValue<double>();
        }

        if (underlyingType == typeof(decimal))
        {
            return ReadDecimalValue(value, underlyingType);
        }

        // TimeSpan: accept milliseconds (number) or parseable string
        if (underlyingType == typeof(TimeSpan))
        {
            if (value.TryGetValue<double>(out var ms))
            {
                return TimeSpan.FromMilliseconds(ms);
            }
            if (value.TryGetValue<string>(out var str))
            {
                // Support standard TimeSpan format (e.g., "01:30:00") or ISO 8601 duration
                return TimeSpan.Parse(str, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // DateOnly: accept ISO date string (yyyy-MM-dd)
        if (underlyingType == typeof(DateOnly))
        {
            if (value.TryGetValue<string>(out var str))
            {
                return DateOnly.Parse(str, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // TimeOnly: accept ISO time string (HH:mm:ss.fffffff)
        if (underlyingType == typeof(TimeOnly))
        {
            if (value.TryGetValue<string>(out var str))
            {
                return TimeOnly.Parse(str, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return JsonSerializer.Deserialize(value.ToJsonString(), targetType, s_jsonOptions);
    }

    /// <summary>
    /// Checks if a type is a DTO type (marked with [AspireDto] and registered in the context).
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <returns><c>true</c> if the type is a DTO type; otherwise, <c>false</c>.</returns>
    public bool IsDtoType(Type type)
    {
        return _context.GetCategory(type) == AtsTypeCategory.Dto;
    }

    /// <summary>
    /// Applies property values from a JSON object to an existing DTO instance.
    /// Used for applying mutations made in callbacks back to the original objects.
    /// Only writable properties and public fields whose types can be deserialized
    /// from the JSON are updated. Properties with complex types that lack a
    /// parameterless constructor (e.g. <c>EndpointReference</c>) are silently skipped.
    /// </summary>
    /// <param name="source">The JSON object containing the updated values.</param>
    /// <param name="target">The existing DTO instance to update.</param>
    /// <param name="targetType">The CLR type of the target object.</param>
    /// <remarks>
    /// <para>
    /// Nested DTO properties are deserialized as new instances, not patched in-place.
    /// External references to the original nested object will not see the mutations.
    /// </para>
    /// <para>
    /// When <c>ReferenceHandler.IgnoreCycles</c> is active, cycle-broken properties
    /// arrive as <c>null</c> and will overwrite the original non-null reference.
    /// </para>
    /// </remarks>
    // Instance method for consistency with IsDtoType and future extensibility.
#pragma warning disable CA1822 // Member does not access instance data
    public void ApplyDtoProperties(JsonObject source, object target, Type targetType)
#pragma warning restore CA1822
    {
        // Use JsonTypeInfo metadata instead of raw reflection so that
        // [JsonPropertyName], [JsonIgnore], the naming policy, and field
        // inclusion configuration are all respected automatically.
        var typeInfo = s_jsonOptions.GetTypeInfo(targetType);

        foreach (var prop in typeInfo.Properties)
        {
            if (prop.Set is null)
            {
                continue;
            }

            if (!source.TryGetPropertyValue(prop.Name, out var jsonValue))
            {
                continue;
            }

            try
            {
                prop.Set(target, jsonValue.Deserialize(prop.PropertyType, s_jsonOptions));
            }
            catch (NotSupportedException)
            {
                // Silently skip properties whose types can't be deserialized by
                // System.Text.Json (e.g. EndpointReference with no parameterless
                // constructor). This is expected for DTO types that carry complex
                // navigation properties alongside simple writable data.
            }
            catch (JsonException)
            {
                // Silently skip properties where the JSON value is incompatible
                // with the CLR property type (e.g. string sent for an int field).
                // This is a best-effort writeback — partial updates are acceptable.
            }
        }
    }
}
