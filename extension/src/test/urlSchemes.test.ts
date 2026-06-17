import * as assert from 'assert';
import { isLinkableUrl } from '../utils/urlSchemes';

suite('isLinkableUrl', () => {
    test('http URLs are linkable', () => {
        assert.strictEqual(isLinkableUrl('http://localhost:5000'), true);
    });

    test('https URLs are linkable', () => {
        assert.strictEqual(isLinkableUrl('https://example.com'), true);
    });

    test('vscode:// URLs are linkable (custom scheme)', () => {
        assert.strictEqual(isLinkableUrl('vscode://extension/id'), true);
    });

    test('ftp URLs are linkable', () => {
        assert.strictEqual(isLinkableUrl('ftp://files.example.com'), true);
    });

    test('tcp URLs are not linkable', () => {
        assert.strictEqual(isLinkableUrl('tcp://localhost:1433'), false);
    });

    test('redis URLs are not linkable', () => {
        assert.strictEqual(isLinkableUrl('redis://localhost:6379'), false);
    });

    test('rediss URLs are not linkable', () => {
        assert.strictEqual(isLinkableUrl('rediss://localhost:6380'), false);
    });

    test('ws URLs are not linkable', () => {
        assert.strictEqual(isLinkableUrl('ws://localhost:8080'), false);
    });

    test('wss URLs are not linkable', () => {
        assert.strictEqual(isLinkableUrl('wss://localhost:8080'), false);
    });

    test('telnet URLs are not linkable', () => {
        assert.strictEqual(isLinkableUrl('telnet://localhost:23'), false);
    });

    test('gopher URLs are not linkable', () => {
        assert.strictEqual(isLinkableUrl('gopher://example.com'), false);
    });

    test('news URLs are not linkable', () => {
        assert.strictEqual(isLinkableUrl('news://news.example.com'), false);
    });

    test('nntp URLs are not linkable', () => {
        assert.strictEqual(isLinkableUrl('nntp://news.example.com'), false);
    });

    test('scheme matching is case-insensitive', () => {
        assert.strictEqual(isLinkableUrl('TCP://localhost:1433'), false);
        assert.strictEqual(isLinkableUrl('Redis://localhost:6379'), false);
        assert.strictEqual(isLinkableUrl('HTTP://localhost:5000'), true);
        assert.strictEqual(isLinkableUrl('HTTPS://example.com'), true);
    });

    test('invalid URLs return false', () => {
        assert.strictEqual(isLinkableUrl('not a url'), false);
        assert.strictEqual(isLinkableUrl(''), false);
    });
});
