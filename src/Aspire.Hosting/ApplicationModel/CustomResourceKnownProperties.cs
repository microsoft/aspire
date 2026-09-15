// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Known properties for resources that show up in the dashboard.
/// </summary>
public static class CustomResourceKnownProperties
{
    /// <summary>
    /// The source of the resource
    /// </summary>
    public static string Source { get; } = KnownProperties.Resource.Source;

    /// <summary>
    /// The connection string of the resource
    /// </summary>
    public static string ConnectionString { get; } = KnownProperties.Resource.ConnectionString;

    /// <summary>
    /// The connection properties of the resource.
    /// </summary>
    public static string ConnectionProperties { get; } = KnownProperties.Resource.ConnectionProperties;
}
