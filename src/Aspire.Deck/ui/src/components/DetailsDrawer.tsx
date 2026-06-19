import { useEffect, useState } from "react";
import type {
  Resource,
  ResourceCommand,
  EnvVar,
  ResourceProperty,
} from "../api/types";
import { openExternal } from "../api/deck";
import { formatTime } from "../lib/format";
import { Badge } from "./Badge";
import { StateDot } from "./StateDot";
import {
  CloseIcon,
  ExternalIcon,
  EyeIcon,
  EyeOffIcon,
  LinkIcon,
  PlayIcon,
  RestartIcon,
  ResourceTypeIcon,
  StopIcon,
} from "./Icons";
import type { ConfirmRequest } from "./ConfirmDialog";

function commandIcon(name: string) {
  if (name.includes("start")) {
    return <PlayIcon size={15} />;
  }
  if (name.includes("stop")) {
    return <StopIcon size={15} />;
  }
  if (name.includes("restart")) {
    return <RestartIcon size={15} />;
  }
  return null;
}

// Masks sensitive values until the user explicitly reveals them.
function SecretValue({ value }: { value: string }) {
  const [revealed, setRevealed] = useState(false);
  return (
    <>
      <span className="secret">{revealed ? value : "•".repeat(Math.min(value.length, 24))}</span>
      <button
        className="reveal-btn"
        onClick={() => setRevealed((v) => !v)}
        title={revealed ? "Hide value" : "Reveal value"}
        aria-label={revealed ? "Hide value" : "Reveal value"}
      >
        {revealed ? <EyeOffIcon size={15} /> : <EyeIcon size={15} />}
      </button>
    </>
  );
}

function PropertyRow({ prop }: { prop: ResourceProperty }) {
  return (
    <>
      <div className="kv__key">{prop.displayName ?? prop.name}</div>
      <div className={`kv__val ${prop.isHighlighted ? "highlight" : ""}`}>
        {prop.isSensitive ? <SecretValue value={prop.value} /> : <span className="secret">{prop.value}</span>}
      </div>
    </>
  );
}

function isSensitiveEnv(env: EnvVar): boolean {
  const name = env.name.toLowerCase();
  return (
    name.includes("password") ||
    name.includes("secret") ||
    name.includes("token") ||
    name.includes("key") ||
    name.includes("connectionstring")
  );
}

function EnvRow({ env }: { env: EnvVar }) {
  const value = env.value ?? "";
  return (
    <>
      <div className="kv__key">{env.name}</div>
      <div className="kv__val">
        {isSensitiveEnv(env) && value.length > 0 ? (
          <SecretValue value={value} />
        ) : (
          <span className="secret">{value || "—"}</span>
        )}
      </div>
    </>
  );
}

