/// <reference types="mocha" />

import * as assert from 'assert';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { createMockDocument } from './testHelpers';
import waitForExpect from 'wait-for-expect';
import { AspireGutterDecorationProvider, classifyState } from '../editor/AspireGutterDecorationProvider';
import { ResourceState } from '../editor/resourceConstants';
import { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';
import { AppHostDisplayInfo, ResourceJson } from '../data/AppHostDataRepository';

function p(...segments: string[]): string {
    return path.join(path.sep, ...segments);
}


function makeResource(name: string): ResourceJson {
    return {
        name,
        displayName: name,
        resourceType: 'container',
        state: 'Running',
        stateStyle: '',
        healthStatus: null,
        healthReports: null,
        exitCode: null,
        dashboardUrl: null,
        urls: null,
        commands: {},
        properties: null,
    };
}

function makeAppHost(appHostPath: string, resources: ResourceJson[]): AppHostDisplayInfo {
    return {
        appHostPath,
        appHostPid: 1234,
        cliPid: null,
        dashboardUrl: null,
        resources,
    };
}

function makeTreeProvider(opts: {
    appHosts?: AppHostDisplayInfo[];
    workspaceResources?: ResourceJson[];
    workspaceAppHostPath?: string;
}): AspireAppHostTreeProvider {
    const onDidChangeTreeData: vscode.Event<void> = () => ({ dispose: () => { } });
    return {
        onDidChangeTreeData,
        appHosts: opts.appHosts ?? [],
        workspaceResources: opts.workspaceResources ?? [],
        workspaceAppHostPath: opts.workspaceAppHostPath,
    } as unknown as AspireAppHostTreeProvider;
}

const APP_HOST_DOC = 'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.AddRedis("cache");\nbuilder.Build().Run();';

suite('AspireGutterDecorationProvider', () => {
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
        sandbox.stub(vscode.workspace, 'getConfiguration').returns({
            get: () => true,
            has: () => true,
            inspect: () => undefined,
            update: () => Promise.resolve(),
        } as any);
    });

    teardown(() => {
        sandbox.restore();
    });

    test('RuntimeUnhealthy uses the warning decoration category', () => {
        assert.strictEqual(classifyState(ResourceState.RuntimeUnhealthy, '', ''), 'warning');
    });

    test('FailedToStart uses the warning decoration category', () => {
        assert.strictEqual(classifyState(ResourceState.FailedToStart, '', ''), 'warning');
    });

    test('FailedToStart with a null exit code uses the warning decoration category', () => {
        assert.strictEqual(classifyState(ResourceState.FailedToStart, '', '', null), 'warning');
    });

    test('FailedToStart with exit code 0 uses the warning decoration category', () => {
        assert.strictEqual(classifyState(ResourceState.FailedToStart, '', '', 0), 'warning');
    });

    test('FailedToStart with exit code -1 uses the error decoration category', () => {
        assert.strictEqual(classifyState(ResourceState.FailedToStart, '', '', -1), 'error');
    });

    test('FailedToStart with a non-zero exit code uses the error decoration category', () => {
        assert.strictEqual(classifyState(ResourceState.FailedToStart, '', '', 1), 'error');
    });

    test('emits resource decorations for a running Rust AppHost', async () => {
        const appHostPath = p('repo', 'AppHost', 'apphost.rs');
        const content = [
            'fn main() {',
            '    let builder = create_builder(None)?;',
            '    let cache = builder.add_redis("cache")?;',
            '}',
        ].join('\n');
        const document = createMockDocument(content, appHostPath);
        const runningAppHost = makeAppHost(appHostPath, [makeResource('cache')]);
        const decorationCalls: vscode.DecorationOptions[][] = [];
        const editor = {
            document,
            setDecorations: (_type: vscode.TextEditorDecorationType, options: readonly vscode.DecorationOptions[]) => {
                decorationCalls.push([...options]);
            },
        } as unknown as vscode.TextEditor;
        sandbox.stub(vscode.window, 'visibleTextEditors').value([editor]);

        const provider = new AspireGutterDecorationProvider(makeTreeProvider({ appHosts: [runningAppHost] }));

        await waitForExpect(() => {
            const decorations = decorationCalls.flat();
            assert.strictEqual(decorations.length, 1);
            assert.strictEqual(decorations[0].range.start.line, 2);
        });
        provider.dispose();
    });

    test('does not emit resource decorations from a different running AppHost', () => {
        const runningHostPath = p('repo', 'RunningAppHost', 'AppHost.csproj');
        const stoppedHostPath = p('repo', 'StoppedAppHost', 'AppHost.cs');
        const runningAppHost = makeAppHost(runningHostPath, [makeResource('cache')]);
        const document = createMockDocument(APP_HOST_DOC, stoppedHostPath);
        const decorationCalls: vscode.DecorationOptions[][] = [];
        const editor = {
            document,
            setDecorations: (_type: vscode.TextEditorDecorationType, options: readonly vscode.DecorationOptions[]) => {
                decorationCalls.push([...options]);
            },
        } as unknown as vscode.TextEditor;

        sandbox.stub(vscode.window, 'visibleTextEditors').value([editor]);

        const provider = new AspireGutterDecorationProvider(makeTreeProvider({ appHosts: [runningAppHost] }));

        assert.strictEqual(decorationCalls.flat().length, 0);

        provider.dispose();
    });

    test('emits resource decorations when Windows AppHost path casing differs from document path', async () => {
        const platformStub = sandbox.stub(process, 'platform').value('win32');
        const runningHostPath = p('repo', 'apphost', 'apphost.csproj');
        const document = createMockDocument(APP_HOST_DOC, p('repo', 'AppHost', 'AppHost.cs'));
        const runningAppHost = makeAppHost(runningHostPath, [makeResource('cache')]);
        const decorationCalls: vscode.DecorationOptions[][] = [];
        const editor = {
            document,
            setDecorations: (_type: vscode.TextEditorDecorationType, options: readonly vscode.DecorationOptions[]) => {
                decorationCalls.push([...options]);
            },
        } as unknown as vscode.TextEditor;

        sandbox.stub(vscode.window, 'visibleTextEditors').value([editor]);

        const provider = new AspireGutterDecorationProvider(makeTreeProvider({ appHosts: [runningAppHost] }));

        await waitForExpect(() => {
            assert.strictEqual(decorationCalls.flat().length, 1);
        });

        provider.dispose();
        platformStub.restore();
    });
});
