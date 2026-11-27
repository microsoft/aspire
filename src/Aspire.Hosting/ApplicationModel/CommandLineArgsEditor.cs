// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides an ATS-first editor for command-line arguments within polyglot callbacks.
/// </summary>
[AspireExport]
internal sealed class CommandLineArgsEditor(IList<object> args)
{
    private readonly IList<object> _args = args ?? throw new ArgumentNullException(nameof(args));

    /// <summary>
    /// Clears all command-line arguments.
    /// </summary>
    [AspireExport]
    public void Clear()
    {
        _args.Clear();
    }

    /// <summary>
    /// Adds a command-line argument.
    /// </summary>
    /// <param name="value">The argument to add.</param>
    [AspireExport]
    public void Add(
        [AspireUnion(
            typeof(string),
            typeof(ReferenceExpression),
            typeof(EndpointReference),
            typeof(IResourceBuilder<ParameterResource>),
            typeof(IResourceBuilder<IResourceWithConnectionString>),
            typeof(IExpressionValue))]
        object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Polyglot AppHosts can only pass a resource builder handle. Store the resource itself, as a
        // C# AppHost does with parameter.Resource, so every consumer of the arguments (publishers
        // included) sees a value it knows how to resolve.
        _args.Add(value is IResourceBuilder<IResource> resourceBuilder ? resourceBuilder.Resource : value);
    }
}
