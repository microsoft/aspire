// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Hosting.Radius.Secrets;

/// <summary>
/// Minimal reader for a committed, encrypted Bitnami <c>SealedSecret</c> manifest. Extracts
/// the <c>metadata.name</c> / <c>metadata.namespace</c> that identify the <c>Secret</c> the
/// controller will materialize (used for the <c>secretStores.resource</c> reference and the
/// deploy-time materialization poll). The manifest content is already encrypted; only its
/// metadata is read. A missing or unreadable manifest fails with <c>ASPIRERADIUS044</c>.
/// </summary>
internal static class SealedSecretManifest
{
    /// <summary>
    /// The result of reading a <c>SealedSecret</c> manifest's identifying metadata.
    /// <see cref="NamespaceWasExplicit"/> distinguishes a namespace read from the manifest from
    /// one defaulted to the owning environment: only when defaulted may the deploy step add
    /// <c>-n</c> to <c>kubectl apply</c> (passing <c>-n</c> when the object already declares a
    /// different namespace makes <c>kubectl apply</c> fail).
    /// </summary>
    internal readonly record struct Metadata(string Namespace, string Name, bool NamespaceWasExplicit);

    /// <summary>
    /// Reads the <c>metadata.name</c> (required) and <c>metadata.namespace</c> (optional,
    /// defaulting to <paramref name="defaultNamespace"/>) from the manifest at
    /// <paramref name="manifestPath"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The manifest is missing, unreadable, has no <c>metadata.name</c>, or is not a single
    /// encrypted Bitnami <c>SealedSecret</c> document (<c>ASPIRERADIUS044</c>).
    /// </exception>
    internal static Metadata ReadMetadata(
        string storeName, string manifestPath, string defaultNamespace)
    {
        string text;
        try
        {
            text = File.ReadAllText(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Secret store '{storeName}' references a SealedSecret manifest at '{manifestPath}' that " +
                $"is missing or unreadable ({ex.GetType().Name}). Diagnostic: ASPIRERADIUS044.", ex);
        }

        // Gate on the document kind BEFORE trusting any metadata. A plaintext `kind: Secret`
        // manifest also carries a `metadata.name`, so without this check we would happily copy it
        // into published artifacts and `kubectl apply` it — leaking cleartext credentials to disk
        // and the cluster. Only an encrypted Bitnami SealedSecret is ever accepted.
        ValidateIsSealedSecret(storeName, manifestPath, text);

        // Restrict the metadata search to the top-level `metadata:` block so we never pick up a
        // nested `spec.template.metadata.name/namespace` or an `encryptedData` key literally named
        // `name`/`namespace`. kubeseal output looks like:
        //   apiVersion: bitnami.com/v1alpha1
        //   kind: SealedSecret
        //   metadata:
        //     name: db-creds
        //     namespace: app
        //   spec:
        //     encryptedData:
        //       username: AgB...
        //     template:
        //       metadata:
        //         name: db-creds        <-- must NOT be matched
        var metadataBlock = ExtractTopLevelMetadataBlock(text);

        var name = MatchMetadataField(metadataBlock, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(
                $"Secret store '{storeName}' references a SealedSecret manifest at '{manifestPath}' that has " +
                "no metadata.name. Diagnostic: ASPIRERADIUS044.");
        }

        var ns = MatchMetadataField(metadataBlock, "namespace");
        var namespaceWasExplicit = !string.IsNullOrWhiteSpace(ns);
        return new Metadata(namespaceWasExplicit ? ns! : defaultNamespace, name!, namespaceWasExplicit);
    }

