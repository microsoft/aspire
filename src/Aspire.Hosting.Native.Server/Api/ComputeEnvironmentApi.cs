// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Native.Api;

internal sealed partial class Composition
{
    /// <summary>Declares a compute environment without relying on an integration-specific resource type naming convention.</summary>
    [AspireExport]
    public DeclaredResource AddComputeEnvironment(string name, string typeId) => new(this, Model.AddComputeEnvironment(name, typeId));
}

internal sealed partial class DeclaredResource
{
    /// <summary>Assigns compute to a declared environment without creating a runtime readiness dependency.</summary>
    [AspireExport]
    public void WithComputeEnvironment(DeclaredResource environment)
    {
        if (!ReferenceEquals(Composition, environment.Composition))
        {
            throw new ArgumentException("The compute environment belongs to another composition.", nameof(environment));
        }
        Composition.Model.SetComputeEnvironment(Handle, environment.Handle);
    }
}
