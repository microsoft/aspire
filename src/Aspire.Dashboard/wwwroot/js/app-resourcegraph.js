import './d3.v7.min.js'

// Layout constants. The node circle is r=56 with its label sitting below it, so the collision radius is
// wider than the circle to keep labels from colliding as well.
const NODE_COLLIDE_RADIUS = 92;
const LAYER_HEIGHT = 230;
const SIBLING_SPACING = 210;

let resourceGraph = null;

export function initializeResourcesGraph(resourcesInterop, graphIcons, instanceId) {
    resourceGraph?.dispose();
    resourceGraph = new ResourceGraph(resourcesInterop, graphIcons, instanceId);
    resourceGraph.resize();
}

export function disposeResourcesGraph(instanceId) {
    // An old Blazor page can finish disposing after the replacement page has initialized its graph.
    if (resourceGraph?.instanceId === instanceId) {
        resourceGraph.dispose();
        resourceGraph = null;
    }
}

export function updateResourcesGraph(resources) {
    if (resourceGraph) {
        resourceGraph.updateResources(resources);
    }
}

export function updateResourcesGraphSelected(resourceName) {
    if (resourceGraph) {
        resourceGraph.switchTo(resourceName);
    }
}

export function updateResourcesGraphContextMenu(open) {
    resourceGraph?.contextMenuChanged(open);
}

export function focusResourceMenuItem(instanceId, itemId, anchorId) {
    return resourceGraph?.instanceId === instanceId
        ? resourceGraph.focusMenuItem(itemId, anchorId)
        : Promise.resolve(false);
}

