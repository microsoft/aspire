import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import process from 'node:process';
import * as vm from 'node:vm';
import ts from '../../../extension/node_modules/typescript/lib/typescript.js';

const [sourcePath, workspaceRoot] = process.argv.slice(2);
assert.ok(sourcePath && workspaceRoot, 'Expected the immutable generator source and temporary workspace.');
assert.equal(process.platform, 'win32');
if (process.env.GITHUB_ACTIONS === 'true') {
    assert.equal(process.arch, 'x64', 'Workflow evidence must use Windows x64.');
}

const text = fs.readFileSync(sourcePath, 'utf8');
const source = ts.createSourceFile('run-e2e.js', text, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
const generator = source.statements.find(statement =>
    ts.isFunctionDeclaration(statement) && statement.name?.text === 'writeWinUiProject');
assert.ok(generator, 'The selected source must declare the actual writeWinUiProject function.');
const header = source.statements
    .filter(ts.isVariableStatement)
    .flatMap(statement => statement.declarationList.declarations)
    .find(declaration => ts.isIdentifier(declaration.name) && declaration.name.text === 'csharpFileHeader');
assert.ok(header, 'The selected source must declare csharpFileHeader.');

// Use the same AST extraction as e2eLaunchProfile.test.ts. Executing run-e2e.js as a
// module would start unrelated VS Code setup; the function body is executed unchanged.
vm.runInNewContext(`const ${header.getText(source)};\n${generator.getText(source)}\nwriteWinUiProject('AspireE2E.WinUI');`, {
    fs,
    path,
    process,
    workspaceRoot,
    winUiReadyMarkerPath: path.join(workspaceRoot, 'winui-e2e-ready.txt'),
});
console.log(JSON.stringify({
    node: process.version,
    architecture: process.arch,
    project: path.join(workspaceRoot, 'AspireE2E.WinUI', 'AspireE2E.WinUI.csproj'),
}));
