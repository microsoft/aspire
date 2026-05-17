// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aspire.Hosting.Diagnostics;
using Microsoft.Extensions.Configuration;
using StreamJsonRpc;

namespace Aspire.Hosting.Backchannel;

/// <summary>
/// Validates that backchannel request/response types follow the contract rules.
/// </summary>
[Trait("Partition", "4")]
public class BackchannelContractTests
{
    private static readonly Type[] s_requestTypes =
    [
        typeof(GetCapabilitiesRequest),
        typeof(GetAppHostInfoRequest),
        typeof(GetDashboardInfoRequest),
        typeof(GetResourcesRequest),
        typeof(WatchResourcesRequest),
        typeof(GetConsoleLogsRequest),
        typeof(CallMcpToolRequest),
        typeof(StopAppHostRequest),
        typeof(ExecuteResourceCommandRequest),
        typeof(WaitForResourceRequest),
        typeof(GetPipelineStepsRequest),
    ];

    // V2 request/response types that must follow the contract
    private static readonly Type[] s_contractTypes =
    [
        .. s_requestTypes,
        typeof(GetCapabilitiesResponse),
        typeof(BackchannelTraceContext),
        typeof(GetAppHostInfoResponse),
        typeof(GetDashboardInfoResponse),
        typeof(GetResourcesResponse),
        typeof(CallMcpToolResponse),
        typeof(McpToolContentItem),
        typeof(StopAppHostResponse),
        typeof(ExecuteResourceCommandOptions),
        typeof(ExecuteResourceCommandResponse),
        typeof(ResourceCommandArgumentValidationError),
        typeof(ExecuteResourceCommandResult),
        typeof(WaitForResourceResponse),
        typeof(RpcResourceState),
        typeof(DashboardUrlsState),
        typeof(PublishingActivity),
        typeof(PublishingActivityData),
        typeof(BackchannelPipelineSummaryItem),
        typeof(PublishingPromptInput),
        typeof(BackchannelLogEntry),
        typeof(PublishingPromptInputAnswer),
        typeof(PipelineStepInfo),
        typeof(GetPipelineStepsResponse),
        typeof(DashboardMcpConnectionInfo),
        typeof(ResourceSnapshot),
        typeof(ResourceSnapshotCommand),
        typeof(ResourceSnapshotCommandArgument),
        typeof(ResourceSnapshotUrl),
        typeof(ResourceSnapshotUrlDisplayProperties),
        typeof(ResourceSnapshotRelationship),
        typeof(ResourceSnapshotHealthReport),
        typeof(ResourceSnapshotVolume),
        typeof(ResourceSnapshotEnvironmentVariable),
        typeof(ResourceSnapshotMcpServer),
        typeof(AppHostInformation),
        typeof(ResourceLogLine),
        typeof(ResourceLogBatch),
    ];

    private static readonly Type[] s_contractEnumTypes =
    [
        typeof(CommandResultFormat),
    ];

    private static readonly Dictionary<string, (Type RequestType, Type ResponseType)> s_auxiliaryV2Contracts = new(StringComparer.Ordinal)
    {
        [nameof(AuxiliaryBackchannelRpcTarget.GetCapabilitiesAsync)] = (typeof(GetCapabilitiesRequest), typeof(GetCapabilitiesResponse)),
        [nameof(AuxiliaryBackchannelRpcTarget.GetAppHostInfoAsync)] = (typeof(GetAppHostInfoRequest), typeof(GetAppHostInfoResponse)),
        [nameof(AuxiliaryBackchannelRpcTarget.GetDashboardInfoAsync)] = (typeof(GetDashboardInfoRequest), typeof(GetDashboardInfoResponse)),
        [nameof(AuxiliaryBackchannelRpcTarget.GetResourcesAsync)] = (typeof(GetResourcesRequest), typeof(GetResourcesResponse)),
        [nameof(AuxiliaryBackchannelRpcTarget.WatchResourcesAsync)] = (typeof(WatchResourcesRequest), typeof(ResourceSnapshot)),
        [nameof(AuxiliaryBackchannelRpcTarget.GetConsoleLogsAsync)] = (typeof(GetConsoleLogsRequest), typeof(ResourceLogLine)),
        [nameof(AuxiliaryBackchannelRpcTarget.GetConsoleLogBatchesAsync)] = (typeof(GetConsoleLogsRequest), typeof(ResourceLogBatch)),
        [nameof(AuxiliaryBackchannelRpcTarget.CallMcpToolAsync)] = (typeof(CallMcpToolRequest), typeof(CallMcpToolResponse)),
        [nameof(AuxiliaryBackchannelRpcTarget.StopAsync)] = (typeof(StopAppHostRequest), typeof(StopAppHostResponse)),
        [nameof(AuxiliaryBackchannelRpcTarget.ExecuteResourceCommandAsync)] = (typeof(ExecuteResourceCommandRequest), typeof(ExecuteResourceCommandResponse)),
        [nameof(AuxiliaryBackchannelRpcTarget.WaitForResourceAsync)] = (typeof(WaitForResourceRequest), typeof(WaitForResourceResponse)),
    };

