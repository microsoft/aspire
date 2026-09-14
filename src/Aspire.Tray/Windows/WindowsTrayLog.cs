// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using System.Text;

namespace Aspire.Tray;

/// <summary>
/// Owns the GUI's diagnostic streams independently of the launching terminal.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsTrayLog : IDisposable
{
    private readonly TextWriter _originalOutput = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    private readonly TextWriter _writer;

    public static string LogPath => Path.Combine(WindowsSingleInstance.DirectoryPath, "aspire-tray.log");

    public WindowsTrayLog()
    {
        _writer = TextWriter.Synchronized(new TimestampedWriter(OpenLog()));
        Console.SetOut(_writer);
        Console.SetError(_writer);
    }

    public static void EnsureWritable()
    {
        using var stream = OpenLog();
    }

    private static FileStream OpenLog()
    {
        Directory.CreateDirectory(WindowsSingleInstance.DirectoryPath);
        // LocalAppData inherits the Windows profile's per-user ACL, not a shared temp ACL.
        // Never follow a redirected log/state directory or overwrite a linked log target.
        foreach (FileSystemInfo entry in new FileSystemInfo[]
        {
            new DirectoryInfo(WindowsSingleInstance.DirectoryPath), new FileInfo(LogPath)
        })
        {
            if (entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new IOException("The tray log must not use symbolic links or reparse points.");
            }
        }

        return new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOutput);
        Console.SetError(_originalError);
        _writer.Dispose();
    }

    private sealed class TimestampedWriter(FileStream stream) : StreamWriter(stream, new UTF8Encoding(false))
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine($"[{DateTimeOffset.UtcNow:O}] {value}");
            Flush();
            stream.Flush(flushToDisk: true);
        }
    }
}
