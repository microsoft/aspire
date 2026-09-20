// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using k8s.Models;

namespace Aspire.Hosting.Tests.Helpers;

internal static class KubernetesHelper
{
    public static async Task<T> GetResourceByNameAsync<T>(IKubernetesService kubernetes, string resourceName, string resourceNameSuffix, Func<T, bool> ready, CancellationToken cancellationToken = default) where T : CustomResource, IKubernetesStaticMetadata
    {
        await foreach (var (_, r) in kubernetes.WatchAsync<T>(cancellationToken: cancellationToken))
        {
            var name = r.Name();

            if ((name == resourceName || (name.StartsWith(resourceName + "-", StringComparison.Ordinal) && name.EndsWith("-" + resourceNameSuffix, StringComparison.Ordinal))) && ready(r))
            {
                return r;
            }
        }

        throw new InvalidOperationException($"Resource {resourceName}, not ready");
    }

    public static async Task<T> GetResourceByNameMatchAsync<T>(IKubernetesService kubernetes, string resourceNamePattern, Func<T, bool> ready, CancellationToken cancellationToken = default) where T : CustomResource, IKubernetesStaticMetadata
    {
        await foreach (var (_, r) in kubernetes.WatchAsync<T>(cancellationToken: cancellationToken))
        {
            var name = r.Name();

            if (Regex.IsMatch(name, resourceNamePattern) && ready(r))
            {
                return r;
            }
        }

        throw new InvalidOperationException($"Pattern {resourceNamePattern} did not match name of any resource that reached ready state");
    }
}
