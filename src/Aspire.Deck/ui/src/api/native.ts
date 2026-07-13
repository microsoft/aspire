import type {
  DashboardApiDiscovery,
  DashboardApiVersion,
  DashboardConfiguration,
  DeckConfig,
  Resource,
} from "./types";

const dashboardProduct = "Aspire.Dashboard";
const discoveryPath = "/api/dashboard";
const configurationCapability = "configuration";
const resourcesCapability = "resources";
const supportedVersions = new Set([1]);

let negotiatedVersion: Promise<DashboardApiVersion> | null = null;
let configuration: Promise<DeckConfig> | null = null;

async function requestJson(path: string): Promise<unknown> {
  const response = await fetch(path, {
    cache: "no-store",
    credentials: "same-origin",
    headers: { Accept: "application/json" },
  });
  if (!response.ok) {
    throw new Error(`Dashboard API request failed with ${response.status} ${response.statusText}.`);
  }

  return await response.json() as unknown;
}

function isVersion(value: unknown): value is DashboardApiVersion {
  if (typeof value !== "object" || value === null) {
    return false;
  }

  const candidate = value as Partial<DashboardApiVersion>;
  return Number.isInteger(candidate.version)
    && typeof candidate.basePath === "string"
    && Array.isArray(candidate.capabilities)
    && candidate.capabilities.every((capability) => typeof capability === "string");
}

function validateBasePath(basePath: string): string {
  const url = new URL(basePath, window.location.origin);
  if (url.origin !== window.location.origin
      || !url.pathname.startsWith(`${discoveryPath}/`)
      || url.search !== ""
      || url.hash !== "") {
    throw new Error(`Dashboard API returned an invalid version base path: ${basePath}.`);
  }

  return url.pathname.replace(/\/$/, "");
}

async function negotiateVersion(): Promise<DashboardApiVersion> {
  const payload = await requestJson(discoveryPath) as Partial<DashboardApiDiscovery>;
  if (payload.product !== dashboardProduct || !Array.isArray(payload.versions)) {
    throw new Error("Dashboard API discovery returned an incompatible product or payload.");
  }

  const version = payload.versions
    .filter(isVersion)
    .filter((candidate) => supportedVersions.has(candidate.version))
    .filter((candidate) => candidate.capabilities.includes(configurationCapability))
    .sort((left, right) => right.version - left.version)[0];
  if (version === undefined) {
    const advertised = payload.versions
      .filter(isVersion)
      .map((candidate) => candidate.version)
      .sort((left, right) => right - left)
      .join(", ") || "none";
    throw new Error(`Dashboard API has no compatible configuration version (server: ${advertised}; client: 1).`);
  }

  return { ...version, basePath: validateBasePath(version.basePath) };
}

function getNegotiatedVersion(): Promise<DashboardApiVersion> {
  if (negotiatedVersion === null) {
    const request = negotiateVersion();
    negotiatedVersion = request;
    void request.catch(() => {
      if (negotiatedVersion === request) {
        negotiatedVersion = null;
      }
    });
  }

  return negotiatedVersion;
}

async function loadConfig(): Promise<DeckConfig> {
  const version = await getNegotiatedVersion();
  const payload = await requestJson(`${version.basePath}/config`) as Partial<DashboardConfiguration>;
  if (typeof payload.applicationName !== "string"
      || typeof payload.dashboardVersion !== "string"
      || typeof payload.runtimeVersion !== "string") {
    throw new Error("Dashboard API configuration returned an incompatible payload.");
  }

  return {
    applicationName: payload.applicationName,
    resourceServiceUrl: null,
    otlpGrpcUrl: null,
    otlpHttpUrl: null,
    version: payload.dashboardVersion,
    runtimeVersion: payload.runtimeVersion,
  };
}

function getConfig(): Promise<DeckConfig> {
  if (configuration === null) {
    const request = loadConfig();
    configuration = request;
    void request.catch(() => {
      if (configuration === request) {
        configuration = null;
      }
    });
  }

  return configuration;
}

async function hasCapability(capability: string): Promise<boolean> {
  return (await getNegotiatedVersion()).capabilities.includes(capability);
}

async function listResources(): Promise<Resource[]> {
  const version = await getNegotiatedVersion();
  if (!version.capabilities.includes(resourcesCapability)) {
    throw new Error("Dashboard API version 1 does not advertise the resources capability.");
  }

  const payload = await requestJson(`${version.basePath}/resources`);
  if (!Array.isArray(payload)) {
    throw new Error("Dashboard API resources returned an incompatible payload.");
  }

  return payload as Resource[];
}

export const nativeBackend = {
  getConfig,
  hasCapability,
  listResources,
};
