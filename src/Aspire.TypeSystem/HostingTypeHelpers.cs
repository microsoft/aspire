// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.TypeSystem;

/// <summary>
/// Contains well-known full type names used by Aspire hosting infrastructure.
/// </summary>
public static class HostingTypeNames
{
    /// <summary>Full name of the AspireExportAttribute type.</summary>
    public const string AspireExportAttribute = "Aspire.Hosting.AspireExportAttribute";

    /// <summary>Full name of the AspireExportIgnoreAttribute type.</summary>
    public const string AspireExportIgnoreAttribute = "Aspire.Hosting.AspireExportIgnoreAttribute";

    /// <summary>Full name of the AspireDtoAttribute type.</summary>
    public const string AspireDtoAttribute = "Aspire.Hosting.AspireDtoAttribute";

    /// <summary>Full name of the AspireUnionAttribute type.</summary>
    public const string AspireUnionAttribute = "Aspire.Hosting.AspireUnionAttribute";

    /// <summary>Full name of the IResource interface.</summary>
    public const string ResourceInterface = "Aspire.Hosting.ApplicationModel.IResource";

    /// <summary>Full name of the generic IResourceBuilder interface.</summary>
    public const string ResourceBuilderInterface = "Aspire.Hosting.ApplicationModel.IResourceBuilder`1";

    /// <summary>Full name of the IDistributedApplicationBuilder interface.</summary>
    public const string DistributedApplicationBuilder = "Aspire.Hosting.IDistributedApplicationBuilder";

    /// <summary>Full name of the DistributedApplication class.</summary>
    public const string DistributedApplication = "Aspire.Hosting.DistributedApplication";

    /// <summary>Full name of the ReferenceExpression class.</summary>
    public const string ReferenceExpression = "Aspire.Hosting.ApplicationModel.ReferenceExpression";

    /// <summary>Full name of the ReferenceExpressionBuilder class.</summary>
    public const string ReferenceExpressionBuilder = "Aspire.Hosting.ApplicationModel.ReferenceExpressionBuilder";

    /// <summary>Full name of the IValueProvider interface.</summary>
    public const string ValueProviderInterface = "Aspire.Hosting.ApplicationModel.IValueProvider";
}

/// <summary>
/// Provides helper methods for identifying well-known Aspire hosting types by full name.
/// </summary>
public static class HostingTypeHelpers
{
    /// <summary>
    /// Determines whether the specified <paramref name="type"/> implements the IResource interface.
    /// </summary>
    public static bool IsResourceType(Type? type) =>
        IsAssignableToType(type, HostingTypeNames.ResourceInterface);

    /// <summary>
    /// Determines whether the specified <paramref name="type"/> implements the generic IResourceBuilder interface.
    /// </summary>
    public static bool IsResourceBuilderType(Type? type)
        => IsAssignableToType(type, HostingTypeNames.ResourceBuilderInterface);

    /// <summary>
    /// Determines whether the specified <paramref name="type"/> is the IDistributedApplicationBuilder interface.
    /// </summary>
    public static bool IsDistributedApplicationBuilderType(Type? type) =>
        string.Equals(type?.FullName, HostingTypeNames.DistributedApplicationBuilder, StringComparison.Ordinal);

    /// <summary>
    /// Determines whether the specified <paramref name="type"/> is the DistributedApplication class.
    /// </summary>
    public static bool IsDistributedApplicationType(Type? type) =>
        string.Equals(type?.FullName, HostingTypeNames.DistributedApplication, StringComparison.Ordinal);

    private static bool IsAssignableToType(Type? type, string fullName)
    {
        if (type is null)
        {
            return false;
        }

        if (string.Equals(type.FullName, fullName, StringComparison.Ordinal))
        {
            return true;
        }

        if (type.IsGenericType &&
            string.Equals(type.GetGenericTypeDefinition().FullName, fullName, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var implementedInterface in type.GetInterfaces())
        {
            if (string.Equals(implementedInterface.FullName, fullName, StringComparison.Ordinal))
            {
                return true;
            }

            if (implementedInterface.IsGenericType &&
                string.Equals(implementedInterface.GetGenericTypeDefinition().FullName, fullName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return IsAssignableToType(type.BaseType, fullName);
    }
}
