// System heartbeat monitor for GitHub Actions runner diagnostics.
// Outputs CPU, memory, network, docker stats, and disk space at regular intervals to help
// diagnose runner hangs and disk space issues during tests.
//
// Usage: dotnet tools/scripts/Heartbeat.cs [interval-seconds]
// Default interval: 60 seconds
//
// Example: dotnet tools/scripts/Heartbeat.cs 10

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

var os = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" :
         RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" :
         RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" :
         throw new NotSupportedException("Unsupported OS platform");

const int defaultIntervalSeconds = 60;
var intervalSeconds = args.Length > 0 &&
                      int.TryParse(args[0], out var parsed) &&
                      parsed >= 1
    ? parsed
    : defaultIntervalSeconds;
var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

// Disable output buffering for real-time visibility in CI logs
Console.Out.Flush();

Console.WriteLine($"[{DateTime.UtcNow:O}] HEARTBEAT | Starting system monitor (interval: {intervalSeconds}s)");
Console.WriteLine($"[{DateTime.UtcNow:O}] HEARTBEAT | Platform: {RuntimeInformation.OSDescription}");
Console.Out.Flush();

// For CPU calculation, we need previous values
long prevIdleTime = 0;
long prevTotalTime = 0;

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        var timestamp = DateTime.UtcNow;
        var parts = new List<string> { $"[{timestamp:O}] HEARTBEAT" };

        // CPU Usage
        try
        {
            var cpuInfo = GetCpuUsage(ref prevIdleTime, ref prevTotalTime);
            parts.Add($"CPU: {cpuInfo}");
        }
        catch (Exception ex)
        {
            parts.Add($"CPU: {ex.Message}");
        }

        // Memory Usage
        try
        {
            var memInfo = GetMemoryUsage();
            parts.Add($"Mem: {memInfo}");
        }
        catch (Exception ex)
        {
            parts.Add($"Mem: {ex.Message}");
        }

        // Disk space
        try
        {
            var diskInfo = GetDiskUsage();
            parts.Add($"Disk: {diskInfo}");
        }
        catch (Exception ex)
        {
            parts.Add($"Disk: {ex.Message}");
        }

        // Network Connections
        try
        {
            var netInfo = GetNetworkConnections();
            parts.Add($"Net: {netInfo}");
        }
        catch (Exception ex)
        {
            parts.Add($"Net: {ex.Message}");
        }

        // Docker stats
        try
        {
            var dockerInfo = GetDockerStats();
            parts.Add($"Docker: {dockerInfo}");
        }
        catch (Exception ex)
        {
            parts.Add($"Docker: {ex.Message}");
        }

        // Collect all processes once on Windows to share between DCP and Top metrics
        Process[]? windowsProcesses = null;
        if (os == "Windows")
        {
            try { windowsProcesses = Process.GetProcesses(); } catch { }
        }

        // DCP processes
        try
        {
            var dcpInfo = GetDcpProcesses(windowsProcesses);
            parts.Add($"DCP: {dcpInfo}");
        }
        catch (Exception ex)
        {
            parts.Add($"DCP: {ex.Message}");
        }

        // Top processes
        try
        {
            var topInfo = GetTopProcesses(windowsProcesses);
            parts.Add($"Top: {topInfo}");
        }
        catch (Exception ex)
        {
            parts.Add($"Top: {ex.Message}");
        }

        // Dispose shared Windows process handles
        if (windowsProcesses is not null)
        {
            foreach (var p in windowsProcesses)
            {
                try { p.Dispose(); } catch { }
            }
        }

        Console.WriteLine(string.Join(" | ", parts));
        Console.Out.Flush(); // Ensure output appears immediately in CI logs

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}
catch (OperationCanceledException)
{
    // Expected on shutdown
}

Console.WriteLine($"[{DateTime.UtcNow:O}] HEARTBEAT | Monitor stopped");
Console.Out.Flush();

