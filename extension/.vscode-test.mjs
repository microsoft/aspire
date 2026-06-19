import { defineConfig } from '@vscode/test-cli';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const userDataDir = join(tmpdir(), `aspire-vscode-test-user-data-${process.pid}`);

export default defineConfig({
	files: 'out/test/**/*.test.js',
	launchArgs: ['--user-data-dir', userDataDir],
	download: {
		timeout: 60000
	},
	mocha: {
		ui: 'tdd',
		timeout: 20000
	}
});
