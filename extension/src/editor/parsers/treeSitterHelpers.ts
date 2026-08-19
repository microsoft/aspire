import { Node as TreeSitterNode } from 'web-tree-sitter';

export function visit(node: TreeSitterNode, visitor: (node: TreeSitterNode) => boolean | void): boolean {
    if (visitor(node) === false) {
        return false;
    }

    for (const child of node.namedChildren) {
        if (!visit(child, visitor)) {
            return false;
        }
    }

    return true;
}

/**
 * Returns true when the offset falls inside a comment or a string literal, and is therefore not
 * executable code. Textual scans over a document use this to skip commented-out or quoted examples.
 *
 * The check walks up from the innermost node because tree-sitter reports the pieces of a literal
 * (`string_content`, `escape_sequence`, an interpolation's surrounding text) as children of the
 * literal rather than as strings themselves.
 */
export function isInInactiveNode(rootNode: TreeSitterNode, index: number): boolean {
    let node: TreeSitterNode | null = rootNode.descendantForIndex(index);
    while (node) {
        // Grammars name these differently: C# has a single `comment`, Rust splits them into
        // `line_comment` and `block_comment`, and both spell literals `*string*`.
        if (node.type.includes('comment') || node.type.includes('string')) {
            return true;
        }

        node = node.parent;
    }

    return false;
}
