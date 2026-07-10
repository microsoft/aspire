import type {
  Resource,
  ResourceCommand,
  EnvVar,
  ResourceProperty,
} from "../api/types";
import { openExternal } from "../api/deck";
import { formatTime } from "../lib/format";
import {
  Badge,
  Button,
  Drawer,
  ExternalIcon,
  LinkIcon,
  PlayIcon,
  RestartIcon,
  ResourceTypeIcon,
  SecretValue,
  StateDot,
  StopIcon,
  type ConfirmRequest,
} from "../toolkit";

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

// The resource-service environment payload does not carry sensitivity metadata.
// Default every value closed so a newly named credential cannot leak by accident.
function EnvRow({ env }: { env: EnvVar }) {
  const value = env.value ?? "";
  return (
    <>
      <div className="kv__key">{env.name}</div>
      <div className="kv__val">
        {value.length > 0 ? <SecretValue value={value} /> : <span className="secret">—</span>}
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
    (left, right) => (left.sortOrder ?? 999) - (right.sortOrder ?? 999),
  );
  const urls = [...resource.urls]
    .filter((url) => !url.isInactive)
    .sort((left, right) => left.sortOrder - right.sortOrder);
  const visibleCommands = resource.commands.filter((command) => command.state !== "hidden");
  const footer = visibleCommands.length > 0
    ? visibleCommands.map((command) => (
        <Button
          key={command.name}
          size="small"
          variant={command.name.includes("stop") ? "danger" : command.isHighlighted ? "primary" : "secondary"}
          disabled={command.state === "disabled"}
          title={command.displayDescription ?? command.displayName}
          onClick={() => handleCommand(command)}
        >
          {commandIcon(command.name)}
          {command.displayName}
        </Button>
      ))
    : undefined;

  return (
    <Drawer
      title={resource.displayName}
      leading={<ResourceTypeIcon type={resource.resourceType} size={18} />}
      subtitle={<StateDot state={resource.state} stateStyle={resource.stateStyle} health={resource.health} />}
      onClose={onClose}
      footer={footer}
    >
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
                onClick={(event) => {
                  event.preventDefault();
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
            {resource.relationships.map((relationship) => (
              <div className="rel-item" key={`${relationship.type}-${relationship.resourceName}`}>
                <LinkIcon size={14} />
                <span>{relationship.resourceName}</span>
                <Badge tone="accent">{relationship.type}</Badge>
              </div>
            ))}
          </div>
        </section>
      ) : null}
    </Drawer>
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
