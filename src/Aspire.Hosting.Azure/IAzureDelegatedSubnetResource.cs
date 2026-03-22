// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure resource that supports subnet delegation.
/// </summary>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/dotnet/aspire/diagnostics#{0}")]
public interface IAzureDelegatedSubnetResource : IResource
{
    /// <summary>
    /// Gets the service delegation service name (e.g., "Microsoft.App/environments").
    /// </summary>
    string DelegatedSubnetServiceName { get; }
}
