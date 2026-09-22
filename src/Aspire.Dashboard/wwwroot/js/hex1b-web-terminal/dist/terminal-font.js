const defaultFontUrl = new URL("./fonts/cascadia-mono-nf/CascadiaMonoNF.woff2", import.meta.url).href;
const genericFamilies = new Set(["monospace", "serif", "sans-serif", "system-ui"]);
/** Resolve caller-relative URLs before sending the configuration to the worker. */
export function normalizeFont(font, baseUrl = import.meta.url) {
    font ??= { family: "Cascadia Mono NF", faces: [{ url: defaultFontUrl, weight: "200 700" }] };
    if (typeof font !== "object" || Array.isArray(font) ||
        typeof font.family !== "string" || !font.family.trim() || font.family.length > 256 ||
        /[\u0000-\u001f\u007f]/u.test(font.family)) {
        throw new TypeError("font.family must be a single, nonempty font family name");
    }
    const faces = font.faces ?? [];
    if (!Array.isArray(faces) || faces.length > 16)
        throw new TypeError("font.faces must contain at most 16 font sources");
    return {
        family: font.family.trim(),
        faces: faces.map(face => {
            if (!face || typeof face.url !== "string" || !face.url.trim()) {
                throw new TypeError("Each font face requires a URL");
            }
            for (const field of ["weight", "style"]) {
                if (face[field] !== undefined && (typeof face[field] !== "string" || face[field].length > 64)) {
                    throw new TypeError(`Font face ${field} must be a CSS descriptor string`);
                }
            }
            return {
                url: new URL(face.url, baseUrl).href,
                weight: face.weight ?? "400",
                style: face.style ?? "normal"
            };
        })
    };
}
/** Use the rendering context's FontFaceSet; a worker cannot inherit page fonts. */
export async function loadFont(configuration) {
    const { family, faces } = normalizeFont(configuration);
    const scope = globalThis;
    const fontSet = "fonts" in scope && isFontSet(scope.fonts)
        ? scope.fonts : globalThis.document?.fonts;
    if (!fontSet || typeof FontFace !== "function") {
        throw new Error("Terminal font loading requires the CSS Font Loading API in the rendering context");
    }
    const generic = genericFamilies.has(family.toLowerCase());
    const cssFamily = generic ? family.toLowerCase() : JSON.stringify(family);
    if (generic && faces.length)
        throw new TypeError("A generic font family cannot have downloadable faces");
    const sources = faces.length ? faces : generic ? [] : [{ local: family }];
    const loaded = [];
    for (const source of sources) {
        try {
            const face = new FontFace(family, "local" in source ? `local(${JSON.stringify(source.local)})` : `url(${JSON.stringify(source.url)})`, { weight: source.weight ?? "400", style: source.style ?? "normal" });
            loaded.push(await face.load());
        }
        catch (error) {
            throw new Error(`Could not load terminal font "${family}" from ${"url" in source ? source.url : "local fonts"}`, { cause: error });
        }
    }
    for (const face of loaded)
        fontSet.add(face);
    return {
        family, cssFamily,
        dispose() { for (const face of loaded)
            fontSet.delete(face); }
    };
}
/** Fit one font-wide advance and line box to the authoritative cell dimensions. */
export function measureFont(raster, cssFamily, attributes, scale, cellWidth, cellHeight) {
    const font = `${attributes & 4 ? "italic " : ""}${attributes & 1 ? "bold " : ""}${16 * scale}px ${cssFamily}`;
    raster.font = font;
    raster.textBaseline = "alphabetic";
    raster.textAlign = "left";
    const metrics = raster.measureText("M");
    const lineHeight = metrics.fontBoundingBoxAscent + metrics.fontBoundingBoxDescent;
    if (!Number.isFinite(metrics.width) || metrics.width <= 0 ||
        !Number.isFinite(lineHeight) || lineHeight <= 0) {
        throw new Error(`Invalid terminal font metrics for ${cssFamily}`);
    }
    const xScale = cellWidth * scale / metrics.width;
    const yScale = cellHeight * scale / lineHeight;
    return { font, xScale, yScale, baseline: metrics.fontBoundingBoxAscent * yScale };
}
import { isRecord } from "./validation.js";
function isFontSet(value) {
    return isRecord(value) && typeof value.add === "function" && typeof value.delete === "function";
}
//# sourceMappingURL=terminal-font.js.map