// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { runInNewContext } from "node:vm";

const source = await readFile(new URL("../../../src/Aspire.Dashboard/wwwroot/js/app.js", import.meta.url), "utf8");

// Runs app.js with a time zone described by the minutes behind UTC (Date.getTimezoneOffset) and the short name the
// browser shows in January and July, the dates getTimeZoneShortNames samples.
function getBrowserInfo({ january, july, lang = "en-US", invalidLocales = [] }) {
    const zone = { 0: january, 6: july };
    const locales = [];

    class FakeDate {
        constructor(year, month) {
            this.month = month ?? 10;
        }

        getFullYear() {
            return 2026;
        }

        getTimezoneOffset() {
            return zone[this.month].minutesBehindUtc;
        }
    }

    // Intl.DateTimeFormat is called both with and without new in app.js, so this is a function rather than a class.
    function DateTimeFormat(locale, options) {
        if (options?.timeZoneName) {
            locales.push(locale);
            if (invalidLocales.includes(locale)) {
                throw new RangeError(`Incorrect locale information provided: ${locale}`);
            }
        }

        return {
            resolvedOptions: () => ({ timeZone: "Test/Zone", hourCycle: "h12" }),
            formatToParts: date => [
                { type: "month", value: String(date.month + 1) },
                ...(zone[date.month].name ? [{ type: "literal", value: ", " }, { type: "timeZoneName", value: zone[date.month].name }] : []),
            ],
        };
    }

    const document = {
        documentElement: { lang },
        getElementById: () => null,
        querySelector: () => null,
        addEventListener() {},
        body: {},
    };
    const window = { document };
    runInNewContext(source, {
        window, document,
        navigator: { userAgent: "test-agent" },
        Date: FakeDate,
        Intl: { DateTimeFormat },
        customElements: { define() {} },
        CSSStyleSheet: class { replaceSync() {} },
        // app.js keeps Fluent control roles current as the page changes.
        MutationObserver: class { observe() {} },
    });

    // The result comes from another realm, so compare its JSON rather than the objects.
    return { info: JSON.parse(JSON.stringify(window.getBrowserInfo())), locales };
}

test("getBrowserInfo names standard and daylight saving time with their UTC offsets", () => {
    // America/Los_Angeles: UTC-8 in January and UTC-7 in July.
    const { info } = getBrowserInfo({
        january: { minutesBehindUtc: 480, name: "PST" },
        july: { minutesBehindUtc: 420, name: "PDT" },
    });

    assert.deepEqual(info.timeZoneShortNames, [
        { offsetMinutes: -480, name: "PST" },
        { offsetMinutes: -420, name: "PDT" },
    ]);
    assert.equal(info.timeZone, "Test/Zone");
});

test("getBrowserInfo names daylight saving time in January in the southern hemisphere", () => {
    // Australia/Sydney: UTC+11 in January and UTC+10 in July.
    const { info } = getBrowserInfo({
        january: { minutesBehindUtc: -660, name: "AEDT" },
        july: { minutesBehindUtc: -600, name: "AEST" },
    });

    assert.deepEqual(info.timeZoneShortNames, [
        { offsetMinutes: 660, name: "AEDT" },
        { offsetMinutes: 600, name: "AEST" },
    ]);
});

test("getBrowserInfo names a zone without daylight saving time once", () => {
    // Asia/Kolkata: UTC+5:30 all year.
    const { info } = getBrowserInfo({
        january: { minutesBehindUtc: -330, name: "GMT+5:30" },
        july: { minutesBehindUtc: -330, name: "GMT+5:30" },
    });

    assert.deepEqual(info.timeZoneShortNames, [{ offsetMinutes: 330, name: "GMT+5:30" }]);
});

test("getBrowserInfo names the zone in the dashboard language", () => {
    const { info, locales } = getBrowserInfo({
        lang: "fr-FR",
        january: { minutesBehindUtc: -60, name: "UTC+1" },
        july: { minutesBehindUtc: -120, name: "UTC+2" },
    });

    assert.deepEqual(locales, ["fr-FR"]);
    assert.deepEqual(info.timeZoneShortNames, [
        { offsetMinutes: 60, name: "UTC+1" },
        { offsetMinutes: 120, name: "UTC+2" },
    ]);
});

test("getBrowserInfo falls back to the browser language when the dashboard language is rejected", () => {
    const { info, locales } = getBrowserInfo({
        lang: "not a locale",
        invalidLocales: ["not a locale"],
        january: { minutesBehindUtc: 480, name: "PST" },
        july: { minutesBehindUtc: 420, name: "PDT" },
    });

    assert.deepEqual(locales, ["not a locale", undefined]);
    assert.deepEqual(info.timeZoneShortNames, [
        { offsetMinutes: -480, name: "PST" },
        { offsetMinutes: -420, name: "PDT" },
    ]);
});

test("getBrowserInfo leaves out offsets the browser doesn't name", () => {
    // The server shows the offset instead, such as "UTC-7".
    const { info } = getBrowserInfo({
        january: { minutesBehindUtc: 480, name: "PST" },
        july: { minutesBehindUtc: 420, name: null },
    });

    assert.deepEqual(info.timeZoneShortNames, [{ offsetMinutes: -480, name: "PST" }]);
});
