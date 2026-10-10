// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.CodeGeneration.Python.Tests.TestTypes;

public static class PythonTestExtensions
{
    /// <summary>
    /// Configures a test resource through a resource builder callback.
    /// </summary>
    /// <param name="builder">The test resource builder.</param>
    /// <param name="configure">The callback used to configure the test resource.</param>
    /// <returns>The test resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<TestRedisResource> WithPythonBuilderCallback(
        this IResourceBuilder<TestRedisResource> builder,
        Action<IResourceBuilder<TestRedisResource>> configure)
    {
        configure(builder);

        return builder;
    }
}
