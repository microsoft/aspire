import * as assert from 'assert';
import * as sinon from 'sinon';
import { runInNewContext } from 'vm';
import { VSBrowser } from '../test-e2e/helpers/extester';
import { waitForExplorerSelection } from '../test-e2e/helpers/vscode';

suite('E2E Explorer source selection', () => {
    const expectedPath = ['AspireE2E.Worker', 'AspireE2E.Worker.csproj'];
    let sandbox: sinon.SinonSandbox;
    let selection: { focused: boolean; path: string[] };

    setup(() => {
        sandbox = sinon.createSandbox();
        selection = { focused: true, path: ['workspace', ...expectedPath] };
        const driver = {
            executeScript: sandbox.stub().callsFake(async (script: string) => {
                const rows = selection.path.map((label, index) => ({
                    getAttribute: (name: string) => {
                        switch (name) {
                            case 'aria-level': return String(index + 1);
                            case 'data-index': return String(index);
                            default: return null;
                        }
                    },
                    classList: { contains: (name: string) => name === 'selected' && index === selection.path.length - 1 },
                    querySelector: (selector: string) => selector === '.label-name' ? { textContent: label } : null,
                }));
                const explorer = {
                    querySelectorAll: (selector: string) => selector === '.monaco-list-row' ? [...rows].reverse() : [],
                    contains: (element: unknown): boolean => element === explorer,
                };
                const document = {
                    querySelector: (selector: string) => selector === '.explorer-folders-view' ? explorer : null,
                    activeElement: selection.focused ? explorer : null,
                };

                return runInNewContext(`(function () { ${script} })()`, { document });
            }),
            wait: sandbox.stub().callsFake(async (condition: () => Promise<boolean>) => {
                if (!await condition()) {
                    throw new Error('Explorer selection did not match.');
                }
            }),
        };
        sandbox.stub(VSBrowser, 'instance').get(() => ({ driver }));
    });

    teardown(() => {
        sandbox.restore();
    });

    test('accepts the expected selected file when Explorer has focus', async () => {
        await waitForExplorerSelection(expectedPath);
    });

    test('rejects the expected selection when Explorer does not have focus', async () => {
        selection.focused = false;
        await assert.rejects(waitForExplorerSelection(expectedPath), /Last Explorer selection:.*"focused":false/);
    });

    test('rejects the same filename in a different project folder', async () => {
        selection.path = ['workspace', 'AnotherWorker', expectedPath[1]];
        await assert.rejects(waitForExplorerSelection(expectedPath), /Last Explorer selection:.*AnotherWorker/);
    });

    test('rejects another file in the expected project folder', async () => {
        selection.path = ['workspace', expectedPath[0], 'Program.cs'];
        await assert.rejects(waitForExplorerSelection(expectedPath), /Last Explorer selection:.*Program.cs/);
    });

    test('rejects a focused Explorer with no selection', async () => {
        selection.path = [];
        await assert.rejects(waitForExplorerSelection(expectedPath), /Last Explorer selection:.*"path":\[\]/);
    });
});
