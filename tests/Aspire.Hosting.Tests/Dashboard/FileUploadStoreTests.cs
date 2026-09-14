// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Dashboard;

public class FileUploadStoreTests
{
    [Fact]
    public void CreateEntry_ValidFileName_ReturnsIdAndPath()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, filePath) = fileUploadStore.CreateEntry("test.txt");

        Assert.NotNull(fileId);
        Assert.NotEmpty(fileId);
        Assert.Equal("test.txt", Path.GetFileName(filePath));
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void GetFilePath_ExistingEntry_ReturnsPath()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, filePath) = fileUploadStore.CreateEntry("test.txt");

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId));
    }

    [Fact]
    public void GetFilePath_NonexistentEntry_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        Assert.Null(fileUploadStore.GetFilePath("nonexistent"));
    }

    [Fact]
    public void GetFileName_ExistingEntry_ReturnsFileName()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, _) = fileUploadStore.CreateEntry("cert.pem");

        Assert.Equal("cert.pem", fileUploadStore.GetFileName(fileId));
    }

    [Fact]
    public void RemoveEntry_ExistingEntry_DeletesFile()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, filePath) = fileUploadStore.CreateEntry("temp.bin");
        Assert.True(File.Exists(filePath));

        fileUploadStore.RemoveEntry(fileId);

        Assert.Null(fileUploadStore.GetFilePath(fileId));
    }

    [Theory]
    [InlineData("../../../etc/passwd", "passwd")]
    [InlineData("/etc/cron.d/evil", "evil")]
    [InlineData("..\\..\\windows\\system32\\evil.exe", "evil.exe")]
    [InlineData("C:\\windows\\system32\\config.sys", "config.sys")]
    public void CreateEntry_PathTraversalFileName_SanitizesToLeafName(string maliciousFileName, string expectedLeafName)
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, filePath) = fileUploadStore.CreateEntry(maliciousFileName);

        Assert.NotEqual(maliciousFileName, filePath);
        Assert.Equal(expectedLeafName, Path.GetFileName(filePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("\\")]
    public void CreateEntry_EmptyOrRootOnlyFileName_GeneratesRandomName(string emptyFileName)
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, filePath) = fileUploadStore.CreateEntry(emptyFileName);

        Assert.NotNull(fileId);
        Assert.NotEmpty(Path.GetFileName(filePath));
    }

    [Fact]
    public void ResolveFileReferences_ValidReference_ResolvesCorrectly()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, filePath) = fileUploadStore.CreateEntry("cert.pem");
        File.WriteAllText(filePath, "certificate-content");

        var json = $"[{{\"Id\":\"{fileId}\",\"Name\":\"cert.pem\"}}]";
        var resolvedFiles = FileUploadStore.ResolveFileReferences(fileUploadStore, json, "CertInput", NullLogger.Instance);

        Assert.NotNull(resolvedFiles);
        var file = Assert.Single(resolvedFiles);
        Assert.Equal(fileId, file.Id);
        Assert.Equal("cert.pem", file.Name);
        Assert.Equal(filePath, file.FilePath);
    }

    [Fact]
    public void ResolveFileReferences_UnknownId_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);
        var json = "[{\"Id\":\"nonexistent-id\",\"Name\":\"file.txt\"}]";

        var result = FileUploadStore.ResolveFileReferences(fileUploadStore, json, "TestInput", NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveFileReferences_MalformedJson_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);
        var json = "not-valid-json";

        var result = FileUploadStore.ResolveFileReferences(fileUploadStore, json, "TestInput", NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveFileReferences_EmptyValue_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var result = FileUploadStore.ResolveFileReferences(fileUploadStore, "", "TestInput", NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void Dispose_CleansUpAllFiles()
    {
        using var fileSystemService = new TestFileSystemService();
        var fileUploadStore = new FileUploadStore(fileSystemService);

        var (_, filePath1) = fileUploadStore.CreateEntry("file1.txt");
        var (_, filePath2) = fileUploadStore.CreateEntry("file2.txt");

        Assert.True(File.Exists(filePath1));
        Assert.True(File.Exists(filePath2));

        fileUploadStore.Dispose();

        Assert.Null(fileUploadStore.GetFilePath("anything"));
    }
}
