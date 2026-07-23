// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Radius.Secrets;

namespace Aspire.Hosting.Radius.Tests.Secrets;

public class SealedSecretManifestTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sealed-secret-manifest-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string content)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.sealed.yaml");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ReadMetadata_ExplicitNamespace_IsMarkedExplicit()
    {
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "  namespace: app\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    username: AgBcipher\n");

        var metadata = SealedSecretManifest.ReadMetadata("store", path, "env-default");

        Assert.Equal("db-creds", metadata.Name);
        Assert.Equal("app", metadata.Namespace);
        Assert.True(metadata.NamespaceWasExplicit);
    }

    [Fact]
    public void ReadMetadata_MissingNamespace_DefaultsAndIsMarkedImplicit()
    {
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    username: AgBcipher\n");

        var metadata = SealedSecretManifest.ReadMetadata("store", path, "env-default");

        Assert.Equal("db-creds", metadata.Name);
        Assert.Equal("env-default", metadata.Namespace);
        Assert.False(metadata.NamespaceWasExplicit);
    }

    [Fact]
    public void ReadMetadata_IgnoresNestedTemplateMetadata()
    {
        // The nested spec.template.metadata block also has name/namespace; the top-level
        // metadata block must win regardless of document ordering.
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: real-name\n" +
            "  namespace: real-ns\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    username: AgBcipher\n" +
            "  template:\n" +
            "    metadata:\n" +
            "      name: decoy-name\n" +
            "      namespace: decoy-ns\n");

        var metadata = SealedSecretManifest.ReadMetadata("store", path, "env-default");

        Assert.Equal("real-name", metadata.Name);
        Assert.Equal("real-ns", metadata.Namespace);
        Assert.True(metadata.NamespaceWasExplicit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\0path")]
    public void ReadMetadata_InvalidPath_Throws_ASPIRERADIUS044(string path)
    {
        // File.ReadAllBytes throws ArgumentException for an empty/whitespace/invalid-character path
        // (not IOException). That must still normalize to ASPIRERADIUS044 like a missing/unreadable file.
        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS044", ex.Message);
    }

    [Fact]
    public void ReadMetadata_MissingName_Throws_ASPIRERADIUS044()
    {
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  namespace: app\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    username: AgBcipher\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS044", ex.Message);
    }

    [Theory]
    [InlineData("Bad_Name")]
    [InlineData("UPPER")]
    [InlineData("name/withslash")]
    [InlineData("-leadinghyphen")]
    public void ReadMetadata_InvalidName_Throws_ASPIRERADIUS044(string name)
    {
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            $"  name: {name}\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    username: AgBcipher\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS044", ex.Message);
    }

    [Fact]
    public void ReadMetadata_InvalidNamespace_Throws_ASPIRERADIUS044()
    {
        // A DNS-1123 label caps at 63 chars, so a 64-char namespace is invalid.
        var longNamespace = new string('a', 64);
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            $"  namespace: {longNamespace}\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    username: AgBcipher\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS044", ex.Message);
    }

    [Fact]
    public void ReadMetadata_PlaintextSecret_Throws_ASPIRERADIUS044()
    {
        // A plaintext Kubernetes Secret also carries metadata.name; it must be rejected before its
        // metadata is trusted so cleartext credentials are never copied or applied.
        var path = Write(
            "apiVersion: v1\n" +
            "kind: Secret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "  namespace: app\n" +
            "data:\n" +
            "  username: dXNlcg==\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS044", ex.Message);
        Assert.Contains("plaintext", ex.Message);
    }

    [Fact]
    public void ReadMetadata_WrongKind_Throws_ASPIRERADIUS044()
    {
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: ConfigMap\n" +
            "metadata:\n" +
            "  name: db-creds\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS044", ex.Message);
    }

    [Fact]
    public void ReadMetadata_WrongApiVersion_Throws_ASPIRERADIUS044()
    {
        var path = Write(
            "apiVersion: v1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS044", ex.Message);
    }

    [Fact]
    public void ReadMetadata_MultiDocument_Throws_ASPIRERADIUS044()
    {
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "---\n" +
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: other-creds\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS044", ex.Message);
        Assert.Contains("multiple YAML", ex.Message);
    }

    [Fact]
    public void ReadMetadata_MultiDocumentWithCommentedSeparator_Throws_ASPIRERADIUS044()
    {
        // A YAML document-start marker may carry a trailing comment (`--- # ...`); it still separates
        // documents, so a second (possibly plaintext) document must not slip past validation.
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "--- # second document\n" +
            "apiVersion: v1\n" +
            "kind: Secret\n" +
            "metadata:\n" +
            "  name: plaintext-creds\n" +
            "data:\n" +
            "  password: c2VjcmV0\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS044", ex.Message);
        Assert.Contains("multiple YAML", ex.Message);
    }

    [Fact]
    public void ReadMetadata_LeadingDocumentMarker_IsAllowed()
    {
        // A single leading `---` document-start marker is valid YAML and must not be mistaken for a
        // multi-document manifest.
        var path = Write(
            "---\n" +
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "  namespace: app\n");

        var metadata = SealedSecretManifest.ReadMetadata("store", path, "env-default");

        Assert.Equal("db-creds", metadata.Name);
        Assert.Equal("app", metadata.Namespace);
    }

    [Theory]
    [InlineData(
        "apiVersion: bitnami.com/v1alpha1\n" +
        "kind: SealedSecret\n" +
        "kind: Secret\n" +
        "metadata:\n" +
        "  name: db-creds\n" +
        "data:\n" +
        "  password: c2VjcmV0\n")]
    [InlineData(
        "apiVersion: bitnami.com/v1alpha1\n" +
        "apiVersion: v1\n" +
        "kind: SealedSecret\n" +
        "metadata:\n" +
        "  name: db-creds\n")]
    [InlineData(
        "apiVersion: bitnami.com/v1alpha1\n" +
        "kind: SealedSecret\n" +
        "metadata:\n" +
        "  name: db-creds\n" +
        "metadata:\n" +
        "  name: other-creds\n")]
    [InlineData(
        "apiVersion: bitnami.com/v1alpha1\n" +
        "\"kind\": Secret\n" +
        "metadata:\n" +
        "  name: db-creds\n" +
        "data:\n" +
        "  password: c2VjcmV0\n")]
    [InlineData(
        "apiVersion: bitnami.com/v1alpha1\n" +
        "kind : Secret\n" +
        "metadata:\n" +
        "  name: db-creds\n" +
        "data:\n" +
        "  password: c2VjcmV0\n")]
    [InlineData(
        "apiVersion: bitnami.com/v1alpha1\n" +
        "kind: !!str SealedSecret\n" +
        "metadata:\n" +
        "  name: db-creds\n")]
    [InlineData("{apiVersion: bitnami.com/v1alpha1, kind: Secret, metadata: {name: db-creds}, data: {password: c2VjcmV0}}\n")]
    [InlineData("{\"apiVersion\":\"bitnami.com/v1alpha1\",\"kind\":\"Secret\",\"metadata\":{\"name\":\"db-creds\"},\"data\":{\"password\":\"c2VjcmV0\"}}\n")]
    [InlineData(
        "apiVersion: bitnami.com/v1alpha1\n" +
        "kind: SealedSecret\n" +
        "metadata: &metadata\n" +
        "  name: db-creds\n" +
        "spec:\n" +
        "  template:\n" +
        "    metadata:\n" +
        "      <<: *metadata\n")]
    [InlineData(
        "apiVersion: bitnami.com/v1alpha1\n" +
        "kind: SealedSecret\n" +
        "metadata:\n" +
        "  name: db-creds\n" +
        "data:\n" +
        "  password: c2VjcmV0\n")]
    [InlineData(
        "apiVersion: bitnami.com/v1alpha1\n" +
        "kind: SealedSecret\n" +
        "metadata:\n" +
        "  name: db-creds\n" +
        "stringData:\n" +
        "  password: secret\n")]
    [InlineData(
        "apiVersion: bitnami.com/v1alpha1\n" +
        "kind: SealedSecret\n" +
        "metadata:\n" +
        "  name: db-creds\n" +
        "spec:\n" +
        "  template:\n" +
        "    data:\n" +
        "      password: c2VjcmV0\n")]
    [InlineData(
        "apiVersion: bitnami.com/v1alpha1\n" +
        "kind: SealedSecret\n" +
        "metadata:\n" +
        "  name: db-creds\n" +
        "spec:\n" +
        "  template:\n" +
        "    stringData:\n" +
        "      password: secret\n")]
    [InlineData("just a scalar\n")]
    [InlineData("- apiVersion: bitnami.com/v1alpha1\n- kind: SealedSecret\n")]
    [InlineData(
        "apiVersion: bitnami.com/v1alpha1\n" +
        "kind: SealedSecret\n" +
        "metadata:\n" +
        "  name: db-creds\n" +
        "---\n" +
        "apiVersion: v1\n" +
        "kind: Secret\n")]
    [InlineData(
        "apiVersion: bitnami.com/v1alpha1\n" +
        "kind: [\n")]
    public void ReadMetadata_UnsafeOrMalformedYaml_Throws_ASPIRERADIUS044(string content)
    {
        var path = Write(content);

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS044", ex.Message);
    }

    [Fact]
    public void ReadMetadata_PlaintextSecretInTopLevelLastAppliedAnnotation_Throws_ASPIRERADIUS063()
    {
        // `kubectl apply` stashes the full applied object under this annotation. It is NOT encrypted,
        // so a plaintext Secret embedded here would be copied into artifacts and re-applied verbatim.
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "  namespace: app\n" +
            "  annotations:\n" +
            "    kubectl.kubernetes.io/last-applied-configuration: '{\"apiVersion\":\"v1\",\"kind\":\"Secret\",\"metadata\":{\"name\":\"db-creds\"},\"data\":{\"password\":\"c2VjcmV0\"}}'\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    password: AgBcipher\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS063", ex.Message);
    }

    [Fact]
    public void ReadMetadata_PlaintextSecretInTemplateLastAppliedAnnotation_Throws_ASPIRERADIUS063()
    {
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    password: AgBcipher\n" +
            "  template:\n" +
            "    metadata:\n" +
            "      name: db-creds\n" +
            "      annotations:\n" +
            "        kubectl.kubernetes.io/last-applied-configuration: '{\"kind\":\"Secret\",\"metadata\":{\"name\":\"db-creds\"},\"stringData\":{\"password\":\"secret\"}}'\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS063", ex.Message);
    }

    [Fact]
    public void ReadMetadata_MalformedLastAppliedAnnotation_FailsClosed_Throws_ASPIRERADIUS063()
    {
        // Present but unparseable content cannot be proven free of cleartext — fail closed.
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "  annotations:\n" +
            "    kubectl.kubernetes.io/last-applied-configuration: 'not-json{'\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    password: AgBcipher\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS063", ex.Message);
    }

    [Fact]
    public void ReadMetadata_NonScalarLastAppliedAnnotation_FailsClosed_Throws_ASPIRERADIUS063()
    {
        // A present annotation whose value is a YAML mapping (not the expected JSON string scalar)
        // cannot be verified free of cleartext, so fail closed rather than skip it.
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "  annotations:\n" +
            "    kubectl.kubernetes.io/last-applied-configuration:\n" +
            "      kind: Secret\n" +
            "      data:\n" +
            "        password: c2VjcmV0\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    password: AgBcipher\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS063", ex.Message);
    }

    [Fact]
    public void ReadMetadata_SecretWithNonObjectDataInAnnotation_FailsClosed_Throws_ASPIRERADIUS063()
    {
        // A `kind: Secret` whose `data` is a non-empty string (malformed but present) still carries
        // potential cleartext and must fail closed.
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "  annotations:\n" +
            "    kubectl.kubernetes.io/last-applied-configuration: '{\"kind\":\"Secret\",\"metadata\":{\"name\":\"db-creds\"},\"data\":\"c2VjcmV0\"}'\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    password: AgBcipher\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS063", ex.Message);
    }

    [Theory]
    [InlineData("bitnami.com/")]
    [InlineData("bitnami.com/v1beta1")]
    [InlineData("bitnami.com/v1alpha1x")]
    public void ReadMetadata_MalformedOrUnsupportedApiVersion_Throws_ASPIRERADIUS044(string apiVersion)
    {
        // A `bitnami.com/*` prefix check used to accept the malformed value `bitnami.com/` and
        // arbitrary unsupported versions; only the exact supported group/version is accepted so such
        // manifests fail fast instead of at cluster apply time.
        var path = Write(
            $"apiVersion: {apiVersion}\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS044", ex.Message);
    }

    [Fact]
    public void ReadMetadata_EmbeddedSealedSecretWithPlaintextTemplateData_Throws_ASPIRERADIUS063()
    {
        // An embedded SealedSecret keeps its sealed payload under spec.encryptedData, but a crafted
        // annotation can still carry cleartext under spec.template.data — the same field rejected on
        // the outer manifest. That must fail closed rather than be copied verbatim into artifacts.
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "  namespace: app\n" +
            "  annotations:\n" +
            "    kubectl.kubernetes.io/last-applied-configuration: '{\"apiVersion\":\"bitnami.com/v1alpha1\",\"kind\":\"SealedSecret\",\"metadata\":{\"name\":\"db-creds\"},\"spec\":{\"encryptedData\":{\"password\":\"AgBcipher\"},\"template\":{\"data\":{\"password\":\"c2VjcmV0\"}}}}'\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    password: AgBcipher\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS063", ex.Message);
    }

    [Fact]
    public void ReadMetadata_EmbeddedSealedSecretWithPlaintextTemplateStringData_Throws_ASPIRERADIUS063()
    {
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "  namespace: app\n" +
            "  annotations:\n" +
            "    kubectl.kubernetes.io/last-applied-configuration: '{\"kind\":\"SealedSecret\",\"spec\":{\"template\":{\"stringData\":{\"password\":\"secret\"}}}}'\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    password: AgBcipher\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS063", ex.Message);
    }

    [Fact]
    public void ReadMetadata_EmbeddedSealedSecretWithEmptyTemplateData_IsAllowed()
    {
        // A SealedSecret whose template carries an empty data map has no cleartext and stays allowed.
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "  namespace: app\n" +
            "  annotations:\n" +
            "    kubectl.kubernetes.io/last-applied-configuration: '{\"kind\":\"SealedSecret\",\"spec\":{\"encryptedData\":{\"password\":\"AgBcipher\"},\"template\":{\"data\":{}}}}'\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    password: AgBcipher\n");

        var metadata = SealedSecretManifest.ReadMetadata("store", path, "env-default");

        Assert.Equal("db-creds", metadata.Name);
        Assert.Equal("app", metadata.Namespace);
    }

    [Fact]
    public void ReadMetadata_SealedSecretInLastAppliedAnnotation_IsAllowed()
    {
        // An embedded *SealedSecret* carries no cleartext, so the annotation is fine.
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "  namespace: app\n" +
            "  annotations:\n" +
            "    kubectl.kubernetes.io/last-applied-configuration: '{\"apiVersion\":\"bitnami.com/v1alpha1\",\"kind\":\"SealedSecret\",\"metadata\":{\"name\":\"db-creds\"},\"spec\":{\"encryptedData\":{\"password\":\"AgBcipher\"}}}'\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    password: AgBcipher\n");

        var metadata = SealedSecretManifest.ReadMetadata("store", path, "env-default");

        Assert.Equal("db-creds", metadata.Name);
        Assert.Equal("app", metadata.Namespace);
    }

    [Fact]
    public void ReadMetadata_EmptySecretInLastAppliedAnnotation_IsAllowed()
    {
        // A `kind: Secret` annotation with no data/stringData carries no cleartext, so it is not a leak.
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "  namespace: app\n" +
            "  annotations:\n" +
            "    kubectl.kubernetes.io/last-applied-configuration: '{\"apiVersion\":\"v1\",\"kind\":\"Secret\",\"metadata\":{\"name\":\"db-creds\"}}'\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    password: AgBcipher\n");

        var metadata = SealedSecretManifest.ReadMetadata("store", path, "env-default");

        Assert.Equal("db-creds", metadata.Name);
        Assert.Equal("app", metadata.Namespace);
    }

    [Fact]
    public void ReadMetadata_DuplicateKindSmugglesPlaintextSecret_FailsClosed_Throws_ASPIRERADIUS063()
    {
        // System.Text.Json keeps duplicate property names and a single TryGetProperty("kind") lookup
        // observes only ONE of them. A crafted payload can lead with `kind: Secret` + data and append a
        // second `kind: SealedSecret` so a naive lookup clears it. Duplicate identity keys make the
        // object ambiguous and must fail closed.
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "  annotations:\n" +
            "    kubectl.kubernetes.io/last-applied-configuration: '{\"kind\":\"Secret\",\"data\":{\"password\":\"c2VjcmV0\"},\"kind\":\"SealedSecret\"}'\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    password: AgBcipher\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS063", ex.Message);
    }

    [Fact]
    public void ReadMetadata_MissingKindWithData_FailsClosed_Throws_ASPIRERADIUS063()
    {
        // Without a `kind` we cannot positively rule out a Secret, so any embedded data/stringData
        // is treated as a potential cleartext leak rather than assumed safe.
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "  annotations:\n" +
            "    kubectl.kubernetes.io/last-applied-configuration: '{\"metadata\":{\"name\":\"db-creds\"},\"data\":{\"password\":\"c2VjcmV0\"}}'\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    password: AgBcipher\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS063", ex.Message);
    }

    [Fact]
    public void ReadMetadata_NonStringKindWithData_FailsClosed_Throws_ASPIRERADIUS063()
    {
        // A non-string `kind` (here an object) is not positively a non-Secret kind, so an accompanying
        // non-empty data payload must fail closed rather than being cleared.
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "  annotations:\n" +
            "    kubectl.kubernetes.io/last-applied-configuration: '{\"kind\":{\"nested\":\"SealedSecret\"},\"data\":{\"password\":\"c2VjcmV0\"}}'\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    password: AgBcipher\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS063", ex.Message);
    }

    [Fact]
    public void ReadMetadata_DuplicateDataKeysInAnnotation_FailsClosed_Throws_ASPIRERADIUS063()
    {
        // Duplicate payload keys are likewise ambiguous (only one is observable), so fail closed even
        // when one of them appears empty.
        var path = Write(
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "  annotations:\n" +
            "    kubectl.kubernetes.io/last-applied-configuration: '{\"kind\":\"Secret\",\"data\":{},\"data\":{\"password\":\"c2VjcmV0\"}}'\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    password: AgBcipher\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SealedSecretManifest.ReadMetadata("store", path, "env-default"));

        Assert.Contains("ASPIRERADIUS063", ex.Message);
    }
}
