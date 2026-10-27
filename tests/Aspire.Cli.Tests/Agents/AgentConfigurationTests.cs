// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Aspire.Cli.Agents;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class AgentConfigurationTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParseObject_FileAndInlineInputsShareJsoncHandling(bool bom)
    {
        const string json = """{/* preserved */"nested":{"value":true,},"items":["one",],}""";
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bom)
        {
            bytes = Encoding.UTF8.GetPreamble().Concat(bytes).ToArray();
        }

        var path = Path.Combine(_context.Project.FullName, "settings.json");
        await File.WriteAllBytesAsync(path, bytes).DefaultTimeout();

        var inline = AgentConfiguration.ParseObject(json);
        var file = AgentConfiguration.ReadObject(path, CancellationToken.None);
        var edit = new AgentConfiguration();
        var tracked = await edit.ReadObjectForEditAsync(path, CancellationToken.None).DefaultTimeout();

        Assert.True(JsonNode.DeepEquals(inline, file));
        Assert.True(JsonNode.DeepEquals(inline, tracked));
        Assert.True((bool)inline["nested"]!["value"]!);
        Assert.Equal(["one"], inline["items"]!.AsArray().Select(value => (string)value!));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path).DefaultTimeout());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadObject_FileReadsPreserveUtf8Characters(bool bom)
    {
        const string json = "{/* JSONC */\"value\":\"caf\u00e9 \u4e16\u754c \ud83d\ude80\",}";
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(json)).ToArray();
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        await File.WriteAllBytesAsync(path, bytes).DefaultTimeout();

        var file = AgentConfiguration.ReadObject(path, CancellationToken.None);
        var edit = new AgentConfiguration();
        var tracked = await edit.ReadObjectForEditAsync(path, CancellationToken.None).DefaultTimeout();

        Assert.Equal("caf\u00e9 \u4e16\u754c \ud83d\ude80", file!["value"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(file, tracked));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path).DefaultTimeout());
    }

    [Theory]
    [InlineData(1200, false)]
    [InlineData(1200, true)]
    [InlineData(1201, false)]
    [InlineData(1201, true)]
    [InlineData(12000, false)]
    [InlineData(12000, true)]
    [InlineData(12001, false)]
    [InlineData(12001, true)]
    public async Task ReadObject_FileReadsRejectNonUtf8Encodings(int codePage, bool bom)
    {
        var encoding = Encoding.GetEncoding(codePage);
        var content = encoding.GetBytes("{\"value\":\"caf\u00e9\"}");
        var bytes = bom ? encoding.GetPreamble().Concat(content).ToArray() : content;
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        await File.WriteAllBytesAsync(path, bytes).DefaultTimeout();

        var fileError = Assert.Throws<InvalidOperationException>(() =>
            AgentConfiguration.ReadObject(path, CancellationToken.None));
        var edit = new AgentConfiguration();
        var trackedError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            edit.ReadObjectForEditAsync(path, CancellationToken.None)).DefaultTimeout();

        Assert.IsAssignableFrom<JsonException>(fileError.InnerException);
        Assert.IsAssignableFrom<JsonException>(trackedError.InnerException);
        Assert.StartsWith($"{path}: ", fileError.Message);
        Assert.StartsWith($"{path}: ", trackedError.Message);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path).DefaultTimeout());
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"duplicate":1,"duplicate":2}""")]
    [InlineData("""{"nested":{"duplicate":1,"duplicate":2}}""")]
    [InlineData("""{"items":[{"duplicate":1,"duplicate":2}]}""")]
    [InlineData("""{"skills":{"urls":[],"urls":[]}}""")]
    public async Task ParseObject_FileAndInlineInputsRejectAmbiguousObjectsAtTheBoundary(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        await File.WriteAllBytesAsync(path, bytes).DefaultTimeout();

        Exception inline = json switch
        {
            "null" => Assert.Throws<ArgumentNullException>(() => AgentConfiguration.ParseObject(json)),
            "[]" => Assert.Throws<InvalidOperationException>(() => AgentConfiguration.ParseObject(json)),
            _ => Assert.ThrowsAny<JsonException>(() => AgentConfiguration.ParseObject(json))
        };
        var file = Assert.Throws<InvalidOperationException>(() =>
            AgentConfiguration.ReadObject(path, CancellationToken.None));
        var edit = new AgentConfiguration();
        var tracked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            edit.ReadObjectForEditAsync(path, CancellationToken.None)).DefaultTimeout();

        Assert.Equal($"{path}: {inline.Message}", file.Message);
        Assert.Equal(inline.GetType(), file.InnerException?.GetType());
        Assert.Equal(file.Message, tracked.Message);
        Assert.Equal(inline.GetType(), tracked.InnerException?.GetType());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path).DefaultTimeout());
    }

    [Fact]
    public async Task ReadOperations_DoNotCreateMissingDirectories()
    {
        var directory = Path.Combine(_context.Project.FullName, "missing");
        var path = Path.Combine(directory, "settings.json");
        var edit = new AgentConfiguration();

        Assert.Null(AgentConfiguration.ReadObject(path, CancellationToken.None));
        Assert.Empty(AgentConfiguration.EnumerateJsonFiles(directory));
        Assert.Null(await edit.ReadBytesForEditAsync(path, CancellationToken.None).DefaultTimeout());
        Assert.Empty(edit.GetJsonFilesForEdit(directory));

        Assert.False(Directory.Exists(directory));
        Assert.Empty(Directory.GetFiles(_context.Project.FullName, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_RequiresDestinationCapturedBySameEdit(bool existed)
    {
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        if (existed)
        {
            await File.WriteAllTextAsync(path, "{}");
        }
        var otherEdit = new AgentConfiguration();
        await otherEdit.ReadBytesForEditAsync(path, CancellationToken.None).DefaultTimeout();
        var edit = new AgentConfiguration();

        await Assert.ThrowsAsync<IOException>(() =>
            edit.PublishAsync(path, new JsonObject(), CancellationToken.None)).DefaultTimeout();

        Assert.Equal(existed ? [path] : [], Directory.GetFiles(_context.Project.FullName));
        if (existed)
        {
            Assert.Equal("{}", await File.ReadAllTextAsync(path).DefaultTimeout());
        }
    }

    [Fact]
    public async Task ReadOnlyDiscovery_DoesNotReplaceCapturedInputs()
    {
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        await File.WriteAllTextAsync(path, """{"value":"original"}""");
        var edit = new AgentConfiguration();
        await edit.ReadObjectForEditAsync(path, CancellationToken.None).DefaultTimeout();
        const string changed = """{"value":"external"}""";
        await File.WriteAllTextAsync(path, changed);

        var discovered = AgentConfiguration.ReadObject(path, CancellationToken.None);
        var captured = await edit.ReadObjectForEditAsync(path, CancellationToken.None).DefaultTimeout();

        Assert.Equal("external", discovered!["value"]!.GetValue<string>());
        Assert.Equal("original", captured!["value"]!.GetValue<string>());
        await Assert.ThrowsAsync<IOException>(() =>
            edit.PublishAsync(path, new JsonObject(), CancellationToken.None)).DefaultTimeout();
        Assert.Equal(changed, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.Equal([path], Directory.GetFiles(_context.Project.FullName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteEdit_RefusesAChangedOrNewDestination(bool existed)
    {
        var path = _context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Project, _context.Project);
        if (existed)
        {
            await AgentConfigurationTestContext.WriteAsync(path, "{}");
        }
        const string changed = """{"userEdit":true}""";
        var edit = new AgentConfiguration();
        var config = await edit.ReadObjectForEditAsync(path, CancellationToken.None) ?? new JsonObject();
        await _context.Copilot.UpdateIntegrationConfigAsync(
            config, _context.Project, AgentConfigurationScope.Project, edit, CancellationToken.None);
        await AgentConfigurationTestContext.WriteAsync(path, changed);

        await Assert.ThrowsAsync<IOException>(() => edit.PublishAsync(path, config, CancellationToken.None)).DefaultTimeout();
        Assert.Equal(changed, await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_context.Project.FullName, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    [InlineData("opencode")]
    public async Task RemoteEdit_RefusesChangedInheritedSettings(string clientId)
    {
        var client = _context.Clients.Single(client => string.Equals(client.Id, clientId, StringComparisons.AgentClientId));
        var inherited = client.GetIntegrationConfigPath(AgentConfigurationScope.Global, _context.Project);
        await AgentConfigurationTestContext.WriteAsync(inherited, "{}");
        var path = client.GetIntegrationConfigPath(AgentConfigurationScope.Project, _context.Project);
        var edit = new AgentConfiguration();
        var config = await edit.ReadObjectForEditAsync(path, CancellationToken.None) ?? new JsonObject();
        await client.UpdateIntegrationConfigAsync(
            config, _context.Project, AgentConfigurationScope.Project, edit, CancellationToken.None);
        const string changed = """{"userEdit":true}""";
        await AgentConfigurationTestContext.WriteAsync(inherited, changed);

        await Assert.ThrowsAsync<IOException>(() => edit.PublishAsync(path, config, CancellationToken.None)).DefaultTimeout();
        Assert.False(File.Exists(path));
        Assert.Equal(changed, await File.ReadAllTextAsync(inherited));
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("added")]
    [InlineData("removed")]
    [InlineData("modified")]
    public async Task ClaudeEdit_RevalidatesManagedFragmentAbsenceMembershipAndContent(string change)
    {
        var directory = Path.Combine(_context.ClaudeManagedDirectory, "managed-settings.d");
        var fragment = Path.Combine(directory, "team.json");
        if (change != "directory")
        {
            Directory.CreateDirectory(directory);
        }
        if (change is "removed" or "modified")
        {
            await File.WriteAllTextAsync(fragment, "{}");
        }
        var path = _context.ClaudeCode.GetIntegrationConfigPath(AgentConfigurationScope.Project, _context.Project);
        var edit = new AgentConfiguration();
        var config = await edit.ReadObjectForEditAsync(path, CancellationToken.None) ?? new JsonObject();
        await _context.ClaudeCode.UpdateIntegrationConfigAsync(
            config, _context.Project, AgentConfigurationScope.Project, edit, CancellationToken.None);
        if (change == "directory")
        {
            Directory.CreateDirectory(directory);
        }
        else if (change == "removed")
        {
            File.Delete(fragment);
        }
        else
        {
            await File.WriteAllTextAsync(fragment, """{"blockedMarketplaces":[]}""");
        }

        await Assert.ThrowsAsync<IOException>(() => edit.PublishAsync(path, config, CancellationToken.None)).DefaultTimeout();
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_context.Project.FullName, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ClaudeEdit_IgnoresNonSettingsFragmentChanges()
    {
        var directory = Path.Combine(_context.ClaudeManagedDirectory, "managed-settings.d");
        Directory.CreateDirectory(directory);
        var path = _context.ClaudeCode.GetIntegrationConfigPath(AgentConfigurationScope.Project, _context.Project);
        var edit = new AgentConfiguration();
        var config = await edit.ReadObjectForEditAsync(path, CancellationToken.None) ?? new JsonObject();
        await _context.ClaudeCode.UpdateIntegrationConfigAsync(
            config, _context.Project, AgentConfigurationScope.Project, edit, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(directory, ".ignored.json"), "{malformed");

        await edit.PublishAsync(path, config, CancellationToken.None).DefaultTimeout();

        Assert.True(JsonNode.DeepEquals(config, JsonNode.Parse(await File.ReadAllTextAsync(path))));
    }

    [Fact]
    public async Task RemoteAndMcpPublication_RejectsTheStaleCompetingWriter()
    {
        var path = _context.OpenCode.GetIntegrationConfigPath(AgentConfigurationScope.Global, _context.Project);
        await AgentConfigurationTestContext.WriteAsync(path, "{}");
        var remoteEdit = new AgentConfiguration();
        var remote = (await remoteEdit.ReadObjectForEditAsync(path, CancellationToken.None))!;
        await _context.OpenCode.UpdateIntegrationConfigAsync(
            remote, _context.Project, AgentConfigurationScope.Global, remoteEdit, CancellationToken.None);
        var mcpEdit = new AgentConfiguration();
        var (mcp, mcpPath) = await _context.OpenCode.ReadMcpConfigAsync(
            path, _context.Project, AgentConfigurationScope.Global, mcpEdit, CancellationToken.None);
        _context.OpenCode.UpdateMcpConfig(mcp, mcpPath, configureServer: true, AgentConfigurationScope.Global);
        Task[] writes =
        [
            remoteEdit.PublishAsync(path, remote, CancellationToken.None),
            mcpEdit.PublishAsync(mcpPath, mcp, CancellationToken.None)
        ];
        var errors = await Task.WhenAll(writes.Select(write => Record.ExceptionAsync(() => write).AsTask())).DefaultTimeout();
        Assert.Single(errors, error => error is null);
        Assert.IsType<IOException>(Assert.Single(errors, error => error is not null));

        var published = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.NotEqual(published["skills"] is not null, published["mcp"] is not null);
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_RefusesExistingAndDanglingSymbolicLinks(bool dangling)
    {
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        var target = Path.Combine(_context.Project.FullName, "linked.json");
        if (!dangling)
        {
            await File.WriteAllTextAsync(target, "{}");
        }
        TestSymlinkHelper.TryCreateSymlink(path, target, isDirectory: false);
        var edit = new AgentConfiguration();
        await edit.ReadBytesForEditAsync(path, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => edit.PublishAsync(
            path, new JsonObject(), CancellationToken.None)).DefaultTimeout();

        Assert.Equal(target, new FileInfo(path).LinkTarget);
        Assert.Equal(!dangling, File.Exists(target));
        if (!dangling)
        {
            Assert.Equal("{}", await File.ReadAllTextAsync(target));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsPublication_PreservesPrivateAclUnderAnInheritablePublicParent(bool publicationFails)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows ACL validation.");
            return;
        }
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        var directory = _context.Project.GetAccessControl();
        directory.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        _context.Project.SetAccessControl(directory);
        await File.WriteAllTextAsync(path, """{"token":"original"}""");
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(identity.User!);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        var before = new FileInfo(path).GetAccessControl();
        var edit = new AgentConfiguration();
        await edit.ReadBytesForEditAsync(path, CancellationToken.None);
        // An open reader that does not share deletion forces ReplaceFile to fail after staging.
        using var reader = publicationFails ? File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        var write = edit.PublishAsync(path, new JsonObject { ["token"] = "replacement" }, CancellationToken.None);
        if (publicationFails)
        {
            await Assert.ThrowsAsync<IOException>(() => write).DefaultTimeout();
        }
        else
        {
            await write.DefaultTimeout();
        }

        Assert.Equal(before.GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group),
            new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group));
        Assert.Equal(publicationFails ? "original" : "replacement",
            JsonNode.Parse(await File.ReadAllTextAsync(path))!["token"]!.GetValue<string>());
        Assert.Equal([path], Directory.GetFiles(_context.Project.FullName));
    }

    // Keep the basic rewrite case runnable on BusyBox installations without libacl.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnixPublication_UpdatesExistingPrivateFileWithSystemMetadataTools(bool parentAcl)
    {
        if (OperatingSystem.IsWindows() || parentAcl && !OperatingSystem.IsLinux())
        {
            Assert.Skip("Unix metadata tools validation.");
            return;
        }
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        await File.WriteAllTextAsync(path, """{"token":"original"}""");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(path, mode);
        string? acl = null;
        if (parentAcl)
        {
            TestPosixAcl.SetDefault(_context.Project.FullName, "user::rwx,user:65534:r-x,group::---,mask::r-x,other::---");
            acl = TestPosixAcl.Get(path);
        }
        var edit = new AgentConfiguration();
        await edit.ReadBytesForEditAsync(path, CancellationToken.None);

        await edit.PublishAsync(path, new JsonObject { ["token"] = "replacement" },
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(mode, File.GetUnixFileMode(path));
        if (parentAcl)
        {
            Assert.Equal(acl, TestPosixAcl.Get(path));
        }
        Assert.Equal("replacement", JsonNode.Parse(await File.ReadAllTextAsync(path))!["token"]!.GetValue<string>());
        Assert.Equal([path], Directory.GetFiles(_context.Project.FullName));
    }

    [Fact]
    public async Task LinuxPublication_PreservesAnExtendedAclRatherThanOnlyItsMode()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux POSIX ACL validation.");
            return;
        }
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        await File.WriteAllTextAsync(path, """{"token":"original"}""");
        // The group mode reports r-- through the mask, but the owning group has no access.
        TestPosixAcl.Set(path, "user::rw-,user:65534:r--,group::---,mask::r--,other::---");
        var before = TestPosixAcl.Get(path);
        var mode = File.GetUnixFileMode(path);
        var edit = new AgentConfiguration();
        await edit.ReadBytesForEditAsync(path, CancellationToken.None);

        await edit.PublishAsync(path, new JsonObject { ["token"] = "replacement" },
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(before, TestPosixAcl.Get(path));
        Assert.Equal(mode, File.GetUnixFileMode(path));
        Assert.Equal("replacement", JsonNode.Parse(await File.ReadAllTextAsync(path))!["token"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnixPublication_RefusesPermissionsChangedDuringStaging(bool accessAcl)
    {
        if (OperatingSystem.IsWindows() || accessAcl && !OperatingSystem.IsLinux())
        {
            Assert.Skip("Unix mode validation; extended ACL validation requires Linux.");
            return;
        }
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        const string original = """{"token":"original"}""";
        await File.WriteAllTextAsync(path, original);
        if (accessAcl)
        {
            TestPosixAcl.Set(path, "user::rw-,user:65534:r--,group::---,mask::r--,other::---");
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        var mode = File.GetUnixFileMode(path);
        const string restrictedAcl = "user::rw-,user:65534:---,group::---,mask::r--,other::---";
        string? expectedAcl = null;
        var converter = new InterceptingStringJsonConverter(() =>
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException();
            }

            // Serialization runs after the writer has copied permissions into staging.
            if (accessAcl)
            {
                TestPosixAcl.Set(path, restrictedAcl);
                expectedAcl = TestPosixAcl.Get(path);
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        });
        // Use the writer's options so JsonNode retains this converter during serialization.
        var settings = new JsonObject
        {
            ["token"] = JsonValue.Create("replacement",
                JsonMetadataServices.CreateValueInfo<string>(JsonSourceGenerationContext.Default.Options, converter))
        };
        var edit = new AgentConfiguration();
        await edit.ReadBytesForEditAsync(path, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => edit.PublishAsync(
            path, settings, CancellationToken.None)).DefaultTimeout();

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        if (accessAcl)
        {
            Assert.Equal(mode, File.GetUnixFileMode(path));
            Assert.Equal(expectedAcl, TestPosixAcl.Get(path));
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        Assert.Equal([path], Directory.GetFiles(_context.Project.FullName));
    }

    [Fact]
    public async Task Copilot_RegistersOneSharedSourcePerScope()
    {
        IAgentClient[] clients = [_context.Copilot];

        var projectResults = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, clients).DefaultTimeout();
        Assert.Equal(Path.Combine(_context.Project.FullName, ".github", "copilot", "settings.json"),
            Assert.Single(projectResults).TargetPath);
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
        var userResults = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Global, clients).DefaultTimeout();
        var results = projectResults.Concat(userResults).ToArray();

        Assert.Equal(2, results.Length);
        Assert.All(results, result =>
        {
            Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
            Assert.Equal(clients, result.Clients);
        });
        Assert.Equal(Path.Combine(_context.CopilotDirectory, "settings.json"), Assert.Single(userResults).TargetPath);
        var project = await File.ReadAllTextAsync(Path.Combine(_context.Project.FullName, ".github", "copilot", "settings.json")).DefaultTimeout();
        var user = await File.ReadAllTextAsync(Path.Combine(_context.CopilotDirectory, "settings.json")).DefaultTimeout();
        Assert.Equal(project, user);
        Assert.False(Directory.Exists(Path.Combine(_context.Project.FullName, ".vscode")));

        await Verify(project, "json");
    }

    [Fact]
    public async Task Claude_PreservesPinsPreferencesAndExistingBytes()
    {
        var userPath = Path.Combine(_context.ClaudeDirectory, "settings.json");
        const string existing = """
            {
              // Keep this comment on a semantic no-op.
              "permissions": { "deny": ["Bash(git push:*)"] },
              "autoUpdate": false,
              "extraKnownMarketplaces": {
                "aspire-skills": {
                  "source": { "source": "github", "repo": "microsoft/aspire-skills", "ref": "v0.0.3" },
                  "autoUpdate": false
                }
              },
              "enabledPlugins": { "aspire@aspire-skills": true },
            }
            """;
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(existing)).ToArray();
        Directory.CreateDirectory(_context.ClaudeDirectory);
        await File.WriteAllBytesAsync(userPath, bytes).DefaultTimeout();
        File.SetLastWriteTimeUtc(userPath, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(userPath);

        var results = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Global, [_context.ClaudeCode]).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Unchanged, Assert.Single(results).Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(userPath).DefaultTimeout());
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(userPath));
        await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [_context.ClaudeCode]).DefaultTimeout();
        var project = await File.ReadAllTextAsync(Path.Combine(_context.Project.FullName, ".claude", "settings.json")).DefaultTimeout();
        await Verify(project, "json");
    }

    [Theory]
    [InlineData("copilot", false, """{"enabledPlugins":{"aspire@aspire-skills":false},"autoUpdate":false}""")]
    [InlineData("copilot", true, """{"enabledPlugins":{"aspire@aspire-skills":false}}""")]
    [InlineData("claude", false, """{"enabledPlugins":{"aspire@aspire-skills":false},"autoUpdate":false}""")]
    [InlineData("claude", true, """{"enabledPlugins":{"aspire@aspire-skills":false}}""")]
    [InlineData("copilot", false, "{broken")]
    [InlineData("copilot", false, """{"extraKnownMarketplaces":[]}""")]
    [InlineData("copilot", false, """{"extraKnownMarketplaces":{"aspire-skills":null}}""")]
    [InlineData("copilot", false, """{"enabledPlugins":{"aspire@aspire-skills":"true"}}""")]
    [InlineData("copilot", false, """{"extraKnownMarketplaces":{"aspire-skills":{"source":{"source":"directory","path":"./private-marketplace"}}}}""")]
    public async Task Plugins_RejectDisabledConflictingOrMalformedSettingsWithoutWriting(string clientId, bool local, string existing)
    {
        var client = _context.Clients.Single(client => string.Equals(client.Id, clientId, StringComparisons.AgentClientId));
        var path = local ? Path.Combine(_context.Project.FullName, ".claude", "settings.local.json")
            : client.GetIntegrationConfigPath(AgentConfigurationScope.Global, _context.Project);
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();

        var results = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [client]).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Failed, Assert.Single(results).Status);
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.Equal(local ? [path] : Array.Empty<string>(),
            Directory.EnumerateFiles(_context.Project.FullName, "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(client.GetIntegrationConfigPath(AgentConfigurationScope.Project, _context.Project)));
    }

    [Theory]
    [InlineData(false, false, """{"strictKnownMarketplaces":[{"source":"github","repo":"microsoft/aspire-skills"}]}""")]
    [InlineData(false, true, """{"blockedMarketplaces":[]}""")]
    [InlineData(true, false, """{"blockedMarketplaces":[]}""")]
    public async Task Claude_LeavesMarketplacePolicyEnforcementToTheClient(bool fragment, bool existingTarget, string policy)
    {
        var managed = fragment
            ? Path.Combine(_context.ClaudeManagedDirectory, "managed-settings.d", "team.json")
            : Path.Combine(_context.ClaudeManagedDirectory, "managed-settings.json");
        await AgentConfigurationTestContext.WriteAsync(managed, policy);
        var target = Path.Combine(_context.Project.FullName, ".claude", "settings.json");
        const string existing = """{"model":"preserved"}""";
        if (existingTarget)
        {
            await AgentConfigurationTestContext.WriteAsync(target, existing);
        }
        var timestamp = File.GetLastWriteTimeUtc(target);

        var results = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [_context.ClaudeCode]);

        Assert.Equal(AgentConfigurationStatus.Failed, Assert.Single(results).Status);
        Assert.Equal(policy, await File.ReadAllTextAsync(managed));
        Assert.Equal(existingTarget, File.Exists(target));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(target));
        if (existingTarget)
        {
            Assert.Equal(existing, await File.ReadAllTextAsync(target));
        }
        else
        {
            Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        }
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claude_PreservesTheDestinationSourceRatherThanAnInheritedPin(bool pinnedDestination)
    {
        var source = pinnedDestination
            ? """{"source":"github","repo":"microsoft/aspire-skills","ref":"approved-release"}"""
            : """{"source":"github","repo":"microsoft/aspire-skills"}""";
        var user = Path.Combine(_context.ClaudeDirectory, "settings.json");
        const string inherited = """{"extraKnownMarketplaces":{"aspire-skills":{"source":{"source":"github","repo":"microsoft/aspire-skills","ref":"other-release"}}}}""";
        await AgentConfigurationTestContext.WriteAsync(user, inherited);
        var target = Path.Combine(_context.Project.FullName, ".claude", "settings.json");
        await AgentConfigurationTestContext.WriteAsync(target, $$"""{"extraKnownMarketplaces":{"aspire-skills":{"source": {{source}} } } }""");

        var results = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [_context.ClaudeCode]);

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(target))!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(source), settings["extraKnownMarketplaces"]!["aspire-skills"]!["source"]));
        Assert.True(settings["enabledPlugins"]!["aspire@aspire-skills"]!.GetValue<bool>());
        Assert.Equal(inherited, await File.ReadAllTextAsync(user));
    }

    [Fact]
    public async Task Claude_UsesDocumentedConfigDirectoryOverrideWithoutChangingUserMcp()
    {
        var custom = Path.Combine(_context.Home.FullName, "claude-work");
        _context.SetVariable("CLAUDE_CONFIG_DIR", custom);
        var state = Path.Combine(custom, ".claude.json");
        const string existing = """{"oauthAccount":{"accountUuid":"preserved"},"projects":{},"theme":"dark"}""";
        await AgentConfigurationTestContext.WriteAsync(state, existing).DefaultTimeout();

        var results = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Global, [_context.ClaudeCode]).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.True(File.Exists(Path.Combine(custom, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_context.Home.FullName, ".claude.json")));
        Assert.Equal(existing, await File.ReadAllTextAsync(state).DefaultTimeout());
    }

    [Fact]
    public async Task Copilot_UserOverride_PreservesTheRequestedScope()
    {
        _context.SetVariable("COPILOT_HOME", Path.Combine(_context.Project.FullName, ".github", "copilot"));
        var results = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Global, [_context.Copilot]).DefaultTimeout();
        var repeated = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Global, [_context.Copilot]).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        Assert.Equal(Path.Combine(_context.Project.FullName, ".github", "copilot", "settings.json"), results[0].TargetPath);
        Assert.Equal([results[0].TargetPath], Directory.EnumerateFiles(_context.Project.FullName, "*", SearchOption.AllDirectories));
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
        Assert.Equal(AgentConfigurationStatus.Unchanged, Assert.Single(repeated).Status);
    }

    [Fact]
    public async Task SharedSettings_ArePublishedOnceAndReportBothClients()
    {
        _context.SetVariable("COPILOT_HOME", _context.ClaudeDirectory);

        var results = await _context.ConfigureIntegrationAsync(
            AgentConfigurationScope.Global, [_context.Copilot, _context.ClaudeCode]);

        var result = Assert.Single(results);
        Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        Assert.Equal([_context.Copilot, _context.ClaudeCode], result.Clients);
        Assert.Equal([result.TargetPath], Directory.EnumerateFiles(_context.Home.FullName, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    public async Task SharedSettings_MustSatisfyEverySelectedClientsPolicy(string suffix)
    {
        _context.SetVariable("COPILOT_HOME", Path.Combine(_context.ClaudeDirectory, suffix));
        var path = Path.Combine(_context.ClaudeDirectory, "settings.json");
        const string original = """{"preserved":true}""";
        await AgentConfigurationTestContext.WriteAsync(path, original);
        await AgentConfigurationTestContext.WriteAsync(
            Path.Combine(_context.ClaudeManagedDirectory, "managed-settings.json"),
            """{"enabledPlugins":{"aspire@aspire-skills":false}}""");

        var results = await _context.ConfigureIntegrationAsync(
            AgentConfigurationScope.Global, [_context.Copilot, _context.ClaudeCode]);

        var result = Assert.Single(results);
        Assert.Equal(AgentConfigurationStatus.Failed, result.Status);
        Assert.Equal([_context.Copilot, _context.ClaudeCode], result.Clients);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task OneUnreadableFile_DoesNotStopIndependentClients()
    {
        var unreadable = Path.Combine(_context.Project.FullName, ".github", "copilot", "settings.json");
        Directory.CreateDirectory(unreadable);
        var results = await _context.ConfigureIntegrationAsync(
            AgentConfigurationScope.Project, [_context.Copilot, _context.ClaudeCode]);

        Assert.Equal([AgentConfigurationStatus.Failed, AgentConfigurationStatus.Configured], results.Select(result => result.Status));
        Assert.False(string.IsNullOrWhiteSpace(results[0].Message));
        Assert.True(File.Exists(Path.Combine(_context.Project.FullName, ".claude", "settings.json")));
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutCreatingConfiguration()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _context.ConfigureIntegrationAsync(
            AgentConfigurationScope.Project, [_context.Copilot], cancellation.Token)).DefaultTimeout();

        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
    }

    public void Dispose() => _context.Dispose();
}
