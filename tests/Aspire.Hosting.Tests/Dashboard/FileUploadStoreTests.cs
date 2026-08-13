// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Runtime.CompilerServices;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Dashboard;

public class FileUploadStoreTests
{
    private const int InteractionId = 1;
    private const string InputName = "File";

    [Fact]
    public void CreateEntry_ValidFileName_ReturnsIdAndPath()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "test.txt");

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

        var (fileId, filePath) = CreateEntry(fileUploadStore, "test.txt");

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
    }

    [Fact]
    public void GetFilePath_NonexistentEntry_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        Assert.Null(fileUploadStore.GetFilePath("nonexistent", InteractionId, InputName));
    }

    [Fact]
    public void GetFileName_ExistingEntry_ReturnsFileName()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, _) = CreateEntry(fileUploadStore, "cert.pem");

        Assert.Equal("cert.pem", fileUploadStore.GetFileName(fileId));
    }

    [Fact]
    public void RemoveEntry_ExistingEntry_DeletesFile()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        Assert.True(File.Exists(filePath));

        fileUploadStore.RemoveEntry(fileId);

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void RemoveUnreferencedFiles_UploadInProgress_KeepsFile()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteInteraction(InteractionId, []);

        fileUploadStore.RemoveUnreferencedFiles();

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void RemoveUnreferencedFiles_UploadCompleteInteractionInProgress_KeepsFile()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteUpload(fileId);

        fileUploadStore.RemoveUnreferencedFiles();

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void CancelInteraction_UploadComplete_RemovesFileImmediately()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteUpload(fileId);

        fileUploadStore.CancelInteraction(InteractionId);

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void CancelInteraction_UploadInProgress_RemovesFileAfterUploadCompletes()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");

        fileUploadStore.CancelInteraction(InteractionId);

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));

        fileUploadStore.CompleteUpload(fileId);

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void RemoveUnreferencedFiles_CompleteInteractionWithLiveReference_KeepsFile()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteUpload(fileId);
        var interactionFile = new InteractionFile(fileId, "temp.bin", filePath);
        fileUploadStore.CompleteInteraction(InteractionId, [interactionFile]);

        fileUploadStore.RemoveUnreferencedFiles();

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));
        GC.KeepAlive(interactionFile);
    }

    [Fact]
    public void RemoveUnreferencedFiles_CompleteInteractionWithoutLiveReference_RemovesFile()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteUpload(fileId);
        var weakReference = CompleteInteractionWithFile(fileUploadStore, fileId, filePath);

        GC.Collect();
        Assert.False(weakReference.TryGetTarget(out _));

        fileUploadStore.RemoveUnreferencedFiles();

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void RemoveUnreferencedFiles_DeletionFails_RetriesOnNextCleanup()
    {
        using var fileSystemService = new TestFileSystemService(failFirstTempFileDispose: true);
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteUpload(fileId);
        fileUploadStore.CompleteInteraction(InteractionId, []);

        fileUploadStore.RemoveUnreferencedFiles();

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));

        fileUploadStore.RemoveUnreferencedFiles();

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.False(File.Exists(filePath));
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

        var (fileId, filePath) = CreateEntry(fileUploadStore, maliciousFileName);

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

        var (fileId, filePath) = CreateEntry(fileUploadStore, emptyFileName);

        Assert.NotNull(fileId);
        Assert.NotEmpty(Path.GetFileName(filePath));
    }

    [Fact]
    public void ResolveFileReferences_ValidReference_ResolvesCorrectly()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "cert.pem", "CertInput");
        File.WriteAllText(filePath, "certificate-content");

        var json = $"[{{\"Id\":\"{fileId}\",\"Name\":\"cert.pem\"}}]";
        var resolvedFiles = FileUploadStore.ResolveFileReferences(fileUploadStore, json, InteractionId, "CertInput", NullLogger.Instance);

        Assert.NotNull(resolvedFiles);
        var file = Assert.Single(resolvedFiles);
        Assert.Equal(fileId, file.Id);
        Assert.Equal("cert.pem", file.Name);
        Assert.Equal(filePath, file.FilePath);
    }

    [Fact]
    public void ResolveFileReferences_DifferentInputName_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, _) = CreateEntry(fileUploadStore, "cert.pem");
        var json = $"[{{\"Id\":\"{fileId}\",\"Name\":\"cert.pem\"}}]";

        var result = FileUploadStore.ResolveFileReferences(fileUploadStore, json, InteractionId, "OtherFile", NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveFileReferences_DifferentInteractionId_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var (fileId, _) = CreateEntry(fileUploadStore, "cert.pem");
        var json = $"[{{\"Id\":\"{fileId}\",\"Name\":\"cert.pem\"}}]";

        var result = FileUploadStore.ResolveFileReferences(fileUploadStore, json, InteractionId + 1, InputName, NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveFileReferences_UnknownId_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);
        var json = "[{\"Id\":\"nonexistent-id\",\"Name\":\"file.txt\"}]";

        var result = FileUploadStore.ResolveFileReferences(fileUploadStore, json, InteractionId, "TestInput", NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveFileReferences_MalformedJson_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);
        var json = "not-valid-json";

        var result = FileUploadStore.ResolveFileReferences(fileUploadStore, json, InteractionId, "TestInput", NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveFileReferences_EmptyValue_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var result = FileUploadStore.ResolveFileReferences(fileUploadStore, "", InteractionId, "TestInput", NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void Dispose_CleansUpAllFiles()
    {
        using var fileSystemService = new TestFileSystemService();
        var fileUploadStore = new FileUploadStore(fileSystemService);

        var (_, filePath1) = CreateEntry(fileUploadStore, "file1.txt");
        var (_, filePath2) = CreateEntry(fileUploadStore, "file2.txt");

        Assert.True(File.Exists(filePath1));
        Assert.True(File.Exists(filePath2));

        fileUploadStore.Dispose();

        Assert.Null(fileUploadStore.GetFilePath("anything", InteractionId, InputName));
    }

    [Fact]
    public void CreateEntry_UnknownInteraction_Throws()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = new FileUploadStore(fileSystemService);

        var exception = Assert.Throws<InvalidOperationException>(() => fileUploadStore.CreateEntry("temp.bin", InteractionId, InputName));

        Assert.Equal($"Interaction '{InteractionId}' is not accepting file uploads.", exception.Message);
    }

    private static (string FileId, string FilePath) CreateEntry(FileUploadStore fileUploadStore, string fileName, string inputName = InputName)
    {
        fileUploadStore.StartInteraction(InteractionId);
        return fileUploadStore.CreateEntry(fileName, InteractionId, inputName);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<InteractionFile> CompleteInteractionWithFile(FileUploadStore fileUploadStore, string fileId, string filePath)
    {
        var interactionFile = new InteractionFile(fileId, "temp.bin", filePath);
        fileUploadStore.CompleteInteraction(InteractionId, [interactionFile]);
        return new WeakReference<InteractionFile>(interactionFile);
    }
}
