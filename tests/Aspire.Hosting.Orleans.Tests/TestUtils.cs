// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Orleans.Tests;

internal static class TestUtils
{
    public static async Task<Dictionary<string, object>> GetEnvironmentVariablesAsync(IResource resource, IDistributedApplicationBuilder builder)
    {
        var env = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, env);

        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context).ConfigureAwait(false);
        }

        return env;
    }
}
