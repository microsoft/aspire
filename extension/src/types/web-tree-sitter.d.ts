declare module 'web-tree-sitter' {
    interface EmscriptenModule {
        locateFile(path: string, prefix?: string): string;
    }
}