class ResourceGraph {
    constructor(resourcesInterop, graphIcons, instanceId) {
        this.instanceId = instanceId;
        this.resources = [];
        this.resourcesInterop = resourcesInterop;

        // Static icon (SVG path + tooltip) shared by every node's context-menu affordance (cog).
        this.menuIcon = graphIcons ? graphIcons.menu : null;

        this.nodes = [];
        this.links = [];

        this.svg = d3.select('.resource-graph');
        this.container = this.svg.node().closest('.resource-graph-container');
        this.baseGroup = this.svg.append("g");

        this.draggingNodeId = null;
        this.activeDrag = null;
        this.dragMoved = false;

        // The view is auto-fitted to the graph until the user zooms or pans, after which their framing is
        // left alone.
        this.userAdjustedView = false;

        // Enable zoom + pan
        // https://www.d3indepth.com/zoom-and-pan/
        // scaleExtent limits zoom to reasonable values
        this.zoom = d3.zoom().scaleExtent([0.1, 4]).on('zoom', (event) => {
            this.baseGroup.attr('transform', event.transform);

            // sourceEvent is only set when the transform came from a real gesture, so programmatic
            // auto-fitting doesn't count as the user taking control of the framing.
            if (event.sourceEvent) {
                this.userAdjustedView = true;
            }
        });
        this.svg.call(this.zoom).on('dblclick.zoom', null);

        // simulation setup with all forces
        this.linkForce = d3
            .forceLink()
            .id(function (link) { return link.id })
            .strength(0.25)
            .distance(LAYER_HEIGHT);

        this.simulation = d3
            .forceSimulation()
            .force('link', this.linkForce)
            .force('charge', d3.forceManyBody().strength(-900).distanceMax(700))
            .force("collide", d3.forceCollide(NODE_COLLIDE_RADIUS).iterations(4))
            // These two are what turn a floating force layout into a hierarchy. Y is pinned hard to the
            // node's depth so every generation forms a row, while X only nudges each node toward the slot
            // computed for it so collision can still spread crowded rows out.
            .force("y", d3.forceY((node) => node.targetY || 0).strength(1))
            .force("x", d3.forceX((node) => node.targetX || 0).strength(0.25));

        this.dragDrop = d3.drag()
            .filter(event => !event.ctrlKey && !event.button && this.activeDrag === null)
            .clickDistance(3)
            .on('start', (event) => {
                this.activeDrag = event.subject;
                this.draggingNodeId = event.subject.id;
                this.dragMoved = false;
                this.simulation.stop();
                event.subject.fx = event.subject.x;
                event.subject.fy = event.subject.y;
            })
            .on('drag', (event) => {
                if (this.activeDrag !== event.subject) {
                    return;
                }

                // Move directly under the pointer with physics paused. D3 caches collision radii, and
                // even a zero-radius node still collides with its neighbours' nonzero radii.
                if (!this.dragMoved) {
                    // Reparenting on mousedown also suppresses ordinary click events in browsers.
                    this.nodeElements.filter(node => node === event.subject).raise();
                }
                this.dragMoved = true;
                this.userAdjustedView = true;
                event.subject.x = event.subject.fx = event.x;
                event.subject.y = event.subject.fy = event.y;
                event.subject.vx = event.subject.vy = 0;
                this.onTick();
            })
            .on('end', () => this.finishDrag());

        d3.select(window).on('blur.resource-graph', () => {
            if (this.activeDrag) {
                d3.select(window).on('.drag', null);
                d3.dragEnable(window, true);
                this.finishDrag();
            }
        });

        var defs = this.svg.append("defs");

        // Dot grid that sits under the graph and pans/zooms with it, so the canvas the nodes live on is
        // visible and it's obvious how far the content extends when dragging around.
        var gridPattern = defs.append("pattern")
            .attr("id", "resource-graph-grid")
            .attr("patternUnits", "userSpaceOnUse")
            .attr("width", "40")
            .attr("height", "40");
        gridPattern
            .append("circle")
            .attr("cx", "2")
            .attr("cy", "2")
            .attr("r", "1.5")
            .attr("class", "resource-graph-grid-dot");

        this.createArrowMarker(defs, "arrow-normal", "arrow-normal", 10, 10, 66);
        this.createArrowMarker(defs, "arrow-highlight", "arrow-highlight", 15, 15, 48);
        this.createArrowMarker(defs, "arrow-highlight-expand", "arrow-highlight-expand", 15, 15, 56);

        var highlightedPattern = defs.append("pattern")
            .attr("id", "highlighted-pattern")
            .attr("patternUnits", "userSpaceOnUse")
            .attr("width", "17.5")
            .attr("height", "17.5")
            .attr("patternTransform", "rotate(45)");

        highlightedPattern
            .append("rect")
            .attr("x", "0")
            .attr("y", "0")
            .attr("width", "17.5")
            .attr("height", "17.5")
            .attr("fill", "var(--aspire-page-background)");

        highlightedPattern
            .append("line")
            .attr("x1", "0")
            .attr("y", "0")
            .attr("x2", "0")
            .attr("y2", "17.5")
            .attr("stroke", "var(--resource-graph-secondary-hover-background)")
            .attr("stroke-width", "15");

        // The grid is deliberately much larger than any realistic graph so panning never runs off the edge
        // of the drawn canvas.
        this.baseGroup
            .insert("rect", ":first-child")
            .attr("class", "resource-graph-background")
            .attr("x", -20000)
            .attr("y", -20000)
            .attr("width", 40000)
            .attr("height", 40000)
            .attr("fill", "url(#resource-graph-grid)");

        this.linkElementsG = this.baseGroup.append("g").attr("class", "links");
        this.nodeElementsG = this.baseGroup.append("g").attr("class", "nodes");
        this.linkElements = this.linkElementsG.selectAll("line");
        this.nodeElements = this.nodeElementsG.selectAll(".resource-group");

        this.initializeButtons();
        this.resizeObserver = new ResizeObserver(() => this.resize());
        this.resizeObserver.observe(this.container);
    }

    dispose() {
        this.cancelMenuFocus?.();
        this.simulation.stop();
        this.resizeObserver.disconnect();
        if (this.activeDrag) {
            d3.select(window).on('.drag', null);
            d3.dragEnable(window, true);
        }
        d3.select(window).on('blur.resource-graph', null);
        this.svg.interrupt().on('.zoom', null);
        this.svg.selectAll('*').interrupt().remove();
        d3.select(this.container).selectAll('.graph-zoom-in, .graph-zoom-out, .graph-reset').on('click', null);
    }

    focusMenuItem(itemId, anchorId) {
        this.cancelMenuFocus?.();
        return new Promise(resolve => {
            const complete = focused => {
                observer.disconnect();
                clearTimeout(timeout);
                this.cancelMenuFocus = null;
                resolve(focused);
            };
            const tryFocus = () => {
                const item = document.getElementById(itemId);
                const anchor = document.getElementById(anchorId);
                // Fluent renders popup items separately and assigns tabindex when its web components
                // are ready. Its anchor's aria-expanded also signals keyboard-listener initialization.
                if (anchor?.getAttribute('aria-expanded') === 'true' &&
                    item?.hasAttribute('tabindex') && item.getClientRects().length > 0) {
                    item.focus();
                    if (document.activeElement === item || item.contains(document.activeElement)) {
                        complete(true);
                    }
                }
            };
            const observer = new MutationObserver(tryFocus);
            const timeout = setTimeout(() => complete(false), 5000);
            this.cancelMenuFocus = () => complete(false);
            observer.observe(document.body, {
                subtree: true,
                childList: true,
                attributes: true,
                attributeFilter: ['aria-expanded', 'tabindex', 'hidden', 'style', 'class']
            });
            tryFocus();
        });
    }

