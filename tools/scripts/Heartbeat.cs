// System heartbeat monitor for GitHub Actions runner diagnostics.
// Outputs CPU, memory, network, and docker stats at regular intervals to help
// diagnose runner hangs during tests.
//
// Usage: dotnet tools/scripts/Heartbeat.cs [interval-seconds]
// Default interval: 5 seconds
//
// Example: dotnet tools/scripts/Heartbeat.cs 10

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

var intervalSeconds = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 5;
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
var prevCpuTime = TimeSpan.Zero;
var prevTime = DateTime.UtcNow;
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
            var cpuInfo = GetCpuUsage(ref prevIdleTime, ref prevTotalTime, ref prevCpuTime, ref prevTime);
            parts.Add($"CPU: {cpuInfo}");
        }
        catch
        {
            parts.Add("CPU: N/A");
        }

        // Memory Usage
        try
        {
            var memInfo = GetMemoryUsage();
            parts.Add($"Mem: {memInfo}");
        }
        catch
        {
            parts.Add("Mem: N/A");
        }

        // Network Connections
        try
        {
            var netInfo = GetNetworkConnections();
            parts.Add($"Net: {netInfo}");
        }
        catch
        {
            parts.Add("Net: N/A");
        }

        // Docker stats
        try
        {
            var dockerInfo = GetDockerStats();
            parts.Add($"Docker: {dockerInfo}");
        }
        catch
        {
            parts.Add("Docker: N/A");
        }

        // DCP processes
        try
        {
            var dcpInfo = GetDcpProcesses();
            parts.Add($"DCP: {dcpInfo}");
        }
        catch
        {
            parts.Add("DCP: N/A");
        }

        // Top processes
        try
        {
            var topInfo = GetTopProcesses();
            parts.Add($"Top: {topInfo}");
        }
        catch
        {
            parts.Add("Top: N/A");
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

string GetCpuUsage(ref long prevIdle, ref long prevTotal, ref TimeSpan prevCpu, ref DateTime prevDateTime)
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
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
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        // Use top command for macOS
        var (success, output) = RunCommand("top", "-l 1 -n 0");
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
        }
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        // Use PowerShell for Windows (wmic is deprecated)
        // Get average CPU load across all processors
        var (success, output) = RunCommand("powershell", "-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average\"");
        if (success)
        {
            var trimmed = output.Trim();
            if (double.TryParse(trimmed, out var loadPercentage))
            {
                return $"{loadPercentage:F1}%";
            }
        }
    }

    return "N/A";
}

string GetMemoryUsage()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
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
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        // Use vm_stat for macOS
        var (success, output) = RunCommand("vm_stat", "");
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
            var (sysctlSuccess, sysctlOutput) = RunCommand("sysctl", "-n hw.memsize");
            if (sysctlSuccess && long.TryParse(sysctlOutput.Trim(), out var memBytes))
            {
                totalGb = memBytes / 1024.0 / 1024.0 / 1024.0;
                pct = totalGb > 0 ? (100.0 * usedGb / totalGb) : 0;
            }

            return $"{usedGb:F1}/{totalGb:F1} GB ({pct:F0}%)";
        }
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        // Use PowerShell for Windows (wmic is deprecated)
        var (success, output) = RunCommand("powershell", "-NoProfile -NonInteractive -Command \"$os = Get-CimInstance Win32_OperatingSystem; Write-Host \\\"$($os.FreePhysicalMemory),$($os.TotalVisibleMemorySize)\\\"\"");
        if (success)
        {
            var parts = output.Trim().Split(',');
            if (parts.Length == 2 &&
                long.TryParse(parts[0].Trim(), out var freeKb) &&
                long.TryParse(parts[1].Trim(), out var totalKb))
            {
                var usedKb = totalKb - freeKb;
                var totalGb = totalKb / 1024.0 / 1024.0;
                var usedGb = usedKb / 1024.0 / 1024.0;
                var pct = totalKb > 0 ? (100.0 * usedKb / totalKb) : 0;

                return $"{usedGb:F1}/{totalGb:F1} GB ({pct:F0}%)";
            }
        }
    }

    // Fallback to GC info (process memory only)
    var gcInfo = GC.GetGCMemoryInfo();
    var gcUsedMb = gcInfo.HeapSizeBytes / 1024.0 / 1024.0;
    return $"{gcUsedMb:F0} MB (process)";
}

string GetNetworkConnections()
{
    var (success, output) = RunCommand("netstat", "-an");
    if (success)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var established = lines.Count(l => l.Contains("ESTABLISHED"));
        var listening = lines.Count(l => l.Contains("LISTEN"));
        var timeWait = lines.Count(l => l.Contains("TIME_WAIT"));

        return $"{established} est, {listening} listen, {timeWait} tw";
    }

    return "N/A";
}

