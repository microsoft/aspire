import * as assert from 'assert';
import { getCommonExcludeGlob, findAspireSettingsFiles } from '../utils/workspace';

suite('utils/workspace tests', () => {

    test('getCommonExcludeGlob returns valid glob pattern', () => {
        const glob = getCommonExcludeGlob();

        assert.ok(glob.startsWith('{'), 'Glob should start with {');
        assert.ok(glob.endsWith('}'), 'Glob should end with }');
        assert.ok(glob.includes('**/node_modules/**'), 'Glob should include node_modules');
        assert.ok(glob.includes('**/[Bb]in/**'), 'Glob should include bin');
        assert.ok(glob.includes('**/[Oo]bj/**'), 'Glob should include obj');
        assert.ok(glob.includes('**/artifacts/**'), 'Glob should include artifacts');
    });

    test('findAspireSettingsFiles uses correct exclude pattern', async function () {
        this.timeout(10000);

        // Call findAspireSettingsFiles and verify it returns results (may be empty if no settings files exist)
        // The main point is that it executes without error and uses the exclude pattern
        const results = await findAspireSettingsFiles();

        // Results should be an array (possibly empty)
        assert.ok(Array.isArray(results), 'findAspireSettingsFiles should return an array');

        // Verify that any results found are not in excluded directories
        const excludeGlob = getCommonExcludeGlob();
        for (const uri of results) {
            const filePath = uri.fsPath;
            assert.ok(!filePath.includes('/node_modules/'), `Result should not be in node_modules: ${filePath}`);
            assert.ok(!filePath.includes('/bin/') && !filePath.includes('/Bin/'), `Result should not be in bin: ${filePath}`);
            assert.ok(!filePath.includes('/obj/') && !filePath.includes('/Obj/'), `Result should not be in obj: ${filePath}`);
            assert.ok(!filePath.includes('/artifacts/'), `Result should not be in artifacts: ${filePath}`);
        }
    });

    test('getCommonExcludeGlob includes all expected directories', () => {
        const glob = getCommonExcludeGlob();

        // Build outputs
        assert.ok(glob.includes('**/artifacts/**'), 'Should exclude artifacts');
        assert.ok(glob.includes('**/[Bb]in/**'), 'Should exclude bin (case-insensitive)');
        assert.ok(glob.includes('**/[Oo]bj/**'), 'Should exclude obj (case-insensitive)');
        assert.ok(glob.includes('**/dist/**'), 'Should exclude dist');
        assert.ok(glob.includes('**/out/**'), 'Should exclude out');
        assert.ok(glob.includes('**/build/**'), 'Should exclude build');
        assert.ok(glob.includes('**/publish/**'), 'Should exclude publish');

        // Dependencies
        assert.ok(glob.includes('**/node_modules/**'), 'Should exclude node_modules');
        assert.ok(glob.includes('**/.venv/**'), 'Should exclude .venv');
        assert.ok(glob.includes('**/packages/**'), 'Should exclude packages');

        // IDE/Tool directories
        assert.ok(glob.includes('**/.vs/**'), 'Should exclude .vs');
        assert.ok(glob.includes('**/.vscode-test/**'), 'Should exclude .vscode-test');
        assert.ok(glob.includes('**/.idea/**'), 'Should exclude .idea');
        assert.ok(glob.includes('**/.git/**'), 'Should exclude .git');
    });
});
