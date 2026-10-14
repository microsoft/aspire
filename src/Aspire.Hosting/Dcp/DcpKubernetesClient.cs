// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Headers;
using Aspire.Hosting.Dcp.Model;
using k8s;
using k8s.Autorest;

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
