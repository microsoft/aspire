// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace Aspire.Hosting.RemoteHost.Tests;

/// <summary>
/// A configurable <see cref="Assembly"/> test double whose name and <see cref="GetTypes"/>
/// behavior are supplied by the caller. Use the factory methods to either return a fixed set of
/// types or to simulate a loader failure (the discovery resolvers probe assemblies via
/// <see cref="Assembly.GetTypes"/>, so throwing from it reproduces a real type-load failure).
/// </summary>
internal sealed class FakeAssembly : Assembly
{
    private readonly AssemblyName _name;
    private readonly Func<Type[]> _getTypes;

    private FakeAssembly(string name, Func<Type[]> getTypes)
    {
        _name = new AssemblyName(name);
        _getTypes = getTypes;
    }

    public override AssemblyName GetName() => _name;

    public override Type[] GetTypes() => _getTypes();

    /// <summary>
    /// Creates a fake assembly whose <see cref="GetTypes"/> returns the supplied types.
    /// </summary>
    public static FakeAssembly WithTypes(string name, params Type[] types)
        => new(name, () => types);

    /// <summary>
    /// Creates a fake assembly whose <see cref="GetTypes"/> throws the supplied exception,
    /// simulating an assembly that loads but cannot have its types enumerated.
    /// </summary>
    public static FakeAssembly ThrowingOnGetTypes(string name, Exception exception)
        => new(name, () => throw exception);
}
