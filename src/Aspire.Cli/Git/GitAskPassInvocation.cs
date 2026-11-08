// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Security;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aspire.Cli.Git;

internal static class GitAskPassInvocation
{
    public static bool Matches(string[] args) => args is ["git", "askpass" or "askpass-server", ..];

    public static async Task<int> InvokeAsync(string[] args, Func<string, string?> environment,
        TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            if (!OperatingSystem.IsMacOS() || args is not ["git", "askpass", var prompt] ||
                string.IsNullOrEmpty(prompt) || prompt.Length > GitAskPassProtocol.MaxPromptBytes)
            {
                throw new AuthenticationException("Invalid Git credential callback invocation.");
            }
            var path = environment(GitAskPassProtocol.SocketVariable)
                ?? throw new AuthenticationException("Missing Git credential callback endpoint.");
            var key = ParseKey(environment(GitAskPassProtocol.KeyVariable));
            var pin = ParseKey(environment(GitAskPassProtocol.PinVariable));
            try
            {
                GitAskPassDirectory.ValidateEndpoint(path);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(GitAskPassProtocol.RequestTimeout + TimeSpan.FromSeconds(10));
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token);
                await using var stream = new SslStream(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: false,
                    (_, certificate, _, _) => certificate is not null &&
                        CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), pin));
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "aspire-git-askpass",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, timeout.Token);
                // Do not reveal the authorization key (or the Git prompt) until the
                // operation's in-memory server certificate has been authenticated.
                await stream.WriteAsync(key, timeout.Token);
                await GitAskPassProtocol.WriteAsync(stream, prompt, GitAskPassProtocol.MaxPromptBytes, timeout.Token);
                var response = await GitAskPassProtocol.ReadAsync(stream, GitAskPassProtocol.MaxResponseBytes, timeout.Token);
                GitAskPassProtocol.ValidateResponse(response);
                await output.WriteLineAsync(response.AsMemory(), timeout.Token);
                await output.FlushAsync(timeout.Token);
                return 0;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(pin);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or
            CryptographicException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            // Raw prompts, remote URLs, response values and exceptions are private.
            // This early entry point intentionally has no logger, DI or telemetry.
            await error.WriteLineAsync("Git credential callback failed or was cancelled.".AsMemory(), CancellationToken.None);
            return 1;
        }
    }

    internal static byte[] ParseKey(string? value)
    {
        if (value is null || value.Length != 64 || !value.All(char.IsAsciiHexDigit))
        {
            throw new AuthenticationException("Invalid Git credential callback authorization.");
        }
        return Convert.FromHexString(value);
    }

    internal static string CreateHelperCommand(string processPath, string assemblyPath)
    {
        if (!Path.IsPathFullyQualified(processPath))
        {
            throw new InvalidOperationException("Git credential callbacks require an absolute CLI executable.");
        }
        var command = Quote(processPath);
        if (Path.GetFileNameWithoutExtension(processPath) == "dotnet")
        {
            if (!Path.IsPathFullyQualified(assemblyPath) || Path.GetFileName(assemblyPath) != "aspire.dll")
            {
                throw new InvalidOperationException("Git credential callbacks require the Aspire assembly.");
            }
            command += " " + Quote(assemblyPath);
        }
        else if (Path.GetFileNameWithoutExtension(processPath) != "aspire")
        {
            throw new InvalidOperationException("Git credential callbacks require the Aspire executable.");
        }
        // The operation-owned executable script launches this fixed command, then
        // forwards Git's prompt through "$@". Only trusted launcher paths are quoted.
        // https://git-scm.com/docs/gitcredentials#_requesting_credentials
        return command + " git askpass";
    }

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty assembly location is expected for NativeAOT; only a dotnet-hosted development CLI needs its physical assembly.")]
    internal static string CreateCurrentHelperCommand()
        => CreateHelperCommand(Environment.ProcessPath ?? throw new InvalidOperationException("The Aspire executable path is unavailable."),
            typeof(Program).Assembly.Location);

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "NativeAOT launches the current executable; a dotnet development launcher uses its physical CLI assembly.")]
    internal static ProcessStartInfo CreateServerStartInfo()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("The Aspire executable path is unavailable.");
        // Validate the same launch shapes as the Git helper, but pass arguments directly.
        CreateHelperCommand(path, typeof(Program).Assembly.Location);
        var info = new ProcessStartInfo(path);
        if (Path.GetFileNameWithoutExtension(path) == "dotnet")
        {
            info.ArgumentList.Add(typeof(Program).Assembly.Location);
        }
        info.ArgumentList.Add("git");
        info.ArgumentList.Add("askpass-server");
        return info;
    }

    private static string Quote(string value)
    {
        if (value.Any(char.IsControl))
        {
            throw new InvalidOperationException("Invalid Git credential callback launcher.");
        }
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
