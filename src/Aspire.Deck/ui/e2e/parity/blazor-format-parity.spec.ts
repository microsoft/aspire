import { expect, test } from "@playwright/test";
import { maskQueryStringValues } from "../../src/lib/maskUrl";
import { parseTelemetryFilters, serializeTelemetryFilters, type TelemetryFilter } from "../../src/lib/telemetryFilters";

// These specs assert on pure functions only, so they never need a browser page. They guard the two
// places where the React dashboard has to agree byte-for-byte with the Blazor dashboard:
//   - DashboardUIHelpers.MaskQueryStringValues        (secret redaction in displayed URLs)
//   - TelemetryFilterFormatter.{Serialize,Deserialize} (shareable telemetry filter deep links)
// A divergence in either is invisible in a screenshot but breaks a real user scenario.

test.describe("URL query-string masking", () => {
  test("masks the browser token in a login URL", () => {
    expect(maskQueryStringValues("http://localhost:5000/login?t=token123")).toBe(
      "http://localhost:5000/login?t=\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf",
    );
  });

  test("leaves URLs without a query string untouched", () => {
    expect(maskQueryStringValues("https://localhost:7001/weather")).toBe("https://localhost:7001/weather");
  });

  test("masks every parameter and keeps the parameter names", () => {
    expect(maskQueryStringValues("https://storage/blob?sv=2021-08-06&sig=abc%2Bdef&se=2030-01-01")).toBe(
      "https://storage/blob?sv=\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf&sig=\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf&se=\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf",
    );
  });

  test("masks value-less parameters so their presence cannot leak a secret flag", () => {
    expect(maskQueryStringValues("https://host/path?token")).toBe(
      "https://host/path?token=\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf",
    );
  });

  test("does not fold the fragment into a masked value", () => {
    expect(maskQueryStringValues("https://host/path?key=secret#section")).toBe(
      "https://host/path?key=\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf#section",
    );
  });

  test("never echoes the secret it was given", () => {
    const secret = "sv-2021-08-06-sig-supersecretvalue";
    expect(maskQueryStringValues(`https://storage/blob?sig=${secret}`)).not.toContain(secret);
  });
});

test.describe("telemetry filter deep links", () => {
  // Produced by TelemetryFilterFormatter.SerializeFiltersToString in the Blazor dashboard.
  const blazorLink = "Severity:equals:Error Message:!contains:health+check";

  test("parses the Blazor space-and-colon delimited format", () => {
    expect(parseTelemetryFilters(blazorLink)).toEqual([
      { id: "restored-0", field: "Severity", condition: "equals", value: "Error", enabled: true },
      { id: "restored-1", field: "Message", condition: "notContains", value: "health check", enabled: true },
    ]);
  });

  test("honours the trailing disabled marker", () => {
    const [filter] = parseTelemetryFilters("url:equals:%2Fapi%2Fv1:disabled");
    expect(filter).toEqual({ id: "restored-0", field: "url", condition: "equals", value: "/api/v1", enabled: false });
  });

  test("maps every condition to the Blazor wire name and back", () => {
    const filters: TelemetryFilter[] = (
      ["equals", "contains", "gt", "lt", "gte", "lte", "notEquals", "notContains"] as const
    ).map((condition, index) => ({
      id: `f${index}`,
      field: "Duration",
      condition,
      value: "100",
      enabled: true,
    }));

    expect(serializeTelemetryFilters(filters)).toBe(
      [
        "Duration:equals:100",
        "Duration:contains:100",
        "Duration:gt:100",
        "Duration:lt:100",
        "Duration:gte:100",
        "Duration:lte:100",
        "Duration:!equals:100",
        "Duration:!contains:100",
      ].join(" "),
    );

    expect(parseTelemetryFilters(serializeTelemetryFilters(filters) ?? null).map((f) => f.condition)).toEqual(
      filters.map((f) => f.condition),
    );
  });

  test("escapes delimiters so fields and values survive a round trip", () => {
    const filters: TelemetryFilter[] = [
      { id: "a", field: "http.request.header", condition: "equals", value: "http://host:8080/a b", enabled: false },
    ];

    const serialized = serializeTelemetryFilters(filters);
    expect(serialized).not.toBeUndefined();
    // The raw colons and space in the value must not appear as delimiters.
    expect(serialized!.split(" ")).toHaveLength(1);
    expect(serialized!.split(":")).toHaveLength(4);

    expect(parseTelemetryFilters(serialized ?? null)).toEqual([
      { id: "restored-0", field: "http.request.header", condition: "equals", value: "http://host:8080/a b", enabled: false },
    ]);
  });

  test("keeps the valid filters when one entry in the link is malformed", () => {
    // Previously the whole string was JSON.parse'd inside a try/catch that returned [], so a single
    // bad entry silently discarded every filter in the link.
    expect(parseTelemetryFilters("Severity:equals:Error garbage Message:contains:boom")).toEqual([
      { id: "restored-0", field: "Severity", condition: "equals", value: "Error", enabled: true },
      { id: "restored-2", field: "Message", condition: "contains", value: "boom", enabled: true },
    ]);
  });

  test("rejects unknown conditions rather than inventing one", () => {
    expect(parseTelemetryFilters("Severity:startsWith:Error")).toEqual([]);
  });

  test("returns nothing for empty input and omits the parameter when there are no filters", () => {
    expect(parseTelemetryFilters(null)).toEqual([]);
    expect(parseTelemetryFilters("")).toEqual([]);
    expect(serializeTelemetryFilters([])).toBeUndefined();
  });
});
