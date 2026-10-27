// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Shared;

namespace Aspire.Cli.Diagnostics;

internal sealed class RollingLogFile : TextWriter
{
    internal const int FileSizeLimit = 10 * 1024 * 1024;
    internal const int BackupFileCount = 2;
    internal static string TruncationMarker { get; } = "[Log entry truncated to the diagnostic file size limit]" + Environment.NewLine;

    private static readonly TimeSpan s_lockTimeout = TimeSpan.FromSeconds(5);
    private static readonly Encoding s_encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly int _fileSizeLimit;

    public RollingLogFile(string path, int fileSizeLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fileSizeLimit, s_encoding.GetByteCount(TruncationMarker) + s_encoding.GetMaxByteCount(1));

        _path = path;
        _fileSizeLimit = fileSizeLimit;

        using var fileLock = FileLock.Acquire(_path + ".lock", s_lockTimeout);
        using var stream = OpenAppendStream();
    }

    public override Encoding Encoding => s_encoding;

    public override async Task WriteLineAsync(string? value)
    {
        var bytes = EncodeEntry(value + Environment.NewLine);

        // The lock covers both rotation and a complete entry, including exception stack traces.
        // Do not retain a log handle between writes: other CLI processes must be able to append
        // and rotate the same file while a long-lived follower is still running.
        using var fileLock = await FileLock.AcquireAsync(_path + ".lock", CancellationToken.None, s_lockTimeout).ConfigureAwait(false);
        using (var stream = OpenAppendStream())
        {
            if (stream.Length + bytes.Length <= _fileSizeLimit)
            {
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                return;
            }
        }

        for (var index = BackupFileCount; index > 0; index--)
        {
            var source = index == 1 ? _path : GetBackupPath(index - 1);
            if (File.Exists(source))
            {
                File.Move(source, GetBackupPath(index), overwrite: true);
            }
        }
        using var newStream = OpenAppendStream();
        await newStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private FileStream OpenAppendStream()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.Write,
            Share = FileShare.Read
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(_path, options);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            stream.Seek(0, SeekOrigin.End);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private ReadOnlyMemory<byte> EncodeEntry(string entry)
    {
        if (s_encoding.GetByteCount(entry) <= _fileSizeLimit)
        {
            return s_encoding.GetBytes(entry);
        }

        // Encoder.Convert stops at a character boundary, so an oversized Unicode message
        // remains valid UTF-8 rather than leaving a partial character at the size limit.
        var bytes = new byte[_fileSizeLimit];
        var marker = s_encoding.GetBytes(TruncationMarker);
        s_encoding.GetEncoder().Convert(entry.AsSpan(), bytes.AsSpan(0, bytes.Length - marker.Length), flush: true,
            out _, out var bytesUsed, out _);
        marker.CopyTo(bytes, bytesUsed);
        return bytes.AsMemory(0, bytesUsed + marker.Length);
    }

    private string GetBackupPath(int index) => Path.ChangeExtension(_path, $"{index}.log");
}