    // Rejects anything that is not a single encrypted Bitnami SealedSecret document. kubeseal output
    // looks like:
    //   apiVersion: bitnami.com/v1alpha1
    //   kind: SealedSecret
    //   metadata: { ... }
    //   spec: { encryptedData: { ... } }
    // We require kind == SealedSecret and apiVersion starting "bitnami.com/", and explicitly reject a
    // plaintext `kind: Secret`. Multi-document (`---`-separated) and list-form (`kind: List`) manifests
    // are rejected because the line-oriented reader below cannot unambiguously identify a single object.
    private static void ValidateIsSealedSecret(string storeName, string manifestPath, string text)
    {
        if (ContainsMultipleDocuments(text))
        {
            throw new InvalidOperationException(
                $"Secret store '{storeName}' references a manifest at '{manifestPath}' that contains multiple YAML " +
                "documents. Provide a single encrypted Bitnami SealedSecret document. Diagnostic: ASPIRERADIUS044.");
        }

        var kind = ReadTopLevelScalar(text, "kind");
        var apiVersion = ReadTopLevelScalar(text, "apiVersion");

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
    }

    // True when the document has more than one YAML document, i.e. a document-start marker appears on
    // its own line after non-blank/non-comment content. A leading marker (document start) is allowed.
    // A YAML directives-end / document-start marker is `---` at line start followed by end-of-line or
    // whitespace (so `---`, `--- `, and `--- # comment` all separate documents), but NOT `----` or
    // `---foo` (which are ordinary scalars/keys, not markers).
    private static bool ContainsMultipleDocuments(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sawContent = false;
        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (IsDocumentMarker(trimmed))
            {
                if (sawContent)
                {
                    return true;
                }

                continue;
            }

            if (trimmed.Length != 0 && !trimmed.StartsWith('#'))
            {
                sawContent = true;
            }
        }

        return false;
    }

    // A YAML document-start marker is exactly `---` optionally followed by whitespace and/or a comment
    // or inline content. `----` (four dashes) and `---x` (no separating whitespace) are not markers.
    private static bool IsDocumentMarker(string trimmedLine) =>
        trimmedLine == "---" ||
        (trimmedLine.StartsWith("---", StringComparison.Ordinal) &&
         trimmedLine.Length > 3 &&
         char.IsWhiteSpace(trimmedLine[3]));

    // Reads a top-level (column-0) scalar such as `kind:` or `apiVersion:` from the whole document,
    // ignoring the same keys when they appear indented under `spec`/`template`/`metadata`.
    private static string? ReadTopLevelScalar(string text, string field)
    {
        var match = Regex.Match(
            text,
            $@"(?m)^{Regex.Escape(field)}:\s*(?<v>[^\s#]+)\s*$");
        return match.Success ? match.Groups["v"].Value.Trim('\'', '"') : null;
    }

    // Returns the region of the document that belongs to the top-level `metadata:` mapping: the
    // lines after a column-0 `metadata:` up to (but not including) the next column-0 key. Falls
    // back to the whole document when no top-level `metadata:` is found (the field match below
    // then simply finds nothing and callers report ASPIRERADIUS044).
    private static string ExtractTopLevelMetadataBlock(string text)
    {
        // Match `metadata:` anchored at column 0 (no leading whitespace), then capture every
        // subsequent line that is either blank or indented (i.e. still inside the mapping),
        // stopping at the next non-indented key such as `spec:`.
        var match = Regex.Match(text, @"(?m)^metadata:[ \t]*\r?$(?<body>(\r?\n([ \t]+\S.*|[ \t]*))*)");
        return match.Success ? match.Groups["body"].Value : text;
    }

    // Matches a metadata field (name/namespace) at any indentation within the already-narrowed
    // top-level `metadata:` block. The block is small and machine-generated by kubeseal, so a
    // line-oriented match is sufficient without taking a YAML dependency.
    private static string? MatchMetadataField(string metadataBlock, string field)
    {
        var match = Regex.Match(
            metadataBlock,
            $@"(?m)^\s+{Regex.Escape(field)}:\s*(?<v>[^\s#]+)\s*$");
        return match.Success ? match.Groups["v"].Value.Trim('\'', '"') : null;
    }
}
