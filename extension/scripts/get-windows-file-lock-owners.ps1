param(
    [Parameter(Mandatory)][string] $FilePath,
    [Parameter(Mandatory)][string] $RunRoot,
    [Parameter(Mandatory)][int] $TimeoutMs
)

# Query only the caller's exact file under its owned run root. Compilation and input
# validation consume the observation budget; the caller separately bounds process startup.
# Exit 0 means a JSON report was emitted, not that cleanup succeeded or no lock exists.
# Neither the foreground code nor a timed-out background query writes files or stops owners.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Initialize-LockOwnerQuery {
    if ('AspireRepro.LockOwners' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AspireRepro;

public static class LockOwners
{
    public sealed class Owner
    {
        public uint Pid { get; init; }
        public string StartTimeFileTimeUtc { get; init; }
        public string StartedUtc { get; init; }
        public string ApplicationName { get; init; }
    }

    public sealed class Observation
    {
        public string File { get; init; }
        public string Status { get; set; } = "query-error";
        public string ObservedUtc { get; set; }
        public string Stage { get; set; } = "path-check";
        public uint? NativeCode { get; set; }
        public uint? SessionEndCode { get; set; }
        public string ErrorType { get; set; }
        public Owner[] Owners { get; set; } = [];
    }

    public sealed class Capture
    {
        public Task Worker { get; internal set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public ConcurrentQueue<Observation> Observations { get; } = new();
    }

    public static Capture Begin(string root, string[] files)
    {
        var capture = new Capture();
        // Native queries have no cancellable timeout. A background task cannot hold up
        // pwsh exit; cancellation only prevents subsequent queries, never touches owners.
        capture.Worker = Task.Run(() =>
        {
            foreach (var file in files)
            {
                if (capture.Cancellation.IsCancellationRequested) { break; }
                capture.Observations.Enqueue(Query(root, file, capture.Cancellation.Token));
            }
        });
        // A timed-out caller will never await this worker again. Observe unexpected
        // late faults without logging, mutating a snapshot, or changing test outcomes.
        _ = capture.Worker.ContinueWith(task => { _ = task.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return capture;
    }

    private static Observation Query(string root, string file, CancellationToken cancellation)
    {
        var result = new Observation { File = file };
        try
        {
            if (!Path.IsPathFullyQualified(file) ||
                !Path.GetFullPath(file).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                result.Status = "out-of-scope";
                return result;
            }
            var current = root;
            var segments = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar);
            // Do not follow junctions/symlinks into an unrelated directory. Check from
            // the known root outward, not by opening a potentially redirected final path.
            if ((System.IO.File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                result.Status = "reparse-point-rejected";
                return result;
            }
            for (var i = 0; i < segments.Length; i++)
            {
                if (segments[i].EndsWith(' ') || segments[i].EndsWith('.'))
                {
                    result.Status = "ambiguous-path-rejected";
                    return result;
                }
                current = Path.Combine(current, segments[i]);
                var attributes = System.IO.File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    result.Status = "reparse-point-rejected";
                    return result;
                }
                if (i == segments.Length - 1 && (attributes & FileAttributes.Directory) != 0)
                {
                    result.Status = "not-a-file";
                    return result;
                }
            }
            result.Stage = "RmStartSession";
            var code = RmStartSession(out var session, 0, new StringBuilder(33));
            result.NativeCode = code;
            if (code != 0) { return result; }
            try
            {
                if (cancellation.IsCancellationRequested) { result.Status = "cancelled-between-queries"; return result; }
                result.Stage = "RmRegisterResources";
                // One file per session makes each owner attribution unambiguous.
                code = RmRegisterResources(session, 1, [file], 0, IntPtr.Zero, 0, IntPtr.Zero);
                result.NativeCode = code;
                if (code != 0) { return result; }
                uint count = 0;
                ProcessInfo[] processes = null;
                result.Stage = "RmGetList";
                // ERROR_MORE_DATA can recur as processes enter/leave; bound both the
                // resize count and allocation. This is enumeration, not a test retry.
                // https://learn.microsoft.com/windows/win32/api/restartmanager/nf-restartmanager-rmgetlist
                for (var read = 0; read < 3; read++)
                {
                    if (cancellation.IsCancellationRequested) { result.Status = "cancelled-between-queries"; return result; }
                    code = RmGetList(session, out var needed, ref count, processes, out _);
                    result.NativeCode = code;
                    if (code == 234)
                    {
                        if (needed == 0 || needed > 256) { result.Status = "owner-buffer-limit"; return result; }
                        count = needed;
                        processes = new ProcessInfo[count];
                        continue;
                    }
                    if (code != 0) { return result; }
                    var owners = new List<Owner>();
                    for (var i = 0; i < count; i++)
                    {
                        var process = processes[i];
                        var started = ((long)process.Process.StartHigh << 32) | process.Process.StartLow;
                        // RM_UNIQUE_PROCESS is PID + creation FILETIME, not PID alone.
                        // No command line, environment, system-wide process sweep or handle mutation.
                        // https://learn.microsoft.com/windows/win32/api/restartmanager/ns-restartmanager-rm_unique_process
                        owners.Add(new Owner
                        {
                            Pid = process.Process.Pid,
                            StartTimeFileTimeUtc = started.ToString(CultureInfo.InvariantCulture),
                            StartedUtc = DateTime.FromFileTimeUtc(started).ToString("O"),
                            ApplicationName = process.ApplicationName
                        });
                    }
                    result.Owners = owners.ToArray();
                    result.Status = count == 0 ? "no-owners-observed" : "owners-observed";
                    return result;
                }
                result.Status = "owner-list-kept-changing";
            }
            finally
            {
                // Ends only our query session. Never shut down/restart an application.
                result.SessionEndCode = RmEndSession(session);
                if (result.SessionEndCode != 0) { result.Status = "session-end-error"; }
            }
        }
        catch (FileNotFoundException) { result.Status = "file-missing"; }
        catch (DirectoryNotFoundException) { result.Status = "file-missing"; }
        catch (Exception error) { result.Status = "query-error"; result.ErrorType = error.GetType().FullName; }
        finally { result.ObservedUtc = DateTime.UtcNow.ToString("O"); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        public uint Pid;
        public uint StartLow;
        public uint StartHigh;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ApplicationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public int ApplicationType;
        public uint ApplicationStatus;
        public uint SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RmRegisterResources(uint session, uint fileCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] files,
        uint applicationCount, IntPtr applications, uint serviceCount, IntPtr services);
    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern uint RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] ProcessInfo[] processes, out uint rebootReasons);
    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern uint RmEndSession(uint session);
}
'@
}

function Wait-LockOwnerQuery($Query, [string[]] $Files, [int] $TimeoutMilliseconds) {
    $status = 'completed'
    try {
        if (-not $Query.Worker.Wait($TimeoutMilliseconds)) {
            $status = 'timeout'
            $Query.Cancellation.Cancel()
        }
    }
    catch {
        $status = 'query-task-error'
        $Query.Cancellation.Cancel()
    }
    $observations = @($Query.Observations.ToArray())
    foreach ($file in $Files) {
        if ($file -notin $observations.File) {
            $missingStatus = if ($status -eq 'completed') { 'missing-query-result' } else { $status }
            $observations += [ordered]@{ File = $file; Status = $missingStatus; Owners = @(); SessionEndCode = $null }
        }
    }
    if ($status -eq 'completed' -and @($observations | Where-Object { $_.Status -notin @('owners-observed', 'no-owners-observed') }).Count -gt 0) {
        $status = 'completed-with-query-errors'
    }
    return [ordered]@{
        status = $status
        backgroundQueryStillRunning = -not $Query.Worker.IsCompleted
        observations = $observations
    }
}

$report = & {
    $entered = [DateTimeOffset]::UtcNow
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $result = [ordered]@{
        schemaVersion = 1
        capturedUtc = $entered.UtcDateTime.ToString('o')
        status = 'diagnostic-error'
        file = $FilePath
        runRoot = $RunRoot
        timeoutMilliseconds = $TimeoutMs
        queryBudgetMilliseconds = 0
        interpretation = 'Point-in-time Restart Manager resource-user observation, not proof of which handle blocked deletion. Empty lists, missing files, errors and timeouts do not establish that the original lock was absent or unrelated.'
        query = $null
        errorType = $null
    }
    try {
        if (-not $IsWindows) {
            $result.status = 'unsupported-platform'
            return $result
        }
        if ($TimeoutMs -le 0 -or $TimeoutMs -gt 30000) {
            $result.status = 'invalid-timeout'
            return $result
        }
        # Scope validation precedes any filesystem or native query. Explicit arguments need
        # no log parsing, but still cannot authorize UNC/device paths, streams or traversal.
        if ($FilePath -notmatch '^[A-Za-z]:\\' -or $RunRoot -notmatch '^[A-Za-z]:\\' -or
            $FilePath.Substring(2) -match '[:*?"<>|\x00-\x1f]|(^|\\)\.\.?($|\\)' -or
            $RunRoot.Substring(2) -match '[:*?"<>|\x00-\x1f]|(^|\\)\.\.?($|\\)') {
            $result.status = 'out-of-scope'
            return $result
        }
        $root = [IO.Path]::GetFullPath($RunRoot).TrimEnd('\')
        $file = [IO.Path]::GetFullPath($FilePath)
        if ($root -match '^[A-Za-z]:$' -or -not $file.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
            $result.status = 'out-of-scope'
            return $result
        }
        $result.file = $file
        $result.runRoot = $root
        Initialize-LockOwnerQuery
        # Compilation and input handling consume the same deadline; use monotonic elapsed
        # time so clock corrections cannot turn the native wait into an unbounded budget.
        $remaining = [int][Math]::Floor($TimeoutMs - $watch.Elapsed.TotalMilliseconds)
        if ($remaining -le 0) {
            $result.status = 'deadline-expired'
            return $result
        }
        $result.queryBudgetMilliseconds = $remaining
        $query = [AspireRepro.LockOwners]::Begin($root, @($file))
        $result.query = Wait-LockOwnerQuery $query @($file) $remaining
        $result.status = $result.query.status
    }
    catch {
        # No command lines, environment values or exception messages in the report.
        $result.errorType = $_.Exception.GetType().FullName
    }
    return $result
}
$report | ConvertTo-Json -Depth 20 -Compress | Write-Output
exit 0
