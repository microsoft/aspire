import { useMemo, useRef, useState } from "react";
import type { Resource, ResourceCommand } from "../api/types";
import { PARAMETER_RESOURCE_TYPE } from "../api/types";
import { executeCommand, openExternal } from "../api/deck";
import { useResources } from "../lib/useDeckEvent";
import { formatRelativeTime } from "../lib/format";
import { DetailsDrawer } from "../components/DetailsDrawer";
import {
  ChevronIcon,
  CommandMenu,
  ConfirmDialog,
  ContextMenu,
  DataTable,
  ExternalIcon,
  FilterIcon,
  FilterMenu,
  ForceGraph,
  IconButton,
  MoreIcon,
  NamedIcon,
  Page,
  PageBody,
  PageHeader,
  PageHeading,
  PageSubtitle,
  PageTitle,
  PageToolbar,
  ResourceTypeIcon,
  ResetViewIcon,
  ResourcesIcon,
  SearchBox,
  StateDot,
  ZoomInIcon,
  ZoomOutIcon,
  type Column,
  type ConfirmRequest,
  type ForceGraphEdge,
  type ForceGraphHandle,
  type ForceGraphNode,
  type SortDirection,
} from "../toolkit";

const PARENT_PROPERTY = "resource.parentName";
const SOURCE_PROPERTIES = ["project.path", "tool.package", "executable.path", "container.image", "resource.source"];

export interface ResourceRouteState {
  resourceName: string | null;
  query: string;
  hiddenTypes: string[];
  hiddenStates: string[];
  hiddenHealth: string[];
  showHidden: boolean;
  showType: boolean;
  collapsed: string[];
  sortColumn: string;
  sortDirection: SortDirection;
  view: "table" | "graph";
}

interface ResourceRow {
  resource: Resource;
  depth: number;
  hasChildren: boolean;
  collapsed: boolean;
}

interface Toast {
  message: string;
  tone: "success" | "error";
}

interface GraphContext {
  resourceName: string;
  x: number;
  y: number;
}

function propertyValue(resource: Resource, name: string): string | null {
  return resource.properties.find((property) => property.name.toLowerCase() === name.toLowerCase())?.value ?? null;
}

function resourceSource(resource: Resource): { value: string; title: string } | null {
  for (const name of SOURCE_PROPERTIES) {
    const value = propertyValue(resource, name);
    if (!value) continue;
    const pathSource = name === "project.path" || name === "executable.path";
    return { value: pathSource ? value.split(/[\\/]/).pop() ?? value : value, title: value };
  }
  return null;
}

function sortedValues(values: Iterable<string>): string[] {
  return [...new Set(values)].sort((left, right) => left.localeCompare(right));
}

function resourceTone(resource: Resource): ForceGraphNode["tone"] {
  if (resource.stateStyle === "success") return "success";
  if (resource.stateStyle === "info") return "info";
  if (resource.stateStyle === "warning") return "warning";
  if (resource.stateStyle === "error") return "error";
  const health = resource.health?.toLowerCase();
  if (health === "healthy") return "success";
  if (health === "degraded") return "warning";
  if (health === "unhealthy") return "error";
  return "neutral";
}

function graphEndpoint(resource: Resource): string | null {
  const endpoint = resource.urls
    .filter((url) => !url.isInternal && !url.isInactive)
    .sort((left, right) => left.sortOrder - right.sortOrder)[0];
  if (!endpoint) return null;
  try {
    const url = new URL(endpoint.url);
    return `${url.hostname}${url.port ? `:${url.port}` : ""}`;
  } catch {
    return endpoint.displayName || endpoint.name || endpoint.url;
  }
}

function updateHidden(values: string[], value: string, visible: boolean): string[] {
  return visible
    ? values.filter((candidate) => candidate !== value)
    : sortedValues([...values, value]);
}