    finishDrag() {
        const node = this.activeDrag;
        if (!node) {
            return;
        }

        this.activeDrag = null;
        this.draggingNodeId = null;
        if (this.nodes.includes(node)) {
            if (this.dragMoved) {
                node.pinned = true;
                this.releasePinnedNodesOverlapping(node);
            } else if (!node.pinned) {
                node.fx = node.fy = null;
            }
        }

        this.updateNodePinnedState();
        if (this.dragMoved) {
            this.simulation.alpha(0.4);
        }
        this.simulation.alphaTarget(0).restart();
    }

    initializeButtons() {
        d3.select('.graph-zoom-in').on("click", () => this.zoomIn());
        d3.select('.graph-zoom-out').on("click", () => this.zoomOut());
        d3.select('.graph-reset').on("click", () => this.resetZoomAndPan());
    }

    resetZoomAndPan() {
        this.svg.interrupt();
        this.simulation.stop();
        this.userAdjustedView = false;
        this.unpinAllNodes();
        this.computeHierarchy();
        for (const node of this.nodes) {
            node.x = node.targetX;
            node.y = node.targetY;
            node.vx = node.vy = 0;
        }
        this.simulation.nodes(this.nodes).alphaTarget(0).alpha(1);
        this.simulation.tick(300);
        this.onTick();
        this.fitToView();
    }

    // Releases every pinned node so the layout is driven by the simulation again.
    unpinAllNodes() {
        for (const node of this.nodes) {
            node.pinned = false;
            node.fx = null;
            node.fy = null;
        }
        this.updateNodePinnedState();
    }

    // Reflects the pinned state of each node in the DOM so it can be styled.
    updateNodePinnedState() {
        if (!this.nodeElements) {
            return;
        }

        this.nodeElements.classed("resource-group-pinned", n => !!n.pinned);
    }

    // Unpins any node that the supplied node has been dropped on top of. Collision alone can't separate two
    // pinned nodes because neither is free to move, so the older pin yields to the newer one.
    releasePinnedNodesOverlapping(node) {
        const minimumDistance = NODE_COLLIDE_RADIUS * 2;

        for (const other of this.nodes) {
            if (other === node || !other.pinned) {
                continue;
            }

            const dx = (other.x || 0) - (node.x || 0);
            const dy = (other.y || 0) - (node.y || 0);
            if (Math.sqrt(dx * dx + dy * dy) < minimumDistance) {
                other.pinned = false;
                other.fx = null;
                other.fy = null;
            }
        }
    }

