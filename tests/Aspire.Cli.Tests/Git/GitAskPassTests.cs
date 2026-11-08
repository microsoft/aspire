// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Authentication;
using System.Security.Principal;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Git;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Git;

public class GitAskPassTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("Username for 'https://example.test': ", true)]
    [InlineData("Password for 'https://private-user@example.test/repository': ", false)]
    [InlineData("Password for 'https://example.test': ", false)]
    public void Prompt_UsesBoundOriginAndNeverRawGitText(string raw, bool username)
        => Assert.Equal(string.Format(username ? InteractionServiceStrings.GitUsernamePrompt : InteractionServiceStrings.GitPasswordPrompt,
            "https://example.test"), GitAskPassSession.GetPrompt(raw, new Uri("https://example.test/repository")));

    [Theory]
    [InlineData("Password for 'https://evil.test': ")]
    [InlineData("Password for 'http://example.test': ")]
    [InlineData("Password for 'https://example.test:444': ")]
    [InlineData("Password for 'https://example.test/other': ")]
    [InlineData("Password for 'https://private:secret@example.test': ")]
    [InlineData("Password for 'https://example.test?private-token': ")]
    [InlineData("Password for 'https://example.test/\u001b[2J': ")]
    [InlineData("Password for 'https://example.test/\u202eprivate': ")]
    [InlineData("Enter passphrase for key '/private/key': ")]
    public void Prompt_RejectsSpoofedOriginsControlsAndOtherProtocols(string raw)
        => Assert.Throws<AuthenticationException>(() => GitAskPassSession.GetPrompt(raw, new Uri("https://example.test/repository")));

    [Theory]
    [InlineData("")]
    [InlineData("secret\ninjected")]
    [InlineData("secret\r")]
    [InlineData("secret\0")]
    public void Protocol_RejectsEmptyAndMultilineResponses(string response)
        => Assert.Throws<AuthenticationException>(() => GitAskPassProtocol.ValidateResponse(response));

    [Fact]
    public async Task Protocol_BoundsFramesByUtf8Bytes()
    {
        using var stream = new MemoryStream();
        var maximum = new string('\u00e9', GitAskPassProtocol.MaxPromptBytes / 2);
        await GitAskPassProtocol.WriteAsync(stream, maximum, GitAskPassProtocol.MaxPromptBytes, TestContext.Current.CancellationToken);
        stream.Position = 0;
        Assert.Equal(maximum, await GitAskPassProtocol.ReadAsync(stream, GitAskPassProtocol.MaxPromptBytes, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AuthenticationException>(() =>
            GitAskPassProtocol.WriteAsync(stream, maximum + "x", GitAskPassProtocol.MaxPromptBytes, TestContext.Current.CancellationToken));
        Assert.Throws<AuthenticationException>(() =>
            GitAskPassProtocol.ValidateResponse(new string('\u00e9', GitAskPassProtocol.MaxResponseBytes / 2 + 1)));
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
    public async Task CredentialPrompt_UsesSharedMaskedExtensionRouting()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IExtensionBackchannel>(new TestExtensionBackchannel()).BuildServiceProvider();
        var calls = 0;
        var interaction = new TestExtensionInteractionService(provider)
        {
            PromptForStringCallback = (prompt, _, secret, required, _, token) =>
            {
                calls++;
                Assert.Equal("Bound origin", prompt);
                Assert.True(secret);
                Assert.True(required);
                Assert.Equal(TestContext.Current.CancellationToken, token);
                return Task.FromResult("synthetic-secret");
            }
        };
        Assert.Equal("synthetic-secret", await
            GitCommandRunner.PromptForCredentialAsync(interaction, "Bound origin", TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Launcher_QuotesOnlyTrustedPaths()
    {
        var directory = Path.GetFullPath(Path.DirectorySeparatorChar.ToString());
        var process = Path.Combine(directory, "it's here", OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        var quoted = (OperatingSystem.IsWindows() ? process.Replace('\\', '/') : process).Replace("'", "'\\''", StringComparison.Ordinal);
        Assert.Equal($"'{quoted}' git askpass", GitAskPassInvocation.CreateHelperCommand(process, ""));
        Assert.Throws<InvalidOperationException>(() => GitAskPassInvocation.CreateHelperCommand("untrusted", ""));
        Assert.Throws<InvalidOperationException>(() => GitAskPassInvocation.CreateHelperCommand(Path.Combine(directory, "other"), ""));
    }

    [Fact]
    public async Task HiddenInvocation_InvalidArgumentsNeverInitializeTheCliOrEchoInputs()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.True(GitAskPassInvocation.Matches(["git", "askpass", "private-prompt"]));
        Assert.False(GitAskPassInvocation.Matches(["git", "askpass-server"]));
        var result = await GitAskPassInvocation.InvokeAsync(["git", "askpass", "private-prompt", "extra"],
            _ => throw new InvalidOperationException("Must not read environment."), output, error, TestContext.Current.CancellationToken);
        Assert.Equal(1, result);
        Assert.Equal("", output.ToString());
        Assert.Equal($"Git credential callback failed or was cancelled.{Environment.NewLine}", error.ToString());
    }

    [Theory]
    [InlineData("success")]
    [InlineData("cancel")]
    [InlineData("invalid-response")]
    [InlineData("wrong-destination")]
    public async Task Session_BrokersResponsesThroughParentAndCleansUp(string scenario)
    {
        var prompts = 0;
        var info = new ProcessStartInfo("git");
        var session = new GitAskPassSession(new Uri("https://example.test/repository"), (_, token) =>
        {
            Interlocked.Increment(ref prompts);
            token.ThrowIfCancellationRequested();
            if (scenario == "cancel")
            {
                throw new OperationCanceledException();
            }
            return Task.FromResult(scenario == "invalid-response" ? "synthetic\ninjected" : "synthetic-secret");
        }, TestContext.Current.CancellationToken);
        await using (session)
        {
            session.Configure(info, "'trusted-launcher' git askpass");
            var path = info.Environment[GitAskPassProtocol.SocketVariable]!;
            GitAskPassDirectory.ValidateEndpoint(path);
            if (OperatingSystem.IsWindows())
            {
                var security = new DirectoryInfo(Path.GetDirectoryName(path)!).GetAccessControl();
                using var identity = WindowsIdentity.GetCurrent();
                Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
                Assert.True(security.AreAccessRulesProtected);
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
                {
                    Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                    Assert.True(identity.User!.Equals(rule.IdentityReference) || system.Equals(rule.IdentityReference));
                }
                foreach (var file in new[] { path, info.Environment["GIT_ASKPASS"]! })
                {
                    var rules = new FileInfo(file).GetAccessControl()
                        .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
                    Assert.NotEmpty(rules);
                    foreach (var rule in rules)
                    {
                        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                        Assert.True(identity.User!.Equals(rule.IdentityReference) || system.Equals(rule.IdentityReference));
                    }
                }
            }
            else
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.GetDirectoryName(path)!));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(info.Environment["GIT_ASKPASS"]!));
            }
            Assert.Equal("0", info.Environment["GIT_TERMINAL_PROMPT"]);
            var output = new StringWriter();
            var error = new StringWriter();
            var raw = scenario == "wrong-destination"
                ? "Password for 'https://other.test': " : "Password for 'https://private-user@example.test': ";
            var result = await GitAskPassInvocation.InvokeAsync(["git", "askpass", raw],
                name => info.Environment.TryGetValue(name, out var value) ? value : null,
                output, error, TestContext.Current.CancellationToken).DefaultTimeout();
            Assert.Equal(scenario == "success" ? 0 : 1, result);
            Assert.Equal(scenario == "success" ? $"synthetic-secret{Environment.NewLine}" : "", output.ToString());
            Assert.Equal(scenario == "success" ? "" : $"Git credential callback failed or was cancelled.{Environment.NewLine}", error.ToString());
            Assert.Equal(scenario == "wrong-destination" ? 0 : 1, prompts);
        }
        Assert.False(File.Exists(info.Environment[GitAskPassProtocol.SocketVariable]!));
        Assert.False(Directory.Exists(Path.GetDirectoryName(info.Environment[GitAskPassProtocol.SocketVariable]!)));
    }

    [Fact]
    public async Task Session_DisconnectCancelsPromptAndOtherOperationsStaySeparate()
    {
        var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherPrompts = 0;
        await using var session = new GitAskPassSession(new Uri("https://example.test"), async (_, token) =>
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
        }, TestContext.Current.CancellationToken);
        await using var other = new GitAskPassSession(new Uri("https://other.test"), (_, _) =>
        {
            otherPrompts++;
            return Task.FromResult("other");
        }, TestContext.Current.CancellationToken);
        var info = new ProcessStartInfo("git");
        var otherInfo = new ProcessStartInfo("git");
        session.Configure(info, "helper");
        other.Configure(otherInfo, "helper");
        Assert.NotEqual(info.Environment[GitAskPassProtocol.SocketVariable], otherInfo.Environment[GitAskPassProtocol.SocketVariable]);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(info.Environment[GitAskPassProtocol.SocketVariable]!), TestContext.Current.CancellationToken);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await GitAskPassProtocol.WriteAsync(stream, "Password for 'https://example.test': ", 4096, TestContext.Current.CancellationToken);
        await prompted.Task.DefaultTimeout();
        socket.Dispose();
        await cancelled.Task.DefaultTimeout();
        Assert.Equal(0, otherPrompts);
        var output = new StringWriter();
        Assert.Equal(0, await GitAskPassInvocation.InvokeAsync(["git", "askpass", "Password for 'https://other.test': "],
            name => otherInfo.Environment.TryGetValue(name, out var value) ? value : null,
            output, new StringWriter(), TestContext.Current.CancellationToken).DefaultTimeout());
        Assert.Equal($"other{Environment.NewLine}", output.ToString());
        Assert.Equal(1, otherPrompts);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("invalid-utf8")]
    [InlineData("truncated")]
    public async Task Session_RejectsInvalidFramesBeforeOpeningPrompt(string scenario)
    {
        var prompts = 0;
        await using var session = new GitAskPassSession(new Uri("https://example.test"), (_, _) =>
        {
            prompts++;
            return Task.FromResult("synthetic");
        }, TestContext.Current.CancellationToken);
        var info = new ProcessStartInfo("git");
        session.Configure(info, "helper");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(info.Environment[GitAskPassProtocol.SocketVariable]!), TestContext.Current.CancellationToken);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        byte[] frame = scenario switch
        {
            "empty" => [0, 0, 0, 0],
            "oversized" => [0, 1, 0, 0],
            "invalid-utf8" => [0, 0, 0, 1, 255],
            _ => [0, 0, 0, 3, 65]
        };
        await stream.WriteAsync(frame, TestContext.Current.CancellationToken);
        socket.Shutdown(SocketShutdown.Send);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Delay(Timeout.Infinite, session.CancellationToken).DefaultTimeout());
        Assert.True(session.Failed);
        Assert.Equal(0, prompts);
    }

    [Fact]
    public async Task Session_CallerCancellationClosesHelperAndCancelsInput()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new GitAskPassSession(new Uri("https://example.test"), async (_, token) =>
        {
            prompted.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "unreachable";
        }, caller.Token);
        var info = new ProcessStartInfo("git");
        session.Configure(info, "helper");
        var output = new StringWriter();
        var request = GitAskPassInvocation.InvokeAsync(["git", "askpass", "Password for 'https://example.test': "],
            name => info.Environment.TryGetValue(name, out var value) ? value : null,
            output, new StringWriter(), TestContext.Current.CancellationToken);
        await prompted.Task.DefaultTimeout();
        await caller.CancelAsync();
        Assert.Equal(1, await request.DefaultTimeout());
        Assert.Equal("", output.ToString());
        Assert.False(session.Failed);
    }

    [Fact]
    [PlatformSpecific(TestPlatforms.AnyUnix)]
    public async Task Endpoint_RejectsUnsafePermissionsWithoutRepairingThem()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        await using var session = new GitAskPassSession(new Uri("https://example.test"),
            (_, _) => Task.FromResult("synthetic"), TestContext.Current.CancellationToken);
        var info = new ProcessStartInfo("git");
        session.Configure(info, "helper");
        var path = info.Environment[GitAskPassProtocol.SocketVariable]!;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
        Assert.Throws<IOException>(() => GitAskPassDirectory.ValidateEndpoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var directory = Path.GetDirectoryName(path)!;
        var publicMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead;
        File.SetUnixFileMode(directory, publicMode);
        try
        {
            Assert.Throws<IOException>(() => GitAskPassDirectory.ValidateEndpoint(path));
            Assert.Equal(publicMode, File.GetUnixFileMode(directory));
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    [RequiresTools(["git", "dotnet"])]
    public async Task RealGit_AskPassCallsBackToParentAndKeepsProtocolStdout()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await using var server = new GitAuthenticationServer(acceptCredentials: true);
        var prompts = new List<string>();
        await using var session = new GitAskPassSession(new Uri(server.Repository), (prompt, _) =>
        {
            prompts.Add(prompt);
            return Task.FromResult(prompts.Count == 1 ? "probe" : "probe-only");
        }, TestContext.Current.CancellationToken);
        var runner = new GitCommandRunner(TestHelpers.CreateInteractiveHostEnvironment(), new TestInteractionService(),
            NullLogger<GitCommandRunner>.Instance, new TestFeatures());
        var info = runner.CreateStartInfo(workspace.Path, ["ls-remote", "--", server.Repository], GitCommandMode.Captured);
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_CONFIG_COUNT"] = "0";
        info.Environment.Remove("GIT_CONFIG_PARAMETERS");
        info.Environment["NO_PROXY"] = "127.0.0.1";
        info.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(workspace.Path, "gitconfig");
        await File.WriteAllTextAsync(info.Environment["GIT_CONFIG_GLOBAL"]!, "[credential]\nhelper =\n", TestContext.Current.CancellationToken);
        // Use the referenced CLI with the test runtime configuration; Program.Main
        // must take the early helper entry point, not initialize the ordinary CLI.
        var dotnet = PathLookupHelper.ResolveExecutablePath("dotnet").Replace('\\', '/');
        var runtime = Path.Combine(AppContext.BaseDirectory, "Aspire.Cli.Tests.runtimeconfig.json").Replace('\\', '/');
        var assembly = typeof(Program).Assembly.Location.Replace('\\', '/');
        session.Configure(info, $"'{dotnet}' exec --runtimeconfig '{runtime}' '{assembly}' git askpass");
        Assert.Equal("", await runner.RunProcessAsync(info, captureCancellationOutput: false, GitCommandMode.Captured,
            session.CancellationToken).DefaultTimeout());
        Assert.Equal(2, prompts.Count);
        Assert.Equal(2, server.Requests);
    }
}
