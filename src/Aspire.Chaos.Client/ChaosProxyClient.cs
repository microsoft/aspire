// <copyright file="ChaosProxyClient.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Net;
using System.Net.Http.Json;

namespace Aspire.Chaos.Client;

/// <summary>
/// Strongly-typed harness-side client for the chaos proxy's runtime <c>/chaos/*</c> API.
/// Wraps an <see cref="HttpClient"/> with one method per endpoint so test harnesses can
/// install / inspect / clear chaos policies without dealing with raw JSON.
/// </summary>
/// <remarks>
/// The HttpClient's <see cref="HttpClient.BaseAddress"/> must point at the chaos proxy
/// (e.g., the URL Aspire's service discovery resolves for the proxy resource). Methods
/// throw <see cref="HttpRequestException"/> on non-success responses with the server's
/// error body surfaced in the message.
///
/// Lifecycle: this type is stateless. Re-use a single instance across the test session
/// or instantiate per-test; both are safe.
/// </remarks>
public sealed class ChaosProxyClient
{
    private readonly HttpClient _http;

    /// <summary>Wraps the supplied <see cref="HttpClient"/>.</summary>
    /// <param name="httpClient">HttpClient with BaseAddress set to the chaos proxy's root URL.</param>
    public ChaosProxyClient(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>Health probe: <c>GET /chaos/healthz</c>. Returns true if the proxy responds 200 OK.</summary>
    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("/chaos/healthz", ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Installs a single policy via <c>POST /chaos/policies</c>. Returns the server-assigned (or supplied) id.</summary>
    public async Task<string> InstallPolicyAsync(ChaosPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        using var response = await _http.PostAsJsonAsync("/chaos/policies", policy, ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<InstallResponse>(ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false);
        return body?.Id ?? throw new InvalidOperationException("server response missing id");
    }

    /// <summary>
    /// Installs a single policy via <c>POST /chaos/policies</c> using a pre-shaped object
    /// body (e.g., <see cref="Dictionary{TKey, TValue}"/>, anonymous type, or untyped JSON).
    /// Use when the caller already has a wire-shaped policy body and wants to bypass the
    /// typed <see cref="ChaosPolicy"/> constructor — e.g., a test runner that round-trips
    /// arbitrary policy shapes from JSON synth-test definitions.
    /// </summary>
    /// <remarks>
    /// The shape constraint (must include at least one transform field) is enforced
    /// server-side; non-conforming bodies surface as 400 with the validation error in
    /// the exception message via <see cref="EnsureSuccessAsync"/>.
    /// </remarks>
    public async Task<string> InstallPolicyAsync(object policyBody, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policyBody);
        using var response = await _http.PostAsJsonAsync("/chaos/policies", policyBody, ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<InstallResponse>(ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false);
        return body?.Id ?? throw new InvalidOperationException("server response missing id");
    }

    /// <summary>Installs a batch of policies atomically via <c>POST /chaos/policies/bulk</c>. Returns assigned ids in input order.</summary>
    public async Task<IReadOnlyList<string>> InstallPoliciesAsync(IEnumerable<ChaosPolicy> policies, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var list = policies.ToList();
        using var response = await _http.PostAsJsonAsync("/chaos/policies/bulk", list, ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<BulkInstallResponse>(ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false);
        return body?.Ids ?? Array.Empty<string>();
    }

    /// <summary>Validates a policy + returns its canonical shape without installing it. Wraps <c>POST /chaos/preview-policy</c>.</summary>
    public async Task<ChaosPolicySummary> PreviewPolicyAsync(ChaosPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        using var response = await _http.PostAsJsonAsync("/chaos/preview-policy", policy, ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChaosPolicySummary>(ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("server response was empty");
    }

    /// <summary>Lists every currently-installed policy via <c>GET /chaos/policies</c>.</summary>
    public async Task<IReadOnlyList<ChaosPolicySummary>> ListPoliciesAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("/chaos/policies", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<PolicyListResponse>(ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false);
        return body?.Policies ?? Array.Empty<ChaosPolicySummary>();
    }

    /// <summary>Fetches a single policy by id. Returns null if the id is unknown.</summary>
    public async Task<ChaosPolicySummary?> GetPolicyAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        using var response = await _http.GetAsync($"/chaos/policies/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChaosPolicySummary>(ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false);
    }

    /// <summary>Fetches per-transform fire counters for a single policy. Returns null if the id is unknown.</summary>
    public async Task<IReadOnlyDictionary<string, long>?> GetFireCountsAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        using var response = await _http.GetAsync($"/chaos/policies/{Uri.EscapeDataString(id)}/fire-counts", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<FireCountsResponse>(ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false);
        return body?.FireCounts ?? new Dictionary<string, long>();
    }

    /// <summary>
    /// Fetches the distinct request paths (as <c>"{method} {path}"</c>) the given
    /// policy actually fired on. Returns null if the id is unknown, or an empty list
    /// if it never fired. Use to assert REPRO FIDELITY — that the injected fault hit
    /// the code path the bug under test is about, not an unrelated request that merely
    /// matched a broad matcher.
    /// </summary>
    public async Task<IReadOnlyList<string>?> GetFiredPathsAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        using var response = await _http.GetAsync($"/chaos/policies/{Uri.EscapeDataString(id)}/fire-counts", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<FireCountsResponse>(ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false);
        return body?.FiredPaths ?? new List<string>();
    }

    /// <summary>Reads the proxy's aggregate state probe via <c>GET /chaos/state</c>.</summary>
    public async Task<ChaosProxyState> GetStateAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("/chaos/state", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChaosProxyState>(ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("server response was empty");
    }

    /// <summary>Predicts which installed policies would fire for a hypothetical request via <c>POST /chaos/match</c>.</summary>
    public async Task<IReadOnlyList<ChaosMatchEntry>> MatchAsync(string? method, string path, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var body = new { method, path, headers };
        using var response = await _http.PostAsJsonAsync("/chaos/match", body, ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var parsed = await response.Content.ReadFromJsonAsync<MatchResponse>(ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false);
        return parsed?.Matches ?? Array.Empty<ChaosMatchEntry>();
    }

    /// <summary>Removes a single policy by id. Returns false if the id was unknown.</summary>
    public async Task<bool> RemovePolicyAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        using var response = await _http.DeleteAsync($"/chaos/policies/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Clears every installed policy + resets all chaos state. Returns the number of policies removed.</summary>
    public async Task<int> ClearPoliciesAsync(CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync("/chaos/policies", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<ClearResponse>(ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false);
        return body?.Removed ?? 0;
    }

    /// <summary>Pauses all transforms via <c>POST /chaos/pause</c>. Idempotent.</summary>
    public async Task PauseAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("/chaos/pause", content: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Resumes all transforms via <c>POST /chaos/resume</c>. Idempotent.</summary>
    public async Task ResumeAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("/chaos/resume", content: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Arms the global fire-once trigger for the named transform. Next matching request fires regardless of probability gates.</summary>
    public async Task FireOnceAsync(string transform, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(transform);
        using var response = await _http.PostAsync($"/chaos/fire-once?transform={Uri.EscapeDataString(transform)}", content: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Arms a per-policy fire-once trigger for the named transform. Targets a single policy without burning the global trigger.</summary>
    public async Task FireOnceAsync(string policyId, string transform, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyId);
        ArgumentException.ThrowIfNullOrEmpty(transform);
        using var response = await _http.PostAsync(
            $"/chaos/policies/{Uri.EscapeDataString(policyId)}/fire-once?transform={Uri.EscapeDataString(transform)}",
            content: null,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Resets the fire counters for a single policy via <c>DELETE /chaos/policies/{id}/fire-counts</c>.</summary>
    public async Task ResetFireCountsAsync(string policyId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyId);
        using var response = await _http.DeleteAsync($"/chaos/policies/{Uri.EscapeDataString(policyId)}/fire-counts", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Bumps a policy's TTL forward by the given number of seconds via <c>POST /chaos/policies/{id}/extend</c>.</summary>
    public async Task<DateTimeOffset?> ExtendTtlAsync(string policyId, int seconds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyId);
        using var response = await _http.PostAsync(
            $"/chaos/policies/{Uri.EscapeDataString(policyId)}/extend?seconds={seconds}",
            content: null,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<ExtendResponse>(ChaosPolicyJsonOptions.CamelCase, ct).ConfigureAwait(false);
        return body?.ExpiresAt;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException(
            $"chaos proxy returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
            inner: null,
            statusCode: response.StatusCode);
    }

    // Wire-shape DTOs - kept private so the public surface only exposes ChaosPolicy / ChaosPolicySummary.
    private sealed record InstallResponse(string Id);

    private sealed record BulkInstallResponse(int Installed, IReadOnlyList<string> Ids);

    private sealed record PolicyListResponse(IReadOnlyList<ChaosPolicySummary> Policies);

    private sealed record FireCountsResponse(string Id, IReadOnlyDictionary<string, long> FireCounts, IReadOnlyList<string>? FiredPaths);

    private sealed record ClearResponse(int Removed);

    private sealed record ExtendResponse(string Id, DateTimeOffset? ExpiresAt);

    private sealed record MatchResponse(IReadOnlyList<ChaosMatchEntry> Matches);
}