export function DetailsDrawer({
  resource,
  onClose,
  onExecuteCommand,
  requestConfirm,
}: {
  resource: Resource;
  onClose: () => void;
  onExecuteCommand: (resource: Resource, command: ResourceCommand) => void;
  requestConfirm: (request: ConfirmRequest) => void;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === "Escape") {
        onClose();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const handleCommand = (command: ResourceCommand): void => {
    if (command.confirmationMessage) {
      requestConfirm({
        title: command.displayName,
        message: command.confirmationMessage,
        confirmLabel: command.displayName,
        danger: command.name.includes("stop"),
        onConfirm: () => onExecuteCommand(resource, command),
      });
    } else {
      onExecuteCommand(resource, command);
    }
  };

  const properties = [...resource.properties].sort(
    (a, b) => (a.sortOrder ?? 999) - (b.sortOrder ?? 999),
  );
  const urls = [...resource.urls]
    .filter((u) => !u.isInactive)
    .sort((a, b) => a.sortOrder - b.sortOrder);
  const visibleCommands = resource.commands.filter((c) => c.state !== "hidden");

  return (
    <>
      <div className="drawer-overlay" onClick={onClose} />
      <aside className="drawer" role="dialog" aria-modal="true">
        <div className="drawer__header">
          <div style={{ flex: 1, minWidth: 0 }}>
            <div className="drawer__title">
              <ResourceTypeIcon type={resource.resourceType} size={18} />
              {resource.displayName}
            </div>
            <div className="drawer__subtitle">
              <StateDot state={resource.state} stateStyle={resource.stateStyle} health={resource.health} />
            </div>
          </div>
          <button className="icon-btn" onClick={onClose} aria-label="Close details">
            <CloseIcon size={16} />
          </button>
        </div>

        <div className="drawer__body">
          <section className="drawer__section">
            <div className="drawer__section-title">Overview</div>
            <div className="kv">
              <div className="kv__key">Type</div>
              <div className="kv__val">{resource.resourceType}</div>
              <div className="kv__key">State</div>
              <div className="kv__val">{resource.state ?? "Unknown"}</div>
              <div className="kv__key">Health</div>
              <div className="kv__val">{resource.health ?? "—"}</div>
              <div className="kv__key">Started</div>
              <div className="kv__val">{formatTime(resource.startedAt)}</div>
              {resource.stoppedAt ? (
                <>
                  <div className="kv__key">Stopped</div>
                  <div className="kv__val">{formatTime(resource.stoppedAt)}</div>
                </>
              ) : null}
              <div className="kv__key">UID</div>
              <div className="kv__val">{resource.uid}</div>
            </div>
          </section>

          {urls.length > 0 ? (
            <section className="drawer__section">
              <div className="drawer__section-title">Endpoints</div>
              <div className="url-list">
                {urls.map((url) => (
                  <a
                    key={`${url.name}-${url.url}`}
                    className="url-chip"
                    href={url.url}
                    title={url.url}
                    onClick={(e) => {
                      e.preventDefault();
                      void openExternal(url.url);
                    }}
                  >
                    <ExternalIcon size={12} />
                    {url.displayName ?? url.name ?? url.url}
                  </a>
                ))}
              </div>
            </section>
          ) : null}

          {properties.length > 0 ? (
            <section className="drawer__section">
              <div className="drawer__section-title">Properties</div>
              <div className="kv">
                {properties.map((prop) => (
                  <PropertyRow key={prop.name} prop={prop} />
                ))}
              </div>
            </section>
          ) : null}

          {resource.environment.length > 0 ? (
            <section className="drawer__section">
              <div className="drawer__section-title">Environment variables</div>
              <div className="kv">
                {resource.environment.map((env) => (
                  <EnvRow key={env.name} env={env} />
                ))}
              </div>
            </section>
          ) : null}

          {resource.healthReports.length > 0 ? (
            <section className="drawer__section">
              <div className="drawer__section-title">Health reports</div>
              {resource.healthReports.map((report) => (
                <div className="health-report" key={report.key}>
                  <Badge tone={healthTone(report.status)}>{report.status ?? "Unknown"}</Badge>
                  <div className="health-report__body">
                    <div className="health-report__key">{report.key}</div>
                    <div className="health-report__desc">{report.description}</div>
                  </div>
                </div>
              ))}
            </section>
          ) : null}

          {resource.relationships.length > 0 ? (
            <section className="drawer__section">
              <div className="drawer__section-title">Relationships</div>
              <div className="rel-list">
                {resource.relationships.map((rel) => (
                  <div className="rel-item" key={`${rel.type}-${rel.resourceName}`}>
                    <LinkIcon size={14} />
                    <span>{rel.resourceName}</span>
                    <Badge tone="accent">{rel.type}</Badge>
                  </div>
                ))}
              </div>
            </section>
          ) : null}
        </div>

        {visibleCommands.length > 0 ? (
          <div className="drawer__commands">
            {visibleCommands.map((command) => (
              <button
                key={command.name}
                className={`btn btn--sm ${command.isHighlighted ? "btn--primary" : ""} ${command.name.includes("stop") ? "btn--danger" : ""}`}
                disabled={command.state === "disabled"}
                title={command.displayDescription ?? command.displayName}
                onClick={() => handleCommand(command)}
              >
                {commandIcon(command.name)}
                {command.displayName}
              </button>
            ))}
          </div>
        ) : null}
      </aside>
    </>
  );
}

function healthTone(status: string | null): "success" | "warning" | "error" | "neutral" {
  switch (status) {
    case "Healthy":
      return "success";
    case "Degraded":
      return "warning";
    case "Unhealthy":
      return "error";
    default:
      return "neutral";
  }
}
