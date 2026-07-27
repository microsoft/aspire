/**
 * Resolves *uninstrumented peers* for trace spans.
 *
 * A client span that calls a service which emits no telemetry of its own leaves a hole in the
 * trace: the call is visible, but the callee is not. The dashboard fills that hole by inferring the
 * callee from the span's peer attributes and attributing the span to it as a second resource, so a
 * trace to an uninstrumented service still reports both ends.
 *
 * This mirrors three pieces of the dashboard:
 *   - `OtlpHelpers.GetPeerAddress` (src/Aspire.Dashboard/Otlp/Model/OtlpHelpers.cs)
 *   - `TelemetryRepository.CalculateTraceUninstrumentedPeers`
 *     (src/Aspire.Dashboard/Otlp/Storage/TelemetryRepository.cs)
 *   - `ResourceOutgoingPeerResolver` / `BrowserLinkOutgoingPeerResolver`
 *     (src/Aspire.Dashboard/Model/)
 */

const PEER_SERVICE = "peer.service";
const SERVER_ADDRESS = "server.address";
const SERVER_PORT = "server.port";
const NET_PEER_NAME = "net.peer.name";
const NET_PEER_PORT = "net.peer.port";

export interface PeerAttribute {
  key: string;
  value: string | null;
}

export interface PeerSpan {
  spanId: string;
  parentSpanId: string | null;
  kind: string;
  attributes: readonly PeerAttribute[];
}

export interface PeerResource {
  name: string;
  displayName: string;
  urls: readonly { url: string }[];
}

function attributeValue(attributes: readonly PeerAttribute[], key: string): string | null {
  for (const attribute of attributes) {
    if (attribute.key === key) {
      return attribute.value;
    }
  }

  return null;
}

/**
 * The address a span says it called, or `null` when the span names no peer.
 *
 * Semantic-convention churn means the same call can be described three different ways depending on
 * the instrumentation's age, so all three are tried in the dashboard's order:
 *   `peer.service`                     -> "aspire-dashboard"
 *   `server.address` + `server.port`   -> "localhost:18889"   (OTEL HTTP 1.7.0 dropped peer.service)
 *   `net.peer.name`  + `net.peer.port` -> "localhost:18889"   (pre-1.7.0 names)
 */
export function getPeerAddress(attributes: readonly PeerAttribute[]): string | null {
  const peerService = attributeValue(attributes, PEER_SERVICE);
  if (peerService !== null) {
    return peerService;
  }

  const server = attributeValue(attributes, SERVER_ADDRESS);
  if (server !== null) {
    const port = attributeValue(attributes, SERVER_PORT);
    return port !== null ? `${server}:${port}` : server;
  }

  const peer = attributeValue(attributes, NET_PEER_NAME);
  if (peer !== null) {
    const port = attributeValue(attributes, NET_PEER_PORT);
    return port !== null ? `${peer}:${port}` : peer;
  }

  return null;
}

/**
 * Cumulative address normalizations, applied in order. Mirrors `s_addressTransformers`.
 */
const addressTransformers: readonly ((value: string) => string)[] = [
  (value) => {
    // SQL Server writes the port after a comma rather than a colon.
    // https://www.connectionstrings.com/sql-server/
    return value.split(",").length === 2 ? value.replace(",", ":") : value;
  },
  (value) => {
    // Some libraries report the loopback address instead of "localhost", and a container calling out
    // to the host uses the runtime's host alias. Both name the same endpoint the resource published.
    return value.replace(/^(?:127\.0\.0\.1|host\.docker\.internal|host\.containers\.internal):/i, "localhost:");
  },
];

function equalsIgnoreCase(left: string, right: string): boolean {
  return left.toLowerCase() === right.toLowerCase();
}

function doesAddressMatch(endpoint: string, value: string): boolean {
  if (equalsIgnoreCase(endpoint, value)) {
    return true;
  }

  // The resource's own address gets the same normalizations as the peer address, so a resource
  // published on "127.0.0.1:18889" still matches a span that reported "localhost:18889".
  let transformed = endpoint;
  for (const transformer of addressTransformers) {
    transformed = transformer(transformed);
    if (equalsIgnoreCase(transformed, value)) {
      return true;
    }
  }

  return false;
}

