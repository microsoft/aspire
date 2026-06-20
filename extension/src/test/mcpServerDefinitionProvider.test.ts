import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireMcpServerDefinitionProvider } from '../mcp/AspireMcpServerDefinitionProvider';

suite('AspireMcpServerDefinitionProvider tests', () => {
    test('wraps Windows cmd shim CLI path for MCP server definition', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';
        const provider = new AspireMcpServerDefinitionProvider();

        try {
            const testProvider = provider as unknown as {
                _cliAvailable: boolean;
                _shouldProvide: boolean;
                _cliPath: string;
            };
            testProvider._cliAvailable = true;
            testProvider._shouldProvide = true;
            testProvider._cliPath = 'C:\\Tools\\aspire.cmd';

            const definitions = provider.provideMcpServerDefinitions(new vscode.CancellationTokenSource().token);

            assert.ok(Array.isArray(definitions));
            assert.strictEqual(definitions.length, 1);
            assert.strictEqual(definitions[0].command, process.env.ComSpec);
            assert.deepStrictEqual(definitions[0].args, ['/d', '/s', '/c', '"C:\\Tools\\aspire.cmd ^"agent^" ^"mcp^""']);
        }
        finally {
            provider.dispose();
            platformStub.restore();

            if (originalComSpec === undefined) {
                delete process.env.ComSpec;
            }
            else {
                process.env.ComSpec = originalComSpec;
            }
        }
    });
});
