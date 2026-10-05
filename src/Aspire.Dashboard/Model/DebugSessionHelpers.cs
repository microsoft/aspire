// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Aspire.Dashboard.Configuration;

namespace Aspire.Dashboard.Model;

internal static class DebugSessionHelpers
{
    public static HttpClient CreateHttpClient(Uri? debugSessionUri, string? token, X509Certificate2? cert, Func<HttpClientHandler, HttpMessageHandler>? createHandler, string? dcpInstanceId = null)
    {
        var handler = new HttpClientHandler();
        if (cert is not null)
        {
            handler.ServerCertificateCustomValidationCallback = (_, c, _, e) =>
            {
                // Server certificate is already considered valid.
                if (e == SslPolicyErrors.None)
                {
                    return true;
                }

                if (c == null)
                {
                    return false;
                }

                // Certificate isn't immediately valid. Check if it is the same as the one we expect.
                // It's ok that comparison isn't time constant because this is public information.
                return cert.RawData.SequenceEqual(c.RawData);
            };
        }

        var resolvedHandler = createHandler?.Invoke(handler) ?? handler;
        var client = new HttpClient(resolvedHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        if (debugSessionUri is not null)
        {
            client.BaseAddress = debugSessionUri;
        }

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Aspire Dashboard");
        if (token is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }
        if (!string.IsNullOrEmpty(dcpInstanceId))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Microsoft-Developer-DCP-Instance-ID", dcpInstanceId);
        }

        return client;
    }

    public static bool HasDebugSession(
        DebugSessionOptions debugSession,
        out X509Certificate2? serverCert,
        [NotNullWhen(true)] out Uri? debugSessionUri,
        [NotNullWhen(true)] out string? token)
    {
        if (debugSession.Port is not null && debugSession.Token is not null)
        {
            serverCert = debugSession.GetServerCertificate();
            var scheme = serverCert is null ? "http" : "https";
            debugSessionUri = new Uri($"{scheme}://localhost:{debugSession.Port}");
            token = debugSession.Token;
            return true;
        }

        debugSessionUri = null;
        token = null;
        serverCert = null;
        return false;
    }
}
