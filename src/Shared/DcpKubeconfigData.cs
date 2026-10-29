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
        var lines = content.AsSpan();
        foreach (var range in lines.Split('\n'))
        {
            var line = lines[range].Trim();
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }
            switch (line[..separator])
            {
                case "server": server ??= ReadScalar(line[(separator + 1)..]); break;
                case "token": token ??= ReadScalar(line[(separator + 1)..]); break;
                case "certificate-authority-data": authority ??= ReadScalar(line[(separator + 1)..]); break;
                case "client-certificate-data": certificate ??= ReadScalar(line[(separator + 1)..]); break;
                case "client-key-data": key ??= ReadScalar(line[(separator + 1)..]); break;
            }
        }

        return new(server, token, authority, certificate, key);
    }

    private static string? ReadScalar(ReadOnlySpan<char> value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        return value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1].ToString()
            : value.ToString();
    }
}
