// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

internal sealed class DenoOtlpExporterAnnotation : OtlpExporterAnnotation, IReadOnlyDictionary<string, string>
{
    // Publish targets consume these settings when they provide an OTLP endpoint. Carrying them on
    // the exporter annotation preserves Deno's activation intent across late publish transformations
    // without requiring environment callbacks to run in a particular order.
    private static readonly IReadOnlyDictionary<string, string> s_activationEnvironmentVariables =
        new Dictionary<string, string>
        {
            ["OTEL_DENO"] = "true",
        };

    public string this[string key] => s_activationEnvironmentVariables[key];

    public IEnumerable<string> Keys => s_activationEnvironmentVariables.Keys;

    public IEnumerable<string> Values => s_activationEnvironmentVariables.Values;

    public int Count => s_activationEnvironmentVariables.Count;

    public bool ContainsKey(string key) => s_activationEnvironmentVariables.ContainsKey(key);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => s_activationEnvironmentVariables.GetEnumerator();

    public bool TryGetValue(string key, out string value) => s_activationEnvironmentVariables.TryGetValue(key, out value!);

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
