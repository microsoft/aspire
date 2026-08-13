// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Runtime.CompilerServices;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Dashboard;

public class InteractionFileUploadStoreTests
{
    private const int InteractionId = 1;
    private const string InputName = "File";

    [Fact]
    public async Task CreateEntry_ValidFileName_ReturnsIdAndPath()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "test.txt");

        Assert.NotNull(fileId);
        Assert.NotEmpty(fileId);
        Assert.Equal("test.txt", Path.GetFileName(filePath));
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task GetFilePath_ExistingEntry_ReturnsPath()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "test.txt");

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
    }

    [Fact]
    public async Task GetFilePath_NonexistentEntry_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        Assert.Null(fileUploadStore.GetFilePath("nonexistent", InteractionId, InputName));
    }

    [Fact]
    public async Task GetFileName_ExistingEntry_ReturnsFileName()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, _) = CreateEntry(fileUploadStore, "cert.pem");

        Assert.Equal("cert.pem", fileUploadStore.GetFileName(InteractionId, fileId));
    }

    [Fact]
    public async Task RemoveEntry_ExistingEntry_DeletesFile()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        Assert.True(File.Exists(filePath));

        fileUploadStore.RemoveEntry(InteractionId, fileId);

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task RemoveEntry_LastFile_RemovesCompletedInteraction()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteUpload(InteractionId, fileId);
        fileUploadStore.CompleteInteraction(InteractionId);

        Assert.Throws<InvalidOperationException>(() => fileUploadStore.CreateEntry("other.bin", InteractionId, InputName));

        fileUploadStore.RemoveEntry(InteractionId, fileId);
        fileUploadStore.StartInteraction(InteractionId);
        var (_, replacementPath) = fileUploadStore.CreateEntry("replacement.bin", InteractionId, InputName);

        Assert.True(File.Exists(replacementPath));
    }

    [Fact]
    public async Task RemoveEntry_TerminalInteraction_RemainsUntilLastFileRemoved()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId1, filePath1) = CreateEntry(fileUploadStore, "file1.bin");
        var (fileId2, filePath2) = fileUploadStore.CreateEntry("file2.bin", InteractionId, InputName);
        fileUploadStore.CompleteUpload(InteractionId, fileId1);
        fileUploadStore.CompleteUpload(InteractionId, fileId2);
        fileUploadStore.CompleteInteraction(InteractionId);

        fileUploadStore.RemoveEntry(InteractionId, fileId1);

        fileUploadStore.StartInteraction(InteractionId);
        Assert.Throws<InvalidOperationException>(() => fileUploadStore.CreateEntry("other.bin", InteractionId, InputName));
        Assert.Equal(filePath2, fileUploadStore.GetFilePath(fileId2, InteractionId, InputName));

        fileUploadStore.RemoveEntry(InteractionId, fileId2);
        fileUploadStore.StartInteraction(InteractionId);
        var (_, replacementPath) = fileUploadStore.CreateEntry("replacement.bin", InteractionId, InputName);

        Assert.True(File.Exists(replacementPath));
    }

    [Fact]
    public async Task FileOperations_DifferentInteractionId_DoNotMutateEntry()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        var otherInteractionId = InteractionId + 1;
        fileUploadStore.StartInteraction(otherInteractionId);

        Assert.Null(fileUploadStore.GetFileName(otherInteractionId, fileId));
        fileUploadStore.CompleteUpload(otherInteractionId, fileId);
        fileUploadStore.RemoveEntry(otherInteractionId, fileId);
        fileUploadStore.CancelInteraction(InteractionId);

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));

        fileUploadStore.CompleteUpload(InteractionId, fileId);

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task RemoveCanceledFiles_CompletedInteractionWithUploadInProgress_KeepsFile()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteInteraction(InteractionId);

        fileUploadStore.RemoveCanceledFiles();

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task RemoveCanceledFiles_UploadCompleteInteractionInProgress_KeepsFile()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteUpload(InteractionId, fileId);

        fileUploadStore.RemoveCanceledFiles();

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task CancelInteraction_UploadComplete_RemovesFileImmediately()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteUpload(InteractionId, fileId);

        fileUploadStore.CancelInteraction(InteractionId);

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task CancelInteraction_UploadInProgress_RemovesFileAfterUploadCompletes()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");

        fileUploadStore.CancelInteraction(InteractionId);

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));

        fileUploadStore.CompleteUpload(InteractionId, fileId);

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task RemoveCanceledFiles_CompletedInteraction_KeepsFile()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteUpload(InteractionId, fileId);
        fileUploadStore.CompleteInteraction(InteractionId);

        fileUploadStore.RemoveCanceledFiles();

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task RemoveCanceledFiles_CompletedInteractionAfterInteractionFileCollected_KeepsFile()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteUpload(InteractionId, fileId);
        var weakReference = CompleteInteractionWithFile(fileUploadStore, fileId, filePath);

        GC.Collect();
        Assert.False(weakReference.TryGetTarget(out _));

        fileUploadStore.RemoveCanceledFiles();

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));
    }

    [Theory]
    [InlineData("../../../etc/passwd", "passwd")]
    [InlineData("/etc/cron.d/evil", "evil")]
    [InlineData("..\\..\\windows\\system32\\evil.exe", "evil.exe")]
    [InlineData("C:\\windows\\system32\\config.sys", "config.sys")]
    public async Task CreateEntry_PathTraversalFileName_SanitizesToLeafName(string maliciousFileName, string expectedLeafName)
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, maliciousFileName);

        Assert.NotEqual(maliciousFileName, filePath);
        Assert.Equal(expectedLeafName, Path.GetFileName(filePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("\\")]
    public async Task CreateEntry_EmptyOrRootOnlyFileName_GeneratesRandomName(string emptyFileName)
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, emptyFileName);

        Assert.NotNull(fileId);
        Assert.NotEmpty(Path.GetFileName(filePath));
    }

    [Fact]
    public async Task ResolveFileReferences_ValidReference_ResolvesCorrectly()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "cert.pem", "CertInput");
        File.WriteAllText(filePath, "certificate-content");

        var json = $"[{{\"Id\":\"{fileId}\",\"Name\":\"cert.pem\"}}]";
        var resolvedFiles = InteractionFileUploadStore.ResolveFileReferences(fileUploadStore, json, InteractionId, "CertInput", NullLogger.Instance);

        Assert.NotNull(resolvedFiles);
        var file = Assert.Single(resolvedFiles);
        Assert.Equal(fileId, file.Id);
        Assert.Equal("cert.pem", file.Name);
        Assert.Equal(filePath, file.FilePath);
    }

    [Fact]
    public async Task ResolveFileReferences_DifferentInputName_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, _) = CreateEntry(fileUploadStore, "cert.pem");
        var json = $"[{{\"Id\":\"{fileId}\",\"Name\":\"cert.pem\"}}]";

        var result = InteractionFileUploadStore.ResolveFileReferences(fileUploadStore, json, InteractionId, "OtherFile", NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveFileReferences_DifferentInteractionId_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (fileId, _) = CreateEntry(fileUploadStore, "cert.pem");
        var json = $"[{{\"Id\":\"{fileId}\",\"Name\":\"cert.pem\"}}]";

        var result = InteractionFileUploadStore.ResolveFileReferences(fileUploadStore, json, InteractionId + 1, InputName, NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveFileReferences_UnknownId_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);
        var json = "[{\"Id\":\"nonexistent-id\",\"Name\":\"file.txt\"}]";

        var result = InteractionFileUploadStore.ResolveFileReferences(fileUploadStore, json, InteractionId, "TestInput", NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveFileReferences_MalformedJson_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);
        var json = "not-valid-json";

        var result = InteractionFileUploadStore.ResolveFileReferences(fileUploadStore, json, InteractionId, "TestInput", NullLogger.Instance);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"Id\":null}]")]
    [InlineData("[{\"Id\":\"\"}]")]
    public async Task ResolveFileReferences_MalformedReference_ReturnsNull(string json)
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var result = InteractionFileUploadStore.ResolveFileReferences(fileUploadStore, json, InteractionId, "TestInput", NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveFileReferences_EmptyValue_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var result = InteractionFileUploadStore.ResolveFileReferences(fileUploadStore, "", InteractionId, "TestInput", NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public async Task DisposeAsync_CleansUpAllFiles()
    {
        using var fileSystemService = new TestFileSystemService();
        var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var (_, filePath1) = CreateEntry(fileUploadStore, "file1.txt");
        var (_, filePath2) = CreateEntry(fileUploadStore, "file2.txt");

        Assert.True(File.Exists(filePath1));
        Assert.True(File.Exists(filePath2));

        await fileUploadStore.DisposeAsync();
        await fileUploadStore.DisposeAsync();

        Assert.Null(fileUploadStore.GetFilePath("anything", InteractionId, InputName));
        Assert.False(File.Exists(filePath1));
        Assert.False(File.Exists(filePath2));
    }

    [Fact]
    public async Task CreateEntry_UnknownInteraction_Throws()
    {
        using var fileSystemService = new TestFileSystemService();
        await using var fileUploadStore = new InteractionFileUploadStore(fileSystemService);

        var exception = Assert.Throws<InvalidOperationException>(() => fileUploadStore.CreateEntry("temp.bin", InteractionId, InputName));

        Assert.Equal($"Interaction '{InteractionId}' is not accepting file uploads.", exception.Message);
    }

    private static (string FileId, string FilePath) CreateEntry(InteractionFileUploadStore fileUploadStore, string fileName, string inputName = InputName)
    {
        fileUploadStore.StartInteraction(InteractionId);
        return fileUploadStore.CreateEntry(fileName, InteractionId, inputName);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<InteractionFile> CompleteInteractionWithFile(InteractionFileUploadStore fileUploadStore, string fileId, string filePath)
    {
        var interactionFile = new InteractionFile(fileId, "temp.bin", filePath);
        fileUploadStore.CompleteInteraction(InteractionId);
        return new WeakReference<InteractionFile>(interactionFile);
    }
}
