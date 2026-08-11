// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Restores an environment variable to its prior value on dispose. Used
/// in DiscoverAll tests to point the discovery walk at a controlled
/// <c>HOME</c> / <c>USERPROFILE</c> / <c>PATH</c> sandbox.
/// </summary>
internal sealed class EnvVarOverride : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    public EnvVarOverride(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_name, _previous);
    }
}

/// <summary>
/// Provides a controlled PATH for testing platform-specific dotnet executable resolution.
/// </summary>
internal sealed class TestDotNetPathEnvironment : IDisposable
{
    private readonly DirectoryInfo _tempDirectory;
    private readonly EnvVarOverride _pathOverride;
    private readonly EnvVarOverride? _pathExtensionsOverride;

    public TestDotNetPathEnvironment()
    {
        _tempDirectory = Directory.CreateTempSubdirectory();
        var firstDirectory = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "first"));
        var secondDirectory = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "second"));

        var extensionlessPath = Path.Combine(firstDirectory.FullName, "dotnet");
        File.WriteAllText(extensionlessPath, string.Empty);

        if (OperatingSystem.IsWindows())
        {
            ExpectedPath = Path.Combine(secondDirectory.FullName, "dotnet.exe");
            File.WriteAllText(ExpectedPath, string.Empty);
            _pathExtensionsOverride = new EnvVarOverride("PATHEXT", string.Empty);
        }
        else
        {
            ExpectedPath = extensionlessPath;
            File.SetUnixFileMode(ExpectedPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        _pathOverride = new EnvVarOverride("PATH", string.Join(Path.PathSeparator, firstDirectory.FullName, secondDirectory.FullName));
    }

    public string ExpectedPath { get; }

    public void Dispose()
    {
        _pathOverride.Dispose();
        _pathExtensionsOverride?.Dispose();
        _tempDirectory.Delete(recursive: true);
    }
}