    /*
     * Works out where each node belongs in the hierarchy.
     *
     * The graph is a DAG rather than a tree, because a resource can be depended on by several others. To lay
     * it out as a readable hierarchy each node is assigned to the first parent that reaches it in a breadth
     * first walk from the roots, which produces a spanning tree. Links to additional parents still render;
     * they just don't get a say in where the node sits.
     *
     * Depth becomes a fixed row (targetY) and the spanning tree drives a tidy left-to-right ordering
     * (targetX): leaves are laid out in order and each parent is centred over its own children.
     */
    computeHierarchy() {
        const nodesById = new Map(this.nodes.map(n => [n.id, n]));
        const childIds = new Map();
        const hasParent = new Set();

        for (const link of this.links) {
            const source = linkEndId(link.source);
            const target = linkEndId(link.target);
            if (!nodesById.has(source) || !nodesById.has(target)) {
                continue;
            }

            if (!childIds.has(source)) {
                childIds.set(source, []);
            }
            childIds.get(source).push(target);
            hasParent.add(target);
        }

        const roots = this.nodes.filter(n => !hasParent.has(n.id));

        const depth = new Map();
        const treeChildren = new Map();
        const visited = new Set();
        const queue = [];

        for (const root of roots) {
            visited.add(root.id);
            depth.set(root.id, 0);
            queue.push(root.id);
        }

        // Any node left unvisited is only reachable through a cycle, so promote it to a root of its own
        // rather than leaving it without a position.
        for (const node of this.nodes) {
            if (!visited.has(node.id)) {
                visited.add(node.id);
                depth.set(node.id, 0);
                roots.push(node);
                queue.push(node.id);
            }

            // Walk what is reachable so far before considering the next unvisited node, otherwise every
            // member of a cycle gets promoted instead of just the first one.
            while (queue.length > 0) {
                const current = queue.shift();
                const currentDepth = depth.get(current);

                for (const child of (childIds.get(current) || [])) {
                    if (visited.has(child)) {
                        continue;
                    }

                    visited.add(child);
                    depth.set(child, currentDepth + 1);

                    if (!treeChildren.has(current)) {
                        treeChildren.set(current, []);
                    }
                    treeChildren.get(current).push(child);
                    queue.push(child);
                }
            }
        }

        const xById = new Map();
        let nextLeafSlot = 0;

        const assignX = (id) => {
            const children = treeChildren.get(id);
            if (!children || children.length === 0) {
                const x = nextLeafSlot * SIBLING_SPACING;
                nextLeafSlot++;
                xById.set(id, x);
                return x;
            }

            const childXs = children.map(assignX);
            const x = (Math.min(...childXs) + Math.max(...childXs)) / 2;
            xById.set(id, x);
            return x;
        };

        for (const root of roots) {
            assignX(root.id);
        }

        // Centre the laid out tree on the origin so the initial view is balanced.
        const allX = [...xById.values()];
        const xOffset = allX.length > 0 ? (Math.min(...allX) + Math.max(...allX)) / 2 : 0;
        const maxDepth = Math.max(0, ...depth.values());
        const yOffset = (maxDepth * LAYER_HEIGHT) / 2;

        for (const node of this.nodes) {
            node.depth = depth.get(node.id) || 0;
            node.targetX = (xById.get(node.id) || 0) - xOffset;
            node.targetY = (node.depth * LAYER_HEIGHT) - yOffset;

            // Seed brand new nodes on their target so the first frame is already laid out as a hierarchy
            // instead of animating in from the middle of the canvas.
            if (node.x === undefined || node.y === undefined) {
                node.x = node.targetX;
                node.y = node.targetY;
            }
        }

        function linkEndId(end) {
            return typeof end === "object" ? end.id : end;
        }
    }

    // Frames the whole graph in the viewport. Skipped once the user has zoomed or panned so their framing
    // isn't yanked away when a resource changes state.
    fitToView() {
        if (this.userAdjustedView || this.nodes.length === 0) {
            return;
        }

        const container = this.container;
        if (container.clientWidth === 0 || container.clientHeight === 0) {
            return;
        }

        // Include labels and selected-node scaling, not just the circle centres.
        const bounds = this.nodeElementsG.node().getBBox();
        const padding = 32;
        const width = Math.max(bounds.width + padding * 2, 1);
        const height = Math.max(bounds.height + padding * 2, 1);

        // Never scale up past 1. A small graph should sit at natural size in the middle rather than being
        // blown up to fill the panel.
        const scale = Math.min(1, container.clientWidth / width, container.clientHeight / height);
        const centerX = bounds.x + bounds.width / 2;
        const centerY = bounds.y + bounds.height / 2;

        const transform = d3.zoomIdentity.scale(scale).translate(-centerX, -centerY);
        this.svg.call(this.zoom.transform, transform);
    }

    zoomIn() {
        this.userAdjustedView = true;
        this.svg.transition().call(this.zoom.scaleBy, 1.5);
    }

    zoomOut() {
        this.userAdjustedView = true;
        this.svg.transition().call(this.zoom.scaleBy, 2 / 3);
    }

    createArrowMarker(parent, id, className, width, height, x) {
        parent.append("marker")
            .attr("id", id)
            .attr("viewBox", "0 -5 10 10")
            .attr("refX", x)
            .attr("refY", 0)
            .attr("markerWidth", width)
            .attr("markerHeight", height)
            .attr("orient", "auto")
            .attr("markerUnits", "userSpaceOnUse")
            .attr("class", className)
            .append("path")
            .attr("d", 'M0,-5L10,0L0,5');
    }

    resize() {
        // Measure the graph container rather than the whole summary panel. The panel also contains the tabs
        // row, so measuring it made the drawing area taller than the space the graph actually occupies.
        var container = this.container;
        if (container && container.clientWidth > 0 && container.clientHeight > 0) {
            var width = container.clientWidth;
            var height = container.clientHeight;
            this.svg.attr("viewBox", [-width / 2, -height / 2, width, height]);

            this.fitToView();
        }
    }

    switchTo(resourceName) {
        this.selectedNode = this.nodes.find(node => node.id === resourceName);
        this.updateNodeHighlights(null);
    }

