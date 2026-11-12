// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Aspire.Shared;
using Aspire.Hosting.Native.Server;
using Aspire.Hosting.Native.Runtime;
using Aspire.Hosting.Native.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Native.Dcp;

/// <summary>Owns a local DCP process and its authenticated administrative API.</summary>
internal sealed class DcpApiClient : IAsyncDisposable
{
    private const string Api = "/apis/usvc-dev.developer.microsoft.com/v1/";
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("aspire-native-dcp-");
    private Process? _process;
    private Task? _output;
    private HttpClient? _client;
    private DcpConnectionMaterial? _connection;
    private readonly NativeServerOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _cleanupGate = new();
    private Task? _cleanup;

    public DcpApiClient() : this(new())
    {
    }

    public DcpApiClient(NativeServerOptions options) : this(options, NullLogger.Instance)
    {
    }

    public DcpApiClient(NativeServerOptions options, ILogger logger)
    {
        options.Validate();
        _options = options;
        _logger = logger;
    }

    internal DcpApiClient(NativeServerOptions options, HttpClient client) : this(options)
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
        _client = client;
    }

    public async Task StartAsync(string executable, CancellationToken cancellationToken)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("DCP has already started.");
        }
        var kubeconfig = Path.Combine(_directory.FullName, "kubeconfig");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Keep DCP's cross-platform --monitor contract, with kernel-enforced
        // ownership on the platforms where .NET supports it.
        // https://learn.microsoft.com/dotnet/api/system.diagnostics.processstartinfo.killonparentexit
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            start.KillOnParentExit = true;
        }
        foreach (var argument in new[] { "start-apiserver", "--monitor",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--kubeconfig", kubeconfig })
        {
            start.ArgumentList.Add(argument);
        }
        foreach (var name in start.Environment.Keys.Where(DcpEnvironmentVariables.IsExcluded).ToArray())
        {
            start.Environment.Remove(name);
        }
        start.Environment["DCP_SESSION_FOLDER"] = _directory.FullName;
        _process = Process.Start(start) ?? throw new InvalidOperationException("DCP did not start.");
        _output = DrainOutputAsync(_process);

        // Bridge shared readiness tracing into the native classified operation
        // stream without recording connection material or exception messages.
        using var operation = new NativeDiagnostics.Operation("dcp.connection.ready", null, null, _options.TimeProvider);
        try
        {
            _connection = await DcpConnectionMaterial.WaitAsync(kubeconfig, () => _process.HasExited,
                _options.ControllerRequestTimeout, _options.RetryInterval, message => _logger.LogDebug("{DcpReadiness}", message),
                _options.TimeProvider, cancellationToken).ConfigureAwait(false);
            operation.Succeeded = true;
        }
        catch (Exception exception)
        {
            operation.ErrorType = exception.GetType().FullName;
            throw;
        }
        _client = _connection.Client;
    }

    private async Task DrainOutputAsync(Process process)
    {
        // ReadAllLinesAsync multiplexes both redirected streams, avoiding pipe
        // deadlocks and giving shutdown an observable, awaitable drain operation.
        // https://learn.microsoft.com/dotnet/api/system.diagnostics.process.readalllinesasync
        await foreach (var line in process.ReadAllLinesAsync().ConfigureAwait(false))
        {
            _logger.LogDebug("{DcpOutput}", line.Content);
        }
    }

    public async Task<DcpResource?> GetAsync(DcpResourceCollection collection, string name, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Get, Api + collection.ToPath() + "/" + name, null,
            DcpJsonContext.Default.DcpResource, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateAsync(DcpResourceCollection collection, string name, JsonElement spec, Dictionary<string, string>? annotations,
        CancellationToken cancellationToken)
    {
        var kind = collection.ToKind();
        var envelope = new DcpResourceEnvelope("usvc-dev.developer.microsoft.com/v1", kind, new DcpMetadata(name, annotations), spec);
        await SendAsync(HttpMethod.Post, Api + collection.ToPath(),
            JsonSerializer.SerializeToElement(envelope, DcpJsonContext.Default.DcpResourceEnvelope),
            DcpJsonContext.Default.JsonElement, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(DcpResourceCollection collection, string name, CancellationToken cancellationToken)
    {
        await SendAsync(HttpMethod.Delete, Api + collection.ToPath() + "/" + name, null,
            DcpJsonContext.Default.JsonElement, cancellationToken).ConfigureAwait(false);
        while (await GetAsync(collection, name, cancellationToken).ConfigureAwait(false) is not null)
        {
            await Task.Delay(_options.RetryInterval, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string[]> ReadLogsAsync(DcpResourceCollection collection, string name, LogStream source, long offset, CancellationToken cancellationToken)
    {
        using var timeout = new TimeProviderCancellation(_options.ControllerLogReadTimeout, _options.TimeProvider, cancellationToken);
        var path = $"{Api}{collection.ToPath()}/{name}/log?source={source.ToWire()}&follow=false&timestamps=false&line_numbers=false&limit=128&skip={offset}";
        using var response = await Client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"DCP log read failed ({(int)response.StatusCode}).");
        }
        using var input = new DcpResponseStream(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), 65536);
        // Preserve a UTF-8 BOM as message content. The byte cap also bounds
        // decoded UTF-16 length; one extra character ensures the read reaches EOF
        // and detects overflow rather than accepting an exactly full buffer.
        using var reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        var text = ArrayPool<char>.Shared.Rent(65537);
        try
        {
            var length = await reader.ReadBlockAsync(text.AsMemory(0, 65537), timeout.Token).ConfigureAwait(false);

            return DcpProtocol.ParseCompleteLogLines(text.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(text);
        }
    }

    private HttpClient Client => _client ?? throw new InvalidOperationException("DCP is not ready.");

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, JsonElement? body, JsonTypeInfo<T> responseType, CancellationToken cancellationToken,
        DcpResponseMode responseMode = DcpResponseMode.Json)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body.Value, DcpJsonContext.Default.JsonElement,
                new MediaTypeHeaderValue(method == HttpMethod.Patch ? "application/merge-patch+json" : "application/json"));
        }
        using var timeout = new TimeProviderCancellation(_options.ControllerRequestTimeout, _options.TimeProvider, cancellationToken);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound && method != HttpMethod.Post)
        {
            return default;
        }
        if (!response.IsSuccessStatusCode)
        {
            // DCP error payloads may echo environment variables or arguments.
            throw new InvalidOperationException($"DCP {method} failed ({(int)response.StatusCode}).");
        }
        if (response.StatusCode == HttpStatusCode.NoContent || responseMode == DcpResponseMode.Empty)
        {
            return default;
        }
        using var input = new DcpResponseStream(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), 1024 * 1024);

        return await JsonSerializer.DeserializeAsync(input, responseType, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Starts dependency-aware shutdown cleanup while resource owners retire.</summary>
    internal Task CleanupResourcesAsync(CancellationToken cancellationToken)
    {
        lock (_cleanupGate)
        {
            if (_cleanup is null || _cleanup.IsFaulted || _cleanup.IsCanceled)
            {
                _cleanup = CleanupResourcesCoreAsync(cancellationToken);
            }

            return _cleanup;
        }
    }

    private async Task CleanupResourcesCoreAsync(CancellationToken cancellationToken)
    {
        await SendAsync(HttpMethod.Patch, "admin/execution", JsonSerializer.SerializeToElement(
            new DcpExecutionRequest(DcpExecutionState.CleaningResources, DcpResourceCleanup.Full), DcpJsonContext.Default.DcpExecutionRequest),
            DcpJsonContext.Default.JsonElement, cancellationToken).ConfigureAwait(false);
        while ((await SendAsync(HttpMethod.Get, "admin/execution", null, DcpJsonContext.Default.DcpExecutionResponse,
            cancellationToken).ConfigureAwait(false))?.Status != DcpExecutionState.CleanupComplete)
        {
            await Task.Delay(_options.RetryInterval, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var cleanup = new CancellationTokenSource(_options.CleanupTimeout, _options.TimeProvider);
        try
        {
            if (_client is not null && _process is { HasExited: false })
            {
                // Use DCP's dependency-aware cleanup, not process exit or API
                // disappearance as a proxy for Docker removal.
                await CleanupResourcesAsync(cleanup.Token).ConfigureAwait(false);
                await SendAsync(HttpMethod.Patch, "admin/execution", JsonSerializer.SerializeToElement(
                    new DcpExecutionRequest(DcpExecutionState.Stopping, DcpResourceCleanup.Full), DcpJsonContext.Default.DcpExecutionRequest),
                    DcpJsonContext.Default.JsonElement, cleanup.Token, DcpResponseMode.Empty).ConfigureAwait(false);
                await _process.WaitForExitStatusAsync().WaitAsync(cleanup.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (_process is { HasExited: false } process)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                        // A normal exit racing Kill must not skip the drain.
                    }
                    using var forcedExit = new CancellationTokenSource(_options.CleanupTimeout, _options.TimeProvider);
                    await process.WaitForExitStatusAsync().WaitAsync(forcedExit.Token).ConfigureAwait(false);
                }
                if (_output is not null)
                {
                    await _output.WaitAsync(_options.CleanupTimeout, _options.TimeProvider).ConfigureAwait(false);
                }
            }
            finally
            {
                _process?.Dispose();
                _connection?.Dispose();
                if (_connection is null)
                {
                    _client?.Dispose();
                }
                if (Directory.Exists(_directory.FullName))
                {
                    _directory.Delete(recursive: true);
                }
            }
        }
    }
}
