// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Tray.Tests.Helpers;

internal sealed class FixtureCli : IDisposable
{
    private readonly DirectoryInfo _directory;
    public string Path { get; }

    public FixtureCli(string body)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This fixture requires /bin/sh.");
        }

        _directory = Directory.CreateTempSubdirectory("aspire-tray-cli-");
        Path = System.IO.Path.Combine(_directory.FullName, "aspire");
        // Quote $0 so fixture paths with spaces remain a single filename. Record argv one
        // argument per line, independently of shell metacharacters in an AppHost path.
        File.WriteAllText(Path, "#!/bin/sh\nprintf '%s\\n' \"$$\" > \"$0.pid\"\nprintf '%s\\n' \"$@\" > \"$0.args\"\n" + body + "\n");
        File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public string[] ReadArguments() => File.ReadAllLines(Path + ".args");

    public Task<int> WaitForPidAsync(CancellationToken cancellationToken)
        => WaitForPidFileAsync(Path + ".pid", cancellationToken);

    public Task<int> WaitForDetachedPidAsync(CancellationToken cancellationToken)
        => WaitForPidFileAsync(Path + ".detached.pid", cancellationToken);

    private static async Task<int> WaitForPidFileAsync(string path, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        while (true)
        {
            if (File.Exists(path)
                && int.TryParse(await File.ReadAllTextAsync(path, lifetime.Token).ConfigureAwait(false),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                return pid;
            }
            await Task.Delay(25, lifetime.Token).ConfigureAwait(false);
        }
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