string GetCpuUsage(ref long prevIdle, ref long prevTotal)
{
    if (os == "Linux")
    {
        // Parse /proc/stat for system-wide CPU usage
        var statLines = File.ReadAllLines("/proc/stat");
        var cpuLine = statLines.FirstOrDefault(l => l.StartsWith("cpu "));
        if (cpuLine != null)
        {
            var values = cpuLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(s => long.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            // user, nice, system, idle, iowait, irq, softirq, steal
            var idle = values[3] + (values.Length > 4 ? values[4] : 0); // idle + iowait
            var total = values.Sum();

            if (prevTotal > 0)
            {
                var idleDelta = idle - prevIdle;
                var totalDelta = total - prevTotal;
                var usage = totalDelta > 0 ? (100.0 * (totalDelta - idleDelta) / totalDelta) : 0;
                prevIdle = idle;
                prevTotal = total;
                return $"{usage:F1}%";
            }

            prevIdle = idle;
            prevTotal = total;
            return "calculating...";
        }
        return $"linux: no cpu line in /proc/stat: {statLines}";
    }
    else if (os == "macOS")
    {
        // Use top command for macOS
        var (success, output, stderr) = RunCommand("top", "-l 1 -n 0");
        if (success)
        {
            var cpuLine = output.Split('\n').FirstOrDefault(l => l.Contains("CPU usage:"));
            if (cpuLine != null)
            {
                // Format: "CPU usage: 10.0% user, 5.0% sys, 85.0% idle"
                var idleMatch = System.Text.RegularExpressions.Regex.Match(cpuLine, @"([\d.]+)%\s*idle");
                if (idleMatch.Success && double.TryParse(idleMatch.Groups[1].Value, out var idle))
                {
                    return $"{100 - idle:F1}%";
                }
            }
            return $"macOS: no CPU usage line in top output: {output}, stderr: {stderr}";
        }
        else
        {
            return $"unavailable: {output}, stderr: {stderr}";
        }
    }
    else if (os == "Windows")
    {
        // Use GetSystemTimes P/Invoke — no external process spawns needed.
        // kernelTime includes idle time, so: total = kernel + user, busy = total - idle.
        if (NativeMethods.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            var idle = idleTime.ToLong();
            var total = kernelTime.ToLong() + userTime.ToLong();

            if (prevTotal > 0)
            {
                var idleDelta = idle - prevIdle;
                var totalDelta = total - prevTotal;
                var usage = totalDelta > 0 ? (100.0 * (totalDelta - idleDelta) / totalDelta) : 0;
                prevIdle = idle;
                prevTotal = total;
                return $"{usage:F1}%";
            }

            prevIdle = idle;
            prevTotal = total;
            return "calculating...";
        }
        else
        {
            return $"GetSystemTimes failed (error {Marshal.GetLastWin32Error()})";
        }
    }

    return "unsupported OS";
}

string GetMemoryUsage()
{
    if (os == "Linux")
    {
        // Parse /proc/meminfo
        var memInfo = File.ReadAllLines("/proc/meminfo")
            .Select(l => l.Split(':'))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => long.Parse(System.Text.RegularExpressions.Regex.Match(p[1], @"\d+").Value, CultureInfo.InvariantCulture));

        var totalKb = memInfo.GetValueOrDefault("MemTotal", 0);
        var availKb = memInfo.GetValueOrDefault("MemAvailable", 0);
        var usedKb = totalKb - availKb;

        var totalGb = totalKb / 1024.0 / 1024.0;
        var usedGb = usedKb / 1024.0 / 1024.0;
        var pct = totalKb > 0 ? (100.0 * usedKb / totalKb) : 0;

        return $"{usedGb:F1}/{totalGb:F1} GB ({pct:F0}%)";
    }
    else if (os == "macOS")
    {
        // Use vm_stat for macOS
        var (success, output, vmstatStdErr) = RunCommand("vm_stat", "");
        if (success)
        {
            var pageSize = 16384L; // Default page size on Apple Silicon, 4096 on Intel
            var lines = output.Split('\n');

            // Try to get actual page size
            var pageSizeLine = lines.FirstOrDefault(l => l.Contains("page size"));
            if (pageSizeLine != null)
            {
                var match = System.Text.RegularExpressions.Regex.Match(pageSizeLine, @"(\d+)");
                if (match.Success)
                {
                    pageSize = long.Parse(match.Value, CultureInfo.InvariantCulture);
                }
            }

            long GetPages(string key) =>
                lines.Where(l => l.StartsWith(key))
                    .Select(l => long.TryParse(System.Text.RegularExpressions.Regex.Match(l, @"\d+").Value, out var v) ? v : 0)
                    .FirstOrDefault();

            var free = GetPages("Pages free:");
            var active = GetPages("Pages active:");
            var inactive = GetPages("Pages inactive:");
            var speculative = GetPages("Pages speculative:");
            var wired = GetPages("Pages wired down:");
            var compressed = GetPages("Pages occupied by compressor:");

            var totalPages = free + active + inactive + speculative + wired + compressed;
            var usedPages = active + wired + compressed;

            var totalGb = totalPages * pageSize / 1024.0 / 1024.0 / 1024.0;
            var usedGb = usedPages * pageSize / 1024.0 / 1024.0 / 1024.0;
            var pct = totalPages > 0 ? (100.0 * usedPages / totalPages) : 0;

            // Get actual total from sysctl
            var (sysctlSuccess, sysctlOutput, sysctlStderr) = RunCommand("sysctl", "-n hw.memsize");
            if (sysctlSuccess && long.TryParse(sysctlOutput.Trim(), out var memBytes))
            {
                totalGb = memBytes / 1024.0 / 1024.0 / 1024.0;
                pct = totalGb > 0 ? (100.0 * usedGb / totalGb) : 0;
            }

            return $"{usedGb:F1}/{totalGb:F1} GB ({pct:F0}%)";
        }
        else
        {
            return $"vm_stat unavailable: {output}, stderr: {vmstatStdErr}";
        }
    }
    else if (os == "Windows")
    {
        // Use GlobalMemoryStatusEx P/Invoke — single syscall, no external process spawns.
        var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (NativeMethods.GlobalMemoryStatusEx(ref memStatus))
        {
            var totalBytes = (long)memStatus.ullTotalPhys;
            var availBytes = (long)memStatus.ullAvailPhys;
            var usedBytes = totalBytes - availBytes;

            var totalGb = totalBytes / 1024.0 / 1024.0 / 1024.0;
            var usedGb = usedBytes / 1024.0 / 1024.0 / 1024.0;
            var pct = totalBytes > 0 ? (100.0 * usedBytes / totalBytes) : 0;

            return $"{usedGb:F1}/{totalGb:F1} GB ({pct:F0}%)";
        }
        else
        {
            return $"GlobalMemoryStatusEx failed (error {Marshal.GetLastWin32Error()})";
        }
    }

    // Fallback to GC info (process memory only)
    var gcInfo = GC.GetGCMemoryInfo();
    var gcUsedMb = gcInfo.HeapSizeBytes / 1024.0 / 1024.0;
    return $"{gcUsedMb:F0} MB (process)";
}

