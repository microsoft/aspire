// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;

/// <summary>
/// A test resource that simulates a Redis resource.
/// </summary>
public class TestRedisResource : ContainerResource, IResourceWithConnectionString
{
    public TestRedisResource(string name) : base(name)
    {
    }

    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"{Name}");
}

/// <summary>
/// A test resource with custom options.
/// </summary>
public class TestDatabaseResource : ContainerResource
{
    public TestDatabaseResource(string name) : base(name)
    {
    }

    public string? DatabaseName { get; set; }
}

/// <summary>
/// Interface for a vault-like resource. Used to test that the codegen does not produce
/// duplicate TypeScript classes when both a concrete type and its interface resolve to
/// the same derived class name (TestVaultResource).
/// </summary>
public interface ITestVaultResource : IResource
{
}

/// <summary>
/// Concrete vault resource implementing <see cref="ITestVaultResource"/>.
/// </summary>
public class TestVaultResource : ContainerResource, ITestVaultResource
{
    public TestVaultResource(string name) : base(name)
    {
    }
}
