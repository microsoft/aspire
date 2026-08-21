// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;

/// <summary>
/// A bare resource used to verify that parameter-only resource builders do not receive Promise wrappers.
/// </summary>
public interface ITestPromiseCollisionResource : IResource
{
}

/// <summary>
/// Concrete resource behind <see cref="ITestPromiseCollisionResource"/>.
/// </summary>
public class TestPromiseCollisionResource : Resource, ITestPromiseCollisionResource
{
    public TestPromiseCollisionResource(string name) : base(name)
    {
    }
}

/// <summary>
/// A bare resource whose generated name collides with the Promise wrapper for
/// <see cref="ITestPromiseCollisionResource"/>.
/// </summary>
public interface ITestPromiseCollisionResourcePromise : IResource
{
}

/// <summary>
/// Concrete resource behind <see cref="ITestPromiseCollisionResourcePromise"/>.
/// </summary>
public class TestPromiseCollisionResourcePromise : Resource, ITestPromiseCollisionResourcePromise
{
    public TestPromiseCollisionResourcePromise(string name) : base(name)
    {
    }
}
