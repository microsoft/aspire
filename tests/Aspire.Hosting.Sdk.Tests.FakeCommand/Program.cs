// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

if (args is ["--list-sdks"])
{
    Console.WriteLine($"13.5.0 [{Path.Combine(AppContext.BaseDirectory, "sdk")}]");
    return;
}

var dnxArgumentIndex = Array.IndexOf(args, "dnx");
var forwardedArgs = dnxArgumentIndex >= 0 ? args[(dnxArgumentIndex + 1)..] : args;

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
