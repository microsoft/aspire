// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Aspire.Hosting.Radius.Secrets;

/// <summary>
/// Reads a committed, encrypted Bitnami <c>SealedSecret</c> manifest and returns both the
/// identifying metadata and the exact bytes that were validated.
/// </summary>
internal readonly record struct ValidatedManifest(
    SealedSecretManifest.Metadata Metadata,
    string SourcePath,
    ReadOnlyMemory<byte> Content);

/// <summary>
/// Minimal reader for a committed, encrypted Bitnami <c>SealedSecret</c> manifest. Extracts
/// the <c>metadata.name</c> / <c>metadata.namespace</c> that identify the <c>Secret</c> the
/// controller will materialize (used for the <c>secretStores.resource</c> reference and the
/// deploy-time materialization poll). The manifest content is already encrypted; only its
/// metadata is read. A missing or unreadable manifest fails with <c>ASPIRERADIUS044</c>.
/// </summary>
internal static class SealedSecretManifest
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The result of reading a <c>SealedSecret</c> manifest's identifying metadata.
    /// <see cref="NamespaceWasExplicit"/> distinguishes a namespace read from the manifest from
    /// one defaulted to the owning environment: only when defaulted may the deploy step add
    /// <c>-n</c> to <c>kubectl apply</c> (passing <c>-n</c> when the object already declares a
    /// different namespace makes <c>kubectl apply</c> fail).
    /// </summary>
    internal readonly record struct Metadata(string Namespace, string Name, bool NamespaceWasExplicit);

    /// <summary>
    /// Reads, validates, and returns the manifest metadata plus the exact validated bytes.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The manifest is missing, unreadable, malformed, has no <c>metadata.name</c>, or is not a
    /// single encrypted Bitnami <c>SealedSecret</c> document (<c>ASPIRERADIUS044</c>); or it embeds
    /// a plaintext <c>Secret</c> in a <c>last-applied-configuration</c> annotation
    /// (<c>ASPIRERADIUS063</c>).
    /// </exception>
    internal static ValidatedManifest ReadValidated(
        string storeName, string manifestPath, string defaultNamespace)
    {
        byte[] content;
        try
        {
            content = File.ReadAllBytes(manifestPath);
        }
        // File.ReadAllBytes surfaces an unreadable path as several exception types: IO/permission
        // failures (IOException — includes FileNotFoundException/DirectoryNotFoundException/
        // PathTooLongException — and UnauthorizedAccessException/NotSupportedException) as well as
        // argument failures for an empty/whitespace/invalid-character path (ArgumentException, which
        // covers ArgumentNullException). Normalize them all to ASPIRERADIUS044 so every "unreadable
        // manifest" failure matches the XML-doc/README contract instead of leaking a raw exception.
        catch (Exception ex) when (ex is IOException or PathTooLongException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Secret store '{storeName}' references a SealedSecret manifest at '{manifestPath}' that " +
                $"is missing or unreadable ({ex.GetType().Name}). Diagnostic: ASPIRERADIUS044.", ex);
        }

        string text;
        try
        {
            text = s_utf8.GetString(content);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException(
                $"Secret store '{storeName}' references a SealedSecret manifest at '{manifestPath}' that " +
                "is not valid UTF-8 YAML. Diagnostic: ASPIRERADIUS044.", ex);
        }

        var metadata = ReadMetadataFromYaml(storeName, manifestPath, defaultNamespace, text);
        return new ValidatedManifest(metadata, manifestPath, content);
    }

    /// <summary>
    /// Reads the <c>metadata.name</c> (required) and <c>metadata.namespace</c> (optional,
    /// defaulting to <paramref name="defaultNamespace"/>) from the manifest at
    /// <paramref name="manifestPath"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The manifest is missing, unreadable, has no <c>metadata.name</c>, or is not a single
    /// encrypted Bitnami <c>SealedSecret</c> document (<c>ASPIRERADIUS044</c>); or it embeds a
    /// plaintext <c>Secret</c> in a <c>last-applied-configuration</c> annotation
    /// (<c>ASPIRERADIUS063</c>).
    /// </exception>
    internal static Metadata ReadMetadata(
        string storeName, string manifestPath, string defaultNamespace) =>
        ReadValidated(storeName, manifestPath, defaultNamespace).Metadata;

    private static Metadata ReadMetadataFromYaml(
        string storeName, string manifestPath, string defaultNamespace, string text)
    {
        try
        {
            ValidateStructure(storeName, manifestPath, text);

            var stream = new YamlStream();
            stream.Load(new StringReader(text));

            if (stream.Documents.Count != 1)
            {
                throw CreateInvalidManifestException(
                    storeName,
                    manifestPath,
                    "contains multiple YAML documents. Provide a single encrypted Bitnami SealedSecret document.");
            }

            if (stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                throw CreateInvalidManifestException(
                    storeName,
                    manifestPath,
                    "does not have a YAML mapping as its root. Provide a single encrypted Bitnami SealedSecret object.");
            }

            return ReadMetadataFromRoot(storeName, manifestPath, defaultNamespace, root);
        }
        catch (YamlException ex)
        {
            throw CreateInvalidManifestException(
                storeName,
                manifestPath,
                "is malformed YAML or uses unsupported YAML features.",
                ex);
        }
    }

    private static Metadata ReadMetadataFromRoot(
        string storeName, string manifestPath, string defaultNamespace, YamlMappingNode root)
    {
        var kind = ReadScalar(root, "kind");
        var apiVersion = ReadScalar(root, "apiVersion");

        if (string.Equals(kind, "Secret", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Secret store '{storeName}' references a plaintext Kubernetes Secret manifest at '{manifestPath}'. " +
                "Only an encrypted Bitnami SealedSecret is accepted so cleartext credentials are never copied into " +
                "artifacts or applied to the cluster. Seal it with kubeseal first. Diagnostic: ASPIRERADIUS044.");
        }

        if (!string.Equals(kind, "SealedSecret", StringComparison.Ordinal) ||
            apiVersion is null || !apiVersion.StartsWith("bitnami.com/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Secret store '{storeName}' references a manifest at '{manifestPath}' that is not an encrypted " +
                "Bitnami SealedSecret (expected 'kind: SealedSecret' and an 'apiVersion' under 'bitnami.com/'; found " +
                $"kind '{kind ?? "<none>"}', apiVersion '{apiVersion ?? "<none>"}'). Diagnostic: ASPIRERADIUS044.");
        }

        if (TryGetNode(root, "data", out _) || TryGetNode(root, "stringData", out _))
        {
            throw CreateInvalidManifestException(
                storeName,
                manifestPath,
                "contains top-level plaintext Kubernetes Secret fields ('data' or 'stringData'). Seal those values under spec.encryptedData instead.");
        }

        if (TryGetNode(root, "spec", out var specNode) &&
            specNode is YamlMappingNode spec &&
            TryGetNode(spec, "template", out var templateNode) &&
            templateNode is YamlMappingNode template &&
            (ContainsPlaintextTemplateData(template, "data") || ContainsPlaintextTemplateData(template, "stringData")))
        {
            throw CreateInvalidManifestException(
                storeName,
                manifestPath,
                "contains plaintext-capable spec.template.data or spec.template.stringData values. Seal secret material under spec.encryptedData instead.");
        }

        // A `kubectl.kubernetes.io/last-applied-configuration` annotation records the full JSON of a
        // previously-applied object. Unlike spec.encryptedData it is NOT encrypted, so a plaintext
        // Secret embedded there (top-level metadata, or the templated Secret's metadata) would be
        // copied verbatim into publish artifacts and re-applied — defeating sealing. Reject it.
        RejectPlaintextLastAppliedAnnotation(storeName, manifestPath, root);

        if (!TryGetNode(root, "metadata", out var metadataNode) || metadataNode is not YamlMappingNode metadata)
        {
            throw CreateInvalidManifestException(
                storeName,
                manifestPath,
                "has no metadata mapping with metadata.name.");
        }

        var name = ReadScalar(metadata, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(
                $"Secret store '{storeName}' references a SealedSecret manifest at '{manifestPath}' that has " +
                "no metadata.name. Diagnostic: ASPIRERADIUS044.");
        }

        var ns = ReadScalar(metadata, "namespace");
        var namespaceWasExplicit = !string.IsNullOrWhiteSpace(ns);
        return new Metadata(namespaceWasExplicit ? ns! : defaultNamespace, name!, namespaceWasExplicit);
    }

    private static bool ContainsPlaintextTemplateData(YamlMappingNode template, string field) =>
        TryGetNode(template, field, out var value) && HasContent(value);

    // The annotation key `kubectl apply` uses to stash the last-applied object JSON.
    private const string LastAppliedConfigurationAnnotation = "kubectl.kubernetes.io/last-applied-configuration";

    // Rejects a plaintext Kubernetes Secret embedded in a last-applied-configuration annotation on
    // either the SealedSecret itself (metadata.annotations) or the Secret it templates
    // (spec.template.metadata.annotations). An embedded *SealedSecret* carries no cleartext and is
    // fine; only a bare `kind: Secret` with data/stringData is a leak. Fails closed when the
    // annotation is present but cannot be parsed/verified.
    private static void RejectPlaintextLastAppliedAnnotation(
        string storeName, string manifestPath, YamlMappingNode root)
    {
        CheckLastAppliedAnnotation(storeName, manifestPath, root);

        if (TryGetNode(root, "spec", out var specNode) &&
            specNode is YamlMappingNode spec &&
            TryGetNode(spec, "template", out var templateNode) &&
            templateNode is YamlMappingNode template)
        {
            CheckLastAppliedAnnotation(storeName, manifestPath, template);
        }
    }

    private static void CheckLastAppliedAnnotation(
        string storeName, string manifestPath, YamlMappingNode owner)
    {
        if (!TryGetNode(owner, "metadata", out var metadataNode) || metadataNode is not YamlMappingNode metadata ||
            !TryGetNode(metadata, "annotations", out var annotationsNode) || annotationsNode is not YamlMappingNode annotations ||
            !TryGetNode(annotations, LastAppliedConfigurationAnnotation, out var valueNode))
        {
            return;
        }

        // The annotation is present. A legitimate value is always a JSON string scalar (Kubernetes
        // annotation values are `map[string]string`). Anything else — a YAML mapping/sequence, or a
        // null/empty scalar where we expected JSON — cannot be verified free of cleartext, so fail
        // closed rather than skip it.
        if (valueNode is not YamlScalarNode { Value: { } lastApplied } || EmbedsPlaintextSecret(lastApplied))
        {
            throw new InvalidOperationException(
                $"Secret store '{storeName}' references a SealedSecret manifest at '{manifestPath}' whose " +
                $"'{LastAppliedConfigurationAnnotation}' annotation embeds a plaintext Kubernetes Secret " +
                "(kind 'Secret' with 'data'/'stringData'), or content that cannot be verified as sealed. Such " +
                "annotations are copied verbatim into publish artifacts and applied to the cluster, so the " +
                "cleartext would leak. Re-seal from a clean manifest without the annotation. " +
                "Diagnostic: ASPIRERADIUS063.");
        }
    }

    // Example annotation value (a single JSON string):
    //   {"apiVersion":"v1","kind":"Secret","metadata":{...},"data":{"password":"cGFzcw=="}}
    // Returns true when that JSON is a plaintext Secret carrying data/stringData, or when it cannot
    // be parsed as the expected object (fail closed). An embedded SealedSecret returns false.
    private static bool EmbedsPlaintextSecret(string lastAppliedJson)
    {
        try
        {
            using var document = JsonDocument.Parse(lastAppliedJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            var kind = root.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String
                ? kindElement.GetString()
                : null;
            if (!string.Equals(kind, "Secret", StringComparison.Ordinal))
            {
                return false;
            }

            return HasNonEmptyValue(root, "data") || HasNonEmptyValue(root, "stringData");
        }
        catch (JsonException)
        {
            return true;
        }
    }

    // For a `kind: Secret`, ANY non-empty representation of data/stringData is treated as a potential
    // cleartext leak — not only a non-empty JSON object. A well-formed Secret uses an object map, but
    // a hand-crafted/malformed annotation could carry cleartext as a string or array; those cannot be
    // proven safe, so they fail closed. Absent, null, empty-object, empty-string, and empty-array
    // representations carry no data and are allowed.
    private static bool HasNonEmptyValue(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => HasAnyChild(element),
            JsonValueKind.Array => element.GetArrayLength() > 0,
            JsonValueKind.String => element.GetString() is { Length: > 0 },
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            _ => true,
        };
    }

    private static bool HasAnyChild(JsonElement obj)
    {
        foreach (var _ in obj.EnumerateObject())
        {
            return true;
        }

        return false;
    }

    private static bool HasContent(YamlNode node) =>
        node switch
        {
            YamlScalarNode scalar => !string.IsNullOrEmpty(scalar.Value),
            YamlMappingNode mapping => mapping.Children.Count > 0,
            YamlSequenceNode sequence => sequence.Children.Count > 0,
            _ => true
        };

    private static string? ReadScalar(YamlMappingNode mapping, string key) =>
        TryGetNode(mapping, key, out var node) && node is YamlScalarNode scalar ? scalar.Value : null;

    private static bool TryGetNode(YamlMappingNode mapping, string key, out YamlNode node)
    {
        foreach (var (candidateKey, value) in mapping.Children)
        {
            if (candidateKey is YamlScalarNode scalarKey &&
                string.Equals(scalarKey.Value, key, StringComparison.Ordinal))
            {
                node = value;
                return true;
            }
        }

        node = null!;
        return false;
    }

    private static void ValidateStructure(string storeName, string manifestPath, string text)
    {
        var parser = new Parser(new StringReader(text));
        var stack = new Stack<MappingFrame>();

        while (parser.MoveNext())
        {
            var yamlEvent = parser.Current;

            if (yamlEvent is AnchorAlias)
            {
                throw CreateInvalidManifestException(
                    storeName,
                    manifestPath,
                    "uses YAML aliases. Provide a self-contained SealedSecret manifest without anchors, aliases, or merge keys.");
            }

            if (yamlEvent is NodeEvent nodeEvent)
            {
                if (!nodeEvent.Anchor.IsEmpty || HasExplicitTag(nodeEvent))
                {
                    throw CreateInvalidManifestException(
                        storeName,
                        manifestPath,
                        "uses YAML anchors or explicit tags. Provide a plain SealedSecret manifest without anchors, aliases, merge keys, or tags.");
                }

                RegisterNodeWithParent(storeName, manifestPath, yamlEvent, stack);
            }

            if (yamlEvent is MappingStart)
            {
                stack.Push(new MappingFrame());
            }
            else if (yamlEvent is MappingEnd)
            {
                // A well-formed stream always pairs MappingStart/MappingEnd, but guard the pop so a
                // malformed or out-of-sync event stream fails as ASPIRERADIUS044 rather than escaping
                // ValidateStructure as a raw InvalidOperationException from Stack.Pop() on an empty stack.
                if (stack.Count == 0)
                {
                    throw CreateInvalidManifestException(
                        storeName,
                        manifestPath,
                        "is malformed YAML with an unbalanced mapping. Provide a single well-formed encrypted Bitnami SealedSecret document.");
                }

                stack.Pop();
            }
            else if (yamlEvent is SequenceStart)
            {
                stack.Push(MappingFrame.s_sequence);
            }
            else if (yamlEvent is SequenceEnd)
            {
                if (stack.Count == 0)
                {
                    throw CreateInvalidManifestException(
                        storeName,
                        manifestPath,
                        "is malformed YAML with an unbalanced sequence. Provide a single well-formed encrypted Bitnami SealedSecret document.");
                }

                stack.Pop();
            }
        }
    }

    private static bool HasExplicitTag(NodeEvent nodeEvent) =>
        !nodeEvent.Tag.IsEmpty &&
        !string.Equals(nodeEvent.Tag.Value, "!", StringComparison.Ordinal);

    private static void RegisterNodeWithParent(
        string storeName, string manifestPath, ParsingEvent yamlEvent, Stack<MappingFrame> stack)
    {
        if (stack.Count == 0)
        {
            return;
        }

        var frame = stack.Peek();
        if (frame.IsSequence)
        {
            return;
        }

        if (frame.ExpectsKey)
        {
            if (yamlEvent is not Scalar scalar)
            {
                throw CreateInvalidManifestException(
                    storeName,
                    manifestPath,
                    "uses a non-scalar YAML mapping key. Provide a plain SealedSecret manifest with scalar keys.");
            }

            var key = scalar.Value;
            if (string.Equals(key, "<<", StringComparison.Ordinal))
            {
                throw CreateInvalidManifestException(
                    storeName,
                    manifestPath,
                    "uses YAML merge keys. Provide a self-contained SealedSecret manifest without anchors, aliases, or merge keys.");
            }

            // YAML allows duplicate keys, and some high-level readers keep the last value. For a
            // security gate that rejects plaintext-capable fields, last-wins semantics would let a
            // document advertise `kind: SealedSecret` first and then override it with `kind: Secret`.
            if (!frame.Keys.Add(key))
            {
                throw CreateInvalidManifestException(
                    storeName,
                    manifestPath,
                    $"contains a duplicate YAML mapping key '{key}'. Provide a single unambiguous SealedSecret manifest.");
            }
        }

        frame.ExpectsKey = !frame.ExpectsKey;
    }

    private static InvalidOperationException CreateInvalidManifestException(
        string storeName, string manifestPath, string reason, Exception? innerException = null) =>
        new(
            $"Secret store '{storeName}' references a SealedSecret manifest at '{manifestPath}' that {reason} " +
            "Diagnostic: ASPIRERADIUS044.",
            innerException);

    private sealed class MappingFrame
    {
        internal static readonly MappingFrame s_sequence = new(isSequence: true);

        private MappingFrame(bool isSequence)
        {
            IsSequence = isSequence;
        }

        internal MappingFrame()
        {
        }

        internal bool IsSequence { get; }

        internal bool ExpectsKey { get; set; } = true;

        internal HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
    }
}
