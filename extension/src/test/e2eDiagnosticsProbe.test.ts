import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { getCodeLensesForFile, getDiagnosticsForFile } from '../testing/e2eStateFileBridge';

import { removeDirectorySafely } from './testHelpers';
/**
 * The Java AppHost E2E spec probes diagnostics for every generated Aspire Java SDK source, which is
 * more than a hundred files. Each probe has to show the document so the language server publishes
 * diagnostics for it, and the tabs that leaves behind are then closed one at a time over WebDriver
 * by whichever suite tears down next - which is how a passing suite still failed the shard on a
 * five minute `after all` timeout.
 */
suite('E2E diagnostics probe', () => {
    let temporaryDirectory: string | undefined;
    let restoreEnablePreview: (() => Promise<void>) | undefined;

    teardown(async () => {
        await vscode.commands.executeCommand('workbench.action.closeAllEditors');

        if (restoreEnablePreview) {
            await restoreEnablePreview();
            restoreEnablePreview = undefined;
        }

        if (temporaryDirectory) {
            const probedDirectory = temporaryDirectory;
            temporaryDirectory = undefined;
            removeDirectorySafely(probedDirectory);
        }
    });

    test('probing many files does not accumulate editor tabs', async () => {
        temporaryDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-diagnostics-probe-'));

        // `workbench.editor.enablePreview` is a user setting, so a probe that relies on the default
        // preview reuse keeps a tab per file on any profile where it is off. Turning it off here is
        // what reproduces the tab pile seen in the real E2E VS Code instance.
        const editorConfiguration = vscode.workspace.getConfiguration('workbench.editor');
        await editorConfiguration.update('enablePreview', false, vscode.ConfigurationTarget.Global);
        restoreEnablePreview = async () => {
            await vscode.workspace.getConfiguration('workbench.editor').update('enablePreview', undefined, vscode.ConfigurationTarget.Global);
        };

        const probedFiles: string[] = [];
        for (let index = 0; index < 12; index++) {
            const filePath = path.join(temporaryDirectory, `Probe${index}.java`);
            fs.writeFileSync(filePath, `class Probe${index} { }\n`);
            probedFiles.push(filePath);
        }

        for (const filePath of probedFiles) {
            await getDiagnosticsForFile(filePath);
        }

        const openTabs = vscode.window.tabGroups.all.reduce((total, group) => total + group.tabs.length, 0);
        assert.ok(
            openTabs <= 1,
            `Probing ${probedFiles.length} files left ${openTabs} editor tabs open. Diagnostics probes must reuse a single preview tab.`);
    });

    test('probing a file the caller already opened leaves it open', async () => {
        temporaryDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-diagnostics-probe-'));

        const filePath = path.join(temporaryDirectory, 'AlreadyOpen.java');
        fs.writeFileSync(filePath, 'class AlreadyOpen { }\n');

        const document = await vscode.workspace.openTextDocument(vscode.Uri.file(filePath));
        await vscode.window.showTextDocument(document, { preview: false });

        await getDiagnosticsForFile(filePath);

        const openPaths = vscode.window.tabGroups.all
            .flatMap(group => group.tabs)
            .map(tab => tab.input instanceof vscode.TabInputText ? tab.input.uri.fsPath : undefined);

        // Compare through Uri.file the way the probe itself does. On Windows a path from os.tmpdir()
        // keeps the drive letter VS Code lowercases, so `C:\Users\RUNNER~1\...` never string-matches
        // the `c:\Users\RUNNER~1\...` a tab reports.
        const expectedPath = vscode.Uri.file(filePath).fsPath;

        assert.ok(
            openPaths.includes(expectedPath),
            `Probing must not close an editor the caller already had open. Open tabs: ${JSON.stringify(openPaths)}`);
    });

    test('probing CodeLenses does not show the document', async () => {
        temporaryDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-diagnostics-probe-'));

        const filePath = path.join(temporaryDirectory, 'CodeLensProbe.ts');
        fs.writeFileSync(filePath, 'const probe = true;\n');
        const expectedPath = vscode.Uri.file(filePath).fsPath;
        const provider = vscode.languages.registerCodeLensProvider(
            { language: 'typescript', scheme: 'file' },
            {
                provideCodeLenses(document) {
                    if (document.uri.fsPath !== expectedPath) {
                        return [];
                    }

                    return [new vscode.CodeLens(
                        new vscode.Range(0, 0, 0, 0),
                        { title: 'Probe CodeLens', command: 'aspire-vscode.test.probeCodeLens' })];
                },
            });

        try {
            const result = await getCodeLensesForFile(filePath);

            assert.strictEqual(result.filePath, expectedPath);
            assert.strictEqual(result.languageId, 'typescript');
            assert.ok(result.commandTitles.includes('Probe CodeLens'));
            assert.ok(!vscode.window.visibleTextEditors.some(editor => editor.document.uri.fsPath === expectedPath));
        }
        finally {
            provider.dispose();
        }
    });
});

/**
 * Windows releases the handle behind a closed editor asynchronously, so removing the probe directory
 * races that release and fails with `EPERM`. Node retries recursive removals for exactly that code,
 * and a directory stranded in the OS temp folder is not worth failing the suite over - the assertion
 * here is about editor tabs - so a removal that still loses the race is only reported.
 * See https://nodejs.org/api/fs.html#fsrmsyncpath-options.
 */