    resourceEqual(r1, r2) {
        if (r1.name !== r2.name) {
            return false;
        }
        if (r1.displayName !== r2.displayName) {
            return false;
        }
        if (!this.iconEqual(r1.resourceIcon, r2.resourceIcon)) {
            return false;
        }
        if (r1.childNames.length !== r2.childNames.length) {
            return false;
        }
        for (var i = 0; i < r1.childNames.length; i++) {
            if (r1.childNames[i] !== r2.childNames[i]) {
                return false;
            }
        }

        return true;
    }

    iconEqual(i1, i2) {
        if (i1.path !== i2.path) {
            return false;
        }
        if (i1.color !== i2.color) {
            return false;
        }
        if (i1.tooltip !== i2.tooltip) {
            return false;
        }

        return true;
    }

    resourcesChanged(existingResource, newResources) {
        if (!existingResource || newResources.length != existingResource.length) {
            return true;
        }

        for (var i = 0; i < newResources.length; i++) {
            if (!this.resourceEqual(newResources[i], existingResource[i], false)) {
                return true;
            }
        }

        return false;
    }

    updateNodes(newResources) {
        const existingNodes = new Map(this.nodes.map(node => [node.id, node]));
        const updatedNodes = [];

        // calculate degree (number of connections) for each resource
        const degreeMap = new Map();
        newResources.forEach(resource => {
            degreeMap.set(resource.name, resource.childNames.length);
        });

        // also count incoming connections
        newResources.forEach(resource => {
            resource.childNames.forEach(childName => {
                const currentDegree = degreeMap.get(childName) || 0;
                degreeMap.set(childName, currentDegree + 1);
            });
        });

        newResources.forEach(resource => {
            const node = existingNodes.get(resource.name) || { id: resource.name };
            const degree = degreeMap.get(resource.name) || 1;

            // D3 retains this object as the subject for the entire gesture. Replacing it on a health
            // update disconnects an active drag from the rendered node and loses the pin on mouse-up.
            Object.assign(node, {
                label: resource.displayName,
                endpointUrl: resource.endpointUrl,
                endpointText: resource.endpointText,
                resourceIcon: createIcon(resource.resourceIcon),
                stateIcon: createIcon(resource.stateIcon),
                healthState: resource.healthState,
                isAppHost: resource.isAppHost,
                degree: degree
            });
            updatedNodes.push(node);
        });

        this.nodes = updatedNodes;

        function createIcon(resourceIcon) {
            return {
                path: resourceIcon.path,
                color: resourceIcon.color,
                tooltip: resourceIcon.tooltip
            };
        }
    }

