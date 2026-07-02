// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Xunit;

namespace Aspire.Playground.Tests;

/// <summary>
/// File-shape tests for the Godot internal playground under playground/Godot.
/// These tests are intentionally Godot-free: they only assert that the expected
/// source files exist and contain the required structural markers.  No AppHost is
/// started and no Godot binary is required.
/// </summary>
public class GodotPlaygroundTests
{
    private static readonly string s_playgroundRoot = GetPlaygroundRoot();

    private static string GetPlaygroundRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            // Helix runs from a staged test payload. The playground sources are copied under
            // staging-archive/playground, while repo-root files like global.json are not.
            var stagedPlaygroundRoot = Path.Combine(dir, "staging-archive", "playground", "Godot");
            if (Directory.Exists(stagedPlaygroundRoot))
            {
                return stagedPlaygroundRoot;
            }

            // Local runs execute from the build output under the real repository worktree.
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
            {
                return Path.Combine(dir, "playground", "Godot");
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException($"Could not locate the Godot playground from {AppContext.BaseDirectory}.");
    }

    // -------------------------------------------------------------------------
    // README
    // -------------------------------------------------------------------------

    [Fact]
    public void Readme_Exists()
    {
        var path = Path.Combine(s_playgroundRoot, "README.md");
        Assert.True(File.Exists(path), $"Expected README at {path}");
    }

