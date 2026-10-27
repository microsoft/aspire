// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Records that a resource references an Entra ID registration, and the configuration prefix of the <c>DownstreamApis</c> entries it receives.
/// </summary>
/// <remarks>
/// A resource can reference more than one registration. For a .NET program the <c>DownstreamApis</c> section doesn't depend on the
/// connection name, so entries from different references can land on the same keys; this lets that be detected for the resource as a whole.
/// </remarks>
internal sealed class EntraIdReferenceAnnotation(EntraIdApplicationResource source, string downstreamApisPrefix, string environmentVariablePrefix) : IResourceAnnotation
{
    public EntraIdApplicationResource Source { get; } = source;

    // The prefix as the caller wrote it, for diagnostics.
    public string DownstreamApisPrefix { get; } = downstreamApisPrefix;

    // The environment variable prefix that each entry's name is appended to.
    public string EnvironmentVariablePrefix { get; } = environmentVariablePrefix;
}
