// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Abstractions.PhysicalFileSystem;
using Microsoft.TemplateEngine.Utils;

namespace Aspire.Cli.Templating;

internal sealed class TemplateEngineHost(IReadOnlyList<(Type InterfaceType, IIdentifiedComponent Instance)> components)
    : Microsoft.TemplateEngine.Edge.DefaultTemplateEngineHost("aspirecli", "1.0.0", builtIns: components, fallbackHostTemplateConfigNames: ["dotnetcli"]), ITemplateEngineHost
{
    // Filter the source view rather than copying or modifying a user's checkout.
    // A .git entry can be a directory or a worktree/submodule pointer file.
    private IPhysicalFileSystem _fileSystem = new GitMetadataExcludingFileSystem(new PhysicalFileSystem());

    IPhysicalFileSystem ITemplateEngineHost.FileSystem => _fileSystem;

    void ITemplateEngineHost.VirtualizeDirectory(string path)
    {
        _fileSystem = new InMemoryFileSystem(path, _fileSystem);
    }

    private sealed class GitMetadataExcludingFileSystem(IPhysicalFileSystem inner) : IPhysicalFileSystem
    {
        public bool DirectoryExists(string directory) => !IsGitMetadata(directory) && inner.DirectoryExists(directory);
        public bool FileExists(string file) => !IsGitMetadata(file) && inner.FileExists(file);
        public Stream CreateFile(string path) => inner.CreateFile(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public string GetCurrentDirectory() => inner.GetCurrentDirectory();
        public void FileCopy(string sourcePath, string targetPath, bool overwrite) => inner.FileCopy(ReadablePath(sourcePath), targetPath, overwrite);
        public void DirectoryDelete(string path, bool recursive) => inner.DirectoryDelete(path, recursive);
        public string ReadAllText(string path) => inner.ReadAllText(ReadablePath(path));
        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(ReadablePath(path));
        public void WriteAllText(string path, string value) => inner.WriteAllText(path, value);
        public Stream OpenRead(string path) => inner.OpenRead(ReadablePath(path));
        public void FileDelete(string path) => inner.FileDelete(path);
        public FileAttributes GetFileAttributes(string file) => inner.GetFileAttributes(ReadablePath(file));
        public void SetFileAttributes(string file, FileAttributes attributes) => inner.SetFileAttributes(file, attributes);
        public IDisposable WatchFileChanges(string filePath, FileSystemEventHandler fileChanged) => inner.WatchFileChanges(filePath, fileChanged);
        public DateTime GetLastWriteTimeUtc(string file) => inner.GetLastWriteTimeUtc(ReadablePath(file));
        public void SetLastWriteTimeUtc(string file, DateTime lastWriteTimeUtc) => inner.SetLastWriteTimeUtc(file, lastWriteTimeUtc);
        public string PathRelativeTo(string target, string relativeTo) => inner.PathRelativeTo(target, relativeTo);

        public IEnumerable<string> EnumerateFileSystemEntries(string path, string pattern, SearchOption searchOption) =>
            Enumerate(path, pattern, searchOption, inner.EnumerateFileSystemEntries);

        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption searchOption) =>
            Enumerate(path, pattern, searchOption, inner.EnumerateDirectories);

        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption searchOption) =>
            Enumerate(path, pattern, searchOption, inner.EnumerateFiles);

        private IEnumerable<string> Enumerate(
            string path, string pattern, SearchOption searchOption,
            Func<string, string, SearchOption, IEnumerable<string>> enumerate)
        {
            if (IsGitMetadata(path))
            {
                yield break;
            }

            foreach (var entry in enumerate(path, pattern, SearchOption.TopDirectoryOnly))
            {
                if (!IsGitMetadata(entry))
                {
                    yield return entry;
                }
            }

            if (searchOption == SearchOption.AllDirectories)
            {
                foreach (var directory in EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly))
                {
                    foreach (var entry in Enumerate(directory, pattern, searchOption, enumerate))
                    {
                        yield return entry;
                    }
                }
            }
        }

        private static bool IsGitMetadata(string path) =>
            path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
                .Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase));

        private static string ReadablePath(string path) =>
            IsGitMetadata(path) ? throw new FileNotFoundException(null, path) : path;
    }
}
