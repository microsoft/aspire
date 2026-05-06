/// <reference types="mocha" />

import * as assert from 'assert';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireGutterDecorationProvider } from '../editor/AspireGutterDecorationProvider';
import { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';
import { AppHostDisplayInfo, ResourceJson } from '../views/AppHostDataRepository';

function p(...segments: string[]): string {
    return path.join(path.sep, ...segments);
}

function createMockDocument(content: string, filePath: string): vscode.TextDocument {
    const lines = content.split('\n');
    return {
        uri: vscode.Uri.file(filePath),
        fileName: filePath,
        isUntitled: false,
        languageId: filePath.endsWith('.cs') ? 'csharp' : filePath.endsWith('.ts') ? 'typescript' : 'javascript',
        version: 1,
        isDirty: false,
        isClosed: false,
        eol: vscode.EndOfLine.LF,
        lineCount: lines.length,
        encoding: 'utf-8',
        save: () => Promise.resolve(false),
        lineAt: (lineOrPos: number | vscode.Position) => {
            const lineNum = typeof lineOrPos === 'number' ? lineOrPos : lineOrPos.line;
            const text = lines[lineNum] || '';
            return {
                lineNumber: lineNum,
                text,
                range: new vscode.Range(lineNum, 0, lineNum, text.length),
                rangeIncludingLineBreak: new vscode.Range(lineNum, 0, lineNum + 1, 0),
                firstNonWhitespaceCharacterIndex: text.search(/\S/),
                isEmptyOrWhitespace: text.trim().length === 0,
            } as vscode.TextLine;
        },
        offsetAt: (position: vscode.Position) => {
            let offset = 0;
            for (let i = 0; i < position.line && i < lines.length; i++) {
                offset += lines[i].length + 1;
            }
            return offset + position.character;
        },
        positionAt: (offset: number) => {
            let remaining = offset;
            for (let i = 0; i < lines.length; i++) {
                if (remaining <= lines[i].length) {
                    return new vscode.Position(i, remaining);
                }
                remaining -= lines[i].length + 1;
            }
            return new vscode.Position(lines.length - 1, lines[lines.length - 1].length);
        },
        getText: () => content,
        getWordRangeAtPosition: () => undefined,
        validateRange: (range: vscode.Range) => range,
        validatePosition: (position: vscode.Position) => position,
        notebook: undefined as any,
    } as vscode.TextDocument;
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
});
