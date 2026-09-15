// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;

/// <summary>
/// Represents a zero-capability resource whose generated Promise wrapper name can collide with
/// <see cref="TestPromiseNameCollisionResourcePromise"/>.
/// </summary>
public class TestPromiseNameCollisionResource(string name) : Resource(name);

/// <summary>
/// Represents a resource whose name matches the Promise wrapper generated for
/// <see cref="TestPromiseNameCollisionResource"/>.
/// </summary>
public class TestPromiseNameCollisionResourcePromise(string name) : Resource(name);
