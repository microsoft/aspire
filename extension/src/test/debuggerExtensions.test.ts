import * as assert from 'assert';
import * as sinon from 'sinon';
import { aspireOwnedResourceDebugConfigurationFieldNames, prepareDebugSession } from '../debugger/debuggerExtensions';
import { nodeDebuggerExtension } from '../debugger/languages/node';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { ExecutableLaunchConfiguration, LaunchOptions } from '../dcp/types';
import { extensionLogOutputChannel } from '../utils/logging';

suite('Debugger Extensions Tests', () => {
    // Two independent mechanisms keep the workspace out of Aspire-owned fields: the `debuggers`
    // merge refuses them by name, and their authoritative values are re-applied after the merge.
    // Either one alone upholds the invariant, which is the point of having both — and it also
    // means neither is individually observable from out here. Verified by mutation:
    //
    // - Disabling the refusal is caught only by the warning test; the re-application repairs the
    //   values, so the override is silent but harmless.
    // - Removing the re-application is caught by nothing while the refusal works. That is the
    //   defense-in-depth layer doing its job, and there is no black-box test that can pin it.
    // - Removing both is caught by the refusal test below.
    //
    // So treat the refusal test as covering the pair, not the ordering.
    teardown(() => {
        sinon.restore();
    });

    const fakeAspireDebugSession = {} as AspireDebugSession;

    // Node is the resource type used throughout this suite because its callback does not touch any
    // Aspire-owned field. A type like browser overwrites several of them itself, which would mask
    // whether the protection comes from prepareDebugSession or from a language callback happening
    // to win the race.
    const nodeLaunchConfig = { type: 'node', program: '/workspace/app/index.js' } as unknown as ExecutableLaunchConfiguration;

    function createLaunchOptions(): LaunchOptions {
        return { debug: true, runId: 'run-1', debugSessionId: 'dcp-1', isApphost: false, debugSession: fakeAspireDebugSession };
    }

    test('refuses every Aspire-owned field a workspace tries to set', async () => {
        // Driven by the exported set rather than a hardcoded list, so a field added to the map is
        // covered here automatically instead of silently escaping the assertion.
        const workspaceSuppliedValue = 'workspace-supplied';
        const ownedFields = [...aspireOwnedResourceDebugConfigurationFieldNames];
        assert.ok(ownedFields.length > 0, 'expected at least one Aspire-owned field to be declared');

        const debuggerSettings: Record<string, unknown> = {};
        for (const field of ownedFields) {
            debuggerSettings[field] = workspaceSuppliedValue;
        }

        const prepared = await prepareDebugSession(
            {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.cs',
                debuggers: { node: debuggerSettings as never }
            },
            nodeLaunchConfig,
            [],
            [],
            createLaunchOptions(),
            nodeDebuggerExtension);

        const configuration = prepared.debugConfiguration as Record<string, unknown>;
        const fieldsThatTookTheWorkspaceValue = ownedFields.filter(field => configuration[field] === workspaceSuppliedValue);
        assert.deepStrictEqual(fieldsThatTookTheWorkspaceValue, []);
    });

    test('applies the authoritative value for every Aspire-owned field', async () => {
        // The other half of the map. Refusing a workspace value is not enough on its own: the field
        // still has to end up holding the value Aspire computed, which is what a removed map entry
        // would break while leaving the refusal above passing.
        const prepared = await prepareDebugSession(            {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.cs'
            },
            nodeLaunchConfig,
            [],
            [],
            createLaunchOptions(),
            nodeDebuggerExtension);

        const configuration = prepared.debugConfiguration;
        assert.strictEqual(configuration.runId, 'run-1');
        assert.strictEqual(configuration.debugSessionId, 'dcp-1');
        assert.strictEqual(configuration.isApphost, false);
        assert.strictEqual(configuration.terminationSignal, nodeDebuggerExtension.terminationSignal);
    });

    test('forwards workspace debugger settings that Aspire does not own', async () => {
        // Refusing the owned fields must not turn the merge into an allowlist. Passing adapter
        // options through is the point of the `debuggers` setting, including keys this extension
        // has never heard of.
        const prepared = await prepareDebugSession(
            {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.cs',
                debuggers: {
                    node: {
                        args: ['--user-supplied'],
                        name: 'User named session',
                        anUnknownAdapterOption: 'passed through'
                    } as never
                }
            },
            nodeLaunchConfig,
            [],
            [],
            createLaunchOptions(),
            nodeDebuggerExtension);

        const configuration = prepared.debugConfiguration;
        assert.deepStrictEqual(configuration.args, ['--user-supplied']);
        assert.strictEqual(configuration.name, 'User named session');
        assert.strictEqual(configuration.anUnknownAdapterOption, 'passed through');
    });

    test('logs a warning naming each refused field so the workspace author can see why', async () => {
        const warnStub = sinon.stub(extensionLogOutputChannel, 'warn');

        await prepareDebugSession(
            {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.cs',
                debuggers: { node: { runId: '..', terminationSignal: 'debugSessionEnd' } as never }
            },
            nodeLaunchConfig,
            [],
            [],
            createLaunchOptions(),
            nodeDebuggerExtension);

        const warnings = warnStub.getCalls().map(call => call.args[0] as string);
        assert.deepStrictEqual(warnings, [
            "Ignoring 'runId' from the 'debuggers' debug configuration because it is managed by Aspire.",
            "Ignoring 'terminationSignal' from the 'debuggers' debug configuration because it is managed by Aspire."
        ]);
    });
});
