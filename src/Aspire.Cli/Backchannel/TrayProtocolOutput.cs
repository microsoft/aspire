// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Writes protocol lines without the console stream's broken-pipe error suppression.
/// </summary>
internal sealed partial class TrayProtocolOutput(Func<TextWriter> createWriter) : IDisposable
{
    private readonly Lazy<TextWriter> _writer = new(createWriter);

    public static TrayProtocolOutput CreateStandardOutput() => new(() =>
    {
        // Console.Out / Console.OpenStandardOutput intentionally ignore EPIPE on Unix. The
        // heartbeat must observe it to terminate a watcher whose consumer has disappeared.
        // A non-owning FileStream preserves errors without closing the process's stdout handle.
        // https://github.com/dotnet/runtime/blob/main/src/libraries/System.Console/src/System/ConsolePal.Unix.cs
        var handle = OperatingSystem.IsWindows() ? GetStdHandle(-11) : (nint)1;
        return CreateWriter(new SafeFileHandle(handle, ownsHandle: false));
    });

    internal static TextWriter CreateWriter(SafeFileHandle handle)
        => new StreamWriter(new FileStream(handle, FileAccess.Write, bufferSize: 4096, isAsync: false),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public async Task WriteLineAsync(string json, CancellationToken cancellationToken)
    {
        await _writer.Value.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _writer.Value.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_writer.IsValueCreated)
        {
            try
            {
                _writer.Value.Dispose();
            }
            catch (IOException)
            {
                // The command already handled a failed output write. Disposal can retry the flush.
            }
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int standardHandle);
}
