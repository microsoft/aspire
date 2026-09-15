// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Default <see cref="ITelemetryHookInstaller"/> that materializes the hook scripts embedded in the
/// CLI assembly to <c>~/.aspire/hooks</c>.
/// </summary>
internal sealed class TelemetryHookInstaller : ITelemetryHookInstaller
{
    // LogicalNames declared in Aspire.Cli.csproj.
    private const string ShellResourceName = "track-telemetry.sh";
    private const string PowerShellResourceName = "track-telemetry.ps1";

    private const string HooksDirectoryName = "hooks";

    // UTF-8 without a BOM: the shell script must not begin with a BOM or the shebang is ignored,
    // and the PowerShell script is ASCII so a BOM is unnecessary.
    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly CliExecutionContext _executionContext;
    private readonly ILogger<TelemetryHookInstaller> _logger;

    public TelemetryHookInstaller(CliExecutionContext executionContext, ILogger<TelemetryHookInstaller> logger)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(logger);
        _executionContext = executionContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TelemetryHookScripts> EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        var hooksDirectory = Path.Combine(_executionContext.AspireHomeDirectory.FullName, HooksDirectoryName);
        Directory.CreateDirectory(hooksDirectory);

        var shellPath = Path.Combine(hooksDirectory, ShellResourceName);
        var powerShellPath = Path.Combine(hooksDirectory, PowerShellResourceName);

        // The shell script must use LF endings even on Windows because the agent may run it under a
        // POSIX shell (WSL/Git bash); CRLF would surface as `$'\r': command not found` errors.
        var shellContent = NormalizeToLf(ReadEmbeddedText(ShellResourceName));
        var powerShellContent = ReadEmbeddedText(PowerShellResourceName);

        await WriteFileIfChangedAsync(shellPath, shellContent, cancellationToken);
        await WriteFileIfChangedAsync(powerShellPath, powerShellContent, cancellationToken);

        // Ensure the shell script is executable so a `bash <path>` (or direct exec) hook entry works.
        // Spawning `chmod` would add PATH/shell failure modes, so use the platform API directly.
        TrySetExecutable(shellPath);

        return new TelemetryHookScripts(shellPath, powerShellPath);
    }

    private static string NormalizeToLf(string content)
        => content.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string ReadEmbeddedText(string resourceName)
    {
        using var stream = typeof(TelemetryHookInstaller).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded telemetry hook script '{resourceName}' was not found in the CLI assembly.");
        using var reader = new StreamReader(stream, s_utf8NoBom);
        return reader.ReadToEnd();
    }

    private async Task WriteFileIfChangedAsync(string path, string content, CancellationToken cancellationToken)
    {
        // Skip the write when the content already matches so a running hook isn't disturbed and the
        // file mtime stays stable across repeated `agent init` runs.
        if (File.Exists(path))
        {
            try
            {
                var existing = await File.ReadAllTextAsync(path, s_utf8NoBom, cancellationToken);
                if (string.Equals(existing, content, StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not read existing telemetry hook script at {Path}; it will be rewritten.", path);
            }
        }

        // Write to a sibling temp file then atomically move into place so a concurrently executing
        // hook never observes a partially written script.
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(tempPath, content, s_utf8NoBom, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }

    private void TrySetExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Non-fatal: a `bash <path>` hook entry still runs the script without the executable bit.
            _logger.LogDebug(ex, "Could not set executable bit on telemetry hook script at {Path}.", path);
        }
    }
}
