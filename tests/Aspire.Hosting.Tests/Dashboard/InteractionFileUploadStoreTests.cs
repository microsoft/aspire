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
    public void CreateEntry_ValidFileName_ReturnsIdAndPath()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "test.txt");

        Assert.NotNull(fileId);
        Assert.NotEmpty(fileId);
        Assert.NotEqual("test.txt", Path.GetFileName(filePath));
        fileUploadStore.CompleteUpload(InteractionId, fileId);
        Assert.Equal("test.txt", Assert.Single(fileUploadStore.GetCompletedFiles(InteractionId, InputName)).Name);
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void GetFilePath_ExistingEntry_ReturnsPath()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "test.txt");
        fileUploadStore.CompleteUpload(InteractionId, fileId);

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
    }

    [Fact]
    public void GetFilePath_UploadInProgress_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "test.txt");

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));

        fileUploadStore.CompleteUpload(InteractionId, fileId);

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
    }

    [Fact]
    public void GetFilePath_NonexistentEntry_ReturnsNull()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        Assert.Null(fileUploadStore.GetFilePath("nonexistent", InteractionId, InputName));
    }

    [Fact]
    public void GetFileName_ExistingEntry_ReturnsFileName()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, _) = CreateEntry(fileUploadStore, "cert.pem");
        fileUploadStore.CompleteUpload(InteractionId, fileId);

        Assert.Equal("cert.pem", Assert.Single(fileUploadStore.GetCompletedFiles(InteractionId, InputName)).Name);
    }

    [Fact]
    public void RemoveEntry_ExistingEntry_DeletesFile()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        Assert.True(File.Exists(filePath));

        fileUploadStore.RemoveEntry(InteractionId, fileId);

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void RemoveEntry_LastFile_RemovesCompletedInteraction()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteUpload(InteractionId, fileId);
        ResolveValidateAndMarkFiles(fileUploadStore, FileReferences(fileId));
        fileUploadStore.CompleteInteraction(InteractionId);

        Assert.Throws<InvalidOperationException>(() => fileUploadStore.CreateEntry("other.bin", InteractionId, InputName));

        fileUploadStore.RemoveEntry(InteractionId, fileId);
        StartInteraction(fileUploadStore);
        var (_, replacementPath) = fileUploadStore.CreateEntry("replacement.bin", InteractionId, InputName);

        Assert.True(File.Exists(replacementPath));
    }

    [Fact]
    public void RemoveEntry_TerminalInteraction_RemainsUntilLastFileRemoved()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId1, filePath1) = CreateEntry(fileUploadStore, "file1.bin");
        var (fileId2, filePath2) = fileUploadStore.CreateEntry("file2.bin", InteractionId, InputName);
        fileUploadStore.CompleteUpload(InteractionId, fileId1);
        fileUploadStore.CompleteUpload(InteractionId, fileId2);
        ResolveValidateAndMarkFiles(fileUploadStore, FileReferences(fileId1, fileId2));
        fileUploadStore.CompleteInteraction(InteractionId);

        fileUploadStore.RemoveEntry(InteractionId, fileId1);

        StartInteraction(fileUploadStore);
        Assert.Throws<InvalidOperationException>(() => fileUploadStore.CreateEntry("other.bin", InteractionId, InputName));
        Assert.Equal(filePath2, fileUploadStore.GetFilePath(fileId2, InteractionId, InputName));

        fileUploadStore.RemoveEntry(InteractionId, fileId2);
        StartInteraction(fileUploadStore);
        var (_, replacementPath) = fileUploadStore.CreateEntry("replacement.bin", InteractionId, InputName);

        Assert.True(File.Exists(replacementPath));
    }

    [Fact]
    public void FileOperations_DifferentInteractionId_DoNotMutateEntry()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        var otherInteractionId = InteractionId + 1;
        StartInteraction(fileUploadStore, otherInteractionId);

        Assert.Empty(fileUploadStore.GetCompletedFiles(otherInteractionId, InputName));
        fileUploadStore.CompleteUpload(otherInteractionId, fileId);
        fileUploadStore.RemoveEntry(otherInteractionId, fileId);
        fileUploadStore.CancelInteraction(InteractionId);

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));

        fileUploadStore.CompleteUpload(InteractionId, fileId);

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void CompleteInteraction_UntransferredUploadInProgress_RemovesFileAfterUploadCompletes()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteInteraction(InteractionId);

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));

        fileUploadStore.CompleteUpload(InteractionId, fileId);

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void UploadCompleteInteractionInProgress_KeepsFile()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteUpload(InteractionId, fileId);

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void CancelInteraction_UploadComplete_RemovesFileImmediately()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteUpload(InteractionId, fileId);

        fileUploadStore.CancelInteraction(InteractionId);

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void CancelInteraction_UploadInProgress_RemovesFileAfterUploadCompletes()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");

        fileUploadStore.CancelInteraction(InteractionId);

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));

        fileUploadStore.CompleteUpload(InteractionId, fileId);

        Assert.Null(fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void CompleteInteraction_TransferredUploadKeepsFile()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteUpload(InteractionId, fileId);
        ResolveValidateAndMarkFiles(fileUploadStore, FileReferences(fileId));
        fileUploadStore.CompleteInteraction(InteractionId);

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void CompleteInteraction_UploadCompletedAfterResolution_RemovesLateFile()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        var (acceptedFileId, acceptedFilePath) = CreateEntry(fileUploadStore, "accepted.bin");
        var (lateFileId, lateFilePath) = fileUploadStore.CreateEntry("late.bin", InteractionId, InputName);
        fileUploadStore.CompleteUpload(InteractionId, acceptedFileId);
        ResolveValidateAndMarkFiles(fileUploadStore, FileReferences(acceptedFileId));

        fileUploadStore.CompleteUpload(InteractionId, lateFileId);
        fileUploadStore.CompleteInteraction(InteractionId);

        Assert.Equal(acceptedFilePath, fileUploadStore.GetFilePath(acceptedFileId, InteractionId, InputName));
        Assert.True(File.Exists(acceptedFilePath));
        Assert.Null(fileUploadStore.GetFilePath(lateFileId, InteractionId, InputName));
        Assert.False(File.Exists(lateFilePath));
    }

    [Fact]
    public void CompleteInteraction_AfterInteractionFileCollected_KeepsFile()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, "temp.bin");
        fileUploadStore.CompleteUpload(InteractionId, fileId);
        var weakReference = CompleteInteractionWithFile(fileUploadStore, fileId, filePath);

        GC.Collect();
        Assert.False(weakReference.TryGetTarget(out _));

        Assert.Equal(filePath, fileUploadStore.GetFilePath(fileId, InteractionId, InputName));
        Assert.True(File.Exists(filePath));
    }

    [Theory]
    [InlineData("../../../etc/passwd", "passwd")]
    [InlineData("/etc/cron.d/evil", "evil")]
    [InlineData("..\\..\\windows\\system32\\evil.exe", "evil.exe")]
    [InlineData("C:\\windows\\system32\\config.sys", "config.sys")]
    public void CreateEntry_PathTraversalFileName_SanitizesToLeafName(string maliciousFileName, string expectedLeafName)
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, maliciousFileName);
        fileUploadStore.CompleteUpload(InteractionId, fileId);

        Assert.NotEqual(maliciousFileName, filePath);
        Assert.NotEqual(expectedLeafName, Path.GetFileName(filePath));
        Assert.Equal(expectedLeafName, Assert.Single(fileUploadStore.GetCompletedFiles(InteractionId, InputName)).Name);
    }

    [Theory]
    [InlineData("bad*.txt")]
    [InlineData("bad:name.txt")]
    [InlineData("CON.txt")]
    [InlineData("bad\0name.txt")]
    public void CreateEntry_PlatformSensitiveFileName_UsesRandomDiskName(string originalFileName)
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, originalFileName);
        fileUploadStore.CompleteUpload(InteractionId, fileId);

        Assert.True(File.Exists(filePath));
        Assert.NotEqual(originalFileName, Path.GetFileName(filePath));
        Assert.Equal(originalFileName, Assert.Single(fileUploadStore.GetCompletedFiles(InteractionId, InputName)).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("\\")]
    public void CreateEntry_EmptyOrRootOnlyFileName_GeneratesRandomName(string emptyFileName)
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, filePath) = CreateEntry(fileUploadStore, emptyFileName);

        Assert.NotNull(fileId);
        Assert.NotEmpty(Path.GetFileName(filePath));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(InteractionHelpers.MaxFileCount)]
    public void CreateEntry_ExceedsInputLimit_Throws(int maxFileCount)
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        StartInteraction(fileUploadStore, maxFileCount: maxFileCount);

        for (var i = 0; i < maxFileCount; i++)
        {
            fileUploadStore.CreateEntry($"file-{i}.txt", InteractionId, InputName);
        }

        var exception = Assert.Throws<InvalidOperationException>(() => fileUploadStore.CreateEntry("excess.txt", InteractionId, InputName));
        var fileLabel = maxFileCount == 1 ? "file" : "files";
        Assert.Equal($"File input '{InputName}' accepts at most {maxFileCount} {fileLabel}.", exception.Message);
    }

    [Fact]
    public void CreateEntry_RemovedEntry_FreesInputCapacity()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        StartInteraction(fileUploadStore, maxFileCount: 1);
        var (fileId, _) = fileUploadStore.CreateEntry("first.txt", InteractionId, InputName);

        fileUploadStore.RemoveEntry(InteractionId, fileId);
        var (_, replacementPath) = fileUploadStore.CreateEntry("replacement.txt", InteractionId, InputName);

        Assert.True(File.Exists(replacementPath));
    }

    [Fact]
    public void CreateEntry_UnregisteredInput_Throws()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        StartInteraction(fileUploadStore);

        var exception = Assert.Throws<InvalidOperationException>(() => fileUploadStore.CreateEntry("file.txt", InteractionId, "OtherFile"));

        Assert.Equal($"Interaction '{InteractionId}' is not accepting file uploads for input 'OtherFile'.", exception.Message);
    }

    [Fact]
    public void ValidateFileReferences_ReturnsFilesInClientOrder()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (firstFileId, firstFilePath) = CreateEntry(fileUploadStore, "first.pem", "CertInput");
        var (secondFileId, secondFilePath) = fileUploadStore.CreateEntry("second.pem", InteractionId, "CertInput");
        fileUploadStore.CompleteUpload(InteractionId, secondFileId);
        fileUploadStore.CompleteUpload(InteractionId, firstFileId);

        IReadOnlyList<InteractionFileUpload>? resolvedFiles = fileUploadStore.GetCompletedFiles(InteractionId, "CertInput");
        resolvedFiles = InteractionFileUploadStore.ValidateFileReferences(
            FileReferences(secondFileId, firstFileId),
            "CertInput",
            resolvedFiles);

        Assert.NotNull(resolvedFiles);
        Assert.Collection(
            resolvedFiles,
            file =>
            {
                Assert.Equal(secondFileId, file.Id);
                Assert.Equal("second.pem", file.Name);
                Assert.Equal(secondFilePath, file.FilePath);
            },
            file =>
            {
                Assert.Equal(firstFileId, file.Id);
                Assert.Equal("first.pem", file.Name);
                Assert.Equal(firstFilePath, file.FilePath);
            });
    }

    [Fact]
    public void GetCompletedFiles_UsesStoredFileName()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, _) = CreateEntry(fileUploadStore, "../../../cert.pem", "CertInput");
        fileUploadStore.CompleteUpload(InteractionId, fileId);
        IReadOnlyList<InteractionFileUpload>? resolvedFiles = fileUploadStore.GetCompletedFiles(InteractionId, "CertInput");
        InteractionFileUploadStore.ValidateFileReferences(FileReferences(fileId), "CertInput", resolvedFiles);

        var file = Assert.Single(resolvedFiles!);
        Assert.Equal("cert.pem", file.Name);
    }

    [Fact]
    public void ValidateFileReferences_MismatchedSubmission_Throws()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        var (firstFileId, _) = CreateEntry(fileUploadStore, "first.txt");
        var (secondFileId, _) = fileUploadStore.CreateEntry("second.txt", InteractionId, InputName);
        fileUploadStore.CompleteUpload(InteractionId, firstFileId);
        fileUploadStore.CompleteUpload(InteractionId, secondFileId);
        IReadOnlyList<InteractionFileUpload>? files = fileUploadStore.GetCompletedFiles(InteractionId, InputName);
        var mismatchedValues = new[]
        {
            "[]",
            FileReferences(firstFileId),
            FileReferences(firstFileId, "unknown"),
            FileReferences(firstFileId, firstFileId),
            "not-json",
            "[null]"
        };

        foreach (var value in mismatchedValues)
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                InteractionFileUploadStore.ValidateFileReferences(value, InputName, files));

            Assert.Equal($"Submitted files for input '{InputName}' do not match the completed uploads.", exception.Message);
        }
    }

    [Fact]
    public void ValidateFileReferences_DifferentInputName_Throws()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, _) = CreateEntry(fileUploadStore, "cert.pem");
        fileUploadStore.CompleteUpload(InteractionId, fileId);

        IReadOnlyList<InteractionFileUpload>? files = fileUploadStore.GetCompletedFiles(InteractionId, "OtherFile");
        var exception = Assert.Throws<InvalidOperationException>(() =>
            InteractionFileUploadStore.ValidateFileReferences(FileReferences(fileId), "OtherFile", files));

        Assert.Equal("Submitted files for input 'OtherFile' do not match the completed uploads.", exception.Message);
    }

    [Fact]
    public void ValidateFileReferences_DifferentInteractionId_Throws()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (fileId, _) = CreateEntry(fileUploadStore, "cert.pem");
        fileUploadStore.CompleteUpload(InteractionId, fileId);

        IReadOnlyList<InteractionFileUpload>? files = fileUploadStore.GetCompletedFiles(InteractionId + 1, InputName);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            InteractionFileUploadStore.ValidateFileReferences(FileReferences(fileId), InputName, files));

        Assert.Equal($"Submitted files for input '{InputName}' do not match the completed uploads.", exception.Message);
    }

    [Fact]
    public void ValidateFileReferences_UploadInProgress_Throws()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);
        var (fileId, _) = CreateEntry(fileUploadStore, "file.txt");

        IReadOnlyList<InteractionFileUpload>? files = fileUploadStore.GetCompletedFiles(InteractionId, InputName);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            InteractionFileUploadStore.ValidateFileReferences(FileReferences(fileId), InputName, files));

        Assert.Equal($"Submitted files for input '{InputName}' do not match the completed uploads.", exception.Message);
    }

    [Fact]
    public void Dispose_CleansUpAllFiles()
    {
        using var fileSystemService = new TestFileSystemService();
        var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var (_, filePath1) = CreateEntry(fileUploadStore, "file1.txt");
        var (_, filePath2) = CreateEntry(fileUploadStore, "file2.txt");

        Assert.True(File.Exists(filePath1));
        Assert.True(File.Exists(filePath2));

        fileUploadStore.Dispose();
        fileUploadStore.Dispose();

        Assert.Null(fileUploadStore.GetFilePath("anything", InteractionId, InputName));
        Assert.False(File.Exists(filePath1));
        Assert.False(File.Exists(filePath2));
    }

    [Fact]
    public void CreateEntry_UnknownInteraction_Throws()
    {
        using var fileSystemService = new TestFileSystemService();
        using var fileUploadStore = CreateFileUploadStore(fileSystemService);

        var exception = Assert.Throws<InvalidOperationException>(() => fileUploadStore.CreateEntry("temp.bin", InteractionId, InputName));

        Assert.Equal($"Interaction '{InteractionId}' is not accepting file uploads.", exception.Message);
    }

    private static InteractionFileUploadStore CreateFileUploadStore(IFileSystemService fileSystemService) =>
        new(fileSystemService, NullLogger<InteractionFileUploadStore>.Instance);

    private static (string FileId, string FilePath) CreateEntry(InteractionFileUploadStore fileUploadStore, string fileName, string inputName = InputName)
    {
        StartInteraction(fileUploadStore, inputName: inputName);
        return fileUploadStore.CreateEntry(fileName, InteractionId, inputName);
    }

    private static void StartInteraction(
        InteractionFileUploadStore fileUploadStore,
        int interactionId = InteractionId,
        string inputName = InputName,
        int maxFileCount = InteractionHelpers.MaxFileCount)
    {
        fileUploadStore.StartInteraction(interactionId, [(inputName, maxFileCount)]);
    }

    private static string FileReferences(params string[] fileIds) =>
        $"[{string.Join(',', fileIds.Select(fileId => $"{{\"Id\":\"{fileId}\"}}"))}]";

    private static void ResolveValidateAndMarkFiles(InteractionFileUploadStore fileUploadStore, string jsonValue)
    {
        IReadOnlyList<InteractionFileUpload>? files = fileUploadStore.GetCompletedFiles(InteractionId, InputName);
        files = InteractionFileUploadStore.ValidateFileReferences(jsonValue, InputName, files);
        fileUploadStore.MarkFilesAccepted(InteractionId, InputName, files!.Select(file => file.Id).ToArray());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<InteractionFile> CompleteInteractionWithFile(InteractionFileUploadStore fileUploadStore, string fileId, string filePath)
    {
        var interactionFile = new InteractionFile(fileId, "temp.bin", filePath);
        ResolveValidateAndMarkFiles(fileUploadStore, FileReferences(fileId));
        fileUploadStore.CompleteInteraction(InteractionId);
        return new WeakReference<InteractionFile>(interactionFile);
    }
}
