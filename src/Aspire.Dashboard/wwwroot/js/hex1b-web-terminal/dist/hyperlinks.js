/** OSC 8 destinations are untrusted output, not page-relative navigation. */
export function hyperlinkUri(uri) {
    if (/[\u0000-\u0020\u007f]/u.test(uri) || !URL.canParse(uri))
        return null;
    const url = new URL(uri);
    return ["https:", "http:", "mailto:"].includes(url.protocol) ? url.href : null;
}
export class Hyperlinks {
    #rows = new Map();
    update(ranges) {
        this.#rows.clear();
        const destinations = new Map();
        for (const range of ranges) {
            if (!destinations.has(range.uri))
                destinations.set(range.uri, hyperlinkUri(range.uri));
            const uri = destinations.get(range.uri);
            if (!uri)
                continue;
            const row = this.#rows.get(range.row) ?? [];
            row.push({ ...range, uri });
            this.#rows.set(range.row, row);
        }
    }
    at(point) {
        return this.#rows.get(point.y)?.find(range => point.x >= range.startColumn && point.x < range.endColumn)?.uri ?? null;
    }
}
//# sourceMappingURL=hyperlinks.js.map