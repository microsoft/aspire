import { defineConfig } from '@vscode/test-cli';

export default defineConfig({
	files: 'out/test/**/*.test.js',
	download: {
		timeout: 60000
	},
	mocha: {
		ui: 'tdd',
		timeout: 20000
	}
});
