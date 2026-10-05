import * as fs from 'fs';
import * as path from 'path';
import { runProcess } from './process';
import { getRunRoot } from './paths';

export interface ProcessEntry {
    pid: number;
    commandLine: string;
    arguments: string[];
}

let macProcessArgumentsHelper: Promise<string> | undefined;

export async function getProcessEntry(pid: number): Promise<ProcessEntry | undefined> {
    if (!Number.isInteger(pid) || pid <= 0) {
        return undefined;
    }

    if (process.platform === 'linux') {
        return readLinuxProcessEntry(pid);
    }

    if (process.platform === 'darwin') {
        return await readMacProcessEntry(pid);
    }

    const entries = await listWindowsProcessEntries();
    return entries.find(entry => entry.pid === pid);
}

export async function listProcessEntries(commandLineHint?: string): Promise<ProcessEntry[]> {
    if (process.platform === 'linux') {
        return listLinuxProcessEntries(commandLineHint);
    }

    if (process.platform === 'darwin') {
        return await listMacProcessEntries(commandLineHint);
    }

    const entries = await listWindowsProcessEntries();
    return commandLineHint === undefined
        ? entries
        : entries.filter(entry => entry.arguments.includes(commandLineHint));
}

function listLinuxProcessEntries(commandLineHint: string | undefined): ProcessEntry[] {
    return fs.readdirSync('/proc', { withFileTypes: true })
        .filter(entry => entry.isDirectory() && /^\d+$/.test(entry.name))
        .flatMap(entry => {
            const processEntry = readLinuxProcessEntry(Number.parseInt(entry.name, 10));
            if (!processEntry || (commandLineHint !== undefined && !processEntry.arguments.includes(commandLineHint))) {
                return [];
            }

            return [processEntry];
        });
}

function readLinuxProcessEntry(pid: number): ProcessEntry | undefined {
    try {
        const argumentsList = parseNullSeparatedArguments(fs.readFileSync(`/proc/${pid}/cmdline`, 'utf8'));
        return argumentsList.length === 0
            ? undefined
            : {
                pid,
                commandLine: formatArgumentsForDiagnostics(argumentsList),
                arguments: argumentsList,
            };
    }
    catch (error) {
        if (isProcessLookupError(error)) {
            return undefined;
        }

        throw error;
    }
}

async function listMacProcessEntries(commandLineHint: string | undefined): Promise<ProcessEntry[]> {
    // macOS ps joins argv with spaces and does not preserve argument boundaries. Use it only
    // to find a small candidate PID set, then read each candidate's real argv via KERN_PROCARGS2.
    const result = await runProcess('/bin/ps', ['-A', '-w', '-w', '-o', 'pid=,args='], { timeoutMs: 30000 });
    const candidates = result.stdout
        .split('\n')
        .flatMap(line => {
            const match = /^\s*(\d+)\s+(.*)$/.exec(line);
            if (!match || (commandLineHint !== undefined && !match[2].includes(commandLineHint))) {
                return [];
            }

            return [{ pid: Number.parseInt(match[1], 10), commandLine: match[2] }];
        });

    const entries: ProcessEntry[] = [];
    for (const candidate of candidates) {
        const entry = await readMacProcessEntry(candidate.pid, candidate.commandLine);
        if (entry && (commandLineHint === undefined || entry.arguments.includes(commandLineHint))) {
            entries.push(entry);
        }
    }

    return entries;
}

async function readMacProcessEntry(pid: number, commandLine?: string): Promise<ProcessEntry | undefined> {
    const helperPath = await getMacProcessArgumentsHelper();
    const result = await runProcess(helperPath, [String(pid)], {
        timeoutMs: 30000,
        rejectOnNonZeroExit: false,
    });
    if (result.exitCode === 3) {
        return undefined;
    }
    if (result.exitCode !== 0) {
        throw new Error(`Failed to read argv for macOS process ${pid} (exit code ${result.exitCode}).\nstderr:\n${result.stderr}`);
    }

    const argumentsList = parseNullSeparatedArguments(result.stdout);
    return argumentsList.length === 0
        ? undefined
        : {
            pid,
            commandLine: commandLine ?? formatArgumentsForDiagnostics(argumentsList),
            arguments: argumentsList,
        };
}

function getMacProcessArgumentsHelper(): Promise<string> {
    macProcessArgumentsHelper ??= createMacProcessArgumentsHelper();
    return macProcessArgumentsHelper;
}

async function createMacProcessArgumentsHelper(): Promise<string> {
    const runRoot = getRunRoot();
    if (!runRoot) {
        throw new Error('ASPIRE_EXTENSION_E2E_RUN_ROOT is required to inspect macOS process arguments.');
    }

    const helperDirectory = path.join(runRoot, 'process-arguments-helper');
    const sourcePath = path.join(helperDirectory, 'read-process-arguments.c');
    const helperPath = path.join(helperDirectory, 'read-process-arguments');
    fs.mkdirSync(helperDirectory, { recursive: true });
    fs.writeFileSync(sourcePath, macProcessArgumentsSource);

    await runProcess('/usr/bin/xcrun', [
        'clang',
        '-std=c11',
        '-O2',
        sourcePath,
        '-o',
        helperPath,
    ], { timeoutMs: 60000 });

    return helperPath;
}

