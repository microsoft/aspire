// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;

namespace Aspire.Hosting.CodeGeneration.Go.Tests;

/// <summary>
/// Exports that only the Go code generation tests use. The TestTypes shared with the other code
/// generators stay unchanged so their snapshots are not affected.
/// </summary>
public static class GoTestExtensions
{
    /// <summary>
    /// Sets a field from a union that allows a number, like KubernetesManifestResource.WithField.
    /// </summary>
    /// <ats-summary>Sets a scalar field value</ats-summary>
    [AspireExport("withScalarField")]
    public static IResourceBuilder<TestRedisResource> WithScalarField(
        this IResourceBuilder<TestRedisResource> builder,
        string path,
        [AspireUnion(typeof(string), typeof(double), typeof(bool))] object value)
    {
        return builder;
    }
}
