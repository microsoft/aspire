// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Certificates;

public class MacOSCertificateManagerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    [PlatformSpecific(TestPlatforms.Linux | TestPlatforms.OSX)]
    public void TrustCertificate_PassesSpacedTemporaryPathAsOneArgument()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = Directory.CreateDirectory(Path.Combine(workspace.Path, "home with spaces")).FullName;
        var tool = Path.Combine(workspace.Path, "security");
        var argumentsPath = Path.Combine(workspace.Path, "security-arguments");
        File.WriteAllText(tool, """
            #!/bin/sh
            if [ "$1" = "verify-cert" ]; then
                exit 1
            fi
            printf '%s\n' "$@" > "$TEST_SECURITY_ARGUMENTS"
            """);
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment["HOME"] = home;
        options.StartInfo.Environment["USERPROFILE"] = home;
        options.StartInfo.Environment["PATH"] = workspace.Path;
        options.StartInfo.Environment["TEST_SECURITY_ARGUMENTS"] = argumentsPath;

        RemoteExecutor.Invoke(static (home, argumentsPath) =>
        {
            Assert.Equal(home, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            using var storage = new PrivateTemporaryStorage(home, new ConfigurationBuilder().Build());
            var manager = new MacOSCertificateManager(NullLogger.Instance, storage);
            using var certificate = manager.CreateAspNetCoreHttpsDevelopmentCertificate(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            Assert.Equal(CertificateManager.TrustLevel.Full, manager.TrustCertificate(certificate));

            var arguments = File.ReadAllLines(argumentsPath);
            Assert.Equal(8, arguments.Length);
            Assert.Equal(
                ["add-trusted-cert", "-p", "basic", "-p", "ssl", "-k", home + "/Library/Keychains/login.keychain-db"],
                arguments[..7]);
            Assert.StartsWith(storage.GetTempPath(), arguments[7], StringComparison.Ordinal);
        }, home, argumentsPath, options).Dispose();
    }
}
