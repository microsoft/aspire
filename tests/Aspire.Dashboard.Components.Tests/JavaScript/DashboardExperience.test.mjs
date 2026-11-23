// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { runInNewContext } from "node:vm";

const source = await readFile(new URL("../../../src/Aspire.Dashboard/wwwroot/js/app-dashboard-experience.js", import.meta.url), "utf8");

// Runs the script for a URL and returns where it redirects, or null when the page stays.
function navigate(pathname, { search = "", cookie = "", legacyAvailable = true, defaultExperience = "modern" } = {}) {
    let redirect = null;
    const meta = {
        'meta[name="aspire-dashboard-legacy-available"]': { content: legacyAvailable ? "true" : "false" },
        'meta[name="aspire-dashboard-default-experience"]': { content: defaultExperience },
    };
    const document = {
        cookie,
        querySelector: selector => meta[selector] ?? null,
    };
    const window = {
        location: {
            pathname,
            search,
            replace(url) {
                redirect = url;
            },
        },
    };
    runInNewContext(source, { window, document });
    return redirect;
}

const legacyCookie = "Aspire_Dashboard_Experience=legacy";

test("legacy paths are matched case-insensitively and keep the shared page", () => {
    assert.equal(navigate("/LEGACY/traces/detail/abc123", { search: "?spanId=def456", cookie: legacyCookie }), null);
    assert.equal(navigate("/Legacy/Traces/detail/abc123", { search: "?spanId=def456" }), "/Traces/detail/abc123?spanId=def456");
    assert.equal(navigate("/LEGACY"), "/");
});

test("excluded routes are matched case-insensitively", () => {
    assert.equal(navigate("/ERROR", { cookie: legacyCookie }), null);
    assert.equal(navigate("/Login", { cookie: legacyCookie }), null);
    assert.equal(navigate("/API/telemetry", { cookie: legacyCookie }), null);
    assert.equal(navigate("/Terminal-Window/1", { cookie: legacyCookie }), null);
});

test("legacy experience redirects modern pages and keeps the original spelling", () => {
    assert.equal(navigate("/Traces/detail/abc123", { search: "?spanId=def456", cookie: legacyCookie }), "/legacy/Traces/detail/abc123?spanId=def456");
    assert.equal(navigate("/resources", { cookie: legacyCookie }), "/legacy");
    assert.equal(navigate("/traces", { defaultExperience: "legacy" }), "/legacy/traces");
});

test("legacy experience keeps the root query for the resources page", () => {
    assert.equal(navigate("/", { search: "?resource=api", cookie: legacyCookie }), "/legacy?resource=api");
    assert.equal(navigate("/", { search: "?view=Graph&hiddenTypes=Container", cookie: legacyCookie }), "/legacy?view=Graph&hiddenTypes=Container");
    assert.equal(navigate("/resources/api", { search: "?tab=details", cookie: legacyCookie }), "/legacy");
});

test("legacy cookie is ignored when the legacy dashboard is unavailable", () => {
    assert.equal(navigate("/traces", { cookie: legacyCookie, legacyAvailable: false }), null);
    assert.equal(navigate("/legacy/traces", { cookie: legacyCookie, legacyAvailable: false }), "/traces");
});
