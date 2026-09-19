// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

const observations = new WeakMap();
const ellipsis = "\u2026";

export function compactPath(path, fits) {
    if (fits(path)) {
        return path;
    }

    // Preserve roots and separator runs, e.g. /home/user/src/, C:\work\src,
    // and \\server\share\src. A backslash in a POSIX path such as /home/a\b/src
    // is part of the name, not a separator.
    const windows = /^[a-z]:[\\/]|^\\\\/i.test(path) || (!path.includes("/") && path.includes("\\"));
    const root = path.match(windows ? /^(?:[a-z]:[\\/]+|[\\/]+)/i : /^\/+/)?.[0] ?? "";
    const segments = path.slice(root.length).match(windows ? /[^\\/]+[\\/]*/gu : /[^/]+\/*/gu) ?? [];
    const separator = windows ? path.match(/[\\/]/)?.[0] ?? "\\" : "/";
    let left = Math.floor((segments.length - 1) / 2);
    let right = left + 1;
    while (left >= 0 && right < segments.length) {
        const candidate = root + segments.slice(0, left).join("") + ellipsis + separator + segments.slice(right).join("");
        if (fits(candidate)) {
            return candidate;
        }
        if (left > 0 && (left >= segments.length - right || right === segments.length - 1)) {
            left--;
        } else {
            right++;
        }
    }

    // A single name can itself exceed the available space. Omit it entirely
    // rather than showing a misleading fragment of that name.
    for (const candidate of [root + ellipsis, ellipsis, ""]) {
        if (fits(candidate)) {
            return candidate;
        }
    }
    return "";
}

export function observePath(element) {
    disconnectPath(element);
    const context = document.createElement("canvas").getContext("2d");
    if (!context) {
        throw new Error("Terminal path measurement requires a 2D canvas context.");
    }
    let text = null;
    let disposed = false;
    const refresh = () => {
        if (disposed) {
            return;
        }
        const current = element.querySelector(".terminal-directory-text");
        if (current !== text) {
            if (text) {
                resize.unobserve(text);
            }
            text = current;
            if (text) {
                resize.observe(text);
            }
        }
        if (!text) {
            return;
        }
        context.font = getComputedStyle(text).font;
        const path = element.getAttribute("data-directory") ?? "";
        // clientWidth rounds to whole pixels and can reject a complete path that actually fits.
        const width = text.getBoundingClientRect().width;
        text.querySelector(".terminal-directory-display").textContent =
            compactPath(path, candidate => context.measureText(candidate).width <= width);
    };
    const resize = new ResizeObserver(refresh);
    const mutations = new MutationObserver(refresh);
    mutations.observe(element, { attributes: true, attributeFilter: ["data-directory"] });
    document.fonts.addEventListener("loadingdone", refresh);
    document.fonts.ready.then(refresh);
    observations.set(element, () => {
        disposed = true;
        resize.disconnect();
        mutations.disconnect();
        document.fonts.removeEventListener("loadingdone", refresh);
    });
    refresh();
}

export function disconnectPath(element) {
    observations.get(element)?.();
    observations.delete(element);
}
