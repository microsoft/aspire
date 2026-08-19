// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

if (args is ["--list-sdks"])
{
    Console.WriteLine($"13.5.0 [{Path.Combine(AppContext.BaseDirectory, "sdk")}]");
    return;
}

if (args is ["--hang"])
{
    var pidPath = Environment.GetEnvironmentVariable("ASPIRE_TEST_HANG_PID_PATH")
        ?? throw new InvalidOperationException("ASPIRE_TEST_HANG_PID_PATH is not set.");
    File.WriteAllText(pidPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

var dnxArgumentIndex = Array.IndexOf(args, "dnx");
var forwardedArgs = dnxArgumentIndex >= 0 ? args[(dnxArgumentIndex + 1)..] : args;

var setupArgumentIndex = Array.IndexOf(forwardedArgs, "setup");
var installPathArgumentIndex = Array.IndexOf(forwardedArgs, "--install-path");
// The targets exercise setup as either:
//   aspire.exe setup
//   dotnet ... dnx --yes aspire.cli[@<version>] -- setup --install-path <path>
var setupInstallPath = forwardedArgs switch
{
    ["setup"] => AppContext.BaseDirectory,
    _ when setupArgumentIndex >= 0 &&
        installPathArgumentIndex >= 0 &&
        installPathArgumentIndex + 1 < forwardedArgs.Length => forwardedArgs[installPathArgumentIndex + 1],
    _ => null
};
if (setupInstallPath is not null)
{
    var dcpDirectory = Directory.CreateDirectory(Path.Combine(setupInstallPath, "bundle", "dcp"));
    var managedDirectory = Directory.CreateDirectory(Path.Combine(setupInstallPath, "bundle", "managed"));
    File.WriteAllText(Path.Combine(dcpDirectory.FullName, OperatingSystem.IsWindows() ? "dcp.exe" : "dcp"), "");
    File.WriteAllText(Path.Combine(managedDirectory.FullName, OperatingSystem.IsWindows() ? "aspire-managed.exe" : "aspire-managed"), "");
    return;
}

if (forwardedArgs.Length > 0 && forwardedArgs[^1] == "--version")
{
    if (File.Exists(Path.Combine(AppContext.BaseDirectory, "fail-version")))
    {
        Environment.ExitCode = 42;
        return;
    }

    Console.WriteLine("13.5.0");
    return;
}

var capturePath = Environment.GetEnvironmentVariable("ASPIRE_TEST_CAPTURE_PATH")
    ?? throw new InvalidOperationException("ASPIRE_TEST_CAPTURE_PATH is not set.");
File.WriteAllLines(capturePath, forwardedArgs);
