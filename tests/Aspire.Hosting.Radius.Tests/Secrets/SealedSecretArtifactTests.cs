// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Radius.Secrets;

namespace Aspire.Hosting.Radius.Tests.Secrets;

public class SealedSecretArtifactTests
{
    [Fact]
    public void RelativePath_NamespacesByStoreName()
    {
        var path = SealedSecretArtifact.RelativePath("db-creds", "/authorhome/secrets/sealed.yaml");

        Assert.Equal(Path.Combine("sealed-secrets", "db-creds", "sealed.yaml"), path);
    }

    [Fact]
    public void RelativePath_SameFileNameDifferentStores_DoNotCollide()
    {
        // Two stores whose source manifests share a file name (from different source directories)
        // must resolve to distinct, per-store destinations so the copy step cannot overwrite.
        var first = SealedSecretArtifact.RelativePath("store-a", "/a/sealed.yaml");
        var second = SealedSecretArtifact.RelativePath("store-b", "/b/sealed.yaml");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ResolvePath_CombinesOutputDirectoryWithRelativePath()
    {
        var resolved = SealedSecretArtifact.ResolvePath("/out", "db-creds", "/author/sealed.yaml");

        Assert.Equal(Path.Combine("/out", "sealed-secrets", "db-creds", "sealed.yaml"), resolved);
    }
}
