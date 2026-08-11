import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { defineConfig } from '@vscode/test-cli';

const userDataDirectory = mkdtempSync(join(tmpdir(), 'aspire-vscode-test-'));
process.once('exit', () => rmSync(userDataDirectory, { recursive: true, force: true }));

export default defineConfig({
	files: 'out/test/**/*.test.js',
	launchArgs: [`--user-data-dir=${userDataDirectory}`],
	download: {
		timeout: 60000
	},
	mocha: {
		ui: 'tdd',
		timeout: 20000
	}
});