async function listWindowsProcessEntries(): Promise<ProcessEntry[]> {
    // CommandLineToArgvW applies the Windows command-line quoting rules used by native
    // processes instead of approximating them with a whitespace split.
    // https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-commandlinetoargvw
    const result = await runProcess('powershell.exe', [
        '-NoProfile',
        '-NonInteractive',
        '-Command',
        windowsProcessArgumentsScript,
    ], { timeoutMs: 60000 });

    const parsed = JSON.parse(result.stdout) as WindowsProcessEntry | WindowsProcessEntry[];
    const entries = Array.isArray(parsed) ? parsed : [parsed];
    return entries.map(entry => ({
        pid: entry.ProcessId,
        commandLine: entry.CommandLine ?? '',
        arguments: Array.isArray(entry.Arguments)
            ? entry.Arguments
            : entry.Arguments === null
                ? []
                : [entry.Arguments],
    }));
}

function parseNullSeparatedArguments(value: string): string[] {
    const argumentsList = value.split('\0');
    if (argumentsList.at(-1) === '') {
        argumentsList.pop();
    }

    return argumentsList;
}

function formatArgumentsForDiagnostics(argumentsList: readonly string[]): string {
    return JSON.stringify(argumentsList);
}

function isProcessLookupError(error: unknown): boolean {
    return error instanceof Error &&
        'code' in error &&
        (error.code === 'ENOENT' || error.code === 'ESRCH' || error.code === 'EACCES' || error.code === 'EPERM');
}

interface WindowsProcessEntry {
    ProcessId: number;
    CommandLine: string | null;
    Arguments: string[] | string | null;
}

const windowsProcessArgumentsScript = String.raw`
$source = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class AspireE2ECommandLine
{
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static string[] Parse(string commandLine)
    {
        int argumentCount;
        IntPtr argumentPointers = CommandLineToArgvW(commandLine, out argumentCount);
        if (argumentPointers == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var arguments = new string[argumentCount];
            for (var index = 0; index < argumentCount; index++)
            {
                IntPtr argumentPointer = Marshal.ReadIntPtr(argumentPointers, index * IntPtr.Size);
                arguments[index] = Marshal.PtrToStringUni(argumentPointer) ?? string.Empty;
            }

            return arguments;
        }
        finally
        {
            LocalFree(argumentPointers);
        }
    }
}
'@

Add-Type -TypeDefinition $source
Get-CimInstance Win32_Process |
    ForEach-Object {
        [PSCustomObject]@{
            ProcessId = [int]$_.ProcessId
            CommandLine = $_.CommandLine
            Arguments = if ($null -eq $_.CommandLine) { @() } else { [AspireE2ECommandLine]::Parse($_.CommandLine) }
        }
    } |
    ConvertTo-Json -Compress -Depth 4
`;

// KERN_PROCARGS2 returns argc followed by the executable path, padding, argv, and
// environment strings. Reading argc and exactly that many NUL-terminated argv values
// avoids treating environment entries as command-line arguments.
// https://opensource.apple.com/source/xnu/xnu-7195.81.3/bsd/kern/kern_sysctl.c.auto.html
const macProcessArgumentsSource = String.raw`#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/sysctl.h>

int main(int argc, char **argv)
{
    if (argc != 2)
    {
        return 2;
    }

    char *end = NULL;
    errno = 0;
    long parsed_pid = strtol(argv[1], &end, 10);
    if (errno != 0 || end == argv[1] || *end != '\0' || parsed_pid <= 0)
    {
        return 2;
    }

    int mib[] = { CTL_KERN, KERN_PROCARGS2, (int)parsed_pid };
    size_t size = 0;
    if (sysctl(mib, 3, NULL, &size, NULL, 0) != 0)
    {
        // KERN_PROCARGS2 reports EINVAL instead of ESRCH when the process has already exited.
        return errno == ESRCH || errno == EINVAL ? 3 : 4;
    }

    char *buffer = malloc(size);
    if (buffer == NULL)
    {
        return 4;
    }

    if (sysctl(mib, 3, buffer, &size, NULL, 0) != 0)
    {
        int error = errno;
        free(buffer);
        return error == ESRCH || error == EINVAL ? 3 : 4;
    }

    int argument_count = 0;
    memcpy(&argument_count, buffer, sizeof(argument_count));
    char *cursor = buffer + sizeof(argument_count);
    char *buffer_end = buffer + size;

    while (cursor < buffer_end && *cursor != '\0')
    {
        cursor++;
    }
    while (cursor < buffer_end && *cursor == '\0')
    {
        cursor++;
    }

    for (int index = 0; index < argument_count; index++)
    {
        if (cursor >= buffer_end)
        {
            free(buffer);
            return 4;
        }

        size_t remaining = (size_t)(buffer_end - cursor);
        size_t length = strnlen(cursor, remaining);
        if (length == remaining)
        {
            free(buffer);
            return 4;
        }

        fwrite(cursor, 1, length, stdout);
        fputc('\0', stdout);
        cursor += length + 1;
    }

    free(buffer);
    return 0;
}
`;
