// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text;

namespace Aspire.Hosting.Backchannel;

/// <summary>
/// Validates that backchannel request/response types follow the contract rules.
/// </summary>
[Trait("Partition", "4")]
public class BackchannelContractTests
{
    // V2 request/response types that must follow the contract
    private static readonly Type[] s_contractTypes =
    [
        typeof(GetCapabilitiesRequest),
        typeof(GetCapabilitiesResponse),
        typeof(GetAppHostInfoRequest),
        typeof(GetAppHostInfoResponse),
        typeof(GetDashboardInfoRequest),
        typeof(GetDashboardInfoResponse),
        typeof(GetResourcesRequest),
        typeof(GetResourcesResponse),
        typeof(WatchResourcesRequest),
        typeof(GetConsoleLogsRequest),
        typeof(CallMcpToolRequest),
        typeof(CallMcpToolResponse),
        typeof(McpToolContentItem),
        typeof(StopAppHostRequest),
        typeof(StopAppHostResponse),
        typeof(ResourceSnapshot),
        typeof(ResourceSnapshotUrl),
        typeof(ResourceSnapshotUrlDisplayProperties),
        typeof(ResourceSnapshotRelationship),
        typeof(ResourceSnapshotHealthReport),
        typeof(ResourceSnapshotVolume),
        typeof(ResourceSnapshotEnvironmentVariable),
        typeof(ResourceSnapshotMcpServer),
        typeof(ResourceLogLine),
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
                type.Name != "McpToolContentItem" &&
                type.Name != "ResourceLogLine")
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
        }

        Assert.True(errors.Length == 0, $"Contract violations found:\n{errors}");
    }

    private static bool IsAllowedCollectionType(Type type)
    {
        var genericDef = type.GetGenericTypeDefinition();
        return genericDef == typeof(Dictionary<,>) ||
               genericDef == typeof(List<>) ||
               genericDef == typeof(IReadOnlyList<>) ||
               genericDef == typeof(IReadOnlyDictionary<,>);
    }
}
