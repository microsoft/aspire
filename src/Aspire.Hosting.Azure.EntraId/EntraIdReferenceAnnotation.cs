// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Records that a resource references an Entra ID registration, and the configuration prefix of the <c>DownstreamApis</c> entries it receives.
/// </summary>
/// <remarks>
/// A resource can reference more than one registration. For a .NET program the <c>DownstreamApis</c> section doesn't depend on the
/// connection name, so entries from different references can land on the same keys; this lets that be detected for the resource as a whole.
/// </remarks>
internal sealed class EntraIdReferenceAnnotation(EntraIdApplicationResource source, string downstreamApisPrefix, string environmentVariablePrefix, bool isDotnetProgram) : IResourceAnnotation
{
    public EntraIdApplicationResource Source { get; } = source;

    // The prefix as the caller wrote it, for diagnostics.
    public string DownstreamApisPrefix { get; } = downstreamApisPrefix;

    // The environment variable prefix that each entry's name is appended to.
    public string EnvironmentVariablePrefix { get; } = environmentVariablePrefix;

    public bool IsDotnetProgram { get; } = isDotnetProgram;

    // The name that the consumer receives for a key. A .NET program reads the key as written, as configuration, whose keys ignore case.
    // Other resources get an encoded, upper snake case name, which maps different keys, such as "orders-api" and "ordersApi", to one name.
    // The emitter and the collision check use this one conversion so that they agree on which keys are the same.
    internal string GetEnvironmentVariableName(string key) => ToEnvironmentVariableName(IsDotnetProgram, key);

    internal static string ToEnvironmentVariableName(bool isDotnetProgram, string key) =>
        isDotnetProgram ? key : JsonNamingPolicy.SnakeCaseUpper.ConvertName(EnvironmentVariableNameEncoder.Encode(key));
}