string GetNetworkConnections()
{
    var (success, output, stderr) = RunCommand("netstat", "-an");
    if (success)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var established = lines.Count(l => l.Contains("ESTABLISHED"));
        var listening = lines.Count(l => l.Contains("LISTEN"));
        var timeWait = lines.Count(l => l.Contains("TIME_WAIT"));

        return $"{established} est, {listening} listen, {timeWait} tw";
    }

    return $"netstat unavailable: {output}, stderr: {stderr}";
}

string GetDockerStats()
{
    // Quick check if docker is available
    var (success, output, stderr) = RunCommand("docker", "ps -q", timeoutMs: 5000);
    if (!success)
    {
        return $"unavailable: {output}, stderr: {stderr}";
    }

    var containerIds = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var containerCount = containerIds.Length;

    if (containerCount == 0)
    {
        return "0 containers";
    }

    // Get basic stats for running containers
    var (statsSuccess, statsOutput, statsStderr) = RunCommand("docker", "stats --no-stream --format \"{{.CPUPerc}}|{{.MemPerc}}\"", timeoutMs: 10000);
    if (statsSuccess)
    {
        var stats = statsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var totalCpu = 0.0;
        var totalMem = 0.0;

        foreach (var stat in stats)
        {
            var parts = stat.Split('|');
            if (parts.Length == 2)
            {
                if (double.TryParse(parts[0].TrimEnd('%'), out var cpu))
                {
                    totalCpu += cpu;
                }
                if (double.TryParse(parts[1].TrimEnd('%'), out var mem))
                {
                    totalMem += mem;
                }
            }
        }

        return $"{containerCount} containers (CPU: {totalCpu:F1}%, Mem: {totalMem:F1}%)";
    }
    else
    {
        return $"{containerCount} containers (stats unavailable: {statsOutput}, stderr: {statsStderr})";
    }
}

string GetDcpProcesses(Process[]? sharedProcesses = null)
{
    var dcpProcesses = new List<(string Name, int Pid, double Cpu, double MemMb)>();

    if (os == "Windows")
    {
        // Use Process.GetProcesses() — no external process spawns needed.
        // Use the shared process list when available to avoid a redundant call.
        var processes = sharedProcesses ?? Process.GetProcesses();
        bool shouldDispose = sharedProcesses is null;
        try
        {
            foreach (var proc in processes)
            {
                try
                {
                    if (!proc.ProcessName.StartsWith("dcp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    double cpu = 0;
                    double memMb = 0;
                    var uptimeSec = (DateTime.UtcNow - proc.StartTime.ToUniversalTime()).TotalSeconds;
                    if (uptimeSec > 0)
                    {
                        cpu = Math.Round((proc.TotalProcessorTime.TotalSeconds / uptimeSec) * 100, 1);
                    }

                    memMb = proc.WorkingSet64 / 1024.0 / 1024.0;
                    dcpProcesses.Add((proc.ProcessName, proc.Id, cpu, memMb));
                }
                catch { /* process may have exited or access may be denied */ }
            }
        }
        finally
        {
            if (shouldDispose)
            {
                foreach (var p in processes)
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
    }
    else
    {
        // Use ps on Linux/macOS to find dcp processes
        var (success, output, stderr) = RunCommand("ps", "aux", timeoutMs: 5000);
        if (success)
        {
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                // ps aux format: USER PID %CPU %MEM VSZ RSS TTY STAT START TIME COMMAND
                if (line.Contains("dcp", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 11)
                    {
                        if (int.TryParse(parts[1], out var pid) &&
                            double.TryParse(parts[2], out var cpu) &&
                            double.TryParse(parts[5], out var rssKb))
                        {
                            // Get command name (last part, may contain path)
                            var command = parts[10];
                            var name = Path.GetFileName(command);
                            if (name.StartsWith("dcp", StringComparison.OrdinalIgnoreCase))
                            {
                                dcpProcesses.Add((name, pid, cpu, rssKb / 1024.0));
                            }
                        }
                    }
                }
            }
        }
        else
        {
            return $"unavailable: {output}, stderr: {stderr}";
        }
    }

    if (dcpProcesses.Count == 0)
    {
        return "none";
    }

    var totalCpu = dcpProcesses.Sum(p => p.Cpu);
    var totalMem = dcpProcesses.Sum(p => p.MemMb);
    var processInfo = string.Join(", ", dcpProcesses.Select(p => $"{p.Name}({p.Pid}):{p.Cpu:F1}%/{p.MemMb:F0}MB"));

    return $"{dcpProcesses.Count} procs ({totalCpu:F1}%/{totalMem:F0}MB) [{processInfo}]";
}

string GetTopProcesses(Process[]? sharedProcesses = null)
{
    var topProcesses = new List<(string Name, int Pid, double Cpu, double MemMb)>();

    if (os == "Windows")
    {
        // Use Process.GetProcesses() — no external process spawns needed.
        // Use the shared process list when available to avoid a redundant call.
        var processes = sharedProcesses ?? Process.GetProcesses();
        bool shouldDispose = sharedProcesses is null;
        try
        {
            var now = DateTime.UtcNow;
            var processInfos = processes
                .Select(p =>
                {
                    try
                    {
                        double cpu = 0;
                        var uptimeSec = (now - p.StartTime.ToUniversalTime()).TotalSeconds;
                        if (uptimeSec > 0)
                        {
                            cpu = Math.Round(p.TotalProcessorTime.TotalSeconds / uptimeSec * 100, 1);
                        }

                        var memMb = p.WorkingSet64 / 1024.0 / 1024.0;
                        return ((string Name, int Pid, double Cpu, double MemMb)?)(p.ProcessName, p.Id, cpu, memMb);
                    }
                    catch
                    {
                        // Process may have exited or access may be denied.
                        return null;
                    }
                })
                .Where(p => p.HasValue)
                .Select(p => p!.Value)
                .OrderByDescending(p => p.Cpu)
                .Take(10);

            topProcesses.AddRange(processInfos);
        }
        finally
        {
            if (shouldDispose)
            {
                foreach (var p in processes)
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
    }
    else if (os == "Linux")
    {
        // Use ps on Linux/macOS to find top processes
        var (success, output, stderr) = RunCommand("ps", "aux --sort=-%cpu", timeoutMs: 5000);
        if (success)
        {
            var processLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(10);
            foreach (var line in processLines)
            {
                // ps aux format: USER PID %CPU %MEM VSZ RSS TTY STAT START TIME COMMAND
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 11)
                {
                    if (int.TryParse(parts[1], out var pid) &&
                        double.TryParse(parts[2], out var cpu) &&
                        double.TryParse(parts[5], out var rssKb))
                    {
                        // Get command name (last part, may contain path)
                        var command = parts[10];
                        var name = Path.GetFileName(command);
                        topProcesses.Add((name, pid, cpu, rssKb / 1024.0));
                    }
                }
            }
        }
        else
        {
            return $"unavailable: {output}, stderr: {stderr}";
        }
    }
    else if (os == "macOS")
    {
        // Use ps on macOS to find top processes
        var (success, output, stderr) = RunCommand("ps", "aux -r", timeoutMs: 5000);
        if (success)
        {
            var processLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(10);
            foreach (var line in processLines)
            {
                // ps aux format: USER PID %CPU %MEM VSZ RSS TTY STAT STARTED TIME COMMAND
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 11)
                {
                    if (int.TryParse(parts[1], out var pid) &&
                        double.TryParse(parts[2], out var cpu) &&
                        double.TryParse(parts[5], out var rssKb))
                    {
                        // Get command name (last part, may contain path)
                        var command = parts[10];
                        var name = Path.GetFileName(command);
                        topProcesses.Add((name, pid, cpu, rssKb / 1024.0));
                    }
                }
            }
        }
        else
        {
            return $"unavailable: {output}, stderr: {stderr}";
        }
    }

    if (topProcesses.Count == 0)
    {
        return "none";
    }

    return string.Join(", ", topProcesses.Select(p => $"{p.Name} (PID: {p.Pid}, CPU: {p.Cpu:F1}%, Mem: {p.MemMb:F1} MB)"));
}

string GetDiskUsage()
{
    var diskInfo = new List<string>();

    if (os == "Linux" || os == "macOS")
    {
        // Use df command on Linux/macOS with -P for POSIX-compliant output format
        // This ensures consistent columns across both OSes: Filesystem Size Used Avail Capacity Mounted
        var (success, output, stderr) = RunCommand("df", "-P -h", timeoutMs: 5000);
        if (success)
        {
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines.Skip(1)) // Skip header
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // df -P -h format: Filesystem Size Used Avail Capacity Mounted
                if (parts.Length >= 6)
                {
                    var mountPoint = parts[^1]; // Last column is mount point
                    var usePercent = parts[^2]; // Second to last is capacity percentage
                    var avail = parts[^3];      // Third to last is available
                    var used = parts[^4];       // Fourth to last is used
                    var size = parts[^5];       // Fifth to last is size

                    // Only report key mount points
                    if (mountPoint is "/" or "/home" or "/tmp" ||
                        mountPoint.StartsWith("/home/") ||
                        mountPoint.StartsWith("/mnt/") ||
                        mountPoint.StartsWith("/Volumes/"))
                    {
                        diskInfo.Add($"{mountPoint}:{used}/{size}({usePercent})");
                    }
                }
            }
        }
        else
        {
            return $"df unavailable: {output}, stderr: {stderr}";
        }
    }
    else if (os == "Windows")
    {
        // Use DriveInfo.GetDrives() — no external process spawns needed.
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)
            {
                continue;
            }

            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                var totalBytes = drive.TotalSize;
                var freeBytes = drive.AvailableFreeSpace;
                var usedBytes = totalBytes - freeBytes;

                var usedGb = usedBytes / 1024.0 / 1024.0 / 1024.0;
                var totalGb = totalBytes / 1024.0 / 1024.0 / 1024.0;
                var usePct = totalBytes > 0 ? (100.0 * usedBytes / totalBytes) : 0;

                // Use drive name without trailing backslash for brevity (e.g. "C:")
                var driveName = drive.Name.TrimEnd('\\');
                diskInfo.Add($"{driveName}:{usedGb:F1}/{totalGb:F1}GB({usePct:F0}%)");
            }
            catch { /* drive may become unavailable between IsReady check and property access */ }
        }
    }

    if (diskInfo.Count == 0)
    {
        return "no disk info";
    }

    return string.Join(", ", diskInfo);
}

