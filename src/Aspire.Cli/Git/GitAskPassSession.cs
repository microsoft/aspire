// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Spectre.Console;

namespace Aspire.Cli.Git;

internal sealed class GitAskPassSession : IAsyncDisposable
{
    private static readonly SemaphoreSlim s_promptLock = new(1);
    private readonly GitAskPassDirectory _directory;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _shutdown;
    private readonly Uri _destination;
    private readonly Func<string, CancellationToken, Task<string>> _prompt;
    private readonly Task _pump;
    private bool _failed;

    public CancellationToken CancellationToken => _shutdown.Token;
    public bool Failed => Volatile.Read(ref _failed);

    public GitAskPassSession(Uri destination, Func<string, CancellationToken, Task<string>> prompt, CancellationToken cancellationToken)
    {
        _destination = destination;
        _prompt = prompt;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        GitAskPassDirectory? directory = null;
        Socket? listener = null;
        try
        {
            directory = GitAskPassDirectory.Create();
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            directory.Bind(listener);
            listener.Listen(4);
            _directory = directory;
            _listener = listener;
            _pump = RunAsync();
        }
        catch
        {
            listener?.Dispose();
            directory?.Dispose();
            _shutdown.Dispose();
            throw;
        }
    }

    public void Configure(ProcessStartInfo startInfo, string helperCommand)
    {
        startInfo.Environment["GIT_ASKPASS"] = _directory.CreateHelper(helperCommand);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment[GitAskPassProtocol.SocketVariable] = _directory.SocketPath;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
        startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
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
            for (var attempts = 0; attempts < 8; attempts++)
            {
                using var socket = await _listener.AcceptAsync(_shutdown.Token);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                request.CancelAfter(TimeSpan.FromSeconds(10));
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                var raw = await GitAskPassProtocol.ReadAsync(stream, GitAskPassProtocol.MaxPromptBytes, request.Token);
                var label = GetPrompt(raw, _destination);
                request.CancelAfter(GitAskPassProtocol.RequestTimeout);
                using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
                var monitor = MonitorDisconnectAsync(stream, request, monitorCancellation.Token);
                try
                {
                    await s_promptLock.WaitAsync(request.Token);
                    string response;
                    try
                    {
                        response = await _prompt(label, request.Token);
                        GitAskPassProtocol.ValidateResponse(response);
                    }
                    finally
                    {
                        s_promptLock.Release();
                    }
                    // Stop the read monitor before sending a successful response:
                    // the helper exits immediately after consuming that frame.
                    await monitorCancellation.CancelAsync();
                    await monitor;
                    await GitAskPassProtocol.WriteAsync(stream, response, GitAskPassProtocol.MaxResponseBytes, request.Token);
                }
                finally
                {
                    await monitorCancellation.CancelAsync();
                    await monitor;
                }
            }
            Volatile.Write(ref _failed, true);
            await _shutdown.CancelAsync();
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or ArgumentException or
            OperationCanceledException or InvalidOperationException or ExtensionOperationCanceledException)
        {
            // Rejection, invalid input, disconnect and UI failure end this operation
            // without fallback. Do not log raw prompts, responses or exception text.
            if (!_shutdown.IsCancellationRequested)
            {
                Volatile.Write(ref _failed, true);
                await _shutdown.CancelAsync();
            }
        }
    }

    private static async Task MonitorDisconnectAsync(Stream stream, CancellationTokenSource request, CancellationToken cancellationToken)
    {
        try
        {
            // A helper that exits must dismiss its UI; no second frame is valid.
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
            _shutdown.Dispose();
            _directory.Dispose();
        }
    }
}