    updateResources(newResources) {
        // Check if the overall structure of the graph has changed. i.e. nodes or links have been added or removed.
        var hasStructureChanged = this.resourcesChanged(this.resources, newResources);

        this.resources = newResources;

        this.updateNodes(newResources);

        this.links = [];
        var healthStateByName = new Map(newResources.map(r => [r.name, r.healthState]));
        for (var i = 0; i < newResources.length; i++) {
            var resource = newResources[i];

            var resourceLinks = resource.childNames
                .filter((childName) => {
                    return healthStateByName.has(childName);
                })
                .map((childName) => {
                    return {
                        id: JSON.stringify([resource.name, childName]),
                        target: childName,
                        source: resource.name,
                        // The link takes the child's rolled up state. Because the child's state already
                        // includes everything below it, an unhealthy leaf colours every link on the path
                        // back to the root without any extra propagation here.
                        healthState: healthStateByName.get(childName),
                        strength: 0.7
                    };
                });

            this.links.push(...resourceLinks);
        }

        // Positions have to be resolved before the nodes are rendered so brand new nodes can be seeded on
        // their place in the hierarchy rather than flying in from the origin.
        this.computeHierarchy();

        // Update nodes
        this.nodeElements = this.nodeElementsG
            .selectAll(".resource-group")
            .data(this.nodes, n => n.id);

        // Remove excess nodes:
        this.nodeElements
            .exit()
            .transition()
            .attr("opacity", 0)
            .remove();

        // Resource node
        var newNodes = this.nodeElements
            .enter().append("g")
            .attr("class", "resource-group")
            .attr("opacity", 0)
            .attr("resource-name", n => n.id)
            .call(this.dragDrop);

        var newNodesContainer = newNodes
            .append("g")
            .attr("class", "resource-scale")
            .on('click', this.selectNode)
            .on('dblclick', this.unpinNode)
            .on('contextmenu', this.nodeContextMenu)
            .on('mouseover', this.hoverNode)
            .on('mouseout', this.unHoverNode);
        newNodesContainer
            .append("circle")
            .attr("r", 56)
            .attr("class", "resource-node")
            .attr("stroke", "white")
            .attr("stroke-width", "4");
        newNodesContainer
            .append("circle")
            .attr("r", 53)
            .attr("class", "resource-node-border");
        var iconTransform = newNodesContainer
            .append("g")
            .attr("transform", n => n.endpointText ? "translate(-24,-37)" : "translate(-24,-24)")
        var iconPath = iconTransform
            .append("path");
        iconPath
            .attr("fill", n => n.resourceIcon.color)
            .attr("d", n => n.resourceIcon.path)
            .append("title")
            .text(n => n.resourceIcon.tooltip);

        // Icon paths could be mixed size. We need to transform icons to always be displayed at a consistent size.
        iconPath.each(function (d) {
            const iconSize = 48;

            const path = d3.select(this);
            const node = path.node();
            const bbox = node.getBBox();

            const available = Math.max(1, iconSize - 2);
            const scale = available / Math.max(bbox.width, bbox.height);

            const cx = bbox.x + bbox.width / 2;
            const cy = bbox.y + bbox.height / 2;

            // apply scaling & centering inside this group
            path.attr("transform",
                `translate(${iconSize / 2},${iconSize / 2}) scale(${scale}) translate(${-cx},${-cy})`);
        });

        var endpointGroup = newNodesContainer
            .append("g")
            .attr("transform", "translate(0,28)")
            .attr("class", "resource-endpoint")
            .style("display", n => n.endpointText ? null : "none");
        endpointGroup.append("text");
        endpointGroup.append("title");

        // Resource status
        var statusGroup = newNodesContainer
            .append("g")
            .attr("transform", "scale(1.6) translate(14,-34)");
        statusGroup
            .append("circle")
            .attr("r", 8)
            .attr("cy", 8)
            .attr("cx", 8)
            .attr("class", "resource-status-circle")
            .append("title");
        statusGroup
            .append("path")
            .attr("class", "resource-status-path")
            .append("title");

        var resourceNameGroup = newNodesContainer
            .append("g")
            .attr("transform", "translate(0,71)")
            .attr("class", "resource-name");
        resourceNameGroup
            .append("text")
            .text(n => trimText(n.label, 30));
        resourceNameGroup
            .append("title")
            .text(n => n.label);

        // Context menu affordance. A cog positioned on the circle rim directly below the status badge.
        // The status badge sits at the top-right via "scale(1.6) translate(14,-34)" (center ~(35,-42)),
        // so mirroring it vertically puts the cog at the bottom-right ~(35,43). Hidden until the node is
        // hovered (see .resource-menu-cog CSS); it makes the node's interactivity discoverable by opening
        // the same context menu as right-clicking the node.
        var cogGroup = newNodesContainer
            .filter(n => !n.isAppHost)
            .append("g")
            .attr("class", "resource-menu-cog")
            .attr("id", n => `resource-menu-cog-${n.id}`)
            .attr("transform", "translate(35,43)")
            .attr("role", "button")
            .attr("tabindex", 0)
            .attr("aria-label", n => this.getResourceMenuLabel(n))
            .attr("aria-haspopup", "menu")
            .attr("aria-expanded", "false")
            // D3's drag handler is attached to the ancestor resource group. Stop drag-start
            // events here so an imprecise cog click can never move the resource node.
            .on('mousedown touchstart', event => event.stopPropagation())
            .on('keydown', this.cogMenuKeyDown)
            .on('click', this.cogMenuClick);
        cogGroup
            .append("circle")
            .attr("r", 14)
            .attr("class", "resource-menu-cog-background");
        cogGroup
            .append("title")
            .text(n => this.getResourceMenuLabel(n));
        if (this.menuIcon) {
            var cogIcon = cogGroup
                .append("path")
                .attr("class", "resource-menu-cog-icon")
                .attr("d", this.menuIcon.path);

            // Scale and center the icon inside the cog background, matching how resource icons are sized.
            cogIcon.each(function () {
                const iconSize = 16;
                const path = d3.select(this);
                const bbox = this.getBBox();
                const scale = iconSize / Math.max(bbox.width, bbox.height);
                const cx = bbox.x + bbox.width / 2;
                const cy = bbox.y + bbox.height / 2;

                path.attr("transform", `scale(${scale}) translate(${-cx},${-cy})`);
            });
        }

        newNodes.transition()
            .attr("opacity", 1);

        this.nodeElements = newNodes.merge(this.nodeElements);

        // Set resource values that change.
        this.nodeElementsG
            .selectAll(".resource-group")
            .attr("data-health", n => n.healthState);
        this.nodeElements.select(".resource-name text").text(n => trimText(n.label, 30));
        this.nodeElements.select(".resource-name title").text(n => n.label);
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-menu-cog")
            .attr("aria-label", n => this.getResourceMenuLabel(n))
            .select("title")
            .text(n => this.getResourceMenuLabel(n));
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-endpoint")
            .style("display", n => n.endpointText ? null : "none")
            .select("text")
            .text(n => trimText(n.endpointText, 15));
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-endpoint")
            .select("title")
            .text(n => n.endpointText || "");
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-status-circle")
            .select("title")
            .text(n => n.stateIcon.tooltip);
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-status-path")
            .attr("d", n => n.stateIcon.path)
            .attr("fill", n => n.stateIcon.color)
            .select("title")
            .text(n => n.stateIcon.tooltip);

        // Update links
        this.linkElements = this.linkElementsG
            .selectAll("line")
            .data(this.links, (d) => { return d.id; });

        this.linkElements
            .exit()
            .transition()
            .attr("opacity", 0)
            .remove();

        var newLinks = this.linkElements
            .enter().append("line")
            .attr("opacity", 0)
            .attr("class", "resource-link");

        newLinks.transition()
            .attr("opacity", 1);

        this.linkElements = newLinks.merge(this.linkElements);

        // Health is refreshed on every update because a resource can change state without the shape of the
        // graph changing at all.
        this.linkElements.attr("data-health", l => l.healthState);

        this.updateNodePinnedState();

        this.simulation
            .nodes(this.nodes)
            .on('tick', this.onTick);

        this.simulation.force("link").links(this.links);
        if (this.activeDrag) {
            this.simulation.stop();
            this.onTick();
            return;
        }
        if (hasStructureChanged) {
            this.simulation.stop();

            // Set alpha (give energy) and simulate the graph before rendering.
            // This prevents the graph from jumping around when loaded or changed.
            this.simulation.alpha(1);
            for (let i = 0; i < 300; i++) {
                this.simulation.tick();
            }

            this.onTick();
            this.fitToView();
        }

        this.simulation.restart();

        function trimText(text, maxLength) {
            if (!text) {
                return "";
            }
            if (text.length > maxLength) {
                return text.slice(0, maxLength) + "\u2026";
            }
            return text;
        }
    }

