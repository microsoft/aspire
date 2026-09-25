import * as path from 'path';
import { Parser } from 'web-tree-sitter';

let initializationPromise: Promise<void> | undefined;

export async function initializeTreeSitter(): Promise<void> {
    initializationPromise ??= Parser.init({
        locateFile: () => getWebTreeSitterWasmPath(),
    }).catch(error => {
        initializationPromise = undefined;
        throw error;
    });

    await initializationPromise;
}

export function __resetTreeSitterForTests(): void {
    initializationPromise = undefined;
}

function getWebTreeSitterWasmPath(): string {
    const resolvedPath = require.resolve('web-tree-sitter/web-tree-sitter.wasm');
    return typeof resolvedPath === 'string'
        ? resolvedPath
        : resolveBundledWasmAssetPath(require('web-tree-sitter/web-tree-sitter.wasm'));
}

export function resolveBundledWasmAssetPath(assetPath: string): string {
    return path.isAbsolute(assetPath) ? assetPath : path.join(__dirname, assetPath);
}