string GetDockerStats()
{
    // Quick check if docker is available
    var (success, output) = RunCommand("docker", "ps -q", timeoutMs: 5000);
    if (!success)
    {
        return "unavailable";
    }

    var containerIds = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var containerCount = containerIds.Length;

    if (containerCount == 0)
    {
        return "0 containers";
    }

    // Get basic stats for running containers
    var (statsSuccess, statsOutput) = RunCommand("docker", "stats --no-stream --format \"{{.CPUPerc}}|{{.MemPerc}}\"", timeoutMs: 10000);
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

    return $"{containerCount} containers";
}

string GetDcpProcesses()
{
    var dcpProcesses = new List<(string Name, int Pid, double Cpu, double MemMb)>();

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        // Use PowerShell to find dcp processes on Windows (wmic is deprecated)
        // Use pipe delimiter to avoid issues with commas in process names
        var (success, output) = RunCommand("powershell", "-NoProfile -NonInteractive -Command \"Get-Process -Name 'dcp*' -ErrorAction SilentlyContinue | Select-Object -Property ProcessName, Id, @{Name='AvgCpuPct';Expression={$uptimeSec = (New-TimeSpan -Start $_.StartTime -End (Get-Date)).TotalSeconds; if ($uptimeSec -gt 0) { [math]::Round(($_.CPU / $uptimeSec) * 100, 1) } else { 0 } }}, WorkingSet64 | ForEach-Object { '{0}|{1}|{2}|{3}' -f $_.ProcessName, $_.Id, $_.AvgCpuPct, $_.WorkingSet64 }\"", timeoutMs: 5000);
        if (success)
        {
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split('|', StringSplitOptions.TrimEntries);

                if (parts.Length >= 3 &&
                    int.TryParse(parts[1], out var pid) &&
                    double.TryParse(parts[2], out var cpu) &&
                    long.TryParse(parts[3], out var workingSet))
                {
                    var name = parts[0];
                    dcpProcesses.Add((name, pid, cpu, workingSet / 1024.0 / 1024.0));
                }
            }
        }
        else
        {
            return "unavailable";
        }
    }
    else
    {
        // Use ps on Linux/macOS to find dcp processes
        var (success, output) = RunCommand("ps", "aux", timeoutMs: 5000);
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
            return "unavailable";
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

string GetTopProcesses()
{
    var topProcesses = new List<(string Name, int Pid, double Cpu, double MemMb)>();

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        // Use PowerShell to find top processes on Windows (wmic is deprecated)
        // Use pipe delimiter to avoid issues with commas in process names
        var (success, output) = RunCommand("powershell", "-NoProfile -NonInteractive -Command \"Get-Process | Select-Object -Property ProcessName, Id, @{Name='AvgCpuPct';Expression={$uptimeSec = (New-TimeSpan -Start $_.StartTime -End (Get-Date)).TotalSeconds; if ($uptimeSec -gt 0) { [math]::Round(($_.CPU / $uptimeSec) * 100, 1) } else { 0 } }}, WorkingSet64 | Sort-Object AvgCpuPct -Descending | Select-Object -First 10 | ForEach-Object { '{0}|{1}|{2}|{3}' -f $_.ProcessName, $_.Id, $_.AvgCpuPct, $_.WorkingSet64 }\"", timeoutMs: 5000);
        if (success)
        {
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split('|', StringSplitOptions.TrimEntries);

                if (parts.Length >= 3 &&
                    int.TryParse(parts[1], out var pid) &&
                    double.TryParse(parts[2], out var cpu) &&
                    long.TryParse(parts[3], out var workingSet))
                {
                    var name = parts[0];
                    topProcesses.Add((name, pid, cpu, workingSet / 1024.0 / 1024.0));
                }
            }
        }
        else
        {
            return "unavailable";
        }
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        // Use ps on Linux/macOS to find top processes
        var (success, output) = RunCommand("ps", "aux --sort=-%cpu", timeoutMs: 5000);
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
            return "unavailable";
        }
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        // Use ps on macOS to find top processes
        var (success, output) = RunCommand("ps", "aux -r", timeoutMs: 5000);
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
            return "unavailable";
        }
    }

    if (topProcesses.Count == 0)
    {
        return "none";
    }

    return string.Join(", ", topProcesses.Select(p => $"{p.Name} (PID: {p.Pid}, CPU: {p.Cpu:F1}%, Mem: {p.MemMb:F1} MB)"));
}

(bool Success, string Output) RunCommand(string fileName, string arguments, int timeoutMs = 3000)
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
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                output.AppendLine(e.Data);
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
            return (false, "timeout");
        }

        // Ensure async output reading completes
        process.WaitForExit();

        return (process.ExitCode == 0, output.ToString());
    }
    catch (Exception ex)
    {
        return (false, ex.Message);
    }
}
