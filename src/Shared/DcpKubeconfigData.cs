// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

/// <summary>Reads scalar connection material from DCP's generated kubeconfig.</summary>
internal sealed record DcpKubeconfigData(
    string? Server,
    string? Token,
    string? CertificateAuthorityData,
    string? ClientCertificateData,
    string? ClientKeyData)
{
    public static DcpKubeconfigData Parse(string content)
    {
        string? server = null, token = null, authority = null, certificate = null, key = null;
        // DCP writes one generated context with scalar connection material:
        //   server: https://127.0.0.1:<port>
        //   certificate-authority-data: <base64 certificate>
        //   token: <bearer token>
        // It can instead use client-certificate-data / client-key-data. This is
        // not a general YAML or multi-context kubeconfig parser.
        foreach (var line in content.Split('\n'))
        {
            server ??= ReadScalar(line, "server");
            token ??= ReadScalar(line, "token");
            authority ??= ReadScalar(line, "certificate-authority-data");
            certificate ??= ReadScalar(line, "client-certificate-data");
            key ??= ReadScalar(line, "client-key-data");
        }

        return new(server, token, authority, certificate, key);
    }

    private static string? ReadScalar(string line, string key)
    {
        var text = line.Trim();
        if (!text.StartsWith(key + ":", StringComparison.Ordinal))
        {
            return null;
        }

        var value = text[(key.Length + 1)..].Trim();
        if (value.Length == 0)
        {
            return null;
        }

        return value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
    }
}
