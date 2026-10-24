// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Cli.Agents;

internal sealed partial class AgentConfiguration
{
    private readonly Dictionary<string, byte[]?> _files = new(AgentPath.Comparer);
    private readonly Dictionary<string, (bool Exists, string[] Files)> _directories = new(AgentPath.Comparer);

    public static async Task<IReadOnlyList<AgentTargetResult>> ApplyRemoteAsync(
        IEnumerable<IAgentClient> clients, DirectoryInfo workspaceRoot, AgentConfigurationScope scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<AgentTargetResult>();
        foreach (var targets in clients.Distinct().GroupBy(client => Path.GetFullPath(client.GetIntegrationConfigPath(scope, workspaceRoot)), AgentPath.Comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = AgentConfigurationStatus.Configured;
            string? message = null;
            try
            {
                var edit = new AgentConfiguration();
                var original = await edit.ReadObjectForEditAsync(targets.Key, cancellationToken);
                var root = original?.DeepClone().AsObject() ?? new JsonObject();
                foreach (var client in targets)
                {
                    await client.UpdateIntegrationConfigAsync(root, workspaceRoot, scope, edit, cancellationToken);
                }

                if (JsonNode.DeepEquals(original ?? new JsonObject(), root))
                {
                    await edit.ValidateAsync(targets.Key, cancellationToken);
                    status = AgentConfigurationStatus.Unchanged;
                }
                else
                {
                    await edit.PublishAsync(targets.Key, root, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                status = AgentConfigurationStatus.Failed;
                message = ex.Message;
            }

            results.Add(new(targets.ToArray(), targets.Key, status, message));
        }

        return results;
    }

    public static JsonObject ParseObject(ReadOnlySpan<byte> content)
    {
        // Files and inline settings use the same UTF-8 JSONC contract, including a BOM.
        if (content.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            content = content[3..];
        }

        var parseOptions = ConfigurationHelper.ParseOptions;
        parseOptions.AllowDuplicateProperties = false;
        var root = JsonNode.Parse(content, documentOptions: parseOptions);
        ArgumentNullException.ThrowIfNull(root);

        return root.AsObject();
    }

    public static async Task<JsonObject?> ReadObjectAsync(string path, CancellationToken cancellationToken)
        => ParseFile(path, await ReadFileAsync(Path.GetFullPath(path), cancellationToken));

    public static IEnumerable<string> EnumerateJsonFiles(string directory)
        => ReadDirectory(Path.GetFullPath(directory)).Files;

    public async Task<byte[]?> ReadBytesForEditAsync(string path, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        if (!_files.TryGetValue(path, out var content))
        {
            content = await ReadFileAsync(path, cancellationToken);
            _files.Add(path, content);
        }

        return content;
    }

    public async Task<JsonObject?> ReadObjectForEditAsync(string path, CancellationToken cancellationToken)
        => ParseFile(path, await ReadBytesForEditAsync(path, cancellationToken));

    public IEnumerable<string> GetJsonFilesForEdit(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!_directories.TryGetValue(directory, out var contents))
        {
            contents = ReadDirectory(directory);
            _directories.Add(directory, contents);
        }

        return contents.Files;
    }

    public async Task PublishAsync(string path, JsonObject settings, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_files.ContainsKey(path))
        {
            throw Refused(path);
        }
        RejectSymbolicLink(path);
        // All agent settings writers use the same sidecar, including MCP and user hooks.
        // Acquire only after read-only configuration validation, so refused setup creates no files.
        FileLock fileLock;
        try
        {
            fileLock = await FileLock.AcquireAsync(
                Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.aspire.lock"),
                cancellationToken, TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException ex)
        {
            throw new IOException(ex.Message, ex);
        }
        using var lease = fileLock;
        await ValidateAsync(path, cancellationToken);
        RejectSymbolicLink(path);

        var existed = File.Exists(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var created = false;
        string? unixMetadata = null;
        try
        {
            await using (var stream = CreateStagingFile(temporary, path, existed))
            {
                created = true;
                if (existed && !OperatingSystem.IsWindows())
                {
                    unixMetadata = await ReadUnixMetadataAsync(path, cancellationToken);
                    await PreserveUnixMetadataAsync(path, temporary, stream, cancellationToken);
                    stream.SetLength(0);
                }

                await JsonSerializer.SerializeAsync(stream, settings, JsonSourceGenerationContext.Default.JsonObject, cancellationToken);
            }

            // Recheck destination permissions, file bytes, missing inputs and policy fragments after staging.
            if (unixMetadata is not null && unixMetadata != await ReadUnixMetadataAsync(path, cancellationToken))
            {
                throw Refused(path);
            }
            await ValidateAsync(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RejectSymbolicLink(path);
            if (existed && OperatingSystem.IsWindows())
            {
                // ReplaceFile preserves the destination DACL, including inheritance flags.
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
            else
            {
                // Same-directory rename is atomic. For a new target, never overwrite a
                // file created after validation. Existing hard-link aliases retain their old bytes.
                File.Move(temporary, path, overwrite: existed);
            }
        }
        finally
        {
            if (created)
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task ValidateAsync(string targetPath, CancellationToken cancellationToken)
    {
        foreach (var (path, content) in _files)
        {
            if (!AgentPath.Comparer.Equals(path, targetPath) &&
                !Equal(content, await ReadFileAsync(path, cancellationToken)))
            {
                throw Refused(path);
            }
        }

        // Check the destination last, not before awaiting inherited settings reads.
        if (_files.TryGetValue(targetPath, out var original) &&
            !Equal(original, await ReadFileAsync(targetPath, cancellationToken)))
        {
            throw Refused(targetPath);
        }

        foreach (var (path, previous) in _directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = ReadDirectory(path);
            if (previous.Exists != current.Exists || !previous.Files.SequenceEqual(current.Files, AgentPath.Comparer))
            {
                throw Refused(path);
            }
        }
    }

    private static JsonObject? ParseFile(string path, byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return ParseObject(bytes);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException($"{path}: {ex.Message}", ex);
        }
    }

    private static async Task<byte[]?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static (bool Exists, string[] Files) ReadDirectory(string path)
    {
        try
        {
            return (true, Directory.EnumerateFiles(path, "*.json")
                .Where(file => !Path.GetFileName(file).StartsWith('.'))
                .Order(StringComparer.Ordinal).ToArray());
        }
        catch (DirectoryNotFoundException)
        {
            return (false, []);
        }
    }

    private static bool Equal(byte[]? left, byte[]? right)
        => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private static FileStream CreateStagingFile(string temporary, string destination, bool existed)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsStagingFile(temporary, destination, existed);
        }

        return new FileStream(temporary, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateWindowsStagingFile(string temporary, string destination, bool existed)
    {
        var security = existed
            ? new FileInfo(destination).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group)
            : new FileSecurity();
        if (!existed)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new UnauthorizedAccessException();
            security.SetOwner(owner);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        // Freeze the original effective ACL before creating any credential-bearing copy.
        // Parent inheritance must not grant readers access while validation awaits.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        return new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write,
            FileShare.None, 4096, FileOptions.Asynchronous, security);
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task PreserveUnixMetadataAsync(
        string source, string destination, FileStream stream, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            // BusyBox cp replaces the staging inode and drops ACLs. Set metadata on the
            // open, still-empty file instead, before writing any credential-bearing JSON.
            var owner = await RunMetadataToolAsync(StatPath, ["-c", "%u:%g", source], source, cancellationToken);
            // stat emits numeric "uid:gid\n", for example "1000:1000\n".
            var parts = owner.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ||
                !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var gid) ||
                ChangeOwner(stream.SafeFileHandle, uid, gid) != 0)
            {
                throw Refused(source);
            }
            File.SetUnixFileMode(stream.SafeFileHandle, File.GetUnixFileMode(source));
            var acl = ReadLinuxAccessAcl(source);
            if (acl.Length > 0 && SetExtendedAttribute(stream.SafeFileHandle, "system.posix_acl_access", acl, (nuint)acl.Length, 0) != 0)
            {
                throw Refused(source);
            }
            // A source without an ACL must not inherit named entries from the staging directory.
            if (acl.Length == 0 && RemoveExtendedAttribute(stream.SafeFileHandle, "system.posix_acl_access") != 0 &&
                Marshal.GetLastPInvokeError() is not (61 or 95))
            {
                throw Refused(source);
            }

            return;
        }

        // macOS cp -p preserves ACLs but can report success without preserving uid/gid.
        // https://www.manpagez.com/man/1/cp/
        await RunMetadataToolAsync("/bin/cp", ["-p", source, destination], source, cancellationToken);
        var owners = await RunMetadataToolAsync(StatPath,
            [OperatingSystem.IsMacOS() ? "-f" : "-c", "%u:%g", source, destination], source, cancellationToken);
        var values = owners.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length != 2 || values[0] != values[1] || File.GetUnixFileMode(source) != File.GetUnixFileMode(destination))
        {
            throw Refused(source);
        }
    }

    private static async Task<string> ReadUnixMetadataAsync(string path, CancellationToken cancellationToken)
    {
        // Compare actual ACLs rather than ctime, which can have coarse filesystem precision.
        if (OperatingSystem.IsMacOS())
        {
            // -e includes access ACL entries; -n includes numeric ownership alongside the mode.
            // https://github.com/apple-oss-distributions/file_cmds/blob/main/ls/ls.1
            return await RunMetadataToolAsync("/bin/ls", ["-lnde", path], path, cancellationToken);
        }

        var metadata = await RunMetadataToolAsync(StatPath, ["-c", "%u:%g:%a", path], path, cancellationToken);

        return metadata + Convert.ToHexString(ReadLinuxAccessAcl(path));
    }

    private static byte[] ReadLinuxAccessAcl(string path)
    {
        const string aclAttribute = "system.posix_acl_access";
        var size = GetExtendedAttribute(path, aclAttribute, null, 0);
        if (size < 0)
        {
            // Linux ENODATA/EOPNOTSUPP mean there is no POSIX ACL to preserve.
            // https://man7.org/linux/man-pages/man2/getxattr.2.html
            if (Marshal.GetLastPInvokeError() is 61 or 95)
            {
                return [];
            }
            throw Refused(path);
        }

        var acl = new byte[checked((int)size)];
        if (GetExtendedAttribute(path, aclAttribute, acl, (nuint)acl.Length) != size)
        {
            throw Refused(path);
        }

        return acl;
    }

    // BusyBox installs stat in /bin. Keep metadata tools independent of the caller's PATH.
    private static string StatPath => File.Exists("/usr/bin/stat") ? "/usr/bin/stat" : "/bin/stat";

    private static async Task<string> RunMetadataToolAsync(
        string executable, string[] arguments, string source, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var result = await ProcessCaptureRunner.RunAsync(start, TimeSpan.FromSeconds(10),
            (process, token) => Task.WhenAll(process.StandardOutput.ReadToEndAsync(token), process.StandardError.ReadToEndAsync(token)),
            () => [], NullLogger.Instance, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.FailureKind is not null || result.ExitCode != 0 ||
            result.Capture.Length != 2 || !string.IsNullOrWhiteSpace(result.Capture[1]))
        {
            throw Refused(source);
        }

        return result.Capture[0];
    }

    private static void RejectSymbolicLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
        {
            throw Refused(path);
        }
    }

    private static IOException Refused(string path)
        => new(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.ConfigurationWriteRefused, path));

    [LibraryImport("libc", EntryPoint = "getxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint GetExtendedAttribute(
        string path, string name, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] byte[]? value, nuint size);

    [LibraryImport("libc", EntryPoint = "fsetxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int SetExtendedAttribute(
        SafeFileHandle descriptor, string name, byte[] value, nuint size, int flags);

    [LibraryImport("libc", EntryPoint = "fremovexattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RemoveExtendedAttribute(SafeFileHandle descriptor, string name);

    [LibraryImport("libc", EntryPoint = "fchown", SetLastError = true)]
    private static partial int ChangeOwner(SafeFileHandle descriptor, uint owner, uint group);
}

internal enum AgentConfigurationStatus
{
    Configured,
    Unchanged,
    Failed
}

internal sealed record AgentTargetResult(
    IReadOnlyList<IAgentClient> Clients,
    string TargetPath,
    AgentConfigurationStatus Status,
    string? Message);
