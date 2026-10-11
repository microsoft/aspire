// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aspire.Shared;
using Aspire.Hosting.Native.Server;

namespace Aspire.Hosting.Native.Dcp;

/// <summary>Owns a local DCP process and its authenticated administrative API.</summary>
internal sealed class DcpApiClient : IAsyncDisposable
{
    private const string Api = "/apis/usvc-dev.developer.microsoft.com/v1/";
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("aspire-native-dcp-");
    private Process? _process;
    private HttpClient? _client;
    private DcpConnectionMaterial? _connection;
    private readonly NativeServerOptions _options;

    public DcpApiClient() : this(new())
    {
    }

    public DcpApiClient(NativeServerOptions options)
    {
        options.Validate();
        _options = options;
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
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "start-apiserver", "--monitor",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--kubeconfig", kubeconfig })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["DCP_SESSION_FOLDER"] = _directory.FullName;
        _process = Process.Start(start) ?? throw new InvalidOperationException("DCP did not start.");
        _process.OutputDataReceived += (_, line) => { if (line.Data is not null) { Console.Error.WriteLine(line.Data); } };
        _process.ErrorDataReceived += (_, line) => { if (line.Data is not null) { Console.Error.WriteLine(line.Data); } };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _connection = await DcpConnectionMaterial.WaitAsync(kubeconfig, () => _process.HasExited,
            _options.ControllerRequestTimeout, _options.RetryInterval, Console.Error.WriteLine,
            cancellationToken).ConfigureAwait(false);
        _client = _connection.Client;
    }

    public async Task<DcpResource?> GetAsync(string collection, string name, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, Api + collection + "/" + name, null, cancellationToken).ConfigureAwait(false);

        return response?.Deserialize(DcpJsonContext.Default.DcpResource);
    }

    public async Task CreateAsync(string collection, string name, JsonElement spec, Dictionary<string, string>? annotations,
        CancellationToken cancellationToken)
    {
        var kind = collection switch
        {
            "containers" => "Container", "executables" => "Executable", "services" => "Service",
            _ => throw new ArgumentException("Unsupported DCP resource collection.", nameof(collection))
        };
        var envelope = new DcpResourceEnvelope("usvc-dev.developer.microsoft.com/v1", kind, new DcpMetadata(name, annotations), spec);
        await SendAsync(HttpMethod.Post, Api + collection,
            JsonSerializer.SerializeToElement(envelope, DcpJsonContext.Default.DcpResourceEnvelope), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string collection, string name, CancellationToken cancellationToken)
    {
        await SendAsync(HttpMethod.Delete, Api + collection + "/" + name, null, cancellationToken).ConfigureAwait(false);
        while (await GetAsync(collection, name, cancellationToken).ConfigureAwait(false) is not null)
        {
            await Task.Delay(_options.RetryInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string[]> ReadLogsAsync(string collection, string name, string source, long offset, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ControllerLogReadTimeout);
        var path = $"{Api}{collection}/{name}/log?source={source}&follow=false&timestamps=false&line_numbers=false&limit=128&skip={offset}";
        using var response = await Client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"DCP log read failed ({(int)response.StatusCode}).");
        }
        var bytes = await ReadBoundedBodyAsync(response, 65536, timeout.Token).ConfigureAwait(false);
        // Log cursors count complete lines, not file offsets. For "ready\npart",
        // retain the unfinished tail at the same cursor instead of duplicating it.
        var completeLength = Array.LastIndexOf(bytes, (byte)'\n') + 1;

        return Encoding.UTF8.GetString(bytes, 0, completeLength).Split('\n', StringSplitOptions.None)[..^1];
    }

    private HttpClient Client => _client ?? throw new InvalidOperationException("DCP is not ready.");

    private async Task<JsonElement?> SendAsync(HttpMethod method, string path, JsonElement? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
            {
                body.Value.WriteTo(writer);
            }
            request.Content = new ByteArrayContent(bytes.ToArray());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(
                method == HttpMethod.Patch ? "application/merge-patch+json" : "application/json");
        }
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound && method != HttpMethod.Post)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            // DCP error payloads may echo environment variables or arguments.
            throw new InvalidOperationException($"DCP {method} failed ({(int)response.StatusCode}).");
        }
        if (response.StatusCode == HttpStatusCode.NoContent || body is { } requestBody &&
            requestBody.TryGetProperty("status", out var requestStatus) && requestStatus.GetString() == "Stopping")
        {
            return null;
        }
        var payload = await ReadBoundedBodyAsync(response, 1024 * 1024, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });

        return document.RootElement.Clone();
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (body.Length + count > limit)
            {
                throw new InvalidDataException("The DCP response exceeds the size limit.");
            }
            body.Write(buffer, 0, count);
        }

        return body.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        using var cleanup = new CancellationTokenSource(_options.CleanupTimeout);
        try
        {
            if (_client is not null && _process is { HasExited: false })
            {
                // Use DCP's dependency-aware cleanup, not process exit or API
                // disappearance as a proxy for Docker removal.
                await SendAsync(HttpMethod.Patch, "admin/execution", JsonSerializer.SerializeToElement(
                    new DcpExecutionRequest("CleaningResources", "Full"), DcpJsonContext.Default.DcpExecutionRequest),
                    cleanup.Token).ConfigureAwait(false);
                while ((await SendAsync(HttpMethod.Get, "admin/execution", null, cleanup.Token).ConfigureAwait(false))?
                    .Deserialize(DcpJsonContext.Default.DcpExecutionResponse)?.Status != "CleanupComplete")
                {
                    await Task.Delay(_options.RetryInterval, cleanup.Token).ConfigureAwait(false);
                }
                await SendAsync(HttpMethod.Patch, "admin/execution", JsonSerializer.SerializeToElement(
                    new DcpExecutionRequest("Stopping", "Full"), DcpJsonContext.Default.DcpExecutionRequest),
                    cleanup.Token).ConfigureAwait(false);
                await _process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            _process?.Dispose();
            _connection?.Dispose();
            if (Directory.Exists(_directory.FullName))
            {
                _directory.Delete(recursive: true);
            }
        }
    }
}
