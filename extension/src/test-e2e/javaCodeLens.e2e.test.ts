import * as assert from 'assert';
import { getJavaAppHostSourcePath, prepareJavaWorkspace, waitForJavaLanguageServerImport } from './helpers/java';
import { executeE2eControlCommand } from './helpers/fixtures';
import { isSamePath } from './helpers/assertions';
import { closeAllEditors, waitForCodeLensText, waitForEditorTitle } from './helpers/vscode';

suite('Java AppHost CodeLens E2E', function () {
    // Matches the other Java specs: the language server import below is allowed 15 minutes on a cold
    // runner, so a 5 minute suite budget would abort the setup rather than the thing it waits for.
    this.timeout(1800000);

    suiteSetup(async () => {
        await prepareJavaWorkspace();

        // Kept because VS Code renders one merged CodeLens set per document: a `java` file shows
        // nothing until every registered provider has answered, including redhat.java's, and the
        // other two Java specs wait here for the same reason. It is not what caused the
        // `CodeLenses: (none)` failure though - that run reached Standard mode in 20 seconds and
        // still saw nothing, because no editor had been opened at all.
        await waitForJavaLanguageServerImport();
    });

    suiteTeardown(async () => {
        await closeAllEditors();
    });

    test('shows the entry point warning on a Java AppHost', async () => {
        const appHostPath = getJavaAppHostSourcePath();

        // Open through the extension host rather than VSBrowser.openResources(). That helper shells
        // out to `code -r <path>` (CodeUtil.open), which exited successfully here while opening
        // nothing: the window sat on the welcome screen for the whole CodeLens wait, so the spec
        // reported `CodeLenses: (none)` for a document that was never open. The other two Java specs
        // already use this command, which is why they passed. It is also observable - it returns the
        // active editor - so a silent no-op fails here instead of three minutes later as a blank wait.
        const opened = await executeE2eControlCommand({ name: 'openFile', filePath: appHostPath });
        const openedFileName = (opened.result as { fileName?: string } | undefined)?.fileName;
        assert.ok(
            openedFileName && isSamePath(openedFileName, appHostPath),
            `expected '${appHostPath}' to be the active editor, got '${openedFileName ?? '<no active editor>'}'`);

        // The command reports the extension host's view; this proves the tab actually rendered,
        // which is the specific thing that was missing when the window sat on the welcome screen.
        await waitForEditorTitle('AppHost.java');

        // The tab now exists, but the lenses are produced asynchronously after that, so this is
        // polled rather than read once. The suiteSetup guarantees the Java language server reached
        // Standard mode; VS Code still has to run the merged provider pass on this document
        // afterwards, so this allows more than the 60s default.
        const texts = await waitForCodeLensText('AppHost.java', 'bypass Aspire', 180000);

        assert.ok(
            texts.some(text => text.includes('bypass Aspire')),
            `expected the Run/Debug bypass warning, got: ${JSON.stringify(texts)}`);
    });
});
