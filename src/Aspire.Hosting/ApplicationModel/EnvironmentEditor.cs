// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides an ATS-first editor for environment variables within polyglot callbacks.
/// </summary>
[AspireExport]
internal sealed class EnvironmentEditor(Dictionary<string, object> environmentVariables)
{
    private readonly Dictionary<string, object> _environmentVariables = environmentVariables ?? throw new ArgumentNullException(nameof(environmentVariables));

    /// <summary>
    /// Sets an environment variable.
    /// </summary>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="value">The value to assign to the environment variable.</param>
    [AspireExport]
    public void Set(
        string name,
        [AspireUnion(
            typeof(string),
            typeof(ReferenceExpression),
            typeof(EndpointReference),
            typeof(IResourceBuilder<ParameterResource>),
            typeof(IResourceBuilder<IResourceWithConnectionString>),
            typeof(IExpressionValue))]
        object value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        // Polyglot AppHosts can only pass a resource builder handle. Store the resource itself, as a
        // C# AppHost does with parameter.Resource, so every consumer of the environment (publishers
        // included) sees a value it knows how to resolve.
        _environmentVariables[name] = value is IResourceBuilder<IResource> resourceBuilder ? resourceBuilder.Resource : value;
    }
}
