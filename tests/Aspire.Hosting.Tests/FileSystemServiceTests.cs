// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class FileSystemServiceTests
{
    private static IConfiguration CreateConfiguration(bool preserveTempFiles = false)
    {
        var configDict = new Dictionary<string, string?>();
        if (preserveTempFiles)
        {
            configDict["ASPIRE_PRESERVE_TEMP_FILES"] = "true";
        }
        return new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();
    }

    [Fact]
    public void CreateTempSubdirectory_CreatesDirectory()
    {
        var service = new FileSystemService(CreateConfiguration());

        using var tempDir = service.TempDirectory.CreateTempSubdirectory();

        Assert.NotNull(tempDir.Path);
        Assert.True(Directory.Exists(tempDir.Path));
    }

    [Fact]
    public void CreateTempSubdirectory_WithPrefix_CreatesDirectoryWithPrefix()
    {
        var service = new FileSystemService(CreateConfiguration());

        using var tempDir = service.TempDirectory.CreateTempSubdirectory("test-prefix");

        Assert.NotNull(tempDir.Path);
        Assert.True(Directory.Exists(tempDir.Path));
        Assert.Contains("test-prefix", tempDir.Path);
    }

    [Fact]
    public void CreateTempSubdirectory_Dispose_DeletesDirectory()
    {
        var service = new FileSystemService(CreateConfiguration());
        var tempDir = service.TempDirectory.CreateTempSubdirectory();
        var path = tempDir.Path;

        Assert.True(Directory.Exists(path));

        tempDir.Dispose();

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void CreateTempFile_CreatesFile()
    {
        var service = new FileSystemService(CreateConfiguration());

        using var tempFile = service.TempDirectory.CreateTempFile();

        Assert.NotNull(tempFile.Path);
        Assert.True(File.Exists(tempFile.Path));
    }

    [Fact]
    public void CreateTempFile_WithFileName_CreatesNamedFile()
    {
        var service = new FileSystemService(CreateConfiguration());

        using var tempFile = service.TempDirectory.CreateTempFile("config.json");

        Assert.NotNull(tempFile.Path);
        Assert.True(File.Exists(tempFile.Path));
        Assert.EndsWith("config.json", tempFile.Path);
    }

    [Fact]
    public void CreateTempFile_Dispose_DeletesFile()
    {
        var service = new FileSystemService(CreateConfiguration());
        var tempFile = service.TempDirectory.CreateTempFile();
        var path = tempFile.Path;

        Assert.True(File.Exists(path));

        tempFile.Dispose();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CreateTempFile_WithFileName_Dispose_DeletesFileAndParentDirectory()
    {
        var service = new FileSystemService(CreateConfiguration());
        var tempFile = service.TempDirectory.CreateTempFile("test-file.txt");
        var filePath = tempFile.Path;
        var parentDir = Path.GetDirectoryName(filePath);

        Assert.True(File.Exists(filePath));
        Assert.True(Directory.Exists(parentDir));

        tempFile.Dispose();

        Assert.False(File.Exists(filePath));
        Assert.False(Directory.Exists(parentDir));
    }

    [Fact]
    public void PathExtractionPattern_DirectoryPersistsAfterScopeEnds()
    {
        // This test verifies the intentional pattern of extracting .Path
        // without disposing, which is common in the codebase
        var service = new FileSystemService(CreateConfiguration());
        string? extractedPath;

        // Simulate the common pattern: extract path, let object go out of scope
        {
            var tempDir = service.TempDirectory.CreateTempSubdirectory("path-extraction-test");
            extractedPath = tempDir.Path;
            // Note: intentionally NOT disposing here - this is the pattern used in production
        }

        // The directory should still exist because we didn't dispose
        Assert.True(Directory.Exists(extractedPath));

        // Clean up manually for test hygiene
        Directory.Delete(extractedPath, recursive: true);
    }

    [Fact]
    public void PathExtractionPattern_FilePersistsAfterScopeEnds()
    {
        // This test verifies the intentional pattern of extracting .Path
        // without disposing, which is common in the codebase
        var service = new FileSystemService(CreateConfiguration());
        string? extractedPath;

        // Simulate the common pattern: extract path, let object go out of scope
        {
            var tempFile = service.TempDirectory.CreateTempFile();
            extractedPath = tempFile.Path;
            // Note: intentionally NOT disposing here - this is the pattern used in production
        }

        // The file should still exist because we didn't dispose
        Assert.True(File.Exists(extractedPath));

        // Clean up manually for test hygiene
        File.Delete(extractedPath);
    }

    [Fact]
    public void TempDirectory_Property_ReturnsSameInstance()
    {
        var service = new FileSystemService(CreateConfiguration());

        var tempDir1 = service.TempDirectory;
        var tempDir2 = service.TempDirectory;

        Assert.Same(tempDir1, tempDir2);
    }

    [Fact]
    public void CreateTempSubdirectory_MultipleCallsCreateDifferentDirectories()
    {
        var service = new FileSystemService(CreateConfiguration());

        using var tempDir1 = service.TempDirectory.CreateTempSubdirectory();
        using var tempDir2 = service.TempDirectory.CreateTempSubdirectory();

        Assert.NotEqual(tempDir1.Path, tempDir2.Path);
        Assert.True(Directory.Exists(tempDir1.Path));
        Assert.True(Directory.Exists(tempDir2.Path));
    }

    [Fact]
    public void CreateTempFile_MultipleCallsCreateDifferentFiles()
    {
        var service = new FileSystemService(CreateConfiguration());

        using var tempFile1 = service.TempDirectory.CreateTempFile();
        using var tempFile2 = service.TempDirectory.CreateTempFile();

        Assert.NotEqual(tempFile1.Path, tempFile2.Path);
        Assert.True(File.Exists(tempFile1.Path));
        Assert.True(File.Exists(tempFile2.Path));
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var service = new FileSystemService(CreateConfiguration());
        var tempDir = service.TempDirectory.CreateTempSubdirectory();
        var tempFile = service.TempDirectory.CreateTempFile();

        // First dispose
        tempDir.Dispose();
        tempFile.Dispose();

        // Second dispose should not throw
        tempDir.Dispose();
        tempFile.Dispose();
    }

    [Fact]
    public void ServiceDispose_CleansUpUndisposedFiles()
    {
        var service = new FileSystemService(CreateConfiguration());
        
        // Create temp files/dirs without disposing them
        var tempDir = service.TempDirectory.CreateTempSubdirectory();
        var tempFile = service.TempDirectory.CreateTempFile();
        var dirPath = tempDir.Path;
        var filePath = tempFile.Path;

        Assert.True(Directory.Exists(dirPath));
        Assert.True(File.Exists(filePath));

        // Dispose the service - should clean up remaining files
        service.Dispose();

        Assert.False(Directory.Exists(dirPath));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void ServiceDispose_WithPreserveConfiguration_SkipsCleanup()
    {
        var service = new FileSystemService(CreateConfiguration(preserveTempFiles: true));
        
        var tempDir = service.TempDirectory.CreateTempSubdirectory();
        var tempFile = service.TempDirectory.CreateTempFile();
        var dirPath = tempDir.Path;
        var filePath = tempFile.Path;

        Assert.True(Directory.Exists(dirPath));
        Assert.True(File.Exists(filePath));

        // Dispose the service - should NOT clean up files
        service.Dispose();

        // Files should still exist
        Assert.True(Directory.Exists(dirPath));
        Assert.True(File.Exists(filePath));

        // Clean up manually
        Directory.Delete(dirPath, recursive: true);
        File.Delete(filePath);
    }

    [Fact]
    public void TempDirectory_Dispose_WithPreserveConfiguration_SkipsCleanup()
    {
        var service = new FileSystemService(CreateConfiguration(preserveTempFiles: true));
        
        var tempDir = service.TempDirectory.CreateTempSubdirectory();
        var dirPath = tempDir.Path;

        Assert.True(Directory.Exists(dirPath));

        // Dispose the temp directory - should NOT clean up
        tempDir.Dispose();

        // Directory should still exist
        Assert.True(Directory.Exists(dirPath));

        // Clean up manually
        Directory.Delete(dirPath, recursive: true);
    }

    [Fact]
    public void TempFile_Dispose_WithPreserveConfiguration_SkipsCleanup()
    {
        var service = new FileSystemService(CreateConfiguration(preserveTempFiles: true));
        
        var tempFile = service.TempDirectory.CreateTempFile();
        var filePath = tempFile.Path;

        Assert.True(File.Exists(filePath));

        // Dispose the temp file - should NOT clean up
        tempFile.Dispose();

        // File should still exist
        Assert.True(File.Exists(filePath));

        // Clean up manually
        File.Delete(filePath);
    }

    [Fact]
    public void ServiceDispose_WithMixedDisposedAndUndisposedItems_CleansUpOnlyUndisposed()
    {
        var service = new FileSystemService(CreateConfiguration());
        
        // Create multiple temp items
        var tempDir1 = service.TempDirectory.CreateTempSubdirectory();
        var tempDir2 = service.TempDirectory.CreateTempSubdirectory();
        var tempFile1 = service.TempDirectory.CreateTempFile();
        var tempFile2 = service.TempDirectory.CreateTempFile();
        
        var dir1Path = tempDir1.Path;
        var dir2Path = tempDir2.Path;
        var file1Path = tempFile1.Path;
        var file2Path = tempFile2.Path;

        // Dispose some of them
        tempDir1.Dispose();
        tempFile1.Dispose();

        Assert.False(Directory.Exists(dir1Path));
        Assert.False(File.Exists(file1Path));
        Assert.True(Directory.Exists(dir2Path));
        Assert.True(File.Exists(file2Path));

        // Dispose the service - should clean up remaining undisposed items
        service.Dispose();

        Assert.False(Directory.Exists(dir2Path));
        Assert.False(File.Exists(file2Path));
    }

    [Fact]
    public void ServiceDispose_EmptyTracking_DoesNotThrow()
    {
        var service = new FileSystemService(CreateConfiguration());
        
        // Dispose without creating any temp items
        service.Dispose();
        
        // Dispose again should also not throw
        service.Dispose();
    }

    [Fact]
    public void ServiceDispose_CalledMultipleTimes_IsIdempotent()
    {
        var service = new FileSystemService(CreateConfiguration());
        
        // Create temp items
        var tempDir = service.TempDirectory.CreateTempSubdirectory();
        var tempFile = service.TempDirectory.CreateTempFile();
        var dirPath = tempDir.Path;
        var filePath = tempFile.Path;

        // First dispose - should clean up
        service.Dispose();

        Assert.False(Directory.Exists(dirPath));
        Assert.False(File.Exists(filePath));

        // Second dispose - should be a no-op and not throw
        service.Dispose();
    }

    [Fact]
    public void CreateTempFile_WithFileName_TracksOnlyFile()
    {
        var service = new FileSystemService(CreateConfiguration());
        
        var tempFile = service.TempDirectory.CreateTempFile("test.txt");
        var filePath = tempFile.Path;
        var parentDir = Path.GetDirectoryName(filePath)!;

        // Both file and parent dir should exist
        Assert.True(File.Exists(filePath));
        Assert.True(Directory.Exists(parentDir));

        // Dispose the temp file - should delete both file and parent directory
        tempFile.Dispose();

        Assert.False(File.Exists(filePath));
        Assert.False(Directory.Exists(parentDir));
    }

    [Fact]
    public void CreateTempFile_AfterServiceDisposed_ThrowsObjectDisposedException()
    {
        var service = new FileSystemService(CreateConfiguration());
        
        // Dispose the service
        service.Dispose();

        // Attempting to create a temp file after disposal should throw
        Assert.Throws<ObjectDisposedException>(() => service.TempDirectory.CreateTempFile());
    }

    [Fact]
    public void CreateTempDirectory_AfterServiceDisposed_ThrowsObjectDisposedException()
    {
        var service = new FileSystemService(CreateConfiguration());
        
        // Dispose the service
        service.Dispose();

        // Attempting to create a temp directory after disposal should throw
        Assert.Throws<ObjectDisposedException>(() => service.TempDirectory.CreateTempSubdirectory());
    }

    [Fact]
    public void SetLogger_CanBeCalledWithoutError()
    {
        var service = new FileSystemService(CreateConfiguration());
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<FileSystemService>();
        
        // Should not throw
        service.SetLogger(logger);
        
        // Should still work normally
        using var tempDir = service.TempDirectory.CreateTempSubdirectory();
        Assert.True(Directory.Exists(tempDir.Path));
    }
}
