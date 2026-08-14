// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.Rust.Tests;

public class RustDockerfileShellTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task EmptyCandidateSetDoesNotPreventTheCurrentArtifactFromBeingCollected()
    {
        SkipWithoutPosixShell();
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var targetDirectory = workspace.CreateDirectory("target").FullName;
        var artifactDirectory = workspace.CreateDirectory("artifacts").FullName;
        var currentArtifact = Path.Combine(targetDirectory, "release", "api");
        var cargoCommand =
            $"mkdir -p {ShellQuote(Path.GetDirectoryName(currentArtifact)!)} && " +
            $"printf current > {ShellQuote(currentArtifact)}";
        var command = RustDockerfileGenerator.BuildArtifactCommand(
            new RustCargoTarget("api", "release", Target: null, IsExample: false),
            cargoCommand,
            targetDirectory,
            artifactDirectory);

        var result = await RunShellAsync(command);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal("current", File.ReadAllText(Path.Combine(artifactDirectory, "api")));
    }

    [Fact]
    public async Task CargoFailurePreservesItsExitCodeAndStandardError()
    {
        SkipWithoutPosixShell();
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var targetDirectory = workspace.CreateDirectory("target").FullName;
        var artifactDirectory = workspace.CreateDirectory("artifacts").FullName;
        var cargoStub = Path.Combine(workspace.WorkspaceRoot.FullName, "cargo-stub");
        await File.WriteAllTextAsync(
            cargoStub,
            "#!/bin/sh\nprintf 'stub cargo failed\\n' >&2\nexit 73\n",
            TestContext.Current.CancellationToken);
        var command = RustDockerfileGenerator.BuildArtifactCommand(
            new RustCargoTarget("api", "release", Target: null, IsExample: false),
            $"/bin/sh {ShellQuote(cargoStub)}",
            targetDirectory,
            artifactDirectory);

        var result = await RunShellAsync(command);

        Assert.Equal(73, result.ExitCode);
        Assert.Equal("stub cargo failed\n", result.StandardError);
        Assert.False(File.Exists(Path.Combine(artifactDirectory, "api")));
    }

    [Fact]
    public async Task StaleCandidatesAreRemovedBeforeTheCurrentArtifactIsCollected()
    {
        SkipWithoutPosixShell();
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var targetDirectory = workspace.CreateDirectory("target").FullName;
        var artifactDirectory = workspace.CreateDirectory("artifacts").FullName;
        var rootCandidate = Path.Combine(targetDirectory, "release", "api");
        var staleTargetCandidate = Path.Combine(targetDirectory, "stale-target", "release", "api");
        var currentTargetCandidate = Path.Combine(targetDirectory, "current-target", "release", "api");
        Directory.CreateDirectory(Path.GetDirectoryName(rootCandidate)!);
        Directory.CreateDirectory(Path.GetDirectoryName(staleTargetCandidate)!);
        File.WriteAllText(rootCandidate, "stale-root");
        File.WriteAllText(staleTargetCandidate, "stale-target");
        var cargoCommand =
            $"mkdir -p {ShellQuote(Path.GetDirectoryName(currentTargetCandidate)!)} && " +
            $"printf current > {ShellQuote(currentTargetCandidate)}";
        var command = RustDockerfileGenerator.BuildArtifactCommand(
            new RustCargoTarget("api", "release", "current-target", IsExample: false),
            cargoCommand,
            targetDirectory,
            artifactDirectory);

        var result = await RunShellAsync(command);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(rootCandidate));
        Assert.False(File.Exists(staleTargetCandidate));
        Assert.Equal("current", File.ReadAllText(Path.Combine(artifactDirectory, "api")));
    }

    [Fact]
    public async Task MissingArtifactDiagnosticDoesNotExecuteTheTargetName()
    {
        SkipWithoutPosixShell();
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var targetDirectory = workspace.CreateDirectory("target").FullName;
        var artifactDirectory = workspace.CreateDirectory("artifacts").FullName;
        var markerPath = Path.Combine(workspace.WorkspaceRoot.FullName, "command-substitution-ran");
        var targetName = $"$(touch${{IFS}}{markerPath})";
        var command = RustDockerfileGenerator.BuildArtifactCommand(
            new RustCargoTarget(targetName, "release", Target: null, IsExample: false),
            "true",
            targetDirectory,
            artifactDirectory);

        var result = await RunShellAsync(command);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal($"no {targetName} under {targetDirectory}\n", result.StandardError);
        Assert.False(File.Exists(markerPath));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TheAppAccountIsOnlyCreatedWhenTheRuntimeImageDoesNotAlreadyDefineIt(bool groupExists, bool userExists)
    {
        SkipWithoutPosixShell();
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var stubDirectory = workspace.CreateDirectory("stubs").FullName;
        var invocationLog = Path.Combine(workspace.WorkspaceRoot.FullName, "invocations");

        // A custom runtime image may already define the `app` group and user. Both toolsets fail when asked
        // to create an account that exists, so stub each tool with the exit code that image would produce.
        WriteStub(stubDirectory, "id", invocationLog, exitCode: userExists ? 0 : 1);
        WriteStub(stubDirectory, "addgroup", invocationLog, exitCode: groupExists ? 1 : 0);
        WriteStub(stubDirectory, "groupadd", invocationLog, exitCode: groupExists ? 1 : 0);
        WriteStub(stubDirectory, "adduser", invocationLog, exitCode: 0);
        WriteStub(stubDirectory, "useradd", invocationLog, exitCode: 0);

        var result = await RunShellAsync(RustDockerfileGenerator.CreateAppUserCommand, stubDirectory);

        string[] expectedInvocations = (groupExists, userExists) switch
        {
            (false, false) => ["id -u app", "addgroup -S app", "adduser -S -G app app"],
            // busybox `addgroup` and shadow-utils `groupadd` both refuse an existing group, so the user is
            // added to the group the image already ships.
            (true, false) => ["id -u app", "addgroup -S app", "groupadd --system app", "adduser -S -G app app"],
            _ => ["id -u app"]
        };

        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedInvocations, ReadInvocations(invocationLog));
    }

    [Fact]
    public async Task AppAccountCreationStillFailsWhenTheRuntimeImageCannotCreateTheUser()
    {
        SkipWithoutPosixShell();
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var stubDirectory = workspace.CreateDirectory("stubs").FullName;
        var invocationLog = Path.Combine(workspace.WorkspaceRoot.FullName, "invocations");

        WriteStub(stubDirectory, "id", invocationLog, exitCode: 1);
        WriteStub(stubDirectory, "addgroup", invocationLog, exitCode: 0);
        WriteStub(stubDirectory, "groupadd", invocationLog, exitCode: 0);
        WriteStub(stubDirectory, "adduser", invocationLog, exitCode: 1, standardError: "adduser: /etc/passwd is read-only");
        WriteStub(stubDirectory, "useradd", invocationLog, exitCode: 1, standardError: "useradd: /etc/passwd is read-only");

        var result = await RunShellAsync(RustDockerfileGenerator.CreateAppUserCommand, stubDirectory);

        // Tolerating a pre-existing account must not tolerate a genuine failure to create one.
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            "adduser: /etc/passwd is read-only\nuseradd: /etc/passwd is read-only\n",
            result.StandardError);
    }

    private static void SkipWithoutPosixShell()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The generated Dockerfile command is executed by /bin/sh.");
        }
    }

    // The stub records its own invocation so a test can assert which account tools ran, then exits with the
    // code the simulated runtime image would produce.
    private static void WriteStub(string directory, string name, string invocationLog, int exitCode, string? standardError = null)    {
        var stubPath = Path.Combine(directory, name);
        var standardErrorLine = standardError is null ? "" : $"echo {ShellQuote(standardError)} >&2\n";

        File.WriteAllText(
            stubPath,
            $"#!/bin/sh\necho {ShellQuote(name)}\" $*\" >> {ShellQuote(invocationLog)}\n{standardErrorLine}exit {exitCode}\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stubPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string[] ReadInvocations(string invocationLog)
        => File.Exists(invocationLog)
            ? File.ReadAllLines(invocationLog)
            : [];

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunShellAsync(
        string command,
        string? path = null)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);

        if (path is not null)
        {
            // Replacing PATH entirely keeps the account commands resolving to the stubs rather than to
            // whatever the host happens to provide.
            startInfo.Environment["PATH"] = path;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start /bin/sh.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return (process.ExitCode, await standardOutput, await standardError);
    }

    private static string ShellQuote(string value)
        => $"'{value.Replace("'", "'\\''")}'";
}