(bool Success, string Output, string StdErr) RunCommand(string fileName, string arguments, int timeoutMs = 3000)
{
    try
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var output = new System.Text.StringBuilder();
        var error = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                output.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                error.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();

        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                try { process.Kill(); } catch { }
            }
            return (false, "timeout", "");
        }

        // Ensure async output reading completes
        process.WaitForExit();

        return (process.ExitCode == 0, output.ToString(), error.ToString());
    }
    catch (Exception ex)
    {
        return (false, ex.Message, "");
    }
}

// Windows P/Invoke declarations — used instead of spawning PowerShell processes.

[StructLayout(LayoutKind.Sequential)]
struct FILETIME
{
    public uint dwLowDateTime;
    public uint dwHighDateTime;

    /// <summary>Converts the FILETIME to a 64-bit integer (100-nanosecond intervals since 1601-01-01).</summary>
    public readonly long ToLong() => ((long)dwHighDateTime << 32) | (long)dwLowDateTime;
}

[StructLayout(LayoutKind.Sequential)]
struct MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;
}

static partial class NativeMethods
{
    /// <summary>
    /// Retrieves system timing information: idle, kernel (includes idle), and user CPU times.
    /// </summary>
#pragma warning disable SYSLIB1054 // LibraryImport requires AllowUnsafeBlocks which is not available in a script file
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(
        out FILETIME lpIdleTime,
        out FILETIME lpKernelTime,
        out FILETIME lpUserTime);

    /// <summary>
    /// Retrieves information about the system's current usage of both physical and virtual memory.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
#pragma warning restore SYSLIB1054
}
