import * as assert from 'assert';
import * as path from 'path';
import { getAbsolutePathSuffix, getDiagnosticLogPath } from '../utils/diagnosticLogPath';

function absolutePath(...segments: string[]): string {
    return path.join(path.parse(process.cwd()).root, ...segments);
}

suite('diagnostic log paths', () => {
    test('extracts an absolute path after a localized label', () => {
        const logFilePath = absolutePath('logs with spaces', 'cli.log');

        assert.strictEqual(
            getAbsolutePathSuffix(`Diagnoseprotokoll: ${logFilePath}`),
            logFilePath);
    });

    test('extracts status-icon and English diagnostic log paths', () => {
        const logFilePath = absolutePath('logs', 'cli.log');

        assert.strictEqual(
            getDiagnosticLogPath(`📄 Diagnoseprotokoll: ${logFilePath}`, '📄', 'See logs at '),
            logFilePath);
        assert.strictEqual(
            getDiagnosticLogPath(`See logs at ${logFilePath}`, '📄', 'See logs at '),
            logFilePath);
    });

    test('rejects relative diagnostic log paths', () => {
        assert.strictEqual(getAbsolutePathSuffix('See logs at relative/cli.log'), undefined);
        assert.strictEqual(
            getDiagnosticLogPath('📄 See logs at relative/cli.log', '📄', 'See logs at '),
            undefined);
    });
});
