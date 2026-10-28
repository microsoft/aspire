// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Agents.OpenCode;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Agents;

/// <summary>
/// Isolates project, user, and managed-policy files from the test host.
/// </summary>
internal sealed class AgentConfigurationTestContext : IDisposable
{
    private readonly Dictionary<string, string?> _variables;

    public AgentConfigurationTestContext(ITestOutputHelper output)
    {
        Workspace = TemporaryWorkspace.Create(output);
        Project = Workspace.CreateDirectory("project");
        Home = Workspace.CreateDirectory("home");
        _variables = new()
        {
            ["ProgramFiles"] = Path.Combine(Workspace.WorkspaceRoot.FullName, "system"),
            ["ProgramData"] = Path.Combine(Workspace.WorkspaceRoot.FullName, "system-data")
        };
        // Keep platform-specific policy reads inside the fixture on every test OS.
        Environment = TestEnvironment.CreateWindows(_variables);
        ExecutionContext = TestExecutionContextHelper.CreateExecutionContext(Project, homeDirectory: Home);
        Copilot = new CopilotAgentClient(ExecutionContext, Environment, NullLogger<CopilotAgentClient>.Instance);
        ClaudeCode = new ClaudeCodeAgentClient(ExecutionContext, Environment, NullLogger<ClaudeCodeAgentClient>.Instance);
        OpenCode = new OpenCodeAgentClient(ExecutionContext, Environment);
        Clients = [Copilot, ClaudeCode, OpenCode];
    }

    public TemporaryWorkspace Workspace { get; }
    public DirectoryInfo Project { get; }
    public DirectoryInfo Home { get; }
    public TestEnvironment Environment { get; }
    public CliExecutionContext ExecutionContext { get; }
    public IReadOnlyList<IAgentClient> Clients { get; }
    public IAgentClient Copilot { get; }
    public IAgentClient ClaudeCode { get; }
    public IAgentClient OpenCode { get; }
    public string CopilotDirectory => AgentPath.Expand(CopilotPaths.GetConfigDirectory(Home, Environment), ExecutionContext);
    public string ClaudeDirectory => ClaudeCodePaths.GetConfigDirectory(ExecutionContext, Environment);
    public string ClaudeManagedDirectory => AgentPath.GetManagedSettingsDirectory(ExecutionContext, Environment, "ClaudeCode", "claude-code");
    public string OpenCodeDirectory => OpenCodePaths.GetConfigDirectory(ExecutionContext, Environment);

    public void SetVariable(string name, string value) => _variables[name] = value;

    public IServiceCollection CreateServiceCollection(
        ITestOutputHelper output, Action<CliServiceCollectionTestOptions>? configure = null)
    {
        var services = CliTestHelper.CreateServiceCollection(Workspace, output, options =>
        {
            options.CliExecutionContextFactory = _ => ExecutionContext;
            configure?.Invoke(options);
        });
        services.AddSingleton<IEnvironment>(Environment);

        return services;
    }

    public Task<IReadOnlyList<AgentTargetResult>> ConfigureIntegrationAsync(
        AgentConfigurationScope scope, IReadOnlyList<IAgentClient> clients, CancellationToken cancellationToken = default)
        => AgentConfiguration.ApplyRemoteAsync(clients, Project, scope, cancellationToken);

    public async Task<(string Path, bool Repaired, bool Added)> ConfigureMcpAsync(
        IAgentClient client, AgentConfigurationScope scope, CancellationToken cancellationToken = default)
    {
        var edit = new AgentConfiguration();
        var (config, path) = await client.ReadMcpConfigAsync(
            client.GetMcpConfigPath(scope, Project), Project, scope, edit, cancellationToken);
        var (repaired, added) = client.UpdateMcpConfig(config, path, configureServer: true, scope);
        if (repaired || added)
        {
            await edit.PublishAsync(path, config, cancellationToken);
        }

        return (path, repaired, added);
    }

    public static async Task WriteAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    public void Dispose() => Workspace.Dispose();
}

internal sealed class InterceptingStringJsonConverter(Action onWrite) : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        onWrite();
        writer.WriteStringValue(value);
    }
}

internal static class TestPosixAcl
{
    private const int AclTypeAccess = 0x8000;

    public static void Set(string path, string text) => Set(path, text, AclTypeAccess);

    public static void SetDefault(string path, string text) => Set(path, text, 0x4000);

    private static void Set(string path, string text, int type)
    {
        if (!NativeLibrary.TryLoad("libacl.so.1", out var library))
        {
            Assert.Skip("POSIX ACL validation requires libacl.");
        }
        NativeLibrary.Free(library);
        var acl = acl_from_text(text);
        Assert.NotEqual(IntPtr.Zero, acl);
        try
        {
            Assert.Equal(0, acl_set_file(path, type, acl));
        }
        finally
        {
            acl_free(acl);
        }
    }

    public static string Get(string path)
    {
        var acl = acl_get_file(path, AclTypeAccess);
        Assert.NotEqual(IntPtr.Zero, acl);
        try
        {
            var text = acl_to_text(acl, out _);
            Assert.NotEqual(IntPtr.Zero, text);
            try
            {
                return Marshal.PtrToStringUTF8(text)!;
            }
            finally
            {
                acl_free(text);
            }
        }
        finally
        {
            acl_free(acl);
        }
    }

    // This test-only probe does not require enabling unsafe code for the test project.
#pragma warning disable SYSLIB1054
    [DllImport("libacl.so.1", CharSet = CharSet.Ansi)]
    private static extern IntPtr acl_from_text(string text);

    [DllImport("libacl.so.1", CharSet = CharSet.Ansi)]
    private static extern int acl_set_file(string path, int type, IntPtr acl);

    [DllImport("libacl.so.1", CharSet = CharSet.Ansi)]
    private static extern IntPtr acl_get_file(string path, int type);

    [DllImport("libacl.so.1")]
    private static extern IntPtr acl_to_text(IntPtr acl, out nint length);

    [DllImport("libacl.so.1")]
    private static extern int acl_free(IntPtr value);
#pragma warning restore SYSLIB1054
}
