// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Git;

[SupportedOSPlatform("macos")]
internal sealed class GitAskPassSession : IAsyncDisposable
{
    private static readonly SemaphoreSlim s_promptLock = new(1);
    private readonly GitAskPassDirectory _directory;
    private readonly CancellationTokenSource _shutdown;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _process;
    private string? _key;
    private string? _pin;
    private bool _failed;

    public CancellationToken CancellationToken => _shutdown.Token;
    public bool Failed => Volatile.Read(ref _failed);

    private GitAskPassSession(Uri destination, Func<string, CancellationToken, Task<string>> prompt,
        ProcessStartInfo serverStartInfo, CancellationToken cancellationToken)
    {
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _directory = GitAskPassDirectory.Create();
        serverStartInfo.WorkingDirectory = Path.GetDirectoryName(_directory.SocketPath)!;
        serverStartInfo.RedirectStandardInput = true;
        serverStartInfo.RedirectStandardOutput = true;
        serverStartInfo.RedirectStandardError = true;
        foreach (var variable in new[] { GitAskPassProtocol.SocketVariable, GitAskPassProtocol.KeyVariable, GitAskPassProtocol.PinVariable })
        {
            serverStartInfo.Environment.Remove(variable);
        }
        _process = RunProcessAsync(serverStartInfo, destination, prompt);
    }

    public static async Task<GitAskPassSession> CreateAsync(Uri destination, Func<string, CancellationToken, Task<string>> prompt,
        ProcessStartInfo serverStartInfo, CancellationToken cancellationToken)
    {
        var session = new GitAskPassSession(destination, prompt, serverStartInfo, cancellationToken);
        try
        {
            await session._ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            session._directory.VerifyBoundSocket();
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public void Configure(ProcessStartInfo startInfo, string helperCommand)
    {
        startInfo.Environment["GIT_ASKPASS"] = _directory.CreateHelper(helperCommand);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment[GitAskPassProtocol.SocketVariable] = _directory.SocketPath;
        startInfo.Environment[GitAskPassProtocol.KeyVariable] = _key;
        startInfo.Environment[GitAskPassProtocol.PinVariable] = _pin;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
        startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
    }

    private async Task RunProcessAsync(ProcessStartInfo startInfo, Uri destination, Func<string, CancellationToken, Task<string>> prompt)
    {
        // The execution factory's line callbacks cannot support a duplex binary
        // credential channel. Use the existing raw capture runner, with logging disabled.
        await ProcessCaptureRunner.RunAsync<bool>(startInfo, Timeout.InfiniteTimeSpan,
            async (process, token) =>
            {
                using var promptCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                var exited = CancelPromptOnExitAsync(process, promptCancellation, token);
                var input = process.StandardInput.BaseStream;
                var output = process.StandardOutput.BaseStream;
                var stderr = process.StandardError.ReadToEndAsync(token);
                await GitAskPassProtocol.WriteAsync(input, _directory.Inode.ToString(CultureInfo.InvariantCulture), 32, token);
                await GitAskPassProtocol.WriteAsync(input, Path.GetFileName(_directory.SocketPath), 64, token);
                await GitAskPassProtocol.WriteAsync(input,
                    destination.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped),
                    GitAskPassProtocol.MaxPromptBytes, token);
                _key = await GitAskPassProtocol.ReadAsync(output, 64, token);
                _pin = await GitAskPassProtocol.ReadAsync(output, 64, token);
                GitAskPassInvocation.ParseKey(_key);
                GitAskPassInvocation.ParseKey(_pin);
                _ready.TrySetResult();
                try
                {
                    while (true)
                    {
                        var label = await GitAskPassProtocol.ReadAsync(output, GitAskPassProtocol.MaxPromptBytes, token);
                        await s_promptLock.WaitAsync(promptCancellation.Token);
                        try
                        {
                            var response = await prompt(label, promptCancellation.Token);
                            GitAskPassProtocol.ValidateResponse(response);
                            await GitAskPassProtocol.WriteAsync(input, response, GitAskPassProtocol.MaxResponseBytes, token);
                        }
                        finally
                        {
                            s_promptLock.Release();
                        }
                    }
                }
                finally
                {
                    // EOF dismisses the isolated server if the parent pump has failed.
                    process.StandardInput.Close();
                    await stderr;
                    await exited;
                }
            }, () => false, NullLogger.Instance, _shutdown.Token);
        if (!_shutdown.IsCancellationRequested)
        {
            Volatile.Write(ref _failed, true);
            _ready.TrySetException(new IOException("Git credential callback server failed."));
            await _shutdown.CancelAsync();
        }
    }

    private static async Task CancelPromptOnExitAsync(Process process, CancellationTokenSource prompt, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await prompt.CancelAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        try
        {
            await _process;
        }
        finally
        {
            _key = null;
            _pin = null;
            _shutdown.Dispose();
            _directory.Dispose();
        }
    }
}
