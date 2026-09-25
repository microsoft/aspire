import * as fs from 'fs';
import * as path from 'path';
import { getRunRoot } from './paths';

export function readCliLogs(): string {
    const runRoot = getRunRoot();
    if (!runRoot) {
        throw new Error('ASPIRE_EXTENSION_E2E_RUN_ROOT is required to find isolated CLI logs.');
    }

    const logsRoot = path.join(runRoot, 'aspire-home', 'logs');
    if (!fs.existsSync(logsRoot)) {
        return '';
    }

    return fs.readdirSync(logsRoot, { withFileTypes: true })
        .filter(entry => entry.isFile() && entry.name.endsWith('.log'))
        .map(entry => fs.readFileSync(path.join(logsRoot, entry.name), 'utf8'))
        .join('\n');
}

export function readExtensionLogs(): string {
    const runRoot = getRunRoot();
    if (!runRoot) {
        throw new Error('ASPIRE_EXTENSION_E2E_RUN_ROOT is required to find isolated extension logs.');
    }

    const logsRoot = path.join(runRoot, 'storage', 'settings', 'logs');
    if (!fs.existsSync(logsRoot)) {
        return '';
    }

    return readFilesRecursively(logsRoot, 'Aspire Extension.log').join('\n');
}

function readFilesRecursively(directory: string, fileName: string): string[] {
    return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
        const entryPath = path.join(directory, entry.name);
        if (entry.isDirectory()) {
            return readFilesRecursively(entryPath, fileName);
        }

        return entry.isFile() && entry.name === fileName ? [fs.readFileSync(entryPath, 'utf8')] : [];
    });
}
