// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire;

// NOTE unit tests enforce that these two classes are kept in sync

internal static class StringComparers
{
    public static StringComparer ResourceName => StringComparer.OrdinalIgnoreCase;
    public static StringComparer ResourceState => StringComparer.OrdinalIgnoreCase;
    public static StringComparer EndpointAnnotationName => StringComparer.OrdinalIgnoreCase;
    public static StringComparer EndpointAnnotationUriScheme => StringComparer.OrdinalIgnoreCase;
    public static StringComparer ResourceType => StringComparer.Ordinal;
    public static StringComparer ResourcePropertyName => StringComparer.Ordinal;
    public static StringComparer ResourceOwnerName => StringComparer.Ordinal;
    public static StringComparer ResourceOwnerKind => StringComparer.Ordinal;
    public static StringComparer ResourceOwnerUid => StringComparer.Ordinal;
    public static StringComparer UserTextSearch => StringComparer.CurrentCultureIgnoreCase;
    public static StringComparer EnvironmentVariableName => StringComparer.OrdinalIgnoreCase;
    public static StringComparer Url => StringComparer.OrdinalIgnoreCase;
    public static StringComparer UrlPath => StringComparer.OrdinalIgnoreCase;
    public static StringComparer UrlHost => StringComparer.OrdinalIgnoreCase;
    public static StringComparer HtmlAttribute => StringComparer.Ordinal;
    public static StringComparer GridColumn => StringComparer.Ordinal;
    public static StringComparer OtlpAttribute => StringComparer.Ordinal;
    public static StringComparer OtlpFieldValue => StringComparer.OrdinalIgnoreCase;
    public static StringComparer OtlpSpanId => StringComparer.Ordinal;
    public static StringComparer HealthReportPropertyValue => StringComparer.Ordinal;
    public static StringComparer ConsoleLogContent => StringComparer.Ordinal;
    public static StringComparer CultureName => StringComparer.OrdinalIgnoreCase;
    public static StringComparer CommandName => StringComparer.Ordinal;
    public static StringComparer CliInputOrOutput => StringComparer.Ordinal;
    public static StringComparer InteractionInputName => StringComparer.OrdinalIgnoreCase;
    public static StringComparer NetworkId => StringComparer.Ordinal;
    public static StringComparer NuGetPackageId => StringComparer.OrdinalIgnoreCase;
    public static StringComparer FullTextSearch => StringComparer.OrdinalIgnoreCase;
    public static StringComparer ChannelName => StringComparer.OrdinalIgnoreCase;

    // Azure control-plane values are protocol identifiers rather than user text. ARM treats these
    // values case-insensitively, and ordinal comparisons keep diagnostics deterministic across UI cultures.
    public static StringComparer AzureLocation => StringComparer.OrdinalIgnoreCase;
    public static StringComparer AzureProvisioningErrorCode => StringComparer.OrdinalIgnoreCase;
    public static StringComparer AzureProvisioningOperation => StringComparer.OrdinalIgnoreCase;
    public static StringComparer AzureProvisioningState => StringComparer.OrdinalIgnoreCase;
    public static StringComparer AzureResourceGroupName => StringComparer.OrdinalIgnoreCase;
    public static StringComparer AzureResourceId => StringComparer.OrdinalIgnoreCase;
    public static StringComparer AzureResourceName => StringComparer.OrdinalIgnoreCase;
    public static StringComparer AzureResourceType => StringComparer.OrdinalIgnoreCase;
    public static StringComparer AzureTenantId => StringComparer.OrdinalIgnoreCase;
}

internal static class StringComparisons
{
    public static StringComparison ResourceName => StringComparison.OrdinalIgnoreCase;
    public static StringComparison ResourceState => StringComparison.OrdinalIgnoreCase;
    public static StringComparison EndpointAnnotationName => StringComparison.OrdinalIgnoreCase;
    public static StringComparison EndpointAnnotationUriScheme => StringComparison.OrdinalIgnoreCase;
    public static StringComparison ResourceType => StringComparison.Ordinal;
    public static StringComparison ResourcePropertyName => StringComparison.Ordinal;
    public static StringComparison ResourceOwnerName => StringComparison.Ordinal;
    public static StringComparison ResourceOwnerKind => StringComparison.Ordinal;
    public static StringComparison ResourceOwnerUid => StringComparison.Ordinal;
    public static StringComparison UserTextSearch => StringComparison.CurrentCultureIgnoreCase;
    public static StringComparison EnvironmentVariableName => StringComparison.OrdinalIgnoreCase;
    public static StringComparison Url => StringComparison.OrdinalIgnoreCase;
    public static StringComparison UrlPath => StringComparison.OrdinalIgnoreCase;
    public static StringComparison UrlHost => StringComparison.OrdinalIgnoreCase;
    public static StringComparison HtmlAttribute => StringComparison.Ordinal;
    public static StringComparison GridColumn => StringComparison.Ordinal;
    public static StringComparison OtlpAttribute => StringComparison.Ordinal;
    public static StringComparison OtlpFieldValue => StringComparison.OrdinalIgnoreCase;
    public static StringComparison OtlpSpanId => StringComparison.Ordinal;
    public static StringComparison HealthReportPropertyValue => StringComparison.Ordinal;
    public static StringComparison ConsoleLogContent => StringComparison.Ordinal;
    public static StringComparison CultureName => StringComparison.OrdinalIgnoreCase;
    public static StringComparison CommandName => StringComparison.Ordinal;
    public static StringComparison CliInputOrOutput => StringComparison.Ordinal;
    public static StringComparison InteractionInputName => StringComparison.OrdinalIgnoreCase;
    public static StringComparison NetworkId => StringComparison.Ordinal;
    public static StringComparison NuGetPackageId => StringComparison.OrdinalIgnoreCase;
    public static StringComparison FullTextSearch => StringComparison.OrdinalIgnoreCase;
    public static StringComparison ChannelName => StringComparison.OrdinalIgnoreCase;

    // Azure control-plane values are protocol identifiers rather than user text. ARM treats these
    // values case-insensitively, and ordinal comparisons keep diagnostics deterministic across UI cultures.
    public static StringComparison AzureLocation => StringComparison.OrdinalIgnoreCase;
    public static StringComparison AzureProvisioningErrorCode => StringComparison.OrdinalIgnoreCase;
    public static StringComparison AzureProvisioningOperation => StringComparison.OrdinalIgnoreCase;
    public static StringComparison AzureProvisioningState => StringComparison.OrdinalIgnoreCase;
    public static StringComparison AzureResourceGroupName => StringComparison.OrdinalIgnoreCase;
    public static StringComparison AzureResourceId => StringComparison.OrdinalIgnoreCase;
    public static StringComparison AzureResourceName => StringComparison.OrdinalIgnoreCase;
    public static StringComparison AzureResourceType => StringComparison.OrdinalIgnoreCase;
    public static StringComparison AzureTenantId => StringComparison.OrdinalIgnoreCase;
}