    onTick = () => {
        this.nodeElements.attr("transform", function (d) { return "translate(" + d.x + "," + d.y + ")"; });
        this.linkElements
            .attr('x1', function (link) { return link.source.x })
            .attr('y1', function (link) { return link.source.y })
            .attr('x2', function (link) { return link.target.x })
            .attr('y2', function (link) { return link.target.y });
    }

    getNeighbors(node) {
        return this.links.reduce(function (neighbors, link) {
            if (link.target.id === node.id) {
                neighbors.push(link.source.id);
            } else if (link.source.id === node.id) {
                neighbors.push(link.target.id);
            }
            return neighbors;
        },
            [node.id]);
    }

    getResourceMenuLabel(node) {
        return this.menuIcon ? this.menuIcon.labelFormat.split('{0}').join(node.label) : "";
    }

    isNeighborLink(node, link) {
        return link.target.id === node.id || link.source.id === node.id
    }

    getLinkClass(nodes, selectedNode, link) {
        if (nodes.find(n => this.isNeighborLink(n, link))) {
            if (this.nodeEquals(selectedNode, link.target)) {
                return 'resource-link-highlight-expand';
            }
            return 'resource-link-highlight';
        }
        return 'resource-link';
    }

    nodeContextMenu = async (event) => {
        var data = event.target.__data__;

        // Prevent default browser context menu.
        event.preventDefault();
        if (data.isAppHost) {
            return;
        }

        await this.openResourceContextMenu(data.id, event.clientX, event.clientY, null, null);
    };

    cogMenuClick = async (event) => {
        // currentTarget is the cog group the handler is attached to. Its datum is inherited from the
        // node group (d3 propagates data to appended children).
        var data = event.currentTarget.__data__;

        // Stop the click from also reaching the node's click handler (which would select/deselect it).
        event.preventDefault();
        event.stopPropagation();

        await this.openResourceContextMenu(data.id, event.clientX, event.clientY, event.currentTarget, null);
    };

