// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Tests.Agents;

public class AgentFileWriterTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_PreservesDestinationUntilPublication(bool destinationExists)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "payload");
        if (destinationExists)
        {
            await File.WriteAllTextAsync(path, "{}");
        }
        var reader = new AgentConfigurationJson.ReadContext();
        var document = await reader.ReadAsync(path, CancellationToken.None);
        var root = new JsonObject
        {
            ["value"] = CallbackJsonConverter.CreateValue(() =>
            {
                if (destinationExists)
                {
                    Assert.Equal("{}", File.ReadAllText(path));
                }
                else
                {
                    Assert.False(File.Exists(path));
                }
            })
        };

        await reader.SaveAsync(document, root, CancellationToken.None);

        Assert.Equal(1, JsonNode.Parse(await File.ReadAllTextAsync(path))!["value"]!.GetValue<int>());
        Assert.Equal(["payload"], workspace.WorkspaceRoot.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    [Fact]
    public async Task WriteAsync_CancelledBeforeStaging_DoesNotCreateDirectories()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var path = Path.Combine(workspace.Path, "missing", "payload");
        var reader = new AgentConfigurationJson.ReadContext();
        var document = await reader.ReadAsync(path, CancellationToken.None);
        var root = new JsonObject { ["value"] = true };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.SaveAsync(document, root, cancellation.Token));

        Assert.Empty(workspace.WorkspaceRoot.EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task WriteAsync_CancelledDuringSerialization_PreservesOriginalAndCleansStaging()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var path = Path.Combine(workspace.Path, "payload");
        const string original = """{"working":"content"}""";
        await File.WriteAllTextAsync(path, original);
        var originalTimestamp = File.GetLastWriteTimeUtc(path);
        var reader = new AgentConfigurationJson.ReadContext();
        var document = await reader.ReadAsync(path, CancellationToken.None);
        var root = new JsonObject { ["value"] = CallbackJsonConverter.CreateValue(cancellation.Cancel) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.SaveAsync(document, root, cancellation.Token));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(["payload"], workspace.WorkspaceRoot.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    [Fact]
    public async Task WriteAsync_ContentWriteFailure_PreservesOriginalAndOtherStagingFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "payload");
        const string original = """{"working":"content"}""";
        await File.WriteAllTextAsync(path, original);
        var unownedPath = Path.Combine(workspace.Path, ".payload.aspire-other.tmp");
        await File.WriteAllTextAsync(unownedPath, "another invocation");
        var reader = new AgentConfigurationJson.ReadContext();
        var document = await reader.ReadAsync(path, CancellationToken.None);
        var root = new JsonObject
        {
            ["value"] = CallbackJsonConverter.CreateValue(() => throw new IOException("content writer failed"))
        };

        var error = await Assert.ThrowsAsync<IOException>(() => reader.SaveAsync(document, root, CancellationToken.None));

        Assert.Equal("content writer failed", error.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal("another invocation", await File.ReadAllTextAsync(unownedPath));
        Assert.Equal([".payload.aspire-other.tmp", "payload"],
            workspace.WorkspaceRoot.EnumerateFileSystemInfos().Select(static entry => entry.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WriteAsync_ConcurrentModification_IsValidatedAfterStaging()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "payload");
        await File.WriteAllTextAsync(path, "{}");
        var reader = new AgentConfigurationJson.ReadContext();
        var document = await reader.ReadAsync(path, CancellationToken.None);
        const string concurrent = """{"concurrent":"edit"}""";
        var root = new JsonObject { ["value"] = CallbackJsonConverter.CreateValue(() => File.WriteAllText(path, concurrent)) };

        var error = await Assert.ThrowsAsync<AgentConfigurationException>(() =>
            reader.SaveAsync(document, root, CancellationToken.None));

        Assert.Equal(AgentCommandStrings.Configuration_ConcurrentChange, error.Message);
        Assert.Equal(concurrent, await File.ReadAllTextAsync(path));
        Assert.Equal(["payload"], workspace.WorkspaceRoot.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    [Fact]
    public async Task WriteAsync_RepointedLink_IsValidatedBeforeReplacement()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var original = workspace.CreateDirectory("original");
        var replacement = workspace.CreateDirectory("replacement");
        var originalPath = Path.Combine(original.FullName, "payload");
        var replacementPath = Path.Combine(replacement.FullName, "payload");
        const string originalContent = """{"original":"destination"}""";
        const string replacementContent = """{"replacement":"destination"}""";
        await File.WriteAllTextAsync(originalPath, originalContent);
        await File.WriteAllTextAsync(replacementPath, replacementContent);
        var link = Path.Combine(workspace.Path, "linked");
        TestSymlinkHelper.TryCreateSymlink(link, original.FullName);
        var logicalPath = Path.Combine(link, "payload");
        var reader = new AgentConfigurationJson.ReadContext();
        var document = await reader.ReadAsync(logicalPath, CancellationToken.None);
        var root = new JsonObject
        {
            ["value"] = CallbackJsonConverter.CreateValue(() =>
            {
                Directory.Delete(link);
                Directory.CreateSymbolicLink(link, replacement.FullName);
            })
        };

        await Assert.ThrowsAsync<AgentConfigurationException>(() => reader.SaveAsync(document, root, CancellationToken.None));

        Assert.Equal(originalContent, await File.ReadAllTextAsync(originalPath));
        Assert.Equal(replacementContent, await File.ReadAllTextAsync(replacementPath));
        Assert.Equal(["payload"], original.EnumerateFileSystemInfos().Select(static entry => entry.Name));
        Assert.Equal(["payload"], replacement.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    [Fact]
    public async Task WriteAsync_ConcurrentCreation_DoesNotOverwriteIt()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "payload");

        var reader = new AgentConfigurationJson.ReadContext();
        var document = await reader.ReadAsync(path, CancellationToken.None);
        const string concurrent = """{"concurrent":"creation"}""";
        var root = new JsonObject { ["value"] = CallbackJsonConverter.CreateValue(() => File.WriteAllText(path, concurrent)) };

        await Assert.ThrowsAsync<AgentConfigurationException>(() => reader.SaveAsync(document, root, CancellationToken.None));

        Assert.Equal(concurrent, await File.ReadAllTextAsync(path));
        Assert.Equal(["payload"], workspace.WorkspaceRoot.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    [Fact]
    public async Task WriteAsync_ReplacementFails_DoesNotRemoveDestinationDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "payload");
        await File.WriteAllTextAsync(path, "{}");
        var reader = new AgentConfigurationJson.ReadContext();
        var document = await reader.ReadAsync(path, CancellationToken.None);
        File.Delete(path);
        Directory.CreateDirectory(path);
        var marker = Path.Combine(path, "user-file");
        await File.WriteAllTextAsync(marker, "keep");

        var error = await Record.ExceptionAsync(() =>
            reader.SaveAsync(document, new JsonObject { ["ours"] = true }, CancellationToken.None));

        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal("keep", await File.ReadAllTextAsync(marker));
        Assert.Equal(["payload"], workspace.WorkspaceRoot.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    [Fact]
    public async Task WriteAsync_Replacement_PreservesUnixPermissionsIncludingUmaskFilteredBits()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permission preservation is not applicable on Windows.");
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "payload");
        await File.WriteAllTextAsync(path, "{}");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;
        File.SetUnixFileMode(path, mode);
        var reader = new AgentConfigurationJson.ReadContext();
        var document = await reader.ReadAsync(path, CancellationToken.None);

        await reader.SaveAsync(document, new JsonObject { ["replacement"] = true }, CancellationToken.None);

        Assert.Equal(mode, File.GetUnixFileMode(path));
        Assert.True(JsonNode.Parse(await File.ReadAllTextAsync(path))!["replacement"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WriteAsync_Replacement_PreservesWindowsDiscretionaryAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows DACL preservation is not applicable on Unix.");
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "payload");
        await File.WriteAllTextAsync(path, "{}");
        using var identity = WindowsIdentity.GetCurrent();
        var user = Assert.IsType<SecurityIdentifier>(identity.User);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        var destination = new FileInfo(path);
        destination.SetAccessControl(security);
        var originalDacl = destination.GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        var reader = new AgentConfigurationJson.ReadContext();
        var document = await reader.ReadAsync(path, CancellationToken.None);
        var root = new JsonObject { ["replacement"] = true };

        await reader.SaveAsync(document, root, CancellationToken.None);

        Assert.Equal(originalDacl, destination.GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.True(JsonNode.Parse(await File.ReadAllTextAsync(path))!["replacement"]!.GetValue<bool>());
        Assert.Equal(["payload"], workspace.WorkspaceRoot.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    [Fact]
    public async Task NativeWriter_NewFile_UsesPrivateUnixMode()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var results = await context.ConfigureNativeAsync(AgentConfigurationScope.Global, [context.Copilot]);
        var path = Assert.Single(results).TargetPath;

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        Assert.Equal([path], Directory.EnumerateFiles(context.Home.FullName, "*", SearchOption.AllDirectories));
    }
}
