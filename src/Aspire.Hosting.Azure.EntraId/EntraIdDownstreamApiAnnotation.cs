// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Records a Microsoft.Identity.Web downstream API configured for a resource, so a name can't be configured twice.
/// </summary>
internal sealed class EntraIdDownstreamApiAnnotation(string name) : IResourceAnnotation
{
    public string Name { get; } = name;
}
