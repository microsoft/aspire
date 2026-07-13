import {
  forwardRef,
  useEffect,
  useImperativeHandle,
  useRef,
  type ReactElement,
} from "react";
import { renderToStaticMarkup } from "react-dom/server";

declare const d3: any;

export interface ForceGraphNode {
  id: string;
  label: string;
  description?: string;
  endpoint?: string | null;
  tone?: "success" | "info" | "warning" | "error" | "neutral";
  icon: ReactElement;
}

export interface ForceGraphEdge {
  source: string;
  target: string;
  label?: string;
}

export interface ForceGraphHandle {
  zoomIn: () => void;
  zoomOut: () => void;
  reset: () => void;
}

interface SimulationNode extends ForceGraphNode {
  x?: number;
  y?: number;
  fx?: number | null;
  fy?: number | null;
  vx?: number;
  vy?: number;
}

export const ForceGraph = forwardRef<ForceGraphHandle, {
  nodes: readonly ForceGraphNode[];
  edges: readonly ForceGraphEdge[];
  selectedId?: string | null;
  ariaLabel: string;
  emptyMessage?: string;
  onSelect?: (id: string) => void;
  onContextMenu?: (id: string, x: number, y: number) => void;
}>(function ForceGraph({
  nodes,
  edges,
  selectedId = null,
  ariaLabel,
  emptyMessage = "No graph nodes.",
  onSelect,
  onContextMenu,
}, forwardedRef) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const svgRef = useRef<SVGSVGElement | null>(null);
  const graphRef = useRef<{ zoomBy: (factor: number) => void; reset: () => void } | null>(null);

  useImperativeHandle(forwardedRef, () => ({
    zoomIn: () => graphRef.current?.zoomBy(1.5),
    zoomOut: () => graphRef.current?.zoomBy(2 / 3),
    reset: () => graphRef.current?.reset(),
  }), []);

  useEffect(() => {
    const container = containerRef.current;
    const svgElement = svgRef.current;
    if (!container || !svgElement || typeof d3 === "undefined") return;

    const svg = d3.select(svgElement);
    svg.selectAll("*").remove();
    const width = Math.max(container.clientWidth, 320);
    const height = Math.max(container.clientHeight, 320);
    svg.attr("viewBox", `0 0 ${width} ${height}`);

    const defs = svg.append("defs");
    defs.append("marker")
      .attr("id", "deck-force-graph-arrow")
      .attr("viewBox", "0 -5 10 10")
      .attr("refX", 49)
      .attr("refY", 0)
      .attr("markerWidth", 7)
      .attr("markerHeight", 7)
      .attr("orient", "auto")
      .append("path")
      .attr("d", "M0,-5L10,0L0,5");

    const viewport = svg.append("g").attr("class", "force-graph__viewport");
    const zoom = d3.zoom().scaleExtent([0.2, 4]).on("zoom", (event: any) => {
      viewport.attr("transform", event.transform);
      svgElement.dataset.zoom = String(event.transform.k);
    });
    svg.call(zoom);

    const reset = (): void => {
      svg.call(zoom.transform, d3.zoomIdentity);
      svgElement.dataset.zoom = "1";
    };
    graphRef.current = {
      zoomBy: (factor) => svg.call(zoom.scaleBy, factor),
      reset,
    };

    if (nodes.length === 0) {
      viewport.append("text")
        .attr("class", "force-graph__empty")
        .attr("x", width / 2)
        .attr("y", height / 2)
        .text(emptyMessage);
      return () => { graphRef.current = null; };
    }

    const simulationNodes: SimulationNode[] = nodes.map((node, index) => ({
      ...node,
      x: width / 2 + Math.cos(index / nodes.length * Math.PI * 2) * Math.min(width, height) * 0.28,
      y: height / 2 + Math.sin(index / nodes.length * Math.PI * 2) * Math.min(width, height) * 0.28,
    }));
    const nodeIds = new Set(simulationNodes.map((node) => node.id));
    const simulationEdges = edges
      .filter((edge) => nodeIds.has(edge.source) && nodeIds.has(edge.target) && edge.source !== edge.target)
      .map((edge) => ({ ...edge }));

    const linkElements = viewport.append("g")
      .attr("class", "force-graph__edges")
      .selectAll("line")
      .data(simulationEdges)
      .join("line")
      .attr("class", "force-graph__edge")
      .attr("marker-end", "url(#deck-force-graph-arrow)");

    const nodeElements = viewport.append("g")
      .attr("class", "force-graph__nodes")
      .selectAll("g")
      .data(simulationNodes, (node: SimulationNode) => node.id)
      .join("g")
      .attr("class", (node: SimulationNode) => `force-graph__node${node.id === selectedId ? " force-graph__node--selected" : ""}`)
      .attr("data-node-id", (node: SimulationNode) => node.id)
      .attr("role", "button")
      .attr("tabindex", 0)
      .attr("aria-label", (node: SimulationNode) => `${node.label}${node.description ? `, ${node.description}` : ""}`)
      .on("click", (_event: unknown, node: SimulationNode) => onSelect?.(node.id))
      .on("keydown", (event: KeyboardEvent, node: SimulationNode) => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          onSelect?.(node.id);
        }
      })
      .on("contextmenu", (event: MouseEvent, node: SimulationNode) => {
        event.preventDefault();
        onContextMenu?.(node.id, event.clientX, event.clientY);
      });

    nodeElements.append("circle").attr("class", "force-graph__node-body").attr("r", 43);
    nodeElements.append("circle")
      .attr("class", (node: SimulationNode) => `force-graph__state ${node.tone ?? "neutral"}`)
      .attr("cx", 31)
      .attr("cy", -31)
      .attr("r", 6);
    nodeElements.append("foreignObject")
      .attr("class", "force-graph__icon")
      .attr("x", -16)
      .attr("y", -21)
      .attr("width", 32)
      .attr("height", 32)
      .html((node: SimulationNode) => `<div xmlns="http://www.w3.org/1999/xhtml">${renderToStaticMarkup(node.icon)}</div>`);
    nodeElements.append("text")
      .attr("class", "force-graph__label")
      .attr("y", 60)
      .text((node: SimulationNode) => node.label.length > 28 ? `${node.label.slice(0, 27)}…` : node.label)
      .append("title")
      .text((node: SimulationNode) => node.label);
    nodeElements.filter((node: SimulationNode) => Boolean(node.endpoint)).append("text")
      .attr("class", "force-graph__endpoint")
      .attr("y", 29)
      .text((node: SimulationNode) => node.endpoint);

    const simulation = d3.forceSimulation(simulationNodes)
      .force("link", d3.forceLink(simulationEdges).id((node: SimulationNode) => node.id).distance(135).strength(0.8))
      .force("charge", d3.forceManyBody().strength(-650))
      .force("collide", d3.forceCollide(74).iterations(3))
      .force("center", d3.forceCenter(width / 2, height / 2));

    const updatePositions = (): void => {
      nodeElements.attr("transform", (node: SimulationNode) => `translate(${node.x ?? 0},${node.y ?? 0})`);
      linkElements
        .attr("x1", (edge: { source: SimulationNode }) => edge.source.x)
        .attr("y1", (edge: { source: SimulationNode }) => edge.source.y)
        .attr("x2", (edge: { target: SimulationNode }) => edge.target.x)
        .attr("y2", (edge: { target: SimulationNode }) => edge.target.y);
    };
    simulation.stop().alpha(1);
    for (let index = 0; index < 240; index++) simulation.tick();
    updatePositions();

    const drag = d3.drag()
      .on("start", (event: any, node: SimulationNode) => {
        node.fx = node.x;
        node.fy = node.y;
        if (!event.active) simulation.alphaTarget(0.15).restart();
      })
      .on("drag", (event: any, node: SimulationNode) => {
        node.fx = event.x;
        node.fy = event.y;
      })
      .on("end", (event: any, node: SimulationNode) => {
        node.fx = null;
        node.fy = null;
        if (!event.active) simulation.alphaTarget(0);
      });
    nodeElements.call(drag);
    simulation.on("tick", updatePositions).restart();

    const resizeObserver = new ResizeObserver(() => {
      const nextWidth = Math.max(container.clientWidth, 320);
      const nextHeight = Math.max(container.clientHeight, 320);
      svg.attr("viewBox", `0 0 ${nextWidth} ${nextHeight}`);
    });
    resizeObserver.observe(container);

    return () => {
      resizeObserver.disconnect();
      simulation.stop();
      svg.on(".zoom", null);
      graphRef.current = null;
    };
  }, [ariaLabel, edges, emptyMessage, nodes, onContextMenu, onSelect, selectedId]);

  return (
    <div className="force-graph" ref={containerRef}>
      <svg ref={svgRef} role="group" aria-label={ariaLabel} data-zoom="1" />
    </div>
  );
});
