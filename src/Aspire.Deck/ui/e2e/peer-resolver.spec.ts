import { expect, test } from "@playwright/test";
import { getPeerAddress, resolvePeerName, uninstrumentedPeers } from "../src/lib/peerResolver";

// Pure-logic coverage for uninstrumented peer resolution. Mirrors OtlpHelpers.GetPeerAddress,
// TelemetryRepository.CalculateTraceUninstrumentedPeers, ResourceOutgoingPeerResolver and
// BrowserLinkOutgoingPeerResolver in the Blazor dashboard.

function attrs(values: Record<string, string>) {
  return Object.entries(values).map(([key, value]) => ({ key, value }));
}

function resource(name: string, urls: string[], displayName = name) {
  return { name, displayName, urls: urls.map((url) => ({ url })) };
}

function span(
  spanId: string,
  kind: string,
  values: Record<string, string>,
  parentSpanId: string | null = null,
) {
  return { spanId, parentSpanId, kind, attributes: attrs(values) };
}

test.describe("getPeerAddress", () => {
  test("prefers peer.service over the newer and older address attributes", () => {
    const address = getPeerAddress(attrs({
      "peer.service": "cache",
      "server.address": "localhost",
      "server.port": "6379",
    }));

    expect(address).toBe("cache");
  });

  test("falls back to server.address and server.port, which OTEL HTTP 1.7.0 emits instead", () => {
    expect(getPeerAddress(attrs({ "server.address": "localhost", "server.port": "18889" })))
      .toBe("localhost:18889");
  });

  test("uses server.address alone when no port is reported", () => {
    expect(getPeerAddress(attrs({ "server.address": "localhost" }))).toBe("localhost");
  });

  test("falls back to the pre-1.7.0 net.peer.name and net.peer.port names", () => {
    expect(getPeerAddress(attrs({ "net.peer.name": "db", "net.peer.port": "5432" })))
      .toBe("db:5432");
  });

  test("returns null when the span names no peer", () => {
    expect(getPeerAddress(attrs({ "http.method": "GET" }))).toBeNull();
  });
});

test.describe("resolvePeerName", () => {
  const resources = [resource("cache", ["http://localhost:6379"]), resource("api", ["https://localhost:7001"])];

  test("matches a peer address against a resource's published endpoint", () => {
    expect(resolvePeerName(attrs({ "server.address": "localhost", "server.port": "6379" }), resources))
      .toBe("cache");
  });

  test("normalizes the loopback address to localhost, as some libraries report 127.0.0.1", () => {
    expect(resolvePeerName(attrs({ "server.address": "127.0.0.1", "server.port": "6379" }), resources))
      .toBe("cache");
  });

  test("normalizes the container host alias used when a container calls back to the host", () => {
    expect(resolvePeerName(attrs({ "peer.service": "host.docker.internal:7001" }), resources))
      .toBe("api");
  });

  test("accepts the comma port separator that SQL Server connection strings use", () => {
    const sql = [resource("sql", ["tcp://localhost:1433"])];

    expect(resolvePeerName(attrs({ "peer.service": "localhost,1433" }), sql)).toBe("sql");
  });

  test("qualifies the name with the instance id when replicas share a display name", () => {
    const replicas = [
      resource("worker-abc", ["http://localhost:9001"], "worker"),
      resource("worker-def", ["http://localhost:9002"], "worker"),
    ];

    expect(resolvePeerName(attrs({ "peer.service": "localhost:9002" }), replicas)).toBe("worker-worker-def");
  });

  test("returns null when no resource publishes the address", () => {
    expect(resolvePeerName(attrs({ "server.address": "example.com", "server.port": "443" }), resources))
      .toBeNull();
  });

  test("recognizes the BrowserLink script-tag request by the shape of its URL", () => {
    const url = "http://localhost:59267/6eed7c2dedc14419901b813e8fe87a86/getScriptTag";

    expect(resolvePeerName(attrs({ "url.full": url }), [])).toBe("Browser Link");
    expect(resolvePeerName(attrs({ "http.url": url }), [])).toBe("Browser Link");
  });

  test("does not mistake an unrelated getScriptTag URL for BrowserLink", () => {
    // Wrong host, and the path segment is not a 32-character id.
    expect(resolvePeerName(attrs({ "url.full": "http://example.com/app/getScriptTag" }), [])).toBeNull();
  });
});

test.describe("uninstrumentedPeers", () => {
  const resources = [resource("cache", ["http://localhost:6379"])];
  const peerAttrs = { "server.address": "localhost", "server.port": "6379" };

  test("attributes a childless client span to the resource it called", () => {
    const peers = uninstrumentedPeers([span("a", "Client", peerAttrs)], resources);

    expect([...peers]).toEqual([["a", "cache"]]);
  });

  test("attributes a producer span, which also represents an outbound call", () => {
    const peers = uninstrumentedPeers([span("a", "Producer", peerAttrs)], resources);

    expect([...peers]).toEqual([["a", "cache"]]);
  });

  test("ignores a span whose callee emitted its own spans", () => {
    // A child span means the callee is instrumented, so there is no hole to fill.
    const peers = uninstrumentedPeers(
      [span("a", "Client", peerAttrs), span("b", "Server", {}, "a")],
      resources,
    );

    expect([...peers]).toEqual([]);
  });

  test("ignores server and internal spans, which do not represent outbound calls", () => {
    const peers = uninstrumentedPeers(
      [span("a", "Server", peerAttrs), span("b", "Internal", peerAttrs)],
      resources,
    );

    expect([...peers]).toEqual([]);
  });

  test("ignores a client span that names no peer", () => {
    expect([...uninstrumentedPeers([span("a", "Client", { "http.method": "GET" })], resources)]).toEqual([]);
  });

  test("ignores a peer address that matches no known resource", () => {
    const peers = uninstrumentedPeers(
      [span("a", "Client", { "server.address": "example.com", "server.port": "443" })],
      resources,
    );

    expect([...peers]).toEqual([]);
  });
});
