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
    [AspireExport(Description = "Sets an environment variable")]
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

        _environmentVariables[name] = value;
    }
}
