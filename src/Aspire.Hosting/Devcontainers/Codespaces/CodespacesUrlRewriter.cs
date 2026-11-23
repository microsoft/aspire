// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Devcontainers.Codespaces;

internal sealed class CodespacesUrlRewriter(IOptions<CodespacesOptions> options)
{
    public bool TryRewriteResourceUrl(string url, [NotNullWhen(true)] out string? rewrittenUrl)
    {
        rewrittenUrl = null;
        if (!options.Value.IsCodespace ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        rewrittenUrl = RewriteUrl(uri);
        return true;
    }

    public string RewriteUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (!options.Value.IsCodespace)
        {
            return url;
        }

        return RewriteUrl(new Uri(url));
    }

    public string RewriteUrl(Uri uri)
    {
        if (!options.Value.IsCodespace)
        {
            return uri.ToString();
        }

        // Forwarded addresses use <codespace>-<local-port>.<forwarding-domain>;
        // the local port is encoded in the hostname, not in the public authority.
        // The public URL uses HTTPS independently of the protocol forwarded to the service:
        // https://docs.github.com/en/codespaces/developing-in-a-codespace/forwarding-ports-in-your-codespace#using-command-line-tools-and-rest-clients-to-access-ports
        var codespacesUrl = $"https://{options.Value.CodespaceName}-{uri.Port}.{options.Value.PortForwardingDomain}{uri.AbsolutePath}{uri.Query}{uri.Fragment}";
        return codespacesUrl;
    }
}