    /// <summary>
    /// Validates all backchannel contract rules:
    /// 1. All types are sealed classes
    /// 2. Properties use { get; init; } pattern (not { get; set; })
    /// 3. Required properties have 'required' modifier and are not nullable
    /// 4. Optional properties are nullable (T?) or have default values
    /// 5. No public fields allowed
    /// 6. Request/Response types follow naming convention
    /// </summary>
    [Fact]
    public void BackchannelTypes_FollowContractRules()
    {
        var errors = new StringBuilder();

        foreach (var type in s_contractTypes)
        {
            // Rule 1: Must be sealed class
            if (!type.IsClass)
            {
                errors.AppendLine($"❌ {type.Name}: Must be a class (not struct or interface)");
            }
            else if (!type.IsSealed)
            {
                errors.AppendLine($"❌ {type.Name}: Must be sealed");
            }

            // Rule 5: No public fields
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                errors.AppendLine($"❌ {type.Name}.{field.Name}: Public fields not allowed, use properties");
            }

            // Rule 6: Naming convention (skip shared payload/helper types)
            if (IsRequestResponseType(type) &&
                !type.Name.EndsWith("Request") &&
                !type.Name.EndsWith("Response") &&
                !type.Name.EndsWith("Options"))
            {
                errors.AppendLine($"❌ {type.Name}: Name should end with 'Request', 'Response', or 'Options'");
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var setMethod = prop.GetSetMethod();

                // Skip computed properties (no setter)
                if (setMethod is null)
                {
                    continue;
                }

                // Rule 2: Must use { get; init; } not { get; set; }
                var isInitOnly = setMethod.ReturnParameter
                    .GetRequiredCustomModifiers()
                    .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

                if (!isInitOnly)
                {
                    errors.AppendLine($"❌ {type.Name}.{prop.Name}: Must use {{ get; init; }} not {{ get; set; }}");
                }

                var isRequired = prop.GetCustomAttribute<RequiredMemberAttribute>() is not null;
                var nullabilityContext = new NullabilityInfoContext();
                var nullabilityInfo = nullabilityContext.Create(prop);

                if (isRequired)
                {
                    // Rule 3: Required properties should not be nullable
                    bool isNullable = prop.PropertyType.IsValueType
                        ? Nullable.GetUnderlyingType(prop.PropertyType) is not null
                        : nullabilityInfo.WriteState == NullabilityState.Nullable;

                    if (isNullable)
                    {
                        errors.AppendLine($"❌ {type.Name}.{prop.Name}: Required properties should not be nullable");
                    }
                }
                else
                {
                    // Rule 4: Optional reference types should be nullable or have defaults
                    if (!prop.PropertyType.IsValueType)
                    {
                        var isNullable = nullabilityInfo.WriteState == NullabilityState.Nullable;
                        var hasExplicitDefaultBackingField = HasExplicitDefaultBackingField(type, prop);
                        var isCollection = IsSupportedCollectionType(prop.PropertyType);

                        if (!isNullable && isCollection && !hasExplicitDefaultBackingField)
                        {
                            errors.AppendLine($"❌ {type.Name}.{prop.Name}: Optional collection properties should use an explicit backing field so explicit JSON null values deserialize as empty");
                        }
                        else if (!isNullable && !hasExplicitDefaultBackingField)
                        {
                            errors.AppendLine($"❌ {type.Name}.{prop.Name}: Optional properties should be nullable (T?) or have a default");
                        }
                    }
                }
            }

            if (s_requestTypes.Contains(type) &&
                !typeof(BackchannelRequest).IsAssignableFrom(type))
            {
                errors.AppendLine($"❌ {type.Name}: Requests must derive from {nameof(BackchannelRequest)} so profiling propagation stays AOT-safe.");
            }
        }

