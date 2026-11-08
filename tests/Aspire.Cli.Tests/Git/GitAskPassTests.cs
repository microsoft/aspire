// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Runtime.Versioning;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Git;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Git;

public class GitAskPassTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("Username for 'https://example.test': ", true)]
    [InlineData("Password for 'https://private-user@example.test/repository': ", false)]
    [InlineData("Password for 'https://example.test': ", false)]
    [PlatformSpecific(TestPlatforms.OSX)]
    [SupportedOSPlatform("macos")]
    public void Prompt_UsesBoundOriginAndNeverRawGitText(string raw, bool username)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        Assert.Equal(string.Format(username ? InteractionServiceStrings.GitUsernamePrompt : InteractionServiceStrings.GitPasswordPrompt,
            "https://example.test"), GitAskPassServer.GetPrompt(raw, new Uri("https://example.test/repository")));
    }

    [Theory]
    [InlineData("Password for 'https://evil.test': ")]
    [InlineData("Password for 'http://example.test': ")]
    [InlineData("Password for 'https://example.test:444': ")]
    [InlineData("Password for 'https://example.test/other': ")]
    [InlineData("Password for 'https://user:private-token@example.test': ")]
    [InlineData("Password for 'https://example.test?private-token': ")]
    [InlineData("Password for 'https://example.test/\u001b[2J': ")]
    [InlineData("Password for 'https://example.test/\u202eprivate': ")]
    [InlineData("Enter passphrase for key '/private/key': ")]
    [PlatformSpecific(TestPlatforms.OSX)]
    [SupportedOSPlatform("macos")]
    public void Prompt_RejectsSpoofedOriginsControlsAndOtherProtocols(string raw)
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Throws<AuthenticationException>(() => GitAskPassServer.GetPrompt(raw, new Uri("https://example.test/repository")));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("secret\ninjected")]
    [InlineData("secret\r")]
    [InlineData("secret\0")]
    public void Protocol_RejectsEmptyAndMultilineResponses(string response)
        => Assert.Throws<AuthenticationException>(() => GitAskPassProtocol.ValidateResponse(response));

    [Fact]
    public async Task Protocol_BoundsFramesAndPreservesUtf8()
    {
        using var stream = new MemoryStream();
        await GitAskPassProtocol.WriteAsync(stream, "synthetic-\u00e9", 100, TestContext.Current.CancellationToken);
        stream.Position = 0;
        Assert.Equal("synthetic-\u00e9", await GitAskPassProtocol.ReadAsync(stream, 100, TestContext.Current.CancellationToken));
        using var oversized = new MemoryStream([0, 1, 0, 0]);
        await Assert.ThrowsAsync<AuthenticationException>(() =>
            GitAskPassProtocol.ReadAsync(oversized, 100, TestContext.Current.CancellationToken));
        using var truncated = new MemoryStream([0, 0, 0, 3, 65]);
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            GitAskPassProtocol.ReadAsync(truncated, 100, TestContext.Current.CancellationToken));
        Assert.Throws<AuthenticationException>(() => GitAskPassProtocol.ValidateResponse(new string('x', GitAskPassProtocol.MaxResponseBytes + 1)));
        using var boundary = new MemoryStream();
        var maximum = new string('\u00e9', GitAskPassProtocol.MaxPromptBytes / 2);
        await GitAskPassProtocol.WriteAsync(boundary, maximum, GitAskPassProtocol.MaxPromptBytes, TestContext.Current.CancellationToken);
        boundary.Position = 0;
        Assert.Equal(maximum, await GitAskPassProtocol.ReadAsync(boundary, GitAskPassProtocol.MaxPromptBytes, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AuthenticationException>(() =>
            GitAskPassProtocol.WriteAsync(boundary, maximum + "x", GitAskPassProtocol.MaxPromptBytes, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CredentialPrompt_MasksResponsesDisablesLoggingAndHonorsCancellation()
    {
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("synthetic-secret");
        Assert.Equal("synthetic-secret", await GitCommandRunner.PromptForCredentialAsync(
            interaction, "Bound origin", TestContext.Current.CancellationToken));
        Assert.Equal(new StringPromptCall("Bound origin", null, true, true), Assert.Single(interaction.StringPromptCalls));
        Assert.False(Assert.Single(interaction.StringPromptLogResponses));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GitCommandRunner.PromptForCredentialAsync(interaction, "Bound origin", cancelled.Token));
    }

    [Fact]
    public async Task CredentialPrompt_RejectsIdeWithoutOpeningSecretUi()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IExtensionBackchannel>(new TestExtensionBackchannel())
            .BuildServiceProvider();
        var interaction = new TestExtensionInteractionService(provider)
        {
            PromptForStringCallback = (_, _, _, _, _, _) => throw new Xunit.Sdk.XunitException("IDE secret UI must not open.")
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitCommandRunner.PromptForCredentialAsync(interaction, "Bound origin", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/opt/aspire", "", "'/opt/aspire' git askpass")]
    [InlineData("/opt/it's here/aspire", "", "'/opt/it'\\''s here/aspire' git askpass")]
    [InlineData("/opt/dotnet", "/opt/app/aspire.dll", "'/opt/dotnet' '/opt/app/aspire.dll' git askpass")]
    [PlatformSpecific(TestPlatforms.AnyUnix)]
    public void Launcher_QuotesOnlyTrustedPaths(string process, string assembly, string expected)
        => Assert.Equal(expected, GitAskPassInvocation.CreateHelperCommand(process, assembly));

    [Fact]
    public async Task HiddenInvocation_InvalidArgumentsNeverInitializeTheCliOrEchoInputs()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.True(GitAskPassInvocation.Matches(["git", "askpass", "private-prompt"]));
        Assert.False(GitAskPassInvocation.Matches(["new", "git", "askpass"]));
        var result = await GitAskPassInvocation.InvokeAsync(["git", "askpass", "private-prompt", "extra"],
            _ => throw new InvalidOperationException("Must not read environment."), output, error, TestContext.Current.CancellationToken);
        Assert.Equal(1, result);
        Assert.Equal("", output.ToString());
        Assert.Equal($"Git credential callback failed or was cancelled.{Environment.NewLine}", error.ToString());
    }

    [Theory]
    [InlineData("success")]
    [InlineData("bad-key")]
    [InlineData("bad-pin")]
    [InlineData("cancel")]
    [InlineData("invalid-response")]
    [PlatformSpecific(TestPlatforms.OSX)]
    public async Task Session_AuthenticatesBothEndpointsAndHandlesRejection(string scenario)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        var prompts = 0;
        var info = new ProcessStartInfo("git");
        var session = await GitAskPassSession.CreateAsync(new Uri("https://example.test/repository"), (_, token) =>
        {
            Interlocked.Increment(ref prompts);
            token.ThrowIfCancellationRequested();
            if (scenario == "cancel")
            {
                throw new OperationCanceledException();
            }
            return Task.FromResult(scenario == "invalid-response" ? "synthetic\ninjected" : "synthetic-secret");
        }, CreateServerStartInfo(), TestContext.Current.CancellationToken);
        await using (session)
        {
            session.Configure(info, "'/opt/aspire' git askpass");
            var path = info.Environment[GitAskPassProtocol.SocketVariable]!;
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(path)!));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(info.Environment["GIT_ASKPASS"]!));
            Assert.Equal("0", info.Environment["GIT_TERMINAL_PROMPT"]);
            if (scenario is "bad-key" or "bad-pin")
            {
                info.Environment[scenario == "bad-key" ? GitAskPassProtocol.KeyVariable : GitAskPassProtocol.PinVariable] = new string('0', 64);
            }
            var output = new StringWriter();
            var error = new StringWriter();
            var result = await GitAskPassInvocation.InvokeAsync(
                ["git", "askpass", "Password for 'https://private-user@example.test': "],
                name => info.Environment.TryGetValue(name, out var value) ? value : null, output, error, TestContext.Current.CancellationToken).DefaultTimeout();
            Assert.Equal(scenario == "success" ? 0 : 1, result);
            Assert.Equal(scenario == "success" ? $"synthetic-secret{Environment.NewLine}" : "", output.ToString());
            Assert.Equal(scenario == "success" ? "" : $"Git credential callback failed or was cancelled.{Environment.NewLine}", error.ToString());
            Assert.Equal(scenario is "bad-key" or "bad-pin" ? 0 : 1, prompts);
        }
        Assert.False(File.Exists(info.Environment[GitAskPassProtocol.SocketVariable]!));
        Assert.False(Directory.Exists(Path.GetDirectoryName(info.Environment[GitAskPassProtocol.SocketVariable]!)));
    }

    [Fact]
    [PlatformSpecific(TestPlatforms.OSX)]
    public async Task Session_DisconnectCancelsPromptAndDoesNotRouteAcrossOperations()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherPrompts = 0;
        await using var session = await GitAskPassSession.CreateAsync(new Uri("https://example.test"), async (_, token) =>
        {
            prompted.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return "unreachable";
            }
            finally
            {
                cancelled.SetResult();
            }
        }, CreateServerStartInfo(), TestContext.Current.CancellationToken);
        await using var other = await GitAskPassSession.CreateAsync(new Uri("https://other.test"), (_, _) =>
        {
            otherPrompts++;
            return Task.FromResult("other");
        }, CreateServerStartInfo(), TestContext.Current.CancellationToken);
        var info = new ProcessStartInfo("git");
        session.Configure(info, "helper");
        var otherInfo = new ProcessStartInfo("git");
        other.Configure(otherInfo, "helper");
        Assert.NotEqual(info.Environment[GitAskPassProtocol.SocketVariable], otherInfo.Environment[GitAskPassProtocol.SocketVariable]);
        Assert.NotEqual(info.Environment[GitAskPassProtocol.KeyVariable], otherInfo.Environment[GitAskPassProtocol.KeyVariable]);
        otherInfo.Environment[GitAskPassProtocol.KeyVariable] = info.Environment[GitAskPassProtocol.KeyVariable];
        var rejectedOutput = new StringWriter();
        var rejectedError = new StringWriter();
        Assert.Equal(1, await GitAskPassInvocation.InvokeAsync(
            ["git", "askpass", "Password for 'https://other.test': "],
            name => otherInfo.Environment.TryGetValue(name, out var value) ? value : null,
            rejectedOutput, rejectedError, TestContext.Current.CancellationToken).DefaultTimeout());
        Assert.Equal("", rejectedOutput.ToString());
        Assert.Equal(0, otherPrompts);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(info.Environment[GitAskPassProtocol.SocketVariable]!), TestContext.Current.CancellationToken);
        await using var stream = new SslStream(new NetworkStream(socket), false, (_, _, _, _) => true);
        await stream.AuthenticateAsClientAsync("aspire-git-askpass");
        await stream.WriteAsync(Convert.FromHexString(info.Environment[GitAskPassProtocol.KeyVariable]!), TestContext.Current.CancellationToken);
        await GitAskPassProtocol.WriteAsync(stream, "Password for 'https://example.test': ", 4096, TestContext.Current.CancellationToken);
        await prompted.Task.DefaultTimeout();
        socket.Dispose();
        await cancelled.Task.DefaultTimeout();
        Assert.Equal(0, otherPrompts);
    }

    [Fact]
    [PlatformSpecific(TestPlatforms.OSX)]
    [SupportedOSPlatform("macos")]
    public async Task Endpoint_RejectsInsecurePermissionsAndUnboundPaths()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        await using var session = await GitAskPassSession.CreateAsync(new Uri("https://example.test"), (_, _) => Task.FromResult("synthetic"), CreateServerStartInfo(), TestContext.Current.CancellationToken);
        var info = new ProcessStartInfo("git");
        session.Configure(info, "helper");
        var path = info.Environment[GitAskPassProtocol.SocketVariable]!;
        GitAskPassDirectory.ValidateEndpoint(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
        Assert.Throws<IOException>(() => GitAskPassDirectory.ValidateEndpoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Throws<IOException>(() => GitAskPassDirectory.ValidateEndpoint(Path.Combine(Path.GetDirectoryName(path)!, "..", Path.GetFileName(path))));
        Assert.Throws<IOException>(() => GitAskPassDirectory.ValidateEndpoint("/tmp/foreign-callback"));
    }

    [Fact]
    [RequiresTools(["git", "dotnet"])]
    [PlatformSpecific(TestPlatforms.OSX)]
    public async Task RealGit_AskPassInvokesEarlyCliEntryPointAndKeepsProtocolStdout()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await using var server = new GitAuthenticationServer(acceptCredentials: true);
        var prompts = new List<string>();
        await using var session = await GitAskPassSession.CreateAsync(new Uri(server.Repository), (prompt, _) =>
        {
            prompts.Add(prompt);
            return Task.FromResult(prompts.Count == 1 ? "probe" : "probe-only");
        }, CreateServerStartInfo(), TestContext.Current.CancellationToken);
        var runner = new GitCommandRunner(TestHelpers.CreateInteractiveHostEnvironment(), new TestInteractionService(),
            NullLogger<GitCommandRunner>.Instance, new TestFeatures());
        var info = runner.CreateStartInfo(workspace.Path, ["ls-remote", "--", server.Repository], GitCommandMode.Captured);
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_CONFIG_COUNT"] = "0";
        info.Environment.Remove("GIT_CONFIG_PARAMETERS");
        info.Environment["NO_PROXY"] = "127.0.0.1";
        info.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(workspace.Path, "gitconfig");
        await File.WriteAllTextAsync(info.Environment["GIT_CONFIG_GLOBAL"]!, "[credential]\nhelper =\n", TestContext.Current.CancellationToken);
        // Run the real referenced CLI assembly with the test runtime configuration.
        // This bypasses no command code: Program.Main must take the early askpass path.
        var helper = $"'{PathLookupHelper.ResolveExecutablePath("dotnet")}' exec --runtimeconfig '{Path.Combine(AppContext.BaseDirectory, "Aspire.Cli.Tests.runtimeconfig.json")}' '{typeof(Program).Assembly.Location}' git askpass";
        session.Configure(info, helper);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(session.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(session.CancellationToken);
        await process.WaitForExitAsync(session.CancellationToken).DefaultTimeout();
        Assert.True(process.ExitCode == 0, await stderr);
        Assert.Equal("", await stdout);
        Assert.Equal(2, prompts.Count);
        Assert.Equal(2, server.Requests);
    }

    [Fact]
    [PlatformSpecific(TestPlatforms.OSX)]
    [SupportedOSPlatform("macos")]
    public async Task Endpoint_SymlinkReplacementCannotChmodOrDeleteItsTarget()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var target = Path.Combine(workspace.Path, "owned-target");
        await File.WriteAllTextAsync(target, "synthetic", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var session = await GitAskPassSession.CreateAsync(new Uri("https://example.test"),
            (_, _) => Task.FromResult("synthetic"), CreateServerStartInfo(), TestContext.Current.CancellationToken);
        var info = new ProcessStartInfo("git");
        session.Configure(info, "helper");
        var path = info.Environment[GitAskPassProtocol.SocketVariable]!;
        File.Delete(path);
        File.CreateSymbolicLink(path, target);
        try
        {
            Assert.Throws<IOException>(() => GitAskPassDirectory.ValidateEndpoint(path));
            await Assert.ThrowsAsync<IOException>(() => session.DisposeAsync().AsTask());
            Assert.Equal("synthetic", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
            Assert.Equal(target, new FileInfo(path).LinkTarget);
        }
        finally
        {
            File.Delete(path);
            File.Delete(Path.Combine(Path.GetDirectoryName(path)!, "askpass"));
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
    }

    private static ProcessStartInfo CreateServerStartInfo()
        => new(PathLookupHelper.ResolveExecutablePath("dotnet"),
            ["exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Aspire.Cli.Tests.runtimeconfig.json"),
                typeof(Program).Assembly.Location, "git", "askpass-server"]);
}
