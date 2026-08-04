// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Yaml;

internal sealed class KubernetesManifestResourceYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type)
    {
        return type == typeof(KubernetesManifestResource);
    }

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        throw new NotSupportedException($"{nameof(KubernetesManifestResource)} does not support YAML deserialization.");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is not KubernetesManifestResource manifest)
        {
            throw new InvalidOperationException($"Expected {nameof(KubernetesManifestResource)} but got {value?.GetType()}.");
        }

        emitter.Emit(new MappingStart());

        WriteProperty("apiVersion", manifest.ApiVersion, serializer);
        WriteProperty("kind", manifest.Kind, serializer);
        WriteProperty("metadata", manifest.Metadata, serializer);

        foreach (var (key, fieldValue) in manifest.Fields)
        {
            WriteProperty(key, fieldValue, serializer);
        }

        emitter.Emit(new MappingEnd());
    }

    private static void WriteProperty(string name, object? value, ObjectSerializer serializer)
    {
        serializer(name);
        serializer(value);
    }
}
