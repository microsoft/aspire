// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Utils;

public static class OtlpExporterActivationTestExtensions
{
    public static IResourceBuilder<TResource> WithOtlpExporterActivationEnvironmentVariable<TResource>(
        this IResourceBuilder<TResource> builder,
        string name,
        string value)
        where TResource : IResourceWithEnvironment
    {
        if (!builder.Resource.TryGetLastAnnotation<OtlpExporterAnnotation>(out var exporter))
        {
            throw new InvalidOperationException($"Resource '{builder.Resource.Name}' does not have an OTLP exporter annotation.");
        }

        var activationEnvironmentVariables = exporter is IReadOnlyDictionary<string, string> existing
            ? existing.ToDictionary()
            : new Dictionary<string, string>();
        activationEnvironmentVariables[name] = value;

        builder.Resource.Annotations.Remove(exporter);
        builder.Resource.Annotations.Add(new TestOtlpExporterAnnotation(activationEnvironmentVariables)
        {
            RequiredProtocol = exporter.RequiredProtocol,
        });

        return builder;
    }

    private sealed class TestOtlpExporterAnnotation(IReadOnlyDictionary<string, string> activationEnvironmentVariables)
        : OtlpExporterAnnotation, IReadOnlyDictionary<string, string>
    {
        public string this[string key] => activationEnvironmentVariables[key];

        public IEnumerable<string> Keys => activationEnvironmentVariables.Keys;

        public IEnumerable<string> Values => activationEnvironmentVariables.Values;

        public int Count => activationEnvironmentVariables.Count;

        public bool ContainsKey(string key) => activationEnvironmentVariables.ContainsKey(key);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => activationEnvironmentVariables.GetEnumerator();

        public bool TryGetValue(string key, out string value) => activationEnvironmentVariables.TryGetValue(key, out value!);

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
