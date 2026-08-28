import * as assert from 'assert';
import * as path from 'path';
import { getAbsoluteLogFilePath, getDiagnosticLogPath } from '../utils/diagnosticLogPath';

function absolutePath(...segments: string[]): string {
    return path.join(path.parse(process.cwd()).root, ...segments);
}

suite('diagnostic log paths', () => {
    test('extracts paths from shipped translations with text after the placeholder', () => {
        const logFilePath = absolutePath('logs with spaces', 'cli.log');

        assert.strictEqual(getAbsoluteLogFilePath(`Protokolle unter ${logFilePath} anzeigen.`), logFilePath);
        assert.strictEqual(getAbsoluteLogFilePath(`${logFilePath} adresinde günlüklere bakın`), logFilePath);
        assert.strictEqual(getAbsoluteLogFilePath(`${logFilePath}에서 로그 보기`), logFilePath);
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
        assert.strictEqual(getAbsoluteLogFilePath('See logs at relative/cli.log'), undefined);
        assert.strictEqual(
            getDiagnosticLogPath('📄 See logs at relative/cli.log', '📄', 'See logs at '),
            undefined);
    });
});
