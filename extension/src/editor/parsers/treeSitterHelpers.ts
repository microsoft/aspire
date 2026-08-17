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
