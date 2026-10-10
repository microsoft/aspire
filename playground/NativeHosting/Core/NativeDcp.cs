// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NativeHosting;

// BCL-only, explicit DCP wire objects. No Hosting, KubernetesClient, reflection
// dispatcher, or service-specific code is loaded into the AOT executable.
internal sealed class NativeDcp : IAsyncDisposable
{
    private const string Api = "/apis/usvc-dev.developer.microsoft.com/v1/";
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("native-dcp-");
    private readonly List<(string Collection, string Name)> _objects = [];
    private readonly SemaphoreSlim _objectsGate = new(1);
    private Process? _process;
    private HttpClient? _client;
    private X509Certificate2? _ca;

    public async Task StartAsync(string executable, CancellationToken cancellationToken)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("DCP is already configured.");
        }

        var kubeconfig = Path.Combine(_directory.FullName, "kubeconfig");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in new[] { "start-apiserver", "--monitor", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--kubeconfig", kubeconfig })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DCP_SESSION_FOLDER"] = _directory.FullName;
        // DCP diagnostics inherit stderr/stdout. Reserve this process's stdout for
        // RPC, so redirect and drain DCP output independently.
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        _process = Process.Start(start) ?? throw new InvalidOperationException("DCP failed to launch.");
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) { Console.Error.WriteLine(e.Data); } };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { Console.Error.WriteLine(e.Data); } };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        while (!File.Exists(kubeconfig))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"DCP exited during configuration ({_process.ExitCode}).");
            }

            await Task.Delay(100, cancellationToken);
        }

        // Read kubeconfig through kubectl's supported JSON projection, e.g.:
        // clusters[0].cluster = { server: "https://127.0.0.1:<port>",
        //   "certificate-authority-data": "<base64>" }; users[0].user.token = "...".
        // Never log this credential-bearing output or bypass TLS verification.
        var configuration = JsonNode.Parse(await CaptureAsync("kubectl",
            ["--kubeconfig", kubeconfig, "config", "view", "--raw", "--flatten", "-o", "json"], cancellationToken))!.AsObject();
        var cluster = configuration["clusters"]![0]!["cluster"]!;
        _ca = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(cluster["certificate-authority-data"]!.GetValue<string>()));
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
            {
                if (certificate is null || chain is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                {
                    return false;
                }

                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(_ca);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(certificate);
            }
        };
        _client = new HttpClient(handler) { BaseAddress = new Uri(cluster["server"]!.GetValue<string>()), Timeout = TimeSpan.FromSeconds(20) };
        var user = configuration["users"]![0]!["user"]!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user["token"]!.GetValue<string>());
        // The kubeconfig is written before the API listener is necessarily ready.
        while (true)
        {
            try
            {
                using var response = await _client.GetAsync("/readyz", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    break;
                }

                if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                {
                    throw new InvalidOperationException($"DCP readiness failed ({(int)response.StatusCode}).");
                }
            }
            catch (HttpRequestException exception) when (!_process.HasExited &&
                exception.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                Console.Error.WriteLine("Waiting for the DCP API listener.");
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    public async Task<JsonObject> CreateAsync(string collection, string name, JsonObject spec, JsonObject? annotations, CancellationToken cancellationToken)
    {
        var kind = collection switch
        {
            "containers" => "Container", "executables" => "Executable", "services" => "Service",
            "containernetworks" => "ContainerNetwork", "containervolumes" => "ContainerVolume",
            _ => throw new ArgumentException("Unsupported DCP collection.")
        };
        var document = new JsonObject
        {
            ["apiVersion"] = "usvc-dev.developer.microsoft.com/v1", ["kind"] = kind,
            ["metadata"] = new JsonObject { ["name"] = name, ["annotations"] = annotations?.DeepClone() },
            ["spec"] = spec.DeepClone()
        };
        await _objectsGate.WaitAsync(cancellationToken);
        try
        {
            // Record intent before sending: cancellation can occur after DCP creates
            // the object but before its HTTP response reaches us.
            _objects.Add((collection, name));
        }
        finally
        {
            _objectsGate.Release();
        }

        return (await SendAsync(HttpMethod.Post, Api + collection, document, cancellationToken))!;
    }

    public Task<JsonObject?> GetAsync(string collection, string name, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, Api + collection + "/" + name, null, cancellationToken);

    public async Task<JsonObject> ReadLogsAsync(string name, string source, long offset, CancellationToken cancellationToken)
    {
        // HttpClient's timeout ends at response headers in streaming mode. Bound
        // the body read too, including a stalled DCP log subresource.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        cancellationToken = timeout.Token;
        if (_client is null || offset < 0)
        {
            throw new InvalidOperationException("DCP log reader requires a configured client and nonnegative cursor.");
        }

        // DCP's log subresource uses line cursors, not file offsets:
        // /executables/<name>/log?source=stdout&follow=false&limit=128&skip=3
        // Read through the API; newer DCP builds need not expose stdOutFile paths.
        var path = Api + "executables/" + name + "/log?source=" + source + "&follow=false&timestamps=false&line_numbers=false&limit=128";
        if (offset > 0)
        {
            path += "&skip=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        using var response = await _client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"DCP log observation failed ({(int)response.StatusCode}).");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int length;
        while ((length = await stream.ReadAsync(bytes, cancellationToken)) > 0)
        {
            if (buffer.Length + length > 65536)
            {
                throw new InvalidOperationException("DCP log batch exceeds the bounded observation limit.");
            }

            buffer.Write(bytes, 0, length);
        }

        var data = buffer.ToArray();
        // DCP can return the current incomplete line, e.g. "Connect via browser: "
        // before the process finishes writing its URL. A line cursor re-reads that
        // entire line next time, so forwarding the partial tail twice corrupts it.
        // Advance and forward only complete lines; the tail remains at this cursor.
        var completeLength = Array.LastIndexOf(data, (byte)'\n') + 1;
        var lines = data.AsSpan(0, completeLength).Count((byte)'\n');
        return new JsonObject { ["offset"] = offset + lines, ["data"] = Convert.ToBase64String(data, 0, completeLength) };
    }

    public async Task DeleteAsync(string collection, string name, CancellationToken cancellationToken)
    {
        await SendAsync(HttpMethod.Delete, Api + collection + "/" + name, null, cancellationToken);
        while (await GetAsync(collection, name, cancellationToken) is not null)
        {
            await Task.Delay(100, cancellationToken);
        }
    }

    private async Task<JsonObject?> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Configure DCP before using orchestration.");
        }

        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, PrototypeJsonContext.Default.JsonObject), Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound && method != HttpMethod.Post)
        {
            return null;
        }

        // DCP errors can echo a submitted spec containing credentials. Report the
        // operation/status, not the raw response payload.
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"DCP {method} {path} failed ({(int)response.StatusCode}).");
        }

        return (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken))!.AsObject();
    }

    private static async Task<string> CaptureAsync(string executable, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not launch {executable}.");
        // Drain both pipes concurrently so a full stderr pipe cannot block stdout.
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        await stderr;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{executable} exited with code {process.ExitCode}.");
        }

        return await stdout;
    }

    public async ValueTask DisposeAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            if (_client is not null && _process is { HasExited: false })
            {
                // Reverse creation order removes workloads before services, volumes,
                // and their shared network. Only this DCP session's objects are touched.
                foreach (var (collection, name) in _objects.AsEnumerable().Reverse())
                {
                    await DeleteAsync(collection, name, cancellation.Token);
                }
            }
        }
        finally
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None);
            }

            _process?.Dispose();
            _client?.Dispose();
            _ca?.Dispose();
            _objectsGate.Dispose();
            if (_directory.Exists)
            {
                _directory.Delete(recursive: true);
            }
        }
    }
}
