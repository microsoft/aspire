// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if ASPIRE_TYPESCRIPT_CODEGEN_TESTS
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;

/// <summary>
/// A bare marker resource interface that contributes no capabilities of its own beyond the base
/// fluent chain, mirroring <c>IResourceWithServiceDiscovery</c> and <c>IComputeEnvironmentResource</c>.
/// </summary>
/// <remarks>
/// This is the shape that regressed in https://github.com/microsoft/aspire/issues/19507: a builder
/// for such a type that is returned directly by an export needs a Promise wrapper even though it
/// has no chainable members of its own.
/// The fixture lives here, rather than relying on an in-the-box type that happens to have zero
/// capabilities today, so the coverage cannot silently disappear when that type gains a capability.
/// </remarks>
public interface ITestMarkerResource : IResource
{
}

/// <summary>
/// Concrete resource behind <see cref="ITestMarkerResource"/>.
/// </summary>
public class TestMarkerResource : Resource, ITestMarkerResource
{
    public TestMarkerResource(string name) : base(name)
    {
    }
}
#endif
