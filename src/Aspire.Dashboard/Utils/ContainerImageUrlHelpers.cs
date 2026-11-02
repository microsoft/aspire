// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Dashboard.Utils;

internal static class ContainerImageUrlHelpers
{
    /// <summary>
    /// Attempts to build a browsable registry page URL for a container image reference, such as
    /// <c>redis:7</c>, <c>mcr.microsoft.com/dotnet/aspire-dashboard:9.0</c>, or <c>myuser/myimage:latest</c>.
    /// </summary>
    /// <remarks>
    /// Only registries with a well-known, stable web catalog URL scheme are supported (Docker Hub and MCR).
    /// Other registries (private registries, GHCR, etc.) don't have a URL scheme we can confidently construct
    /// without risking a broken or misleading link, so they're left without a link.
    /// </remarks>
    public static bool TryGetRegistryPageUrl(string image, [NotNullWhen(returnValue: true)] out string? url)
    {
        url = null;

        // Image references look like: [registry[:port]/]repository[:tag|@digest]
        var reference = image;

        var atIndex = reference.IndexOf('@');
        if (atIndex >= 0)
        {
            reference = reference[..atIndex];
        }

        string? registry = null;
        var slashIndex = reference.IndexOf('/');
        if (slashIndex >= 0)
        {
            var firstSegment = reference[..slashIndex];

            // A registry host contains a '.' or ':' (port), or is exactly "localhost"; otherwise the first
            // path segment is a Docker Hub namespace (e.g. "library" or a username), not a registry host.
            if (firstSegment.Contains('.') || firstSegment.Contains(':') || firstSegment == "localhost")
            {
                registry = firstSegment;
                reference = reference[(slashIndex + 1)..];
            }
        }

        var colonIndex = reference.LastIndexOf(':');
        var repository = colonIndex >= 0 ? reference[..colonIndex] : reference;
        if (string.IsNullOrEmpty(repository))
        {
            return false;
        }

        switch (registry)
        {
            case null or "docker.io" or "index.docker.io" or "registry-1.docker.io":
                // Docker Hub. Official images have no namespace and live under "_/<repo>"; namespaced
                // images (user or org) live under "r/<namespace>/<repo>". The "library/" namespace is
                // Docker Hub's internal name for official images, so strip it back to the "_/<repo>" form.
                repository = repository.StartsWith("library/", StringComparison.Ordinal)
                    ? repository["library/".Length..]
                    : repository;
                url = repository.Contains('/')
                    ? $"https://hub.docker.com/r/{repository}"
                    : $"https://hub.docker.com/_/{repository}";
                return true;
            case "mcr.microsoft.com":
                url = $"https://mcr.microsoft.com/en-us/product/{repository}";
                return true;
            default:
                return false;
        }
    }
}