    [Fact]
    public void Readme_StatesNoBuildDependencyOnGodot()
    {
        var path = Path.Combine(s_playgroundRoot, "README.md");
        Assert.True(File.Exists(path), $"README not found at {path}");

        var content = File.ReadAllText(path, Encoding.UTF8);

        // The README must tell readers that CI/build does not require Godot.
        Assert.Contains("does not require Godot", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Readme_DocumentsGodotBinEnvVar()
    {
        var path = Path.Combine(s_playgroundRoot, "README.md");
        Assert.True(File.Exists(path), $"README not found at {path}");

        var content = File.ReadAllText(path, Encoding.UTF8);

        // Manual runs must document the GODOT_BIN env-var or Godot-on-PATH option.
        Assert.Contains("GODOT_BIN", content, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // AppHost source
    // -------------------------------------------------------------------------

    [Fact]
    public void AppHost_ProgramCsExists()
    {
        var path = Path.Combine(s_playgroundRoot, "Godot.AppHost", "Program.cs");
        Assert.True(File.Exists(path), $"Expected AppHost Program.cs at {path}");
    }

    [Fact]
    public void AppHost_CallsAddExecutableForGodotServer()
    {
        var path = Path.Combine(s_playgroundRoot, "Godot.AppHost", "Program.cs");
        Assert.True(File.Exists(path), $"AppHost Program.cs not found at {path}");

        var content = File.ReadAllText(path, Encoding.UTF8);

        // The AppHost must declare the Godot dedicated-server executable resource.
        Assert.Contains("AddExecutable", content, StringComparison.Ordinal);
        Assert.Contains("godot-server", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AppHost_UsesWithExplicitStart()
    {
        var path = Path.Combine(s_playgroundRoot, "Godot.AppHost", "Program.cs");
        Assert.True(File.Exists(path), $"AppHost Program.cs not found at {path}");

        var content = File.ReadAllText(path, Encoding.UTF8);

        // WithExplicitStart prevents AppHost from failing on machines without Godot.
        Assert.Contains("WithExplicitStart", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AppHost_UsesWithReferenceToMatchmaker()
    {
        var path = Path.Combine(s_playgroundRoot, "Godot.AppHost", "Program.cs");
        Assert.True(File.Exists(path), $"AppHost Program.cs not found at {path}");

        var content = File.ReadAllText(path, Encoding.UTF8);

        // The Godot server resource must reference the matchmaker so Aspire wires
        // up service-discovery / environment variables automatically.
        Assert.Contains("WithReference", content, StringComparison.Ordinal);
        Assert.Contains("matchmaker", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppHost_WiresGodotServerEndpointToMatchmaker()
    {
        var path = Path.Combine(s_playgroundRoot, "Godot.AppHost", "Program.cs");
        Assert.True(File.Exists(path), $"AppHost Program.cs not found at {path}");

        var content = File.ReadAllText(path, Encoding.UTF8);

        Assert.Contains("matchmaker.WithReference", content, StringComparison.Ordinal);
        Assert.Contains("godotServer.GetEndpoint(\"game\")", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AppHost_WiresGodotServerPortViaEnvOrEndpoint()
    {
        var path = Path.Combine(s_playgroundRoot, "Godot.AppHost", "Program.cs");
        Assert.True(File.Exists(path), $"AppHost Program.cs not found at {path}");

        var content = File.ReadAllText(path, Encoding.UTF8);

        // The AppHost must expose an endpoint or env-var for the Godot server UDP/TCP port
        // so the matchmaker and clients can discover it.
        var hasEndpoint = content.Contains("WithEndpoint", StringComparison.Ordinal);
        var hasEnvVar = content.Contains("WithEnvironment", StringComparison.Ordinal)
                        || content.Contains("GODOT_SERVER_PORT", StringComparison.Ordinal);

        Assert.True(hasEndpoint || hasEnvVar,
            "AppHost must wire the Godot server port via WithEndpoint or WithEnvironment/GODOT_SERVER_PORT.");
    }

    [Fact]
    public void AppHost_DeclaresGameEndpointAsUdp()
    {
        var path = Path.Combine(s_playgroundRoot, "Godot.AppHost", "Program.cs");
        Assert.True(File.Exists(path), $"AppHost Program.cs not found at {path}");

        var content = File.ReadAllText(path, Encoding.UTF8);

        Assert.Contains("ProtocolType.Udp", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AppHost_UsesProxylessUdpEndpoint()
    {
        var path = Path.Combine(s_playgroundRoot, "Godot.AppHost", "Program.cs");
        Assert.True(File.Exists(path), $"AppHost Program.cs not found at {path}");

        var content = File.ReadAllText(path, Encoding.UTF8);

        Assert.Contains("isProxied: false", content, StringComparison.Ordinal);
        Assert.Contains("port: 7000", content, StringComparison.Ordinal);
        Assert.Contains("targetPort: 7000", content, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Matchmaker API source
    // -------------------------------------------------------------------------

    [Fact]
    public void Matchmaker_ProgramCsExists()
    {
        var path = Path.Combine(s_playgroundRoot, "Godot.Matchmaker", "Program.cs");
        Assert.True(File.Exists(path), $"Expected Matchmaker Program.cs at {path}");
    }

    [Fact]
    public void Matchmaker_ExposesHealthEndpoint()
    {
        var path = Path.Combine(s_playgroundRoot, "Godot.Matchmaker", "Program.cs");
        Assert.True(File.Exists(path), $"Matchmaker Program.cs not found at {path}");

        var content = File.ReadAllText(path, Encoding.UTF8);

        // The matchmaker must map a /health route.
        Assert.Contains("/health", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Matchmaker_ExposesServersEndpoint()
    {
        var path = Path.Combine(s_playgroundRoot, "Godot.Matchmaker", "Program.cs");
        Assert.True(File.Exists(path), $"Matchmaker Program.cs not found at {path}");

        var content = File.ReadAllText(path, Encoding.UTF8);

        // The matchmaker must map a /servers route for clients to discover game servers.
        Assert.Contains("/servers", content, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Godot GameServer scaffold
    // -------------------------------------------------------------------------

    [Fact]
    public void GameServer_ProjectGodotExists()
    {
        var path = Path.Combine(s_playgroundRoot, "GameServer", "project.godot");
        Assert.True(File.Exists(path), $"Expected Godot project file at {path}");
    }

    [Fact]
    public void GameServer_ProjectGodotIsGodotProject()
    {
        var path = Path.Combine(s_playgroundRoot, "GameServer", "project.godot");
        Assert.True(File.Exists(path), $"project.godot not found at {path}");

        var content = File.ReadAllText(path, Encoding.UTF8);

        // Godot project files are INI-style engine configuration files that include
        // a config version and application section, not the [gd_resource] header
        // used by .tres/.tscn resources.
        Assert.Contains("config_version=", content, StringComparison.Ordinal);
        Assert.Contains("[application]", content, StringComparison.Ordinal);
    }

    [Fact]
    public void GameServer_ServerGdExists()
    {
        var path = Path.Combine(s_playgroundRoot, "GameServer", "server.gd");
        Assert.True(File.Exists(path), $"Expected server GDScript at {path}");
    }

    [Fact]
    public void GameServer_ServerGdContainsDedicatedServerMarkers()
    {
        var path = Path.Combine(s_playgroundRoot, "GameServer", "server.gd");
        Assert.True(File.Exists(path), $"server.gd not found at {path}");

        var content = File.ReadAllText(path, Encoding.UTF8);

        // A dedicated-server GDScript should reference the MultiplayerAPI or
        // ENetMultiplayerPeer to prove this is a server scaffold, not a blank file.
        var hasPeer = content.Contains("ENetMultiplayerPeer", StringComparison.Ordinal)
                      || content.Contains("MultiplayerPeer", StringComparison.Ordinal);
        var hasListen = content.Contains("create_server(", StringComparison.Ordinal)
                        || content.Contains(".listen(", StringComparison.Ordinal);

        Assert.True(hasPeer && hasListen,
            "server.gd must contain ENetMultiplayerPeer/MultiplayerPeer and a create_server/listen call " +
            "to prove it is a dedicated-server scaffold.");
    }
}
