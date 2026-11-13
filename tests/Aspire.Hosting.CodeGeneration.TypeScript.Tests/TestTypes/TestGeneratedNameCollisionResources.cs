// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.NameCollisionOne
{
    public sealed class SameNameResource(string name) : Resource(name);
}

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.NameCollisionTwo
{
    public sealed class SameNameResource(string name) : Resource(name);
}
