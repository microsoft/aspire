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
    /// Adds a command-line argument.
    /// </summary>
    /// <param name="value">The argument to add.</param>
    [AspireExport(Description = "Adds a command-line argument")]
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
        _args.Add(value);
    }
}