        Assert.True(errors.Length == 0, $"Contract violations found:\n{errors}");
    }

    [Fact]
    public void RequestWithTraceContext_PreservesRequestProperties()
    {
        var errors = new StringBuilder();
        var traceContext = new BackchannelTraceContext
        {
            Baggage = new()
            {
                ["aspire.profiling.session_id"] = "new-session"
            }
        };

        foreach (var requestType in s_requestTypes)
        {
            var request = (BackchannelRequest)Activator.CreateInstance(requestType)!;
            var defaultRequest = (BackchannelRequest)Activator.CreateInstance(requestType)!;
            var expectedValues = new Dictionary<PropertyInfo, object?>();

            foreach (var property in requestType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetSetMethod() is null)
                {
                    continue;
                }

                var value = property.Name == nameof(BackchannelRequest.TraceContext)
                    ? new BackchannelTraceContext
                    {
                        Baggage = new()
                        {
                            ["aspire.profiling.session_id"] = "original-session"
                        }
                    }
                    : CreateNonDefaultValue(requestType, property, property.GetValue(defaultRequest));

                property.SetValue(request, value);
                expectedValues.Add(property, value);
            }

            var copy = request.WithTraceContext(traceContext);

            if (copy.GetType() != requestType)
            {
                errors.AppendLine($"ERROR {requestType.Name}: {nameof(BackchannelRequest.WithTraceContext)} returned {copy.GetType().Name}");
                continue;
            }

            foreach (var (property, originalValue) in expectedValues)
            {
                var expectedValue = property.Name == nameof(BackchannelRequest.TraceContext)
                    ? traceContext
                    : originalValue;
                var actualValue = property.GetValue(copy);

                if (!PropertyValuesEqual(expectedValue, actualValue))
                {
                    errors.AppendLine($"ERROR {requestType.Name}.{property.Name}: Expected {FormatValue(expectedValue)}, actual {FormatValue(actualValue)}");
                }
            }
        }

        Assert.True(errors.Length == 0, $"Trace context copy violations found:\n{errors}");
    }

    [Fact]
    public void ActivityTracingStrategy_PropagatesW3CTraceContextOnJsonRpcRequest()
    {
        using var listener = CreateActivityListener("test-json-rpc-trace");
        using var source = new ActivitySource("test-json-rpc-trace");
        using var clientActivity = source.StartActivity("client", ActivityKind.Client);
        Assert.NotNull(clientActivity);

        var formatter = new SystemTextJsonFormatter();
        var request = ((IJsonRpcMessageFactory)formatter).CreateRequestMessage();
        request.Method = "GetCapabilitiesAsync";
        request.Arguments = Array.Empty<object>();

        var strategy = new ActivityTracingStrategy(source);
        strategy.ApplyOutboundActivity(request);

        Assert.NotNull(request.TraceParent);
        using (strategy.ApplyInboundActivity(request))
        {
            Assert.NotNull(Activity.Current);
            Assert.Equal(clientActivity.TraceId, Activity.Current.TraceId);
            Assert.Equal(clientActivity.SpanId, Activity.Current.ParentSpanId);
        }
    }

    [Fact]
    public void JsonRpcServerCall_RestoresTraceContextBaggage()
    {
        Activity? startedActivity = null;
        using var listener = CreateActivityListener(ProfilingTelemetry.ActivitySourceName, activity => startedActivity = activity);
        var telemetry = new ProfilingTelemetry(CreateConfiguration(
            (KnownConfigNames.ProfilingEnabled, "true")));

        using var activity = telemetry.StartJsonRpcServerCall(
            "GetCapabilitiesAsync",
            streaming: false,
            new BackchannelTraceContext
            {
                Baggage = new()
                {
                    [ProfilingTelemetry.Tags.ProfilingSessionId] = "session-1",
                    ["custom"] = "value"
                }
            });

        Assert.NotNull(startedActivity);
        Assert.Equal("session-1", startedActivity.GetBaggageItem(ProfilingTelemetry.Tags.ProfilingSessionId));
        Assert.Equal("value", startedActivity.GetBaggageItem("custom"));
        Assert.Equal("session-1", startedActivity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));
    }

    [Fact]
    public void AuxiliaryBackchannelV2Methods_UseRequestAndResponseContracts()
    {
        var methods = GetAuxiliaryV2Methods();

        Assert.Equal(s_auxiliaryV2Contracts.Keys.Order(StringComparer.Ordinal), methods.Keys.Order(StringComparer.Ordinal));

        foreach (var (methodName, contract) in s_auxiliaryV2Contracts)
        {
            var method = methods[methodName];
            var parameters = method.GetParameters()
                .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
                .ToArray();

            var parameter = Assert.Single(parameters);
            Assert.Equal(contract.RequestType, parameter.ParameterType);
            Assert.Contains(contract.RequestType, s_contractTypes);

            var responseType = GetResponseContractType(method.ReturnType);
            Assert.Equal(contract.ResponseType, responseType);
            Assert.Contains(contract.ResponseType, s_contractTypes);
        }
    }

    [Fact]
    public void BackchannelTypes_IncludeNestedBackchannelPayloadTypes()
    {
        var contractTypeSet = s_contractTypes.Concat(s_contractEnumTypes).ToHashSet();
        var errors = new StringBuilder();

        foreach (var type in s_contractTypes)
        {
            foreach (var referencedType in EnumerateBackchannelPayloadTypes(type))
            {
                if (!contractTypeSet.Contains(referencedType))
                {
                    errors.AppendLine($"❌ {type.Name}: Referenced backchannel payload type '{referencedType.Name}' must be added to the contract inventory.");
                }
            }
        }

        Assert.True(errors.Length == 0, $"Contract inventory violations found:\n{errors}");
    }

    [Fact]
    public void BackchannelEnums_HaveSafeDefaultAndJsonConverter()
    {
        var errors = new StringBuilder();

        foreach (var type in s_contractEnumTypes)
        {
            var defaultName = Enum.GetName(type, 0);
            if (defaultName is not ("None" or "Unknown" or "Unspecified"))
            {
                errors.AppendLine($"❌ {type.Name}: Boundary enums must define a safe 0 value named None, Unknown, or Unspecified.");
            }

            if (type.GetCustomAttribute<JsonConverterAttribute>() is null)
            {
                errors.AppendLine($"❌ {type.Name}: Boundary enums must declare a JSON converter that handles unknown and null wire values.");
            }

            AssertDeserializesToDefaultEnumValue(type, "null");
            AssertDeserializesToDefaultEnumValue(type, """
                "__unknown__"
                """);
            AssertDeserializesToDefaultEnumValue(type, "2147483647");
        }

        Assert.True(errors.Length == 0, $"Enum contract violations found:\n{errors}");
    }

    [Fact]
    public void AuxiliaryBackchannelV2OptionalRequests_AcceptMissingRequestObject()
    {
        var optionalRequestMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(AuxiliaryBackchannelRpcTarget.GetCapabilitiesAsync),
            nameof(AuxiliaryBackchannelRpcTarget.GetAppHostInfoAsync),
            nameof(AuxiliaryBackchannelRpcTarget.GetDashboardInfoAsync),
            nameof(AuxiliaryBackchannelRpcTarget.GetResourcesAsync),
            nameof(AuxiliaryBackchannelRpcTarget.WatchResourcesAsync),
            nameof(AuxiliaryBackchannelRpcTarget.StopAsync),
        };

        var nullabilityContext = new NullabilityInfoContext();

        foreach (var methodName in s_auxiliaryV2Contracts.Keys)
        {
            var method = typeof(AuxiliaryBackchannelRpcTarget).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException($"Could not find method '{methodName}'.");
            var requestParameter = method.GetParameters()
                .Single(parameter => parameter.ParameterType != typeof(CancellationToken));
            var requestNullability = nullabilityContext.Create(requestParameter);

            if (optionalRequestMethods.Contains(methodName))
            {
                Assert.True(requestParameter.HasDefaultValue, $"{methodName} should allow a missing request object.");
                Assert.Null(requestParameter.DefaultValue);
                Assert.Equal(NullabilityState.Nullable, requestNullability.WriteState);
            }
            else
            {
                Assert.False(requestParameter.HasDefaultValue, $"{methodName} should require its request object.");
                Assert.NotEqual(NullabilityState.Nullable, requestNullability.WriteState);
            }
        }
    }

    [Fact]
    public void AuxiliaryBackchannelCapabilities_AreStable()
    {
        Assert.Equal("aux.v1", AuxiliaryBackchannelCapabilities.V1);
        Assert.Equal("aux.v2", AuxiliaryBackchannelCapabilities.V2);
        Assert.Equal("aux.v3", AuxiliaryBackchannelCapabilities.V3);
    }

    private static bool IsSupportedCollectionType(Type type)
    {
        if (type.IsArray)
        {
            return true;
        }

        if (!type.IsGenericType)
        {
            return false;
        }

        var genericDef = type.GetGenericTypeDefinition();
        return genericDef == typeof(Dictionary<,>) ||
               genericDef == typeof(List<>) ||
               genericDef == typeof(IReadOnlyList<>) ||
               genericDef == typeof(IReadOnlyDictionary<,>);
    }

    private static object CreateNonDefaultValue(Type requestType, PropertyInfo property, object? defaultValue)
    {
        var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var propertyName = $"{requestType.Name}.{property.Name}";

        if (propertyType == typeof(string))
        {
            return propertyName;
        }

        if (propertyType == typeof(bool))
        {
            return defaultValue is bool value ? !value : true;
        }

        if (propertyType == typeof(int))
        {
            return defaultValue is 42 ? 43 : 42;
        }

        if (propertyType == typeof(JsonElement))
        {
            using var document = JsonDocument.Parse($$"""{ "property": "{{propertyName}}" }""");
            return document.RootElement.Clone();
        }

        if (propertyType == typeof(JsonNode))
        {
            return JsonNode.Parse($$"""{ "property": "{{propertyName}}" }""")!;
        }

        if (property.PropertyType == typeof(Dictionary<string, string>))
        {
            return new Dictionary<string, string> { ["property"] = propertyName };
        }

        throw new NotSupportedException($"{requestType.Name}.{property.Name} has unsupported test value type {property.PropertyType}.");
    }

    private static bool PropertyValuesEqual(object? expected, object? actual)
    {
        if (expected is JsonElement expectedJson && actual is JsonElement actualJson)
        {
            return expectedJson.ValueKind == actualJson.ValueKind &&
                   expectedJson.GetRawText() == actualJson.GetRawText();
        }

        if (expected is JsonNode expectedNode && actual is JsonNode actualNode)
        {
            return expectedNode.ToJsonString() == actualNode.ToJsonString();
        }

        if (expected is Dictionary<string, string> expectedDictionary && actual is Dictionary<string, string> actualDictionary)
        {
            return expectedDictionary.Count == actualDictionary.Count &&
                   expectedDictionary.All(item => actualDictionary.TryGetValue(item.Key, out var actualValue) && item.Value == actualValue);
        }

        return Equals(expected, actual);
    }

    private static string FormatValue(object? value) =>
        value switch
        {
            null => "<null>",
            JsonElement json => json.GetRawText(),
            JsonNode node => node.ToJsonString(),
            BackchannelTraceContext context => $"{nameof(BackchannelTraceContext)}({context.Baggage.Count} baggage items)",
            _ => value.ToString() ?? string.Empty
        };

    private static ActivityListener CreateActivityListener(string sourceName, Action<Activity>? activityStarted = null)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activityStarted
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static IConfiguration CreateConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
    }

    private static bool HasExplicitDefaultBackingField(Type type, PropertyInfo property)
    {
        var backingFieldName = "_" + char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
        var backingField = type.GetField(backingFieldName, BindingFlags.Instance | BindingFlags.NonPublic);

        return backingField is not null && property.PropertyType.IsAssignableFrom(backingField.FieldType);
    }

    private static IReadOnlyDictionary<string, MethodInfo> GetAuxiliaryV2Methods()
    {
        return typeof(AuxiliaryBackchannelRpcTarget)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(IsAuxiliaryV2ContractMethod)
            .ToDictionary(method => method.Name, StringComparer.Ordinal);
    }

    private static bool IsAuxiliaryV2ContractMethod(MethodInfo method)
    {
        var parameters = method.GetParameters()
            .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
            .ToArray();

        return parameters is [{ ParameterType.Name: { } requestTypeName }] &&
            requestTypeName.EndsWith("Request", StringComparison.Ordinal);
    }

    private static IEnumerable<Type> EnumerateBackchannelPayloadTypes(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(property => EnumeratePayloadTypes(property.PropertyType))
            .Where(IsBackchannelPayloadType);
    }

    private static IEnumerable<Type> EnumeratePayloadTypes(Type type)
    {
        if (type.IsArray)
        {
            yield return type.GetElementType()!;
            yield break;
        }

        var nullableType = Nullable.GetUnderlyingType(type);
        if (nullableType is not null)
        {
            yield return nullableType;
            yield break;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var payloadType in EnumeratePayloadTypes(argument))
                {
                    yield return payloadType;
                }
            }
            yield break;
        }

        yield return type;
    }

    private static bool IsBackchannelPayloadType(Type type)
    {
        return type.Namespace == typeof(GetCapabilitiesRequest).Namespace &&
            !type.IsPrimitive &&
            type != typeof(string);
    }

    private static void AssertDeserializesToDefaultEnumValue(Type enumType, string json)
    {
        var value = JsonSerializer.Deserialize(json, enumType);

        Assert.NotNull(value);
        Assert.Equal(0, Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }

    private static bool IsRequestResponseType(Type type)
    {
        return type == typeof(GetCapabilitiesRequest) ||
            type == typeof(GetCapabilitiesResponse) ||
            type == typeof(GetAppHostInfoRequest) ||
            type == typeof(GetAppHostInfoResponse) ||
            type == typeof(GetDashboardInfoRequest) ||
            type == typeof(GetDashboardInfoResponse) ||
            type == typeof(GetResourcesRequest) ||
            type == typeof(GetResourcesResponse) ||
            type == typeof(WatchResourcesRequest) ||
            type == typeof(GetConsoleLogsRequest) ||
            type == typeof(CallMcpToolRequest) ||
            type == typeof(CallMcpToolResponse) ||
            type == typeof(StopAppHostRequest) ||
            type == typeof(StopAppHostResponse) ||
            type == typeof(ExecuteResourceCommandRequest) ||
            type == typeof(ExecuteResourceCommandOptions) ||
            type == typeof(ExecuteResourceCommandResponse) ||
            type == typeof(WaitForResourceRequest) ||
            type == typeof(WaitForResourceResponse) ||
            type == typeof(GetPipelineStepsRequest) ||
            type == typeof(GetPipelineStepsResponse);
    }

    private static Type GetResponseContractType(Type returnType)
    {
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            return returnType.GenericTypeArguments[0];
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
        {
            return returnType.GenericTypeArguments[0];
        }

        throw new InvalidOperationException($"Unsupported RPC return type '{returnType}'.");
    }
}
