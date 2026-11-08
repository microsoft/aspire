// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Shared;
using Spectre.Console;

namespace Aspire.Cli.Git;

[SupportedOSPlatform("macos")]
internal sealed class GitAskPassServer : IAsyncDisposable
{
    // Serialize credential UI across concurrent Git operations as well as connections.
    private static readonly SemaphoreSlim s_promptLock = new(1);
    private readonly Socket _listener;
    private readonly X509Certificate2 _certificate;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly CancellationTokenSource _shutdown;
    private readonly Uri _destination;
    private readonly Func<string, CancellationToken, Task<string>> _prompt;
    private readonly Task _pump;
    private bool _failed;

    public CancellationToken CancellationToken => _shutdown.Token;
    public bool Failed => Volatile.Read(ref _failed);

    public GitAskPassServer(Uri destination, string socketName, ulong directoryInode, Func<string, CancellationToken, Task<string>> prompt, CancellationToken cancellationToken)
    {
        _destination = destination;
        _prompt = prompt;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Socket? listener = null;
        X509Certificate2? certificate = null;
        try
        {
            // The private key stays in this process; nothing is exported to disk or Git.
            certificate = CertificateFactory.CreateSelfSignedCertificate(
                new X500DistinguishedName("CN=aspire-git-askpass"), [],
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            GitAskPassDirectory.BindInCurrentDirectory(listener, socketName, directoryInode);
            listener.Listen(4);
            _listener = listener;
            _certificate = certificate;
            _pump = RunAsync();
        }
        catch
        {
            listener?.Dispose();
            certificate?.Dispose();
            _shutdown.Dispose();
            CryptographicOperations.ZeroMemory(_key);
            throw;
        }
    }

    public async Task WriteHandshakeAsync(Stream output, CancellationToken cancellationToken)
    {
        await GitAskPassProtocol.WriteAsync(output, Convert.ToHexString(_key), 64, cancellationToken);
        await GitAskPassProtocol.WriteAsync(output, _certificate.GetCertHashString(HashAlgorithmName.SHA256), 64, cancellationToken);
    }

    public Task Completion => _pump;

    public static async Task<int> InvokeAsync(Stream input, Stream output, TextWriter error)
    {
        using var lifetime = new CancellationTokenSource();
        TaskCompletionSource<string>? pending = null;
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var inodeText = await GitAskPassProtocol.ReadAsync(input, 32, startup.Token);
            var name = await GitAskPassProtocol.ReadAsync(input, 64, startup.Token);
            var destinationText = await GitAskPassProtocol.ReadAsync(input, GitAskPassProtocol.MaxPromptBytes, startup.Token);
            if (!ulong.TryParse(inodeText, NumberStyles.None, CultureInfo.InvariantCulture, out var inode) ||
                !Uri.TryCreate(destinationText, UriKind.Absolute, out var destination) ||
                destination.Scheme is not ("https" or "http") || destination.UserInfo.Length != 0 ||
                destination.Query.Length != 0 || destination.Fragment.Length != 0)
            {
                throw new AuthenticationException("Invalid Git credential callback server startup.");
            }
            await using var server = new GitAskPassServer(destination, name, inode, async (label, token) =>
            {
                var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref pending, response);
                try
                {
                    await GitAskPassProtocol.WriteAsync(output, label, GitAskPassProtocol.MaxPromptBytes, token);
                    return await response.Task.WaitAsync(token);
                }
                finally
                {
                    Volatile.Write(ref pending, null);
                }
            }, lifetime.Token);
            await server.WriteHandshakeAsync(output, startup.Token);
            var reader = ReadResponsesAsync();
            try
            {
                await server.Completion;
                return server.Failed ? 1 : 0;
            }
            finally
            {
                await lifetime.CancelAsync();
                // Console pipe reads can be uncancellable on Unix. This isolated
                // process exits now; joining an idle stdin read would keep it alive
                // and prevent the parent from dismissing a disconnected helper's UI.
                if (reader.IsCompleted)
                {
                    await reader;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or
            CryptographicException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            await error.WriteLineAsync("Git credential callback server failed.");
            return 1;
        }

        async Task ReadResponsesAsync()
        {
            try
            {
                while (true)
                {
                    var value = await GitAskPassProtocol.ReadAsync(input, GitAskPassProtocol.MaxResponseBytes, lifetime.Token);
                    var response = Volatile.Read(ref pending);
                    if (response is null || !response.TrySetResult(value))
                    {
                        throw new AuthenticationException("Unsolicited Git credential callback response.");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException)
            {
                // EOF also detects a crashed parent while no Git helper is connected.
                await lifetime.CancelAsync();
            }
        }
    }
    internal static string GetPrompt(string rawPrompt, Uri destination)
    {
        // Git emits "Username for 'https://host': " or
        // "Password for 'https://user@host/repo': ". With useHttpPath=false
        // the path is omitted. Never display/log the raw URL or its userinfo.
        var username = rawPrompt.StartsWith("Username for '", StringComparison.Ordinal);
        var prefix = username ? "Username for '" : "Password for '";
        if (!rawPrompt.StartsWith(prefix, StringComparison.Ordinal) || !rawPrompt.EndsWith("': ", StringComparison.Ordinal) ||
            rawPrompt.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) ||
            !Uri.TryCreate(rawPrompt[prefix.Length..^3], UriKind.Absolute, out var uri) ||
            uri.Scheme != destination.Scheme || uri.IdnHost != destination.IdnHost || uri.Port != destination.Port ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Contains(':') ||
            (uri.AbsolutePath != "/" && uri.AbsolutePath.TrimEnd('/') != destination.AbsolutePath.TrimEnd('/')))
        {
            throw new AuthenticationException("Git credential callback destination mismatch.");
        }
        var origin = destination.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        return string.Format(CultureInfo.CurrentCulture,
            username ? InteractionServiceStrings.GitUsernamePrompt : InteractionServiceStrings.GitPasswordPrompt,
            origin.EscapeMarkup());
    }

    private async Task RunAsync()
    {
        try
        {
            // Bound unauthenticated attempts and handshake time, not just frame sizes.
            for (var attempts = 0; attempts < 8; attempts++)
            {
                using var socket = await _listener.AcceptAsync(_shutdown.Token);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await using var stream = new SslStream(new NetworkStream(socket, ownsSocket: false));
                var authorized = false;
                try
                {
                    await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    }, requestTimeout.Token);
                    var key = new byte[32];
                    try
                    {
                        await stream.ReadExactlyAsync(key, requestTimeout.Token);
                        if (!CryptographicOperations.FixedTimeEquals(key, _key))
                        {
                            throw new AuthenticationException("Git credential callback authorization failed.");
                        }
                        authorized = true;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(key);
                    }
                    var raw = await GitAskPassProtocol.ReadAsync(stream, GitAskPassProtocol.MaxPromptBytes, requestTimeout.Token);
                    var prompt = GetPrompt(raw, _destination);
                    requestTimeout.CancelAfter(GitAskPassProtocol.RequestTimeout);
                    using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(requestTimeout.Token);
                    var monitor = MonitorDisconnectAsync(stream, requestTimeout, monitorCancellation.Token);
                    try
                    {
                        await s_promptLock.WaitAsync(requestTimeout.Token);
                        string response;
                        try
                        {
                            response = await _prompt(prompt, requestTimeout.Token);
                            GitAskPassProtocol.ValidateResponse(response);
                        }
                        finally
                        {
                            s_promptLock.Release();
                        }
                        // Once UI completes, writes detect a lost helper. Stop the read
                        // monitor first: a successful helper exits immediately after reading
                        // its response and must not race cancellation of our final flush.
                        await monitorCancellation.CancelAsync();
                        await monitor;
                        await GitAskPassProtocol.WriteAsync(stream, response, GitAskPassProtocol.MaxResponseBytes, requestTimeout.Token);
                    }
                    finally
                    {
                        await monitorCancellation.CancelAsync();
                        await monitor;
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or
                    OperationCanceledException or InvalidOperationException or ExtensionOperationCanceledException)
                {
                    // Unauthorized callers never reach UI. An authenticated rejection,
                    // disconnect, timeout or UI failure ends the operation, with no fallback.
                    if (authorized && !_shutdown.IsCancellationRequested)
                    {
                        Volatile.Write(ref _failed, true);
                        await _shutdown.CancelAsync();
                        return;
                    }
                }
            }
            Volatile.Write(ref _failed, true);
            await _shutdown.CancelAsync();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private static async Task MonitorDisconnectAsync(Stream stream, CancellationTokenSource request, CancellationToken cancellationToken)
    {
        try
        {
            // A helper that exits must dismiss its UI immediately; no second frame is valid.
            await stream.ReadExactlyAsync(new byte[1], cancellationToken);
            await request.CancelAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            await request.CancelAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        try
        {
            await _pump;
        }
        finally
        {
            _listener.Dispose();
            _certificate.Dispose();
            CryptographicOperations.ZeroMemory(_key);
            _shutdown.Dispose();
        }
    }
}
