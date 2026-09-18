import { LINK_LIMITS } from "./link-options.js";
import { linkMatchTextSize } from "./link-worker-protocol.js";
function trimProse(text) {
    let end = text.length;
    const pairs = { ")": "(", "]": "[", "}": "{" };
    while (end > 0) {
        const char = text[end - 1];
        if (".,;:!?".includes(char)) {
            end--;
            continue;
        }
        if (pairs[char]) {
            const current = text.slice(0, end);
            if (current.split(char).length > current.split(pairs[char]).length) {
                end--;
                continue;
            }
        }
        break;
    }
    return text.slice(0, end);
}
export function scanLinks(request) {
    try {
        const builtin = "builtin" in request.rule ? request.rule.builtin : undefined;
        let regex;
        if ("source" in request.rule) {
            const flags = request.rule.flags.replaceAll("g", "");
            regex = new RegExp(request.rule.source, `${flags}g`);
        }
        else {
            const sources = {
                url: String.raw `https?:\/\/[^\s\0<>"'\x60]+`,
                uri: String.raw `[A-Za-z][A-Za-z0-9+.-]*:[^\s\0<>"'\x60]+`,
                absolutePath: String.raw `(?:[A-Za-z]:[\\/]|/)[^\s\0<>"'\x60]*`,
                homePath: String.raw `~/[^\s\0<>"'\x60]+`,
            };
            regex = new RegExp(sources[request.rule.builtin], "giu");
        }
        let count = 0, attempts = 0, total = 0, resultText = 0;
        const results = [];
        for (const chunk of request.chunks) {
            total += chunk.text.length;
            if (chunk.text.length > LINK_LIMITS.chunk || total > LINK_LIMITS.totalText) {
                return { id: request.id, error: "limit", message: "Link scan text limit exceeded" };
            }
            regex.lastIndex = 0;
            const matches = [];
            let match;
            while ((match = regex.exec(chunk.text)) !== null) {
                if (++attempts > LINK_LIMITS.totalText || count >= LINK_LIMITS.matches) {
                    return { id: request.id, error: "limit", message: "Link match limit exceeded" };
                }
                if (!match[0].length) {
                    const point = chunk.text.codePointAt(regex.lastIndex);
                    regex.lastIndex += (regex.unicode || regex.unicodeSets) && point !== undefined && point > 0xffff ? 2 : 1;
                    continue;
                }
                let text = match[0];
                if (builtin) {
                    const before = chunk.text[match.index - 1];
                    if (before !== undefined && !/[\s([{"'<>=]/u.test(before))
                        continue;
                    if (builtin === "uri" && /^[a-z]:[\\/]/iu.test(text))
                        continue;
                    text = trimProse(text);
                    if (!text || (builtin === "absolutePath" && (text === "/" || /^[a-z]:[\\/]$/iu.test(text))))
                        continue;
                    if (builtin === "url") {
                        try {
                            if (!new URL(text).hostname)
                                continue;
                        }
                        catch {
                            continue;
                        }
                    }
                }
                const candidate = { index: match.index, text, captures: match.slice(1), groups: { ...match.groups } };
                resultText += linkMatchTextSize(candidate);
                if (resultText > LINK_LIMITS.resultText) {
                    return { id: request.id, error: "limit", message: "Link result text limit exceeded" };
                }
                matches.push(candidate);
                count++;
            }
            results.push({ key: chunk.key, matches });
        }
        return { id: request.id, results };
    }
    catch (error) {
        return { id: request.id, error: "worker", message: error instanceof Error ? error.message : String(error) };
    }
}
if (typeof self !== "undefined" && typeof self.postMessage === "function") {
    self.addEventListener("message", (event) => {
        self.postMessage(scanLinks(event.data));
    });
}
//# sourceMappingURL=link-detection-worker.js.map