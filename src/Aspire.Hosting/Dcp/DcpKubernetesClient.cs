// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Dcp.Model;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Aspire.Hosting.Dcp;

// We need to create a custom Kubernetes client to support reading arbitrary subresources from a Kubernetes resource as a stream.
// k8s.Kubernetes does not support this operation natively, and required machinery (SendRequest() in particular) is protected.

internal sealed class DcpKubernetesClient : k8s.Kubernetes
{
    private const string ExecutionDocumentPath = "admin/execution";

    public DcpKubernetesClient(KubernetesClientConfiguration config, params DelegatingHandler[] handlers) : base(config, handlers)
    {
        // KubernetesService supplies the operation deadlines. The KubernetesClient NuGet package's
        // separate HttpClientTimeout timer defaults to 100 seconds and would otherwise truncate
        // configured budgets and surface as an unrelated cancellation.
        HttpClientTimeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// Opens a DCP resource watch without waiting for its first event.
    /// This avoids KubernetesClient's setup-cancellation path that can leave an unread response open.
    /// </summary>
    /// <typeparam name="T">The resource type returned by watch events.</typeparam>
    /// <param name="group">The Kubernetes API group.</param>
    /// <param name="version">The resource API version.</param>
    /// <param name="plural">The plural resource name, e.g. "executables".</param>
    /// <param name="namespaceParameter">The namespace, or null/empty for a cluster-scoped watch.</param>
    /// <param name="connectionCancellationToken">Closes the watch response on cancellation, even before enumeration starts.</param>
    /// <param name="cancellationToken">Cancels connection establishment only; does not affect the returned stream.</param>
    /// <returns>
    /// A single-use event stream, returned once response headers arrive.
    /// Ending enumeration closes its HTTP response.
    /// </returns>
    public async Task<IAsyncEnumerable<(WatchEventType, T)>> CreateWatchAsync<T>(
        string group,
        string version,
        string plural,
        string? namespaceParameter,
        CancellationToken connectionCancellationToken,
        CancellationToken cancellationToken)
    {
        var collectionPath = string.IsNullOrEmpty(namespaceParameter)
            ? $"apis/{group}/{version}/{plural}"
            : $"apis/{group}/{version}/namespaces/{Uri.EscapeDataString(namespaceParameter)}/{plural}";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, $"{collectionPath}?watch=true"));
        request.Version = HttpVersion.Version20;
        if (Credentials is not null)
        {
            await Credentials.ProcessHttpRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // KubernetesClient's list-and-watch adapter peeks the first event during setup and
        // can abandon its reader when that wait is canceled. Own the raw response instead.
        // https://github.com/microsoft/dcp/issues/286
        var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpOperationException($"Operation returned an invalid status code '{response.StatusCode}', response body {content}")
                {
                    Request = new HttpRequestMessageWrapper(request, string.Empty),
                    Response = new HttpResponseMessageWrapper(response, content)
                };
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new WatchConnection<T>(response, stream, connectionCancellationToken);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Asynchronously reads a sub-resource from a Kubernetes resource as a stream.
    /// </summary>
    /// <param name="group">The API group of the Kubernetes resource.</param>
    /// <param name="version">The API version of the Kubernetes resource.</param>
    /// <param name="plural">The plural name (API kind) of the Kubernetes resource, e.g. "executables".</param>
    /// <param name="name">The name of the Kubernetes resource to use for sub-resource read operation.</param>
    /// <param name="subResource">The sub-resource to read from the Kubernetes resource.</param>
    /// <param name="namespaceParameter">The namespace of the Kubernetes resource.
    /// If null or empty, the resource is assumed to be non-namespaced.</param>
    /// <param name="queryParams">Optional query parameters to append to the request URL.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    public async Task<HttpOperationResponse<Stream>> ReadSubResourceAsStreamAsync(
        string group,
        string version,
        string plural,
        string name,
        string subResource,
        string? namespaceParameter,
        IReadOnlyCollection<(string name, string value)>? queryParams = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group, nameof(group));
        ArgumentException.ThrowIfNullOrWhiteSpace(version, nameof(version));
        ArgumentException.ThrowIfNullOrWhiteSpace(plural, nameof(plural));
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        ArgumentException.ThrowIfNullOrWhiteSpace(subResource, nameof(subResource));

        string url;
        if (string.IsNullOrEmpty(namespaceParameter))
        {
            url = $"apis/{group}/{version}/{plural}/{name}/{subResource}";
        }
        else
        {
            url = $"apis/{group}/{version}/namespaces/{namespaceParameter}/{plural}/{name}/{subResource}";
        }

        var q = new QueryBuilder();
        if (queryParams != null)
        {
            foreach (var (param, paramVal) in queryParams)
            {
                q.Append(param, paramVal);
            }
        }
        url += q.ToString();

        var httpResponse = await SendRequest<object?>(url, HttpMethod.Get, customHeaders: null, body: null, cancellationToken).ConfigureAwait(false);
        var httpRequest = httpResponse.RequestMessage;
        var result = new HttpOperationResponse<Stream>()
        {
            Request = httpRequest,
            Response = httpResponse,
            Body = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false)
        };
        return result;
    }

    /// <summary>
    /// GET DCP Execution document (part of the DCP administrative interface).
    /// </summary>
    public async Task<ApiServerExecution> GetExecutionDocumentAsync(CancellationToken cancellationToken = default)
    {
        var httpRequest = new HttpRequestMessage
        {
            Method = HttpMethod.Get,
            RequestUri = new Uri(this.BaseUri, ExecutionDocumentPath)
        };
        httpRequest.Version = HttpVersion.Version20;

        var httpResponse = await SendRequestRaw(null, httpRequest, cancellationToken).ConfigureAwait(false);
        var content = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return KubernetesJson.Deserialize<ApiServerExecution>(content);
    }

    /// <summary>
    /// PATCH Execution document (part of the DCP administrative interface) with supplied data. 
    /// </summary>
    /// <returns>
    /// The ApiServerExecution object representing the updated state of the API server.
    /// </returns>
    public async Task<ApiServerExecution> PatchExecutionDocumentAsync(
        ApiServerExecution apiServerExecution,
        CancellationToken cancellationToken = default)
    {
        var httpRequest = new HttpRequestMessage
        {
            Method = HttpMethod.Patch,
            RequestUri = new Uri(this.BaseUri, ExecutionDocumentPath)
        };
        httpRequest.Version = HttpVersion.Version20;

        var content = KubernetesJson.Serialize(apiServerExecution, null);
        httpRequest.Content = new StringContent(content, System.Text.Encoding.UTF8);
        httpRequest.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/merge-patch+json");

        var httpResponse = await SendRequestRaw(content, httpRequest, cancellationToken).ConfigureAwait(false);
        if (httpResponse.StatusCode == HttpStatusCode.NoContent || apiServerExecution.ApiServerStatus == ApiServerStatus.Stopping)
        {
            // NoContent means that the server successfully processed the request and the current state
            // is equivalent to the sent patch, so we can just return that.
            //
            // The check for ApiServerStatus.Stopping is a workaround for DCP 0.11 series issue that it may not send complete response
            // when programmatic server stoppage is requested. But it is not really possible for the server to respond
            // with anything other than Stopping if the request was successful, so it does not matter.
            return apiServerExecution;
        }
        else
        {
            var responseContent = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return KubernetesJson.Deserialize<ApiServerExecution>(responseContent);
        }
    }

    private sealed class WatchConnection<T> : IAsyncEnumerable<(WatchEventType, T)>
    {
        private readonly HttpResponseMessage _response;
        private readonly PipeReader _reader;
        private readonly CancellationToken _connectionToken;
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private bool _firstLine = true;

        public WatchConnection(HttpResponseMessage response, Stream stream, CancellationToken cancellationToken)
        {
            _response = response;
            _reader = PipeReader.Create(stream);
            _connectionToken = cancellationToken;
            // A canceled body read does not necessarily reset an HTTP/2 stream. Close the
            // response even when enumeration is paused in the consumer or has not started.
            _cancellationRegistration = cancellationToken.Register(static state => ((HttpResponseMessage)state!).Dispose(), response);
        }

        public async IAsyncEnumerator<(WatchEventType, T)> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            using var response = _response;
            using var cancellationRegistration = _cancellationRegistration;
            using var consumerCancellation = cancellationToken.Register(static state => ((HttpResponseMessage)state!).Dispose(), response);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_connectionToken, cancellationToken);
            var receivedEvent = false;

            try
            {
                while (true)
                {
                    string? line;
                    bool terminated;
                    try
                    {
                        (line, terminated) = await ReadEventLineAsync(linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception) when (linkedCts.IsCancellationRequested)
                    {
                        // Disposing the response can surface as IOException or ObjectDisposedException.
                        // Preserve the original token so the periodic-restart helper recognizes its deadline.
                        _connectionToken.ThrowIfCancellationRequested();
                        cancellationToken.ThrowIfCancellationRequested();
                        throw;
                    }

                    _connectionToken.ThrowIfCancellationRequested();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (line is null)
                    {
                        if (!receivedEvent)
                        {
                            // An empty response is a failed watch, not a successful short poll.
                            // Surface it to the retry pipeline rather than reconnecting in a tight loop.
                            throw new EndOfStreamException("DCP watch ended before returning an event.");
                        }

                        yield break;
                    }

                    if (DeserializeEvent(line, terminated) is { } watchEvent)
                    {
                        receivedEvent = true;
                        yield return watchEvent;
                    }
                }
            }
            finally
            {
                await _reader.CompleteAsync().ConfigureAwait(false);
            }
        }

        private async ValueTask<(string? Line, bool Terminated)> ReadEventLineAsync(CancellationToken cancellationToken)
        {
            SequencePosition? searchedTo = null;
            while (true)
            {
                var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                var unsearched = searchedTo is { } position ? buffer.Slice(position) : buffer;
                if (unsearched.PositionOf((byte)'\n') is { } newline)
                {
                    var line = DecodeLine(buffer.Slice(0, newline));
                    _reader.AdvanceTo(buffer.GetPosition(1, newline));

                    return (line, true);
                }
                if (result.IsCompleted)
                {
                    var line = buffer.IsEmpty ? null : DecodeLine(buffer);
                    _reader.AdvanceTo(buffer.End);

                    return (line, false);
                }

                // No bytes are consumed until a complete record is found, so this position
                // stays valid across reads and avoids rescanning the retained prefix.
                searchedTo = buffer.IsEmpty ? null : buffer.End;
                _reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }

        private string DecodeLine(in ReadOnlySequence<byte> buffer)
        {
            var line = Encoding.UTF8.GetString(buffer);
            if (_firstLine)
            {
                _firstLine = false;
                if (line.Length > 0 && line[0] == '\uFEFF')
                {
                    line = line[1..];
                }
            }

            return line;
        }

        private static (WatchEventType, T)? DeserializeEvent(string line, bool terminated)
        {
            // Kubernetes watches contain newline-delimited events:
            // {"type":"MODIFIED","object":{"kind":"Executable","metadata":{...},...}}
            // An ERROR can contain a Status instead; BOOKMARK objects contain only metadata.
            // https://kubernetes.io/docs/reference/using-api/api-concepts/#efficient-detection-of-changes
            if (!terminated)
            {
                // A missing newline does not make a complete payload such as null truncated.
                // Validate syntax separately so resource shape errors remain non-retryable.
                try
                {
                    using var document = JsonDocument.Parse(line);
                }
                catch (JsonException ex)
                {
                    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line), isFinalBlock: false, state: default);
                    if (!JsonDocument.TryParseValue(ref reader, out var document))
                    {
                        // EOF can leave a partial record such as {"type":"MODIFIED","object":
                        throw new EndOfStreamException("DCP watch ended in an incomplete event.", ex);
                    }
                    document.Dispose();
                    throw;
                }
            }

            var envelope = KubernetesJson.Deserialize<Watcher<KubernetesObject>.WatchEvent>(line);
            if (envelope?.Object is null)
            {
                throw new JsonException("DCP watch event does not contain an object.");
            }
            if (envelope.Object.Kind == "Status")
            {
                var status = KubernetesJson.Deserialize<Watcher<V1Status>.WatchEvent>(line).Object;
                if (status.Status == "Failure")
                {
                    throw new KubernetesException(status);
                }
                if (status.Status != "Success")
                {
                    throw new JsonException("DCP watch Status does not report Success or Failure.");
                }

                // A successful Status is not a resource update and must not be deserialized as T.
                return null;
            }

            var watchEvent = KubernetesJson.Deserialize<Watcher<T>.WatchEvent>(line);

            return (watchEvent.Type, watchEvent.Object);
        }
    }

    private sealed class QueryBuilder
    {
        private readonly List<string> _parameters = new List<string>();

        public void Append(string key, int val)
        {
            _parameters.Add($"{key}={val}");
        }

        public void Append(string key, bool? val)
        {
            _parameters.Add($"{key}={(val == true ? "true" : "false")}");
        }

        public void Append(string key, string val)
        {
            _parameters.Add($"{key}={Uri.EscapeDataString(val)}");
        }

        public override string ToString()
        {
            if (_parameters.Count > 0)
            {
                return $"?{string.Join("&", _parameters)}";
            }
            else
            {
                return string.Empty;
            }
        }
    }
}
