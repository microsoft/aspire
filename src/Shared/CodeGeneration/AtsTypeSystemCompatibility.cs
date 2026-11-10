// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.TypeSystem;

namespace Aspire.Shared.CodeGeneration;

/// <summary>
/// Reads members of the shared ATS contract that may be missing from an older Aspire.TypeSystem.
/// Used by the polyglot code generators.
/// </summary>
internal static class AtsTypeSystemCompatibility
{
    private static readonly PropertyInfo? s_isExperimentalProperty =
        typeof(AtsCapabilityInfo).GetProperty(nameof(AtsCapabilityInfo.IsExperimental), BindingFlags.Instance | BindingFlags.Public);
    private static readonly PropertyInfo? s_handleIsObsoleteProperty =
        typeof(AtsTypeInfo).GetProperty("IsObsolete", BindingFlags.Instance | BindingFlags.Public);
    private static readonly PropertyInfo? s_dtoIsObsoleteProperty =
        typeof(AtsDtoTypeInfo).GetProperty("IsObsolete", BindingFlags.Instance | BindingFlags.Public);
    private static readonly PropertyInfo? s_dtoPropertyIsObsoleteProperty =
        typeof(AtsDtoPropertyInfo).GetProperty("IsObsolete", BindingFlags.Instance | BindingFlags.Public);
    private static readonly PropertyInfo? s_enumIsObsoleteProperty =
        typeof(AtsEnumTypeInfo).GetProperty("IsObsolete", BindingFlags.Instance | BindingFlags.Public);
    private static readonly PropertyInfo? s_enumValueIsObsoleteProperty =
        typeof(AtsEnumValueInfo).GetProperty("IsObsolete", BindingFlags.Instance | BindingFlags.Public);
    private static readonly PropertyInfo? s_exportedValueIsObsoleteProperty =
        typeof(AtsExportedValueInfo).GetProperty("IsObsolete", BindingFlags.Instance | BindingFlags.Public);

    public static bool IsExperimental(AtsCapabilityInfo capability)
    {
        // The CLI force-shares Aspire.TypeSystem with code generators across SDK version skew.
        // Probe additive contract members so a newer generator still runs with an older CLI bundle.
        return s_isExperimentalProperty?.GetValue(capability) is true;
    }

    public static bool IsObsolete(AtsTypeInfo type) =>
        ReadObsolete(s_handleIsObsoleteProperty, type, type.ClrType);

    public static bool IsObsolete(AtsDtoTypeInfo type) =>
        ReadObsolete(s_dtoIsObsoleteProperty, type, type.ClrType);

    public static bool IsObsolete(AtsDtoTypeInfo _, AtsDtoPropertyInfo property)
    {
        if (s_dtoPropertyIsObsoleteProperty?.GetValue(property) is true)
        {
            return true;
        }

        return false;
    }

    public static bool IsObsolete(AtsEnumTypeInfo type) =>
        ReadObsolete(s_enumIsObsoleteProperty, type, type.ClrType);

    public static bool IsObsolete(AtsEnumTypeInfo _, AtsEnumValueInfo value)
    {
        if (s_enumValueIsObsoleteProperty?.GetValue(value) is true)
        {
            return true;
        }

        return false;
    }

    public static bool IsObsolete(AtsExportedValueInfo value) =>
        s_exportedValueIsObsoleteProperty?.GetValue(value) is true;

    private static bool ReadObsolete(PropertyInfo? property, object value, MemberInfo? fallbackMember) =>
        property?.GetValue(value) is true || fallbackMember is not null && HasObsoleteAttribute(fallbackMember);

    private static bool HasObsoleteAttribute(MemberInfo member) =>
        member.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType.FullName == "System.ObsoleteAttribute");
}
