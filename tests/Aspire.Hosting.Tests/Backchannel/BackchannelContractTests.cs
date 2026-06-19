// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Diagnostics;
using Aspire.Tests;
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
        typeof(GetTerminalInfoRequest),
        typeof(ListTerminalsRequest),
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
        typeof(ExecuteResourceCommandResponse),
        typeof(WaitForResourceResponse),
        typeof(GetPipelineStepsResponse),
        typeof(GetTerminalInfoResponse),
        typeof(TerminalReplicaInfo),
        typeof(TerminalPeerInfo),
        typeof(ListTerminalsResponse),
        typeof(TerminalSummary),
        typeof(ResourceSnapshot),
        typeof(ResourceSnapshotUrl),
        typeof(ResourceSnapshotUrlDisplayProperties),
        typeof(ResourceSnapshotRelationship),
        typeof(ResourceSnapshotHealthReport),
        typeof(ResourceSnapshotVolume),
        typeof(ResourceSnapshotEnvironmentVariable),
        typeof(ResourceSnapshotMcpServer),
        typeof(ResourceLogLine),
        typeof(ResourceLogBatch),
    ];

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

            // Rule 6: Naming convention (skip helper types)
            if (!type.Name.StartsWith("ResourceSnapshot") &&
                type != typeof(BackchannelTraceContext) &&
                type.Name != "McpToolContentItem" &&
                type.Name != "ResourceLogLine" &&
                type.Name != "ResourceLogBatch" &&
                type.Name != "TerminalReplicaInfo" &&
                type.Name != "TerminalPeerInfo" &&
                type.Name != "TerminalSummary")
            {
                if (!type.Name.EndsWith("Request") && !type.Name.EndsWith("Response"))
                {
                    errors.AppendLine($"❌ {type.Name}: Name should end with 'Request' or 'Response'");
                }
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

                var isRequired = prop.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() is not null;
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
                        var isCollectionWithDefault = prop.PropertyType.IsArray ||
                            (prop.PropertyType.IsGenericType && IsAllowedCollectionType(prop.PropertyType));

                        if (!isNullable && !isCollectionWithDefault)
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
        using var source = new ActivitySource("test-json-rpc-trace");
        using var listener = ActivityListenerHelper.Create(source);
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
        var telemetry = new ProfilingTelemetry(CreateConfiguration(
            (KnownConfigNames.ProfilingEnabled, "true")));
        using var listener = ActivityListenerHelper.Create(ProfilingTelemetry.ActivitySource, onActivityStarted: activity => startedActivity = activity);

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
    public void DcpRunApplication_UsesConfiguredProfilingParentWhenAmbientActivityIsNotProfiling()
    {
        var activities = new List<Activity>();
        using var profilingListener = ActivityListenerHelper.Create(ProfilingTelemetry.ActivitySource, onActivityStarted: activities.Add);
        using var processSource = new ActivitySource("test.process");
        using var processListener = ActivityListenerHelper.Create(processSource);
        using var processActivity = processSource.StartActivity("process npx.CMD", ActivityKind.Internal);
        Assert.NotNull(processActivity);
        var traceParent = processActivity.Id;
        Assert.NotNull(traceParent);

        processActivity.Stop();

        using var ambientSource = new ActivitySource("test.ambient");
        using var ambientListener = ActivityListenerHelper.Create(ambientSource);
        using var ambientActivity = ambientSource.StartActivity("hidden ambient", ActivityKind.Internal);
        Assert.NotNull(ambientActivity);

        var configuration = CreateConfiguration(
            (KnownConfigNames.ProfilingEnabled, "true"),
            (KnownConfigNames.ProfilingSessionId, "session-1"),
            (KnownConfigNames.ProfilingTraceParent, traceParent));

        using var activity = ProfilingTelemetry.StartDcpRunApplication(configuration, resourceCount: 1);

        var dcpActivity = Assert.Single(activities, activity => activity.OperationName == ProfilingTelemetry.Activities.DcpRunApplication);
        Assert.Equal(processActivity.TraceId, dcpActivity.TraceId);
        Assert.Equal(processActivity.SpanId, dcpActivity.ParentSpanId);
    }

    private static bool IsAllowedCollectionType(Type type)
    {
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

        if (property.PropertyType == typeof(string[]))
        {
            return new[] { propertyName };
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

        if (expected is string[] expectedArray && actual is string[] actualArray)
        {
            return expectedArray.SequenceEqual(actualArray);
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

    private static IConfiguration CreateConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
    }
}
