export function linkMatchTextSize(match) {
    // Include entry overhead so empty capture groups cannot evade the payload budget.
    let size = match.text.length + 16;
    for (const capture of match.captures)
        size += 8 + (capture?.length ?? 0);
    for (const [name, group] of Object.entries(match.groups))
        size += 8 + name.length + (group?.length ?? 0);
    return size;
}
//# sourceMappingURL=link-worker-protocol.js.map