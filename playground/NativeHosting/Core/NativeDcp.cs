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
using Aspire.Shared;

namespace NativeHosting;

// BCL-only, explicit DCP wire objects. No Hosting, KubernetesClient, reflection
// dispatcher, or service-specific code is loaded into the AOT executable.
internal sealed class NativeDcp : IAsyncDisposable
{
    private const string Api = "/apis/usvc-dev.developer.microsoft.com/v1/";
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("native-dcp-");
    private Process? _process;
    private HttpClient? _client;
    private X509Certificate2? _ca;
    private X509Certificate2? _clientCertificate;

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

        // DCP creates the path before flushing all connection material. Read its
        // generated scalar contract directly, with no kubectl/YAML runtime.
        DcpKubeconfigData configuration;
        for (var attempt = 0; ; attempt++)
        {
            configuration = DcpKubeconfigData.Parse(await File.ReadAllTextAsync(kubeconfig, cancellationToken));
            if (configuration.Server is not null && configuration.CertificateAuthorityData is not null &&
                (configuration.Token is not null || (configuration.ClientCertificateData is not null && configuration.ClientKeyData is not null)))
            {
                break;
            }

            if (attempt >= 49 || _process.HasExited)
            {
                throw new InvalidOperationException("DCP did not write complete connection material.");
            }

            await Task.Delay(100, cancellationToken);
        }

        if (!Uri.TryCreate(configuration.Server, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps || !server.IsLoopback)
        {
            throw new InvalidOperationException("DCP must advertise a loopback HTTPS API.");
        }

        _ca = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(configuration.CertificateAuthorityData));
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
        if (configuration.ClientCertificateData is not null && configuration.ClientKeyData is not null)
        {
            _clientCertificate = X509Certificate2.CreateFromPem(
                Encoding.UTF8.GetString(Convert.FromBase64String(configuration.ClientCertificateData)),
                Encoding.UTF8.GetString(Convert.FromBase64String(configuration.ClientKeyData)));
            handler.ClientCertificates.Add(_clientCertificate);
        }

        _client = new HttpClient(handler) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(20) };
        if (configuration.Token is not null)
        {
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", configuration.Token);
        }
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
            request.Content = new StringContent(JsonSerializer.Serialize(body, PrototypeJsonContext.Default.JsonObject), Encoding.UTF8);
            // DCP's administrative PATCH endpoint rejects a charset parameter.
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(
                method == HttpMethod.Patch ? "application/merge-patch+json" : "application/json");
        }

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

        if (response.StatusCode == HttpStatusCode.NoContent ||
            (path == "admin/execution" && body?["status"]?.GetValue<string>() == "Stopping"))
        {
            return null;
        }

        return (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken))!.AsObject();
    }

    public async ValueTask DisposeAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            if (_client is not null && _process is { HasExited: false })
            {
                // Keep resource objects available to DCP's dependency-aware cleanup:
                // API disappearance does not prove Docker removal has completed.
                // Use Hosting's KubernetesService.CleanupResourcesAsync protocol:
                // PATCH /admin/execution {status:"CleaningResources",
                // shutdownResourceCleanup:"Full"}, then GET until CleanupComplete.
                await SendAsync(HttpMethod.Patch, "admin/execution",
                    new JsonObject { ["status"] = "CleaningResources", ["shutdownResourceCleanup"] = "Full" }, cancellation.Token);
                while ((await SendAsync(HttpMethod.Get, "admin/execution", null, cancellation.Token))?["status"]?.GetValue<string>() != "CleanupComplete")
                {
                    await Task.Delay(100, cancellation.Token);
                }

                await SendAsync(HttpMethod.Patch, "admin/execution",
                    new JsonObject { ["status"] = "Stopping", ["shutdownResourceCleanup"] = "Full" }, cancellation.Token);
                await _process.WaitForExitAsync(cancellation.Token);
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
            _clientCertificate?.Dispose();
            if (_directory.Exists)
            {
                _directory.Delete(recursive: true);
            }
        }
    }
}
