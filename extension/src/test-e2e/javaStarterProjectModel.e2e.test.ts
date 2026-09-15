import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { fileURLToPath } from 'url';
import { isSamePath } from './helpers/assertions';
import { executeE2eControlCommand } from './helpers/fixtures';
import { getJavaStarterAppHostSourcePath, prepareJavaStarterWorkspace, waitForJavaLanguageServerImport } from './helpers/java';
import { getWorkspaceRoot } from './helpers/paths';

interface DefinitionInfo {
    filePath: string;
    line: number;
}

// A cold Java language server can report import completion before its semantic project commands
// become responsive under CI load, so these requests need a separate post-import timeout budget.
const javaLanguageServerCommandTimeoutMs = 120000;

suite('Java starter project model E2E', function () {
    this.timeout(1200000);

    suiteSetup(async () => {
        await prepareJavaStarterWorkspace();
        await executeE2eControlCommand({ name: 'openFile', filePath: getJavaStarterAppHostSourcePath() });
        await waitForJavaLanguageServerImport();
    });

    test('assigns the root AppHost and generated Aspire SDK to one semantic project while keeping the API in Gradle', async () => {
        const workspaceRoot = getWorkspaceRoot();
        const appHostPath = getJavaStarterAppHostSourcePath();
        const source = fs.readFileSync(appHostPath, 'utf8');
        const symbol = 'DistributedApplication';
        const symbolOffset = source.indexOf(symbol);
        assert.ok(symbolOffset >= 0, `Expected ${symbol} in ${appHostPath}.`);

        const beforeSymbol = source.slice(0, symbolOffset);
        const line = beforeSymbol.split(/\r?\n/).length - 1;
        const character = symbolOffset - (beforeSymbol.lastIndexOf('\n') + 1);
        const definitions = (await executeE2eControlCommand({
            name: 'getDefinitions',
            filePath: appHostPath,
            line,
            character,
        }, { timeoutMs: javaLanguageServerCommandTimeoutMs })).result as DefinitionInfo[];

        const generatedModulesRoot = path.join(workspaceRoot, '.aspire', 'modules');
        assert.ok(
            definitions.some(definition => isPathInside(definition.filePath, generatedModulesRoot)),
            `Expected ${symbol} to resolve under ${generatedModulesRoot}. Definitions: ${JSON.stringify(definitions)}`);

        const projectUris = (await executeE2eControlCommand({ name: 'getJavaProjects' }, { timeoutMs: javaLanguageServerCommandTimeoutMs })).result as string[];
        const projectPaths = projectUris.map(uri => fileURLToPath(uri));
        assert.ok(projectPaths.some(projectPath => isSamePath(projectPath, workspaceRoot)), `Expected a Java project rooted at ${workspaceRoot}. Projects: ${JSON.stringify(projectPaths)}`);
        assert.ok(projectPaths.some(projectPath => isSamePath(projectPath, path.join(workspaceRoot, 'api'))), `Expected the nested Gradle API project. Projects: ${JSON.stringify(projectPaths)}`);
    });
});

function isPathInside(filePath: string, directoryPath: string): boolean {
    const relativePath = path.relative(directoryPath, filePath);
    return relativePath !== '' && !relativePath.startsWith('..') && !path.isAbsolute(relativePath);
}