// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

internal static class IntegrationHostLifetimeTestHelper
{
    internal static string CaptureEvidence(string directory, string testName, string target)
    {
        var recording = CliE2ETestHelpers.GetTestResultsRecordingPath(testName);
        var evidence = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(recording)!, testName));
        foreach (var file in new[]
        {
            "session.log", "run.log", "probe-before.log", "probe-after.log",
            "recovered-resource.json", "stop.log", "before.json", "stop-guest.json"
        })
        {
            File.Copy(Path.Combine(directory, file), Path.Combine(evidence.FullName, file), overwrite: true);
        }
        File.Copy(
            Path.Combine(directory, "integration", "processes.jsonl"),
            Path.Combine(evidence.FullName, "processes.jsonl"),
            overwrite: true);
        if (target is "server" or "cli" or "crashloop" or "callback" or "guestcallback")
        {
            File.Copy(Path.Combine(directory, "restart.log"), Path.Combine(evidence.FullName, "restart.log"), overwrite: true);
        }
        if (target == "stall")
        {
            File.Copy(Path.Combine(directory, "probe-stalled.log"), Path.Combine(evidence.FullName, "probe-stalled.log"), overwrite: true);
        }
        if (target == "install")
        {
            foreach (var file in new[] { "install-session.log", "install.log" })
            {
                File.Copy(Path.Combine(directory, file), Path.Combine(evidence.FullName, file), overwrite: true);
            }
            File.Copy(Path.Combine(directory, "integration", "install-processes.json"),
                Path.Combine(evidence.FullName, "install-processes.json"), overwrite: true);
        }

        return evidence.FullName;
    }

    internal static void WriteFixture(string directory, string repoRoot, string target)
    {
        var integration = Directory.CreateDirectory(Path.Combine(directory, "integration"));
        File.Copy(
            Path.Combine(repoRoot, "playground", "TsIntegrationSpike", "kafka-integration", "host-runtime.ts"),
            Path.Combine(integration.FullName, "host-runtime.ts"));
        File.WriteAllText(Path.Combine(integration.FullName, "host.ts"), HostSource);
        File.WriteAllText(Path.Combine(integration.FullName, "package.json"), """
            {
              "name": "@e2e/lifetime",
              "private": true,
              "type": "module",
              "dependencies": {
                "vscode-jsonrpc": "^8.2.0"
              },
              "devDependencies": {
                "@types/node": "^22.0.0",
                "tsx": "^4.19.0"
              }
            }
            """);
        var probeSource = target switch
        {
            "callback" => "await builder.integrationOwnedProbe();",
            "guestcallback" => """
                await builder.integrationGuestProbe(async () => {
                    const generation = await builder.integrationGeneration();
                    fs.writeFileSync('probe-result', generation);
                    return generation;
                });
                """,
            _ => """
            const probe = await builder.addParameter('probe');
            // Keep this callback in the guest so host-only recovery can rediscover
            // the integration without recreating the resource model.
            await probe.withCommand('probe', 'Probe integration host', async () => {
                const generation = await builder.integrationGeneration();
                fs.writeFileSync('probe-result', generation);
                return { success: true };
            });
            """
        };
        File.WriteAllText(Path.Combine(directory, "apphost.mts"), $$"""
            import * as fs from 'node:fs';
            import { createBuilder } from './.aspire/modules/aspire.mjs';

            fs.writeFileSync('guest.pid', String(process.pid));
            const builder = await createBuilder();
            {{probeSource}}
            await builder.build().run();
            """);
        File.WriteAllText(Path.Combine(directory, "process-control.mjs"), ControlSource);

        var configPath = Path.Combine(directory, "aspire.config.json");
        var config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        config["features"] ??= new JsonObject();
        config["features"]!.AsObject()["experimentalHostingIntegrations"] = true;
        config["packages"] ??= new JsonObject();
        var packages = config["packages"]!.AsObject();
        packages["@e2e/lifetime"] = new JsonObject
        {
            ["source"] = "npm",
            ["path"] = "./integration/host.ts"
        };
        File.WriteAllText(configPath, config.ToJsonString());
    }

    internal static void WriteInstallCrashFixture(string directory)
    {
        var integrationDirectory = Path.Combine(directory, "integration");
        var manifestPath = Path.Combine(integrationDirectory, "package.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["scripts"] = new JsonObject { ["postinstall"] = "node install-lifetime.mjs" };
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        File.WriteAllText(Path.Combine(integrationDirectory, "block-install"), "");
        File.WriteAllText(Path.Combine(integrationDirectory, "install-lifetime.mjs"), InstallSource);
    }

    private const string InstallSource = """
        import * as fs from 'node:fs';
        import { spawn } from 'node:child_process';

        if (!fs.existsSync('block-install')) {
            console.log('INSTALL_RECOVERED');
            process.exit(0);
        }
        const worker = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'inherit' });
        // /proc/<pid>/stat fields after the last ')' start with state, PPID, and later start ticks.
        function identity(pid) {
            const stat = fs.readFileSync(`/proc/${pid}/stat`, 'utf8');
            const fields = stat.slice(stat.lastIndexOf(')') + 2).split(' ');
            return { pid, started: fields[19], parent: Number(fields[1]) };
        }
        const owned = [identity(process.pid), identity(worker.pid)];
        let ancestor = identity(process.ppid);
        while (!fs.readFileSync(`/proc/${ancestor.pid}/environ`).includes('ASPIRE_PROCESS_SUPERVISOR_COMMAND=')) {
            owned.push(ancestor);
            ancestor = identity(ancestor.parent);
        }
        owned.push(ancestor);
        const cli = identity(ancestor.parent);
        console.log(`INSTALL_LIFETIME_READY guardian=${ancestor.pid} cli=${cli.pid}`);
        console.error('INSTALL_LIFETIME_STDERR');
        // Publish atomically so readiness cannot observe a partially written PID record.
        fs.writeFileSync('install-processes.json.tmp', JSON.stringify({ cli, owned }));
        fs.renameSync('install-processes.json.tmp', 'install-processes.json');
        // Block the lifecycle script's event loop so cleanup cannot rely on its signal handlers.
        while (true) {}
        """;

    private const string HostSource = """
        import * as fs from 'node:fs';
        import { spawn } from 'node:child_process';
        import { AspireExport, defineIntegration } from '../.aspire/modules/base.mjs';
        import type { DistributedApplicationBuilder } from '../.aspire/modules/aspire.mjs';
        import { runIntegrationHost } from './host-runtime.js';

        const generationPath = 'generation';
        const generation = fs.existsSync(generationPath) ? Number(fs.readFileSync(generationPath, 'utf8')) + 1 : 1;
        fs.writeFileSync(generationPath, String(generation));
        const worker = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'inherit' });

        // /proc/<pid>/stat has a parenthesized command (which can contain spaces).
        // After its final ')', fields start at state (3), PPID (4), and starttime (22).
        function identity(pid: number) {
            const stat = fs.readFileSync(`/proc/${pid}/stat`, 'utf8');
            const fields = stat.slice(stat.lastIndexOf(')') + 2).split(' ');
            return { pid, started: fields[19], parent: Number(fields[1]) };
        }
        const owned = [identity(process.pid), identity(worker.pid!)];
        let ancestor = identity(process.ppid);
        while (!fs.readFileSync(`/proc/${ancestor.pid}/environ`).includes('ASPIRE_PROCESS_SUPERVISOR_COMMAND=')) {
            owned.push(ancestor);
            ancestor = identity(ancestor.parent);
        }
        owned.push(ancestor);
        const server = identity(ancestor.parent);
        fs.appendFileSync('processes.jsonl', JSON.stringify({
            generation, runtime: identity(process.pid),
            supervisor: ancestor, server, cli: identity(server.parent), owned
        }) + '\n');
        console.log(`LIFETIME_READY generation=${generation} runtime=${process.pid} worker=${worker.pid} supervisor=${ancestor.pid}`);
        setInterval(() => {
            if (fs.existsSync('crash-loop') && generation > Number(fs.readFileSync('crash-loop', 'utf8'))) {
                console.error(`LIFETIME_CRASH_LOOP generation=${generation}`);
                process.exit(23);
            }
            if (fs.existsSync('block-runtime')) {
                fs.writeFileSync('runtime-blocked', '');
                // A wedged integration cannot cooperate with socket-close or signal
                // handlers. Cleanup must come from the independent guardian/server.
                while (true) {}
            }
        }, 50);

        const builderType = {
            typeId: 'Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder',
            category: 'Handle' as const,
            isInterface: true
        };
        const integrationGeneration = AspireExport<{ builder: DistributedApplicationBuilder }, string>({
            id: 'e2e.lifetime/generation',
            method: 'integrationGeneration',
            description: 'Reports the currently serving integration host generation',
            projection: {
                capabilityKind: 'Method',
                targetTypeId: builderType.typeId,
                targetType: builderType,
                targetParameterName: 'builder',
                returnType: { typeId: 'string', category: 'Primitive' },
                parameters: []
            }
        }, async () => {
            if (fs.existsSync('stall-invocation') && generation === Number(fs.readFileSync('stall-invocation', 'utf8'))) {
                fs.appendFileSync('stalled-side-effects', 'once\n');
                console.log(`LIFETIME_STALLED generation=${generation} runtime=${process.pid}`);
                await new Promise(() => {});
            }
            return `generation-${generation}`;
        });

        const integrationOwnedProbe = AspireExport<{ builder: DistributedApplicationBuilder }, string>({
            id: 'e2e.lifetime/ownedProbe',
            method: 'integrationOwnedProbe',
            description: 'Attaches an integration-owned deferred resource command',
            projection: {
                capabilityKind: 'Method',
                targetTypeId: builderType.typeId,
                targetType: builderType,
                targetParameterName: 'builder',
                returnType: { typeId: 'string', category: 'Primitive' },
                parameters: []
            }
        }, async ({ builder }) => {
            const probe = await builder.addParameter('probe');
            // Deliberately leave a real generated fluent call unawaited: export completion
            // must drain it before the guest starts the resource model.
            probe.withCommand('probe', 'Probe integration-owned callback', async () => {
                fs.writeFileSync('../probe-result', `generation-${generation}`);
                return { success: true };
            });
            return `generation-${generation}`;
        });

        const integrationGuestProbe = AspireExport<{
            builder: DistributedApplicationBuilder,
            configure: () => Promise<string>
        }, string>({
            id: 'e2e.lifetime/guestProbe',
            method: 'integrationGuestProbe',
            description: 'Retains a guest callback in an integration-owned resource command',
            projection: {
                capabilityKind: 'Method',
                targetTypeId: builderType.typeId,
                targetType: builderType,
                targetParameterName: 'builder',
                returnType: { typeId: 'string', category: 'Primitive' },
                parameters: [{
                    name: 'configure',
                    isCallback: true,
                    callbackParameters: [],
                    callbackReturnType: { typeId: 'string', category: 'Primitive' }
                }]
            }
        }, async ({ builder, configure }) => {
            const probe = await builder.addParameter('probe');
            probe.withCommand('probe', 'Probe deferred guest callback', async () => {
                await configure();
                return { success: true };
            });
            return `generation-${generation}`;
        });

        await runIntegrationHost({
            packageName: '@e2e/lifetime',
            integrations: [defineIntegration({ name: 'LifetimeIntegration', capabilities: [integrationGeneration, integrationOwnedProbe, integrationGuestProbe] })]
        });
        """;

    private const string ControlSource = """
        import * as fs from 'node:fs';
        import assert from 'node:assert/strict';
        import { execFileSync } from 'node:child_process';

        const [operation, target] = process.argv.slice(2);
        const records = () => fs.readFileSync('integration/processes.jsonl', 'utf8').trim().split('\n').map(JSON.parse);
        const snapshot = () => JSON.parse(fs.readFileSync('before.json', 'utf8'));
        // Use kernel start ticks as well as PID, so PID reuse cannot turn a cleanup
        // assertion into a false positive or cause a later test to kill another process.
        function identity(pid) {
            try {
                const stat = fs.readFileSync(`/proc/${pid}/stat`, 'utf8');
                const fields = stat.slice(stat.lastIndexOf(')') + 2).split(' ');
                return { pid, started: fields[19] };
            } catch (error) {
                if (error.code === 'ENOENT' || error.code === 'ESRCH') return null;
                throw error;
            }
        }
        const exists = original => identity(original.pid)?.started === original.started;
        async function until(predicate, description) {
            const deadline = performance.now() + 60000;
            while (!predicate()) {
                if (performance.now() >= deadline) throw new Error(`Timed out: ${description}`);
                await new Promise(resolve => setTimeout(resolve, 100));
            }
        }
        function terminate(original) {
            assert.ok(exists(original), `Process ${original.pid} no longer has the expected identity`);
            process.kill(original.pid, 'SIGKILL');
        }

        switch (operation) {
            case 'install-ready': {
                await until(() => {
                    if (!fs.existsSync('integration/install-processes.json')) return false;
                    const log = fs.readFileSync('install-session.log', 'utf8');
                    return log.includes('INSTALL_LIFETIME_READY') && log.includes('INSTALL_LIFETIME_STDERR');
                }, 'dependency-install workers and captured stdout/stderr ready');
                const record = JSON.parse(fs.readFileSync('integration/install-processes.json', 'utf8'));
                assert.equal(record.cli.pid, Number(fs.readFileSync('install-cli.pid', 'utf8')));
                assert.ok(record.owned.every(exists));
                console.log('INSTALL_READY');
                break;
            }
            case 'install-kill':
                terminate(JSON.parse(fs.readFileSync('integration/install-processes.json', 'utf8')).cli);
                break;
            case 'install-stopped': {
                const record = JSON.parse(fs.readFileSync('integration/install-processes.json', 'utf8'));
                await until(() => [...record.owned, record.cli].every(original => !exists(original)),
                    'dependency-install guardian, npm, blocked lifecycle script and worker reaped after CLI death');
                fs.unlinkSync('integration/block-install');
                break;
            }
            case 'ready':
                await until(() => {
                    const output = execFileSync('aspire', ['describe', 'probe', '--format', 'json'], {
                        encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe']
                    });
                    // Before an AppHost is discoverable, describe exits successfully with empty
                    // stdout. Keep one readiness command alive rather than retrying terminal prompts.
                    return output.trim().length > 0 && JSON.parse(output).resources.some(resource => resource.name === 'probe');
                }, 'probe resource registered');
                console.log('PROBE_READY');
                break;
            case 'describe': {
                assert.equal(JSON.parse(fs.readFileSync('recovered-resource.json', 'utf8')).resources[0].name, 'probe');
                // Capture the guest before stop, including a replacement created by explicit restart.
                const guest = identity(Number(fs.readFileSync('guest.pid', 'utf8')));
                assert.ok(guest);
                fs.writeFileSync('stop-guest.json', JSON.stringify(guest));
                break;
            }
            case 'snapshot': {
                const current = records().at(-1);
                assert.equal(current.runtime.parent, current.supervisor.pid,
                    'TypeScript integrations must launch directly under the guardian, without npm or tsx wrapper processes');
                current.guest = identity(Number(fs.readFileSync('guest.pid', 'utf8')));
                assert.ok(current.guest);
                assert.ok(current.owned.every(exists));
                fs.writeFileSync('before.json', JSON.stringify(current));
                break;
            }
            case 'kill':
                terminate(snapshot()[target]);
                if (fs.existsSync('integration/block-runtime')) fs.unlinkSync('integration/block-runtime');
                break;
            case 'block':
                fs.writeFileSync('integration/block-runtime', '');
                await until(() => fs.existsSync('integration/runtime-blocked'), 'runtime event loop blocked');
                break;
            case 'crashloop':
                fs.writeFileSync('integration/crash-loop', String(snapshot().generation));
                terminate(snapshot().runtime);
                break;
            case 'stall':
                fs.writeFileSync('integration/stall-invocation', String(snapshot().generation));
                break;
            case 'stall-diagnostics': {
                const log = fs.readFileSync('session.log', 'utf8');
                assert.ok(log.includes('LIFETIME_STALLED'));
                assert.ok(log.includes('stalled: capability e2e.lifetime/generation'));
                assert.ok(log.includes('In-flight work is not replayed'));
                assert.ok(log.includes('Still executing e2e.lifetime/generation'));
                assert.ok(log.includes('awaiting integration code'));
                // Server and runtime logs share the opaque invocation ID, without argument payloads.
                const matches = [...log.matchAll(/Integration invocation ([a-f0-9]{32}) stalled: capability e2e\.lifetime\/generation/g)];
                assert.equal(matches.length, 1);
                assert.ok(log.includes(`Dispatching e2e.lifetime/generation (invocation ${matches[0][1]}, PID`));
                assert.ok(fs.readFileSync('probe-stalled.log', 'utf8').includes('timed out'));
                assert.equal(fs.readFileSync('integration/stalled-side-effects', 'utf8'), 'once\n');
                break;
            }
            case 'exhausted': {
                const log = fs.readFileSync('session.log', 'utf8');
                assert.ok(log.includes('LIFETIME_CRASH_LOOP'));
                assert.ok(log.includes('failed after 3 restart attempts. Stopping the AppHost session.'));
                assert.ok(log.includes('Integration host recovery failed; the AppHost session was stopped.'));
                assert.equal(records().filter(record => record.generation > snapshot().generation).length, 3);
                fs.unlinkSync('integration/crash-loop');
                break;
            }
            case 'callbacks': {
                const log = fs.readFileSync('session.log', 'utf8');
                assert.ok(log.includes('owns callbacks that cannot be restored by restarting the host.'));
                assert.ok(log.includes('Stopping the AppHost session. Restart it to rebuild the resource model.'));
                assert.ok(log.includes('Integration host recovery failed; the AppHost session was stopped.'));
                assert.equal(records().filter(record => record.generation > snapshot().generation).length, 0);
                break;
            }
            case 'recovered': {
                const before = snapshot();
                await until(() => records().at(-1).generation > before.generation, 'replacement integration host');
                await until(() => before.owned.every(original => !exists(original)), 'old guardian, runtime, and worker reaped');
                assert.ok(exists(before.server), 'Recovery must preserve the AppHost server');
                assert.ok(exists(before.guest), 'Recovery must preserve the guest and its callbacks');
                await until(() => fs.readFileSync('session.log', 'utf8').includes('capabilities rediscovered.'), 'recovery diagnostic');
                const log = fs.readFileSync('session.log', 'utf8');
                assert.ok(log.includes("Integration host '@e2e/lifetime' is unavailable."));
                assert.ok(log.includes('Restart attempt 1/3'));
                assert.ok(log.includes("IntegrationHost[@e2e/lifetime]: LIFETIME_READY"));
                break;
            }
            case 'stopped': {
                const before = snapshot();
                await until(() => [...before.owned, before.server, before.guest, before.cli].every(original => !exists(original)),
                    'the whole AppHost session reaped after owner death');
                break;
            }
            case 'probe':
                assert.equal(fs.readFileSync('probe-result', 'utf8'), `generation-${records().at(-1).generation}`);
                break;
            case 'clean': {
                const guest = JSON.parse(fs.readFileSync('stop-guest.json', 'utf8'));
                await until(() => records().flatMap(record => [...record.owned, record.server, record.cli])
                    .every(original => !exists(original)) && !exists(guest),
                    'all recorded integration scopes, owners, and guest reaped');
                break;
            }
            default:
                throw new Error(`Unknown operation: ${operation}`);
        }
        console.log(`LIFETIME_ASSERTION_OK ${operation}`);
        """;
}