function flattenResources(resources: Resource[], collapsedNames: ReadonlySet<string>): ResourceRow[] {
  const byParent = new Map<string, Resource[]>();
  const names = new Set(resources.map((resource) => resource.name));
  const roots: Resource[] = [];
  for (const resource of resources) {
    const parent = propertyValue(resource, PARENT_PROPERTY);
    if (parent && names.has(parent)) {
      const children = byParent.get(parent) ?? [];
      children.push(resource);
      byParent.set(parent, children);
    } else {
      roots.push(resource);
    }
  }
  const compare = (left: Resource, right: Resource) => left.displayName.localeCompare(right.displayName);
  const rows: ResourceRow[] = [];
  const append = (resource: Resource, depth: number): void => {
    const children = (byParent.get(resource.name) ?? []).sort(compare);
    const collapsed = collapsedNames.has(resource.name);
    rows.push({ resource, depth, hasChildren: children.length > 0, collapsed });
    if (!collapsed) children.forEach((child) => append(child, depth + 1));
  };
  roots.sort(compare).forEach((resource) => append(resource, 0));
  return rows;
}

export function ResourcesPage({
  route,
  onRouteChange,
}: {
  route: ResourceRouteState;
  onRouteChange: (state: ResourceRouteState) => void;
}) {
  const { resources, ready } = useResources();
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);
  const [toast, setToast] = useState<Toast | null>(null);
  const [graphContext, setGraphContext] = useState<GraphContext | null>(null);
  const graphRef = useRef<ForceGraphHandle | null>(null);
  const hiddenTypes = useMemo(() => new Set(route.hiddenTypes), [route.hiddenTypes]);
  const hiddenStates = useMemo(() => new Set(route.hiddenStates), [route.hiddenStates]);
  const hiddenHealth = useMemo(() => new Set(route.hiddenHealth), [route.hiddenHealth]);
  const collapsed = useMemo(() => new Set(route.collapsed), [route.collapsed]);

  const inventory = useMemo(
    () => resources.filter((resource) => resource.resourceType !== PARAMETER_RESOURCE_TYPE),
    [resources],
  );
  const filteredResources = useMemo(() => {
    const trimmed = route.query.trim().toLowerCase();
    return inventory.filter((resource) => {
      if (resource.isHidden && !route.showHidden) return false;
      if (hiddenTypes.has(resource.resourceType)) return false;
      if (hiddenStates.has(resource.state ?? "")) return false;
      if (hiddenHealth.has(resource.health ?? "")) return false;
      return !trimmed || resource.displayName.toLowerCase().includes(trimmed) ||
        resource.resourceType.toLowerCase().includes(trimmed) ||
        (resource.state ?? "").toLowerCase().includes(trimmed);
    });
  }, [hiddenHealth, hiddenStates, hiddenTypes, inventory, route.query, route.showHidden]);
  const rows = useMemo(() => flattenResources(filteredResources, collapsed), [collapsed, filteredResources]);

  const selected = useMemo(
    () => resources.find((resource) => resource.name === route.resourceName) ?? null,
    [resources, route.resourceName],
  );

  const changeRoute = (change: Partial<ResourceRouteState>): void => onRouteChange({ ...route, ...change });
  const toggleCollapsed = (name: string): void => changeRoute({
    collapsed: collapsed.has(name) ? route.collapsed.filter((value) => value !== name) : sortedValues([...route.collapsed, name]),
  });
  const byName = useMemo(() => new Map(inventory.map((resource) => [resource.name, resource])), [inventory]);
  const rootOf = (resource: Resource): Resource => {
    let current = resource;
    const visited = new Set<string>();
    while (!visited.has(current.name)) {
      visited.add(current.name);
      const parentName = propertyValue(current, PARENT_PROPERTY);
      const parent = parentName ? byName.get(parentName) : undefined;
      if (!parent) break;
      current = parent;
    }
    return current;
  };
  const hierarchyCompare = (valueCompare: (left: Resource, right: Resource) => number) =>
    (left: ResourceRow, right: ResourceRow, direction: SortDirection): number => {
      const leftRoot = rootOf(left.resource);
      const rightRoot = rootOf(right.resource);
      const factor = direction === "ascending" ? 1 : -1;
      if (leftRoot.name !== rightRoot.name) return factor * valueCompare(leftRoot, rightRoot);
      if (left.resource.name === leftRoot.name) return -1;
      if (right.resource.name === rightRoot.name) return 1;
      return factor * valueCompare(left.resource, right.resource);
    };

  const runCommand = async (resource: Resource, command: ResourceCommand): Promise<void> => {
    try {
      const response = await executeCommand({ resourceName: resource.name, resourceType: resource.resourceType, commandName: command.name });
      setToast(response.kind === "succeeded"
        ? { message: `${command.displayName} succeeded`, tone: "success" }
        : { message: response.message ?? `${command.displayName} ${response.kind}`, tone: "error" });
    } catch (err) {
      setToast({ message: `Command failed: ${String(err)}`, tone: "error" });
    }
    window.setTimeout(() => setToast(null), 3200);
  };

  const columns: Column<ResourceRow>[] = [
    {
      key: "name",
      header: "Name",
      minWidth: "220px",
      render: ({ resource, depth, hasChildren, collapsed: isCollapsed }) => (
        <span className="cell-name resource-name" style={{ paddingInlineStart: `${depth * 22}px` }}>
          {hasChildren ? (
            <button
              className="resource-name__toggle"
              type="button"
              aria-label={`${isCollapsed ? "Expand" : "Collapse"} ${resource.displayName}`}
              aria-expanded={!isCollapsed}
              onClick={(event) => { event.stopPropagation(); toggleCollapsed(resource.name); }}
            >
              <ChevronIcon size={14} className={isCollapsed ? "" : "resource-name__chevron--expanded"} />
            </button>
          ) : <span className="resource-name__spacer" />}
          <ResourceTypeIcon type={resource.resourceType} iconName={resource.iconName} iconVariant={resource.iconVariant} size={16} className="cell-type-icon" />
          {resource.displayName}
        </span>
      ),
      compareWithDirection: hierarchyCompare((left, right) => left.displayName.localeCompare(right.displayName)),
    },
    {
      key: "state",
      header: "State",
      width: "170px",
      render: ({ resource }) => <StateDot state={resource.state} stateStyle={resource.stateStyle} health={resource.health} />,
      compareWithDirection: hierarchyCompare((left, right) => (left.state ?? "").localeCompare(right.state ?? "")),
    },
    ...(route.showType ? [{
      key: "type",
      header: "Type",
      width: "120px",
      render: ({ resource }: ResourceRow) => <span className="cell-muted">{resource.resourceType}</span>,
      compareWithDirection: hierarchyCompare((left, right) => left.resourceType.localeCompare(right.resourceType)),
    }] : []),
    {
      key: "source",
      header: "Source",
      minWidth: "160px",
      render: ({ resource }) => {
        const source = resourceSource(resource);
        return source ? <span className="cell-muted resource-source" title={source.title}>{source.value}</span> : <span className="cell-muted">—</span>;
      },
    },
    {
      key: "urls",
      header: "URLs",
      minWidth: "180px",
      render: ({ resource }) => {
        const urls = resource.urls.filter((url) => !url.isInactive && !url.isInternal).sort((left, right) => left.sortOrder - right.sortOrder);
        return urls.length === 0 ? <span className="cell-muted">—</span> : (
          <span className="url-list">
            {urls.map((url) => (
              <a key={`${url.name}-${url.url}`} className="url-chip" href={url.url} title={url.url} onClick={(event) => {
                event.preventDefault(); event.stopPropagation(); void openExternal(url.url);
              }}>
                <ExternalIcon size={11} />{url.displayName || url.name || url.url}
              </a>
            ))}
          </span>
        );
      },
    },
    {
      key: "started",
      header: "Started",
      width: "120px",
      render: ({ resource }) => <span className="cell-muted">{formatRelativeTime(resource.startedAt)}</span>,
      compareWithDirection: hierarchyCompare((left, right) => (left.startedAt ? Date.parse(left.startedAt) : 0) - (right.startedAt ? Date.parse(right.startedAt) : 0)),
    },
  ];

  const types = sortedValues(inventory.map((resource) => resource.resourceType));
  const states = sortedValues(inventory.map((resource) => resource.state ?? ""));
  const health = sortedValues(inventory.map((resource) => resource.health ?? ""));
  const filtersActive = route.hiddenTypes.length + route.hiddenStates.length + route.hiddenHealth.length > 0;
  const parentNames = sortedValues(inventory.filter((resource) => inventory.some((candidate) => propertyValue(candidate, PARENT_PROPERTY) === resource.name)).map((resource) => resource.name));
  const graphNodes: ForceGraphNode[] = filteredResources.map((resource) => ({
    id: resource.name,
    label: resource.displayName,
    description: `${resource.resourceType}, ${resource.state ?? "Unknown"}${resource.health ? `, ${resource.health}` : ""}`,
    endpoint: graphEndpoint(resource),
    tone: resourceTone(resource),
    icon: <ResourceTypeIcon type={resource.resourceType} iconName={resource.iconName} iconVariant={resource.iconVariant} size={32} />,
  }));
  const graphResourceNames = new Set(filteredResources.map((resource) => resource.name));
  const displayNameToName = new Map(filteredResources.map((resource) => [resource.displayName, resource.name]));
  const graphEdgeKeys = new Set<string>();
  const graphEdges: ForceGraphEdge[] = [];
  for (const resource of filteredResources) {
    const targets = [
      ...resource.relationships.map((relationship) => displayNameToName.get(relationship.resourceName) ?? relationship.resourceName),
      propertyValue(resource, PARENT_PROPERTY),
    ];
    for (const target of targets) {
      if (!target || target === resource.name || !graphResourceNames.has(target)) continue;
      const key = `${resource.name}\0${target}`;
      if (graphEdgeKeys.has(key)) continue;
      graphEdgeKeys.add(key);
      graphEdges.push({ source: resource.name, target });
    }
  }
  const contextResource = graphContext ? resources.find((resource) => resource.name === graphContext.resourceName) ?? null : null;

  return (
    <Page aria-labelledby="deck-page-resources-title">
      <PageHeader><PageHeading><PageTitle id="deck-page-resources-title">Resources</PageTitle><PageSubtitle>{ready ? `${rows.length} resource${rows.length === 1 ? "" : "s"}` : "Loading…"}</PageSubtitle></PageHeading></PageHeader>
      <PageToolbar ariaLabel="Resource tools" className="resources-toolbar">
        <SearchBox value={route.query} onChange={(query) => changeRoute({ query })} placeholder="Filter by name, type or state…" />
        <FilterMenu
          ariaLabel="Resource filters"
          icon={<FilterIcon size={16} />}
          active={filtersActive}
          onClear={() => changeRoute({ hiddenTypes: [], hiddenStates: [], hiddenHealth: [] })}
          groups={[
            { id: "state", label: "State", options: states.map((value) => ({ value, label: value, checked: !hiddenStates.has(value) })), onChange: (value, checked) => changeRoute({ hiddenStates: updateHidden(route.hiddenStates, value, checked) }) },
            { id: "type", label: "Type", options: types.map((value) => ({ value, label: value, checked: !hiddenTypes.has(value) })), onChange: (value, checked) => changeRoute({ hiddenTypes: updateHidden(route.hiddenTypes, value, checked) }) },
            { id: "health", label: "Health", options: health.map((value) => ({ value, label: value, checked: !hiddenHealth.has(value) })), onChange: (value, checked) => changeRoute({ hiddenHealth: updateHidden(route.hiddenHealth, value, checked) }) },
          ]}
        />
        <CommandMenu
          ariaLabel="Resource view options"
          triggerContent={null}
          triggerIcon={<MoreIcon size={17} />}
          triggerSize="small"
          entries={[
            { id: "types", label: route.showType ? "Hide resource types" : "Show resource types", onSelect: () => changeRoute({ showType: !route.showType }) },
            { id: "hidden", label: route.showHidden ? "Hide hidden resources" : "Show hidden resources", onSelect: () => changeRoute({ showHidden: !route.showHidden }) },
            { id: "branches", label: route.collapsed.length > 0 ? "Expand all children" : "Collapse all children", disabled: parentNames.length === 0, onSelect: () => changeRoute({ collapsed: route.collapsed.length > 0 ? [] : parentNames }) },
          ]}
        />
        <div className="resource-view-switch" role="group" aria-label="Resource view">
          <IconButton label="Table view" icon={<ResourcesIcon size={17} />} aria-pressed={route.view === "table"} onClick={() => changeRoute({ view: "table" })} />
          <IconButton label="Graph view" icon={<NamedIcon name="Graph" size={17} />} aria-pressed={route.view === "graph"} onClick={() => changeRoute({ view: "graph" })} />
        </div>
      </PageToolbar>
      <PageBody className={route.view === "graph" ? "resources-graph-body" : undefined}>
        {route.view === "graph" ? (
          <div className="resources-graph">
            <ForceGraph
              ref={graphRef}
              ariaLabel="Resource graph"
              nodes={graphNodes}
              edges={graphEdges}
              selectedId={route.resourceName}
              emptyMessage={ready ? "No resources match your filter." : "Connecting to resource service…"}
              onSelect={(resourceName) => changeRoute({ resourceName })}
              onContextMenu={(resourceName, x, y) => setGraphContext({ resourceName, x, y })}
            />
            <div className="resources-graph__controls" role="toolbar" aria-label="Graph controls">
              <IconButton label="Zoom in" icon={<ZoomInIcon size={17} />} onClick={() => graphRef.current?.zoomIn()} />
              <IconButton label="Zoom out" icon={<ZoomOutIcon size={17} />} onClick={() => graphRef.current?.zoomOut()} />
              <IconButton label="Reset view" icon={<ResetViewIcon size={17} />} onClick={() => graphRef.current?.reset()} />
            </div>
          </div>
        ) : (
          <DataTable columns={columns} rows={rows} rowKey={(row) => row.resource.name} onRowClick={(row) => changeRoute({ resourceName: row.resource.name })} isSelected={(row) => row.resource.name === route.resourceName} sort={{ columnKey: route.sortColumn, direction: route.sortDirection }} onSortChange={(sort) => changeRoute({ sortColumn: sort.columnKey, sortDirection: sort.direction })} emptyMessage={ready ? "No resources match your filter." : "Connecting to resource service…"} />
        )}
      </PageBody>
      {selected ? <DetailsDrawer resource={selected} onClose={() => changeRoute({ resourceName: null })} onExecuteCommand={(resource, command) => void runCommand(resource, command)} requestConfirm={setConfirm} /> : null}
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
      <ContextMenu
        open={graphContext !== null}
        x={graphContext?.x ?? 0}
        y={graphContext?.y ?? 0}
        ariaLabel="Resource graph actions"
        onClose={() => setGraphContext(null)}
        entries={contextResource ? [
          { id: "details", label: "View details", onSelect: () => changeRoute({ resourceName: contextResource.name }) },
          ...contextResource.commands.filter((command) => command.state !== "hidden").map((command) => ({
            id: command.name,
            label: command.displayName,
            disabled: command.state === "disabled",
            onSelect: () => command.confirmationMessage
              ? setConfirm({ title: command.displayName, message: command.confirmationMessage, confirmLabel: command.displayName, onConfirm: () => void runCommand(contextResource, command) })
              : void runCommand(contextResource, command),
          })),
        ] : []}
      />
      {toast ? <div className="toast" role="status" aria-live="polite"><span className={`state__dot ${toast.tone === "success" ? "success" : "error"}`} />{toast.message}</div> : null}
    </Page>
  );
}
