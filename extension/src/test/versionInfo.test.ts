import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { readVersionFile } from '../utils/versionInfo';

import { removeDirectorySafely } from './testHelpers';
suite('utils/versionInfo tests', () => {
	test('readVersionFile returns SHA when .version file exists', () => {
		const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'versioninfo-test-'));
		try {
			const sha = 'abc123def456abc123def456abc123def456abc1';
			fs.writeFileSync(path.join(tmpDir, '.version'), sha);
			const result = readVersionFile(tmpDir);
			assert.strictEqual(result, sha);
		} finally {
			removeDirectorySafely(tmpDir);
		}
	});

	test('readVersionFile trims whitespace from .version file', () => {
		const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'versioninfo-test-'));
		try {
			const sha = 'abc123def456abc123def456abc123def456abc1';
			fs.writeFileSync(path.join(tmpDir, '.version'), `  ${sha}\n`);
			const result = readVersionFile(tmpDir);
			assert.strictEqual(result, sha);
		} finally {
			removeDirectorySafely(tmpDir);
		}
	});

	test('readVersionFile returns "unknown" when .version file is missing', () => {
		const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'versioninfo-test-'));
		try {
			const result = readVersionFile(tmpDir);
			assert.strictEqual(result, 'unknown');
		} finally {
			removeDirectorySafely(tmpDir);
		}
	});
});