    cogMenuKeyDown = async (event) => {
        if (event.repeat || (event.key !== 'Enter' && event.key !== ' ')) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();

        var data = event.currentTarget.__data__;
        var bounds = event.currentTarget.getBoundingClientRect();
        await this.openResourceContextMenu(
            data.id,
            Math.round(bounds.left + bounds.width / 2),
            Math.round(bounds.top + bounds.height / 2),
            event.currentTarget,
            event.currentTarget.id);
    };

    openResourceContextMenu = async (id, clientX, clientY, trigger, focusElementId) => {
        this.contextMenuTrigger?.setAttribute("aria-expanded", "false");
        this.contextMenuTrigger = trigger;
        this.contextMenuChanged(true);

        try {
            // Opening completes immediately; subsequent menu events report the open state separately.
            await this.resourcesInterop.invokeMethodAsync('ResourceContextMenu', id, clientX, clientY, focusElementId);
        } catch (error) {
            this.contextMenuChanged(false);
            throw error;
        }
    };

    contextMenuChanged = (open) => {
        this.openContextMenu = open;
        this.contextMenuTrigger?.setAttribute("aria-expanded", open ? "true" : "false");
        if (!open) {
            this.cancelMenuFocus?.();
            this.contextMenuTrigger = null;
            this.updateNodeHighlights(null);
        }
    };

    selectNode = (event) => {
        var data = event.target.__data__;
        if (data.isAppHost) {
            return;
        }

        // Always send the clicked on resource to the server. It will clear the selection if the same resource is clicked again.
        this.resourcesInterop.invokeMethodAsync('SelectResource', data.id);

        // Unscale the previous selected node.
        if (this.selectedNode) {
            changeScale(this, this.selectedNode.id, 1);
        }

        // Scale selected node if it is not the same as the previous selected node.
        var clearSelection = this.nodeEquals(data, this.selectedNode);
        if (!clearSelection) {
            changeScale(this, data.id, 1.2);
        }

        this.selectedNode = clearSelection ? null : data;

        function changeScale(self, id, scale) {
            let match = self.nodeElementsG
                .selectAll(".resource-group")
                .filter(function (d) {
                    return d.id == id;
                });

            match
                .select(".resource-scale")
                .transition()
                .duration(300)
                .style("transform", `scale(${scale})`)
                .on("end", s => {
                    match.select(".resource-scale").style("transform", null);
                    self.updateNodeHighlights(null);
                });
        }
    }

    hoverNode = (event) => {
        var mouseoverNode = event.target.__data__;

        this.updateNodeHighlights(mouseoverNode);
    }

    // Releases a node pinned by dragging so the simulation can lay it out again.
    unpinNode = (event) => {
        var id = event.target.__data__?.id;
        var node = id ? this.nodes.find(n => n.id === id) : null;
        if (!node || !node.pinned) {
            return;
        }

        // The zoom behavior also handles dblclick. Without this the graph would zoom in while unpinning.
        event.preventDefault();
        event.stopPropagation();

        node.pinned = false;
        node.fx = null;
        node.fy = null;

        this.updateNodePinnedState();
        this.simulation.alpha(0.3).restart();
    }

    unHoverNode = (event) => {
        this.updateNodeHighlights(null);
    };

    nodeEquals(resource1, resource2) {
        if (!resource1 || !resource2) {
            return false;
        }
        return resource1.id === resource2.id;
    }

    updateNodeHighlights = (mouseoverNode) => {
        var mouseoverNeighbors = mouseoverNode ? this.getNeighbors(mouseoverNode) : [];
        var selectNeighbors = this.selectedNode ? this.getNeighbors(this.selectedNode) : [];
        var neighbors = [...mouseoverNeighbors, ...selectNeighbors];

        // we modify the styles to highlight selected nodes
        this.nodeElements.attr('class', (node) => {
            var classNames = ['resource-group'];
            if (this.nodeEquals(node, mouseoverNode)) {
                classNames.push('resource-group-hover');
            }
            if (this.nodeEquals(node, this.selectedNode)) {
                classNames.push('resource-group-selected');
            }
            if (neighbors.indexOf(node.id) > -1) {
                classNames.push('resource-group-highlight');
            }
            // The class attribute is rebuilt from scratch here, so the pinned marker has to be reapplied
            // or dragging a node and then hovering any node would silently unpin it visually.
            if (node.pinned) {
                classNames.push('resource-group-pinned');
            }
            return classNames.join(' ');
        });
        this.linkElements.attr('class', (link) => {
            var nodes = [];
            if (mouseoverNode) {
                nodes.push(mouseoverNode);
            }
            if (this.selectedNode) {
                nodes.push(this.selectedNode);
            }
            return this.getLinkClass(nodes, this.selectedNode, link);
        });
    };
};