/**
 * Host and port of each URL the resource publishes.
 *
 * `ResourceViewModel.ExtractResourceAddresses` also derives addresses from connection strings and
 * parameter values via `ConnectionStringParser`. That path is not ported yet, so a resource that
 * publishes no URL - a database reached only through a connection string, for example - will not be
 * matched as a peer here.
 */
function resourceAddresses(resource: PeerResource): string[] {
  const addresses: string[] = [];

  for (const { url } of resource.urls) {
    try {
      const parsed = new URL(url);
      addresses.push(parsed.port ? `${parsed.hostname}:${parsed.port}` : parsed.hostname);
    } catch {
      // Not an absolute URL; nothing to match against.
    }
  }

  return addresses;
}

function resourceName(resource: PeerResource, resources: readonly PeerResource[]): string {
  // Mirrors `ResourceViewModel.GetResourceName`: the display name alone when it is unambiguous,
  // otherwise qualified with the instance name so replicas stay distinguishable.
  const sameDisplayName = resources.filter((other) => equalsIgnoreCase(other.displayName, resource.displayName));
  return sameDisplayName.length > 1 ? `${resource.displayName}-${resource.name}` : resource.displayName;
}

function tryResolveBrowserLink(attributes: readonly PeerAttribute[]): string | null {
  // The BrowserLink middleware's call to the IDE carries no identifying attributes, so the dashboard
  // recognizes it by the shape of its URL instead:
  //   http://localhost:59267/6eed7c2dedc14419901b813e8fe87a86/getScriptTag
  // `url.full` replaced `http.url`; both are checked for backwards compatibility.
  const url = attributeValue(attributes, "url.full") ?? attributeValue(attributes, "http.url");
  if (url === null || !url.endsWith("getScriptTag")) {
    return null;
  }

  try {
    const parsed = new URL(url);
    if (!equalsIgnoreCase(parsed.hostname, "localhost")) {
      return null;
    }

    // Expect exactly "/{32-hex-guid}/getScriptTag".
    const segments = parsed.pathname.split("/").filter((segment) => segment.length > 0);
    if (segments.length === 2 && /^[0-9a-f]{32}$/i.test(segments[0]!)) {
      return "Browser Link";
    }
  } catch {
    return null;
  }

  return null;
}

/**
 * Resolves the peer address a span reports to a known resource's name, or `null` when no resource
 * publishes that address.
 */
export function resolvePeerName(
  attributes: readonly PeerAttribute[],
  resources: readonly PeerResource[],
): string | null {
  const browserLink = tryResolveBrowserLink(attributes);
  if (browserLink !== null) {
    return browserLink;
  }

  const address = getPeerAddress(attributes);
  if (address === null) {
    return null;
  }

  // The exact address is tried first, then each normalization is applied cumulatively.
  let candidate = address;
  const candidates = [candidate];
  for (const transformer of addressTransformers) {
    candidate = transformer(candidate);
    candidates.push(candidate);
  }

  for (const value of candidates) {
    for (const resource of resources) {
      for (const resourceAddress of resourceAddresses(resource)) {
        if (doesAddressMatch(resourceAddress, value)) {
          return resourceName(resource, resources);
        }
      }
    }
  }

  return null;
}

/**
 * Maps each span that calls an uninstrumented service to that service's resource name.
 *
 * Mirrors `CalculateTraceUninstrumentedPeers`: a span qualifies when it names a peer, it is a
 * client or producer, and it has no child spans - the absence of children is what indicates the
 * callee produced no telemetry of its own.
 */
export function uninstrumentedPeers(
  spans: readonly PeerSpan[],
  resources: readonly PeerResource[],
): Map<string, string> {
  const parentIds = new Set<string>();
  for (const span of spans) {
    if (span.parentSpanId) {
      parentIds.add(span.parentSpanId);
    }
  }

  const peers = new Map<string, string>();
  for (const span of spans) {
    const kind = span.kind.toLowerCase();
    if (kind !== "client" && kind !== "producer") {
      continue;
    }

    if (parentIds.has(span.spanId)) {
      continue;
    }

    if (getPeerAddress(span.attributes) === null && tryResolveBrowserLink(span.attributes) === null) {
      continue;
    }

    const name = resolvePeerName(span.attributes, resources);
    if (name !== null) {
      peers.set(span.spanId, name);
    }
  }

  return peers;
}
