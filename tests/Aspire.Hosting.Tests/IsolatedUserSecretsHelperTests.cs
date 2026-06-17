// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.UserSecrets;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class IsolatedUserSecretsHelperTests : IDisposable
{
    private readonly List<string> _createdSecretIds = [];

    public void Dispose()
    {
        foreach (var secretId in _createdSecretIds)
        {
            IsolatedUserSecretsHelper.CleanupIsolatedUserSecrets(secretId);
        }
    }

    [Fact]
    public void CreateIsolatedUserSecrets_WithNullId_ReturnsNull()
    {
        var result = IsolatedUserSecretsHelper.CreateIsolatedUserSecrets(null);

        Assert.Null(result);
    }

    [Fact]
    public void CreateIsolatedUserSecrets_WithEmptyId_ReturnsNull()
    {
        var result = IsolatedUserSecretsHelper.CreateIsolatedUserSecrets("");

        Assert.Null(result);
    }

    [Fact]
    public void CreateIsolatedUserSecrets_WithWhitespaceId_ReturnsNull()
    {
        var result = IsolatedUserSecretsHelper.CreateIsolatedUserSecrets("   ");

        Assert.Null(result);
    }

    [Fact]
    public void CreateIsolatedUserSecrets_WithNonExistentSecrets_CreatesIsolatedIdWithoutCopy()
    {
        var nonExistentId = Guid.NewGuid().ToString();

        var result = IsolatedUserSecretsHelper.CreateIsolatedUserSecrets(nonExistentId);

        Assert.NotNull(result);
        Assert.NotEqual(nonExistentId, result);
        _createdSecretIds.Add(result);

        var isolatedPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(result);
        Assert.False(File.Exists(isolatedPath));
    }

    [Fact]
    public void CreateIsolatedUserSecrets_WithExistingSecrets_CreatesIsolatedCopy()
    {
        var sourceId = Guid.NewGuid().ToString();
        var sourcePath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(sourceId);
        var sourceDir = Path.GetDirectoryName(sourcePath)!;
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(sourcePath, """{"TestKey": "TestValue"}""");
        _createdSecretIds.Add(sourceId);

        var isolatedId = IsolatedUserSecretsHelper.CreateIsolatedUserSecrets(sourceId);

        Assert.NotNull(isolatedId);
        Assert.NotEqual(sourceId, isolatedId);
        _createdSecretIds.Add(isolatedId);

        var isolatedPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(isolatedId);
        Assert.True(File.Exists(isolatedPath));

        var content = File.ReadAllText(isolatedPath);
        Assert.Contains("TestKey", content);
        Assert.Contains("TestValue", content);
    }

    [Fact]
    public void CleanupIsolatedUserSecrets_WithNullId_DoesNotThrow()
    {
        var exception = Record.Exception(() => IsolatedUserSecretsHelper.CleanupIsolatedUserSecrets(null));

        Assert.Null(exception);
    }

    [Fact]
    public void CleanupIsolatedUserSecrets_WithEmptyId_DoesNotThrow()
    {
        var exception = Record.Exception(() => IsolatedUserSecretsHelper.CleanupIsolatedUserSecrets(""));

        Assert.Null(exception);
    }

    [Fact]
    public void CleanupIsolatedUserSecrets_WithNonExistentSecrets_DoesNotThrow()
    {
        var nonExistentId = Guid.NewGuid().ToString();

        var exception = Record.Exception(() => IsolatedUserSecretsHelper.CleanupIsolatedUserSecrets(nonExistentId));

        Assert.Null(exception);
    }

    [Fact]
    public void CleanupIsolatedUserSecrets_WithExistingSecrets_DeletesSecretsFileAndDirectory()
    {
        var secretId = Guid.NewGuid().ToString();
        var secretsPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(secretId);
        var secretsDir = Path.GetDirectoryName(secretsPath)!;
        Directory.CreateDirectory(secretsDir);
        File.WriteAllText(secretsPath, """{"TestKey": "TestValue"}""");

        Assert.True(File.Exists(secretsPath));
        Assert.True(Directory.Exists(secretsDir));

        IsolatedUserSecretsHelper.CleanupIsolatedUserSecrets(secretId);

        Assert.False(File.Exists(secretsPath));
        Assert.False(Directory.Exists(secretsDir));
    }
}
