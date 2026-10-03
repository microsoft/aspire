import * as assert from 'assert';
import { spawnSync } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

suite('Windows file-lock owner diagnostics', () => {
    if (process.platform !== 'win32') {
        return;
    }

    test('reports the exact live file owner without releasing its lock and distinguishes empty observations', function () {
        this.timeout(120000);
        const root = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-lock-test-'));
        const helper = path.resolve(__dirname, '..', '..', 'scripts', 'get-windows-file-lock-owners.ps1');
        const script = String.raw`
$ErrorActionPreference = 'Stop'
$root = $env:ASPIRE_LOCK_TEST_ROOT
$file = Join-Path $root 'held.log'
$helper = $env:ASPIRE_LOCK_TEST_HELPER
function Query-Lock([string] $Target, [int] $Budget = 10000) {
    $json = & pwsh -NoLogo -NoProfile -NonInteractive -File $helper -FilePath $Target -RunRoot $root -TimeoutMs $Budget
    if ($LASTEXITCODE -ne 0) { throw 'The diagnostic host failed.' }
    return ($json | ConvertFrom-Json)
}
$process = [Diagnostics.Process]::GetCurrentProcess()
$expectedStart = $process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString([Globalization.CultureInfo]::InvariantCulture)
$stream = [IO.File]::Open($file, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite)
try {
    $held = Query-Lock $file
    $deletionBlocked = $false
    try { [IO.File]::Delete($file) }
    catch [IO.IOException] { $deletionBlocked = $true }
    $stillExists = [IO.File]::Exists($file)
}
finally {
    $stream.Dispose()
}
$unlocked = Query-Lock $file
$missing = Query-Lock (Join-Path $root 'missing.log')
$invalidBudget = Query-Lock $file 0
$outside = Query-Lock (Join-Path ($root + '-neighbor') 'held.log')
$traversal = Query-Lock (Join-Path $root '..\held.log')
$streamPath = Query-Lock ($file + ':stream')
[ordered]@{
    pid = $PID
    start = $expectedStart
    held = $held
    deletionBlocked = $deletionBlocked
    stillExists = $stillExists
    unlocked = $unlocked
    missing = $missing
    invalidBudget = $invalidBudget
    rejected = @($outside, $traversal, $streamPath)
} | ConvertTo-Json -Depth 20 -Compress
`;
        try {
            const result = spawnSync('pwsh', ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', script], {
                encoding: 'utf8',
                shell: false,
                windowsHide: true,
                timeout: 90000,
                env: {
                    ...process.env,
                    ASPIRE_LOCK_TEST_ROOT: root,
                    ASPIRE_LOCK_TEST_HELPER: helper,
                },
            });
            assert.ifError(result.error);
            assert.strictEqual(result.status, 0, result.stderr);
            assert.strictEqual(result.stderr, '');
            const report = JSON.parse(result.stdout);
            assert.strictEqual(report.held.status, 'completed');
            const observation = report.held.query.observations[0];
            assert.strictEqual(observation.Status, 'owners-observed');
            assert.strictEqual(observation.SessionEndCode, 0);
            assert.deepStrictEqual(observation.Owners.map((owner: { Pid: number; StartTimeFileTimeUtc: string }) => ({
                pid: owner.Pid,
                start: owner.StartTimeFileTimeUtc,
            })), [{ pid: report.pid, start: report.start }]);
            assert.strictEqual(report.deletionBlocked, true);
            assert.strictEqual(report.stillExists, true);
            assert.strictEqual(report.unlocked.status, 'completed');
            assert.strictEqual(report.unlocked.query.observations[0].Status, 'no-owners-observed');
            assert.deepStrictEqual(report.unlocked.query.observations[0].Owners, []);
            assert.strictEqual(report.missing.status, 'completed-with-query-errors');
            assert.strictEqual(report.missing.query.observations[0].Status, 'file-missing');
            assert.strictEqual(report.invalidBudget.status, 'invalid-timeout');
            assert.strictEqual(report.invalidBudget.query, null);
            assert.deepStrictEqual(report.rejected.map((entry: { status: string; query: unknown }) => ({
                status: entry.status,
                query: entry.query,
            })), Array(3).fill({ status: 'out-of-scope', query: null }));
        }
        finally {
            fs.rmSync(root, { recursive: true, force: true });
        }
    });
});
