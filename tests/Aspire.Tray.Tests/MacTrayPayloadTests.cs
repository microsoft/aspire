// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class MacTrayPayloadTests(ITestOutputHelper output)
{
    public static bool SupportsMac => OperatingSystem.IsMacOS();

    [Theory(Skip = "Payload verification requires macOS signing and Mach-O tools.", SkipUnless = nameof(SupportsMac))]
    [InlineData("osx-arm64", "arm64", "valid", 0)]
    [InlineData("osx-x64", "x86_64", "valid", 0)]
    [InlineData("osx-arm64", "x86_64", "wrong-architecture", 1)]
    [InlineData("osx-x64", "arm64", "wrong-architecture", 1)]
    [InlineData("osx-arm64", "universal", "valid", 0)]
    [InlineData("osx-x64", "universal", "valid", 0)]
    [InlineData("osx-arm64", "arm64", "invalid-signature", 1)]
    [InlineData("osx-x64", "x86_64", "invalid-signature", 1)]
    [InlineData("osx-arm64", "arm64", "not-mach-o", 1)]
    public async Task VerifierChecksArchitectureAndSignatureOfActualArchive(string rid, string architecture, string scenario, int expectedExitCode)
    {
        var workspace = Directory.CreateTempSubdirectory("tray-payload-");
        try
        {
            // Run the unmodified verifier under an isolated root so parallel runs do not share
            // its fixed artifacts/tray-payload-verification/<rid> extraction directory.
            var script = Path.Combine(workspace.FullName, "tools", "CreateLayout", "verify-tray-payload.sh");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "verify-tray-payload.sh"), script);
            var app = Path.Combine(workspace.FullName, rid, "tray", "Aspire Tray.app");
            Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
            Directory.CreateDirectory(Path.Combine(app, "Contents", "Resources"));
            var executable = Path.Combine(app, "Contents", "MacOS", "aspire-tray");
            var icon = Path.Combine(app, "Contents", "Resources", "Aspire.icns");
            File.WriteAllText(icon, "test resource sealed by codesign");
            File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), """
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>CFBundleExecutable</key><string>aspire-tray</string>
                  <key>CFBundleIdentifier</key><string>com.microsoft.aspire.tray.test</string>
                  <key>CFBundlePackageType</key><string>APPL</string>
                </dict>
                </plist>
                """);
            var source = Path.Combine(workspace.FullName, "main.c");
            File.WriteAllText(source, "int main(void) { return 0; }\n");
            string[] architectures = architecture == "universal" ? ["-arch", "arm64", "-arch", "x86_64"] : ["-arch", architecture];
            await RunSuccessfullyAsync("/usr/bin/xcrun", ["clang", .. architectures, source, "-o", executable]);
            await RunSuccessfullyAsync("/usr/bin/codesign", "--force", "--sign", "-", app);
            await RunSuccessfullyAsync("/usr/bin/codesign", "--verify", "--strict", app);

            if (scenario == "invalid-signature")
            {
                File.WriteAllText(icon, "tampered resource");
            }
            else if (scenario == "not-mach-o")
            {
                File.WriteAllText(executable, "#!/bin/sh\nexit 0\n");
            }

            var archive = Path.Combine(workspace.FullName, "payload.tar.gz");
            await RunSuccessfullyAsync("/usr/bin/tar", "-czf", archive, "-C", workspace.FullName, rid);
            var result = await TestNativeCommand.RunAsync("/bin/bash", script, archive, rid);
            output.WriteLine(result.Output);
            output.WriteLine(result.Error);

            Assert.Equal(expectedExitCode, result.ExitCode);
            var expectedArchitecture = rid == "osx-arm64" ? "arm64" : "x86_64";
            if (scenario is "wrong-architecture" or "not-mach-o")
            {
                Assert.Contains($"Tray executable does not contain the required {expectedArchitecture} architecture for {rid}.", result.Error);
            }
            else if (scenario == "invalid-signature")
            {
                Assert.Contains("a sealed resource is missing or invalid", result.Error);
            }
            else
            {
                Assert.Contains($"Verified tray payload: {archive} ({rid}/tray/Aspire Tray.app, executable mode ", result.Output);
            }
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    private async Task RunSuccessfullyAsync(string command, params string[] arguments)
    {
        var result = await TestNativeCommand.RunAsync(command, arguments);
        output.WriteLine(result.Output);
        output.WriteLine(result.Error);
        Assert.Equal(0, result.ExitCode);
    }
}
