import './d3.v7.min.js'
import { attachResourceGraphGame } from './app-resourcegraph-game.js'

let resourceGraph = null;
const resourceNodeRadius = 53;
const resourceLinkMaskRadius = resourceNodeRadius + 3;
const selectedNodeScale = 1.2;
const tagGroupGap = 24;

export function initializeResourcesGraph(resourcesInterop, graphIcons, enableTagSelection) {
    resourceGraph = new ResourceGraph(resourcesInterop, graphIcons, enableTagSelection);
    resourceGraph.resize();
    // Pages that don't offer the Aspire Outage game (the previous dashboard experience) pass no game strings. Don't
    // attach it there: besides the Play button, it would also take the O key from the dashboard's global shortcuts.
    if (graphIcons?.game) {
        attachResourceGraphGame(resourceGraph, graphIcons.game);
    }

    const observer = new ResizeObserver(function () {
        resourceGraph.resize();
    });

    // Observe the element the graph is drawn in rather than the page layout around it. Its size also changes when
    // the surrounding chrome changes (for example the previous experience's tab header wrapping) and when the graph is
    // shown or hidden.
    const viewport = resourceGraph.getViewportElement();
    if (viewport) {
        observer.observe(viewport);
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
    if (resourceGraph) {
        resourceGraph.contextMenuChanged(open);
    }
}

class ResourceGraph {
    constructor(resourcesInterop, graphIcons, enableTagSelection) {
        this.resources = [];
        this.resourcesInterop = resourcesInterop;
        this.enableTagSelection = enableTagSelection;

        // Static icon (SVG path + tooltip) shared by every node's context-menu affordance (cog).
        this.menuIcon = graphIcons ? graphIcons.menu : null;
        this.hiddenIcon = graphIcons ? graphIcons.hidden : null;

        this.nodes = [];
        this.links = [];
        this.tagGroups = [];
        this.tagLayouts = [];
        this.tagCollisionGroups = [];
        this.outerTagGroups = [];
        this.hasFitGraph = false;

        this.svg = d3.select('.resource-graph');
        this.baseGroup = this.svg.append("g");

        // Enable zoom + pan
        // https://www.d3indepth.com/zoom-and-pan/
        // scaleExtent limits zoom to reasonable values
        this.zoom = d3.zoom().scaleExtent([0.2, 4]).on('zoom', (event) => {
            this.baseGroup.attr('transform', event.transform);
        });
        this.svg.call(this.zoom).on('dblclick.zoom', null);

        // simulation setup with all forces
        this.linkForce = d3
            .forceLink()
            .id(function (link) { return link.id })
            .strength(1.0)
            .distance(function (link) {
                // adaptive distance: longer for highly connected nodes
                const sourceDegree = link.source.degree || 1;
                const targetDegree = link.target.degree || 1;
                const maxDegree = Math.max(sourceDegree, targetDegree);

                // scale distance with degree: 150 for low degree, up to 250 for high degree
                return Math.min(150 + (maxDegree * 10), 250);
            });

        this.simulation = d3
            .forceSimulation()
            .force('link', this.linkForce)
            .force('charge', d3.forceManyBody().strength(-800))
            .force("collide", d3.forceCollide(node => this.getCollisionRadius(node)).iterations(10))
            .force("x", d3.forceX().strength(0.2))
            .force("y", d3.forceY().strength(0.4))
            .force("center", d3.forceCenter().strength(0.01));

        // Drag start is trigger on mousedown from click.
        // Only change the state of the simulation when the drag event is triggered.
        var dragActive = false;
        var dragged = false;
        this.dragDrop = d3.drag().filter(event => !event.ctrlKey && !event.button && !this.draggingTagGroup).on('start', (event) => {
            dragActive = event.active;
            event.subject.fx = event.subject.x;
            event.subject.fy = event.subject.y;
        }).on('drag', (event) => {
            if (!dragActive) {
                this.simulation.alphaTarget(0.1).restart();
                dragActive = true;
            }
            dragged = true;
            event.subject.fx = event.x;
            event.subject.fy = event.y;
        }).on('end', (event) => {
            if (dragged) {
                this.simulation.alphaTarget(0);
                dragged = false;
            }
            event.subject.fx = null;
            event.subject.fy = null;
        });

        this.tagGroupDrag = d3.drag()
            // The label's own container moves with the tag. Measure pointer movement in graph coordinates instead.
            .container(() => this.baseGroup.node())
            .subject((event, group) => {
                // Don't let a second pointer move or release nodes already held by another drag.
                if (this.draggingTagGroup || this.nodes.some(node => node.fx != null || node.fy != null)) {
                    return null;
                }
                const { x, y } = event;
                return { x, y, members: group.nodes.map(node => ({ node, dx: node.x - x, dy: node.y - y })) };
            })
            .on("start", ({ subject }) => {
                this.draggingTagGroup = true;
                for (const { node } of subject.members) {
                    node.fx = node.x;
                    node.fy = node.y;
                }
            })
            .on("drag", ({ subject, x, y }) => {
                if (!subject.dragged && x === subject.x && y === subject.y) {
                    return;
                }
                if (!subject.dragged) {
                    subject.dragged = true;
                    this.simulation.alphaTarget(0.1).restart();
                }
                for (const { node, dx, dy } of subject.members) {
                    // Set the position as well as the pin so a quick mouseup before the next tick doesn't lose the move.
                    node.x = node.fx = x + dx;
                    node.y = node.fy = y + dy;
                    node.vx = node.vy = 0;
                }
            })
            .on("end", ({ subject }) => {
                this.draggingTagGroup = false;
                for (const { node } of subject.members) {
                    node.fx = node.fy = null;
                }
                if (subject.dragged) {
                    this.simulation.alphaTarget(0);
                }
            });

        var defs = this.svg.append("defs");
        this.createArrowMarker(defs, "arrow-normal", "arrow-normal", 10, 10, 66);
        this.createArrowMarker(defs, "arrow-highlight", "arrow-highlight", 15, 15, 48);
        this.createArrowMarker(defs, "arrow-highlight-expand", "arrow-highlight-expand", 15, 15, 56);

        // Mask only links, including in the transparent gap just outside each node's border.
        this.linkMask = defs.append("mask")
            .attr("id", "resource-links-mask")
            .attr("maskUnits", "userSpaceOnUse")
            .attr("maskContentUnits", "userSpaceOnUse")
            .attr("mask-type", "luminance");
        this.linkMaskBackground = this.linkMask.append("rect").attr("fill", "white");

        // Replay the group fills in graph coordinates to preserve partial overlaps over any node background.
        const nodeTagTint = defs.append("pattern")
            .attr("id", "resource-node-tag-tint")
            .attr("patternUnits", "userSpaceOnUse")
            .attr("x", -resourceNodeRadius)
            .attr("y", -resourceNodeRadius)
            .attr("width", resourceNodeRadius * 2)
            .attr("height", resourceNodeRadius * 2);
        this.nodeBackgroundFills = nodeTagTint.append("g").attr("opacity", 0.5);

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

        this.tagElementsG = this.baseGroup.append("g").attr("class", "resource-tag-groups").attr("pointer-events", "none");
        this.linkElementsG = this.baseGroup.append("g").attr("class", "links")
            .attr("mask", "url(#resource-links-mask)");
        // Labels paint over links, but resource nodes still paint above both.
        this.tagControlsG = this.baseGroup.append("g").attr("class", "resource-tag-labels").attr("pointer-events", "none");
        this.nodeElementsG = this.baseGroup.append("g").attr("class", "nodes");
        // Selection updates can arrive before the first resource snapshot.
        this.linkElements = this.linkElementsG.selectAll(".resource-link");
        this.nodeElements = this.nodeElementsG.selectAll(".resource-group");

        this.initializeButtons();
    }

    initializeButtons() {
        d3.select('.graph-zoom-in').on("click", () => this.zoomIn());
        d3.select('.graph-zoom-out').on("click", () => this.zoomOut());
        d3.select('.graph-reset').on("click", () => this.resetZoomAndPan());
    }

    resetZoomAndPan() {
        this.fitGraph(true);
    }

    // Returns false when the graph couldn't be measured, e.g. because it is hidden behind another tab.
    fitGraph(animate) {
        const bounds = this.baseGroup.node()?.getBBox();
        const viewport = this.getViewportSize();
        if (!bounds || !viewport || bounds.width <= 0 || bounds.height <= 0) {
            const selection = animate ? this.svg.transition().duration(300) : this.svg;
            selection.call(this.zoom.transform, d3.zoomIdentity);
            return false;
        }

        const padding = 64;
        const availableWidth = Math.max(viewport.width - (padding * 2), 1);
        const availableHeight = Math.max(viewport.height - (padding * 2), 1);
        const fitScale = Math.min(availableWidth / bounds.width, availableHeight / bounds.height);
        // Avoid both extremes: don't enlarge a small graph, and don't reduce a large graph to an unreadable thumbnail.
        // Larger topologies can still be panned after reset.
        const scale = Math.min(Math.max(fitScale, 0.55), 1);
        const centerX = bounds.x + (bounds.width / 2);
        const centerY = bounds.y + (bounds.height / 2);
        const transform = d3.zoomIdentity
            .translate(-centerX * scale, -centerY * scale)
            .scale(scale);

        const selection = animate ? this.svg.transition().duration(300) : this.svg;
        selection.call(this.zoom.transform, transform);
        return true;
    }

    zoomIn() {
        this.svg.transition().call(this.zoom.scaleBy, 1.5);
    }

    zoomOut() {
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

    // The element the SVG is drawn in (.resource-graph-container). Every page that hosts the graph (the overview, the
    // /graph page and the previous experience's resources page) sizes the SVG with CSS to fill it, so its size is the
    // drawing area. A page that left the SVG sized by its viewBox would create a resize feedback loop here. Don't measure an ancestor such as
    // .resources-summary-layout: on the previous experience's page it also contains the tab header, so a viewBox built
    // from it is taller than the graph area and pushes the bottom-anchored graph controls out of view.
    getViewportElement() {
        return this.svg.node()?.parentElement ?? null;
    }

    getViewportSize() {
        const viewport = this.getViewportElement();
        if (!viewport || viewport.clientWidth <= 0 || viewport.clientHeight <= 0) {
            return null;
        }

        return { width: viewport.clientWidth, height: viewport.clientHeight };
    }

    resize() {
        // A hidden graph has no size. Keep the last viewBox until it is shown.
        const viewport = this.getViewportSize();
        if (!viewport) {
            return;
        }

        this.svg.attr("viewBox", [-viewport.width / 2, -viewport.height / 2, viewport.width, viewport.height]);
        this.positionTagMenuCogs();
        if (this.tagGroups.length > 0) {
            this.measureNodeBounds();
            this.onTick();
        }

        // Resources can arrive while the graph is hidden. Fit the new layout once it has a size.
        if (!this.hasFitGraph && this.nodes.length > 0) {
            this.hasFitGraph = this.fitGraph(false);
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
        if (r1.isHidden !== r2.isHidden) {
            return false;
        }
        if (!this.iconEqual(r1.resourceIcon, r2.resourceIcon)) {
            return false;
        }
        if (r1.referencedNames.length !== r2.referencedNames.length) {
            return false;
        }
        for (var i = 0; i < r1.referencedNames.length; i++) {
            if (r1.referencedNames[i] !== r2.referencedNames[i]) {
                return false;
            }
        }
        if (r1.tags.length !== r2.tags.length ||
            r1.tags.some((tag, index) => tag.name !== r2.tags[index].name || tag.color !== r2.tags[index].color)) {
            return false;
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

    resourcesChanged(existingResources, resourcesByName) {
        if (existingResources.length !== resourcesByName.size) {
            return true;
        }

        return existingResources.some(resource => {
            const updated = resourcesByName.get(resource.name);
            return !updated || !this.resourceEqual(resource, updated);
        });
    }

    updateNodes(newResources) {
        const existingNodes = this.nodes || []; // Ensure nodes is initialized
        const updatedNodes = [];
        let hasAddedNodes = false;

        // calculate degree (number of connections) for each resource
        const degreeMap = new Map();
        newResources.forEach(resource => {
            degreeMap.set(resource.name, resource.referencedNames.length);
        });

        // also count incoming connections
        newResources.forEach(resource => {
            resource.referencedNames.forEach(refName => {
                const currentDegree = degreeMap.get(refName) || 0;
                degreeMap.set(refName, currentDegree + 1);
            });
        });

        newResources.forEach(resource => {
            const existingNode = existingNodes.find(node => node.id === resource.name);
            const degree = degreeMap.get(resource.name) || 1;

            if (existingNode) {
                // D3 retains the drag subject until mouseup. Replacing it during a resource update interrupts the
                // drag and leaves the replacement pinned because only the original subject's fx/fy are cleared.
                updatedNodes.push(Object.assign(existingNode, {
                    label: resource.displayName,
                    endpointUrl: resource.endpointUrl,
                    endpointText: resource.endpointText,
                    resourceIcon: createIcon(resource.resourceIcon),
                    stateIcon: createIcon(resource.stateIcon),
                    isHidden: resource.isHidden,
                    tags: resource.tags,
                    degree: degree
                }));
            } else {
                hasAddedNodes = true;
                updatedNodes.push({
                    id: resource.name,
                    label: resource.displayName,
                    endpointUrl: resource.endpointUrl,
                    endpointText: resource.endpointText,
                    resourceIcon: createIcon(resource.resourceIcon),
                    stateIcon: createIcon(resource.stateIcon),
                    isHidden: resource.isHidden,
                    tags: resource.tags,
                    degree: degree
                });
            }
        });

        this.nodes = updatedNodes;
        return hasAddedNodes;

        function createIcon(resourceIcon) {
            return {
                path: resourceIcon.path,
                color: resourceIcon.color,
                tooltip: resourceIcon.tooltip
            };
        }
    }

    updateTagGroups() {
        const groups = new Map();
        for (const node of this.nodes) {
            node.tagKeys = new Set(node.tags.map(tag => tag.name.toUpperCase()));
            for (const tag of node.tags) {
                const key = tag.name.toUpperCase();
                if (!groups.has(key)) {
                    groups.set(key, { key, label: tag.name, color: tag.color, nodes: [] });
                }
                const members = groups.get(key).nodes;
                if (!members.includes(node)) {
                    members.push(node);
                }
            }
        }

        this.tagGroups = [...groups.values()].sort((a, b) => a.nodes.length - b.nodes.length || a.key.localeCompare(b.key));
        const layouts = new Map();
        for (let i = 0; i < this.tagGroups.length; i++) {
            const group = this.tagGroups[i];
            // Nested and identical memberships need separate outlines and label lanes, not coincident paths.
            group.level = 0;
            for (const inner of this.tagGroups.slice(0, i)) {
                if (inner.nodes.every(node => node.tagKeys.has(group.key))) {
                    group.level = Math.max(group.level, inner.level + 1);
                }
            }
            const membership = JSON.stringify(group.nodes.map(node => node.id).sort());
            if (!layouts.has(membership)) {
                layouts.set(membership, { nodes: group.nodes });
            }
            group.anchor = layouts.get(membership);
        }

        // Keep overlapping groups adjacent around the circle. An alphabetical/size-only order can put two tags
        // of the same resource on opposite sides, stretching their boundaries across unrelated resources.
        const remaining = [...layouts.values()].reverse();
        const anchors = [];
        while (remaining.length) {
            let nextIndex = 0;
            let bestOverlap = -1;
            for (let i = 0; i < remaining.length; i++) {
                const overlap = anchors.length
                    ? remaining[i].nodes.filter(node => anchors[anchors.length - 1].nodes.includes(node)).length
                    : remaining[i].nodes.length;
                if (overlap > bestOverlap) {
                    bestOverlap = overlap;
                    nextIndex = i;
                }
            }
            anchors.push(remaining.splice(nextIndex, 1)[0]);
        }
        const largestGroup = Math.max(0, ...this.tagGroups.map(group => group.nodes.length));
        const spacing = 220 * Math.sqrt(largestGroup) + 100;
        const radius = anchors.length <= 1 ? 0 : Math.max(300, spacing / (2 * Math.sin(Math.PI / anchors.length)));
        anchors.forEach((anchor, index) => {
            const angle = index * 2 * Math.PI / anchors.length - Math.PI / 2;
            anchor.x = Math.cos(angle) * radius;
            anchor.y = Math.sin(angle) * radius;
        });

        for (const node of this.nodes) {
            // A shared resource remains a single node. Pull it between its groups and include it in every boundary.
            // Coextensive tags share an anchor so adding an alias doesn't bias the layout.
            const targets = [...new Set(this.tagGroups.filter(group => node.tagKeys.has(group.key)).map(group => group.anchor))];
            node.tagAnchors = targets;
            node.tagX = targets.length ? d3.mean(targets, anchor => anchor.x) : 0;
            node.tagY = targets.length ? d3.mean(targets, anchor => anchor.y) : 0;
        }
        this.tagLayouts = [...new Set(this.tagGroups.map(group => group.anchor))];
        // Aliases share one collision envelope, using the outermost outline and its padding.
        const collisionLayouts = new Set();
        this.tagCollisionGroups = [...this.tagGroups].reverse().filter(group => {
            if (collisionLayouts.has(group.anchor)) {
                return false;
            }
            collisionLayouts.add(group.anchor);
            return true;
        });
        this.outerTagGroups = this.tagCollisionGroups.filter(a => !this.tagCollisionGroups.some(b =>
            a !== b && a.nodes.length < b.nodes.length && a.nodes.every(node => node.tagKeys.has(b.key))));
        this.simulation
            .force("x", d3.forceX(node => node.tagX).strength(node => !groups.size ? 0.2 : node.tagKeys.size ? 0.03 : 0.005))
            .force("y", d3.forceY(node => node.tagY).strength(node => !groups.size ? 0.4 : node.tagKeys.size ? 0.03 : 0.005))
            .force("tagSeparation", groups.size ? this.separateTagGroups : null)
            .force("untaggedRepulsion", groups.size ? this.repelUntaggedNodes : null)
            .force("tagCohesion", groups.size ? this.pullTagGroupsTogether : null);

        const paintOrder = [...this.tagGroups].reverse();
        this.tagElements = this.tagElementsG.selectAll(".resource-tag-boundary")
            .data(paintOrder, group => group.key)
            .join("path")
            .classed("resource-tag-boundary", true)
            .style("--tag-color", group => group.color);
        this.tagControls = this.tagControlsG.selectAll(".resource-tag-controls")
            .data(paintOrder, group => group.key)
            .join(enter => {
                const controls = enter.append("g").attr("class", "resource-tag-controls");
                controls.append("text").attr("class", "resource-tag-label");
                if (this.menuIcon) {
                    // Keep the hover bridge outside the button so it doesn't stretch the focus outline.
                    controls.append("rect")
                        .attr("class", "resource-tag-menu-bridge")
                        .attr("y", -19).attr("width", 8).attr("height", 28).attr("fill", "transparent");
                    this.appendMenuCog(controls, this.openTagContextMenu)
                        .classed("resource-tag-menu-cog", true);
                }
                return controls;
            });
        const tagLabels = this.tagControls.select("text")
            .text(group => group.label)
            .attr("pointer-events", "all")
            .call(this.tagGroupDrag)
            .on("mousedown touchstart", event => event.stopPropagation());
        this.tagControls
            .on("mouseenter", (event, group) => this.updateNodeHighlights(group.nodes, group.key))
            .on("mouseleave", this.unHoverNode);
        if (this.enableTagSelection) {
            tagLabels.on("click", this.selectTagGroup);
        }
        if (this.menuIcon) {
            const getTagMenuLabel = group => this.menuIcon.tagLabelFormat.split("{0}").join(group.label);
            this.tagControls.select(".resource-tag-menu-cog")
                .attr("aria-label", getTagMenuLabel)
                .attr("data-tooltip", getTagMenuLabel);
            this.positionTagMenuCogs();
            if (document.fonts.status === "loading") {
                document.fonts.ready.then(() => this.positionTagMenuCogs());
            }
        }
        this.nodeTagElements = this.nodeBackgroundFills.selectAll("path")
            .data(paintOrder, group => group.key)
            .join("path")
            .classed("resource-tag-fill", true)
            .style("--tag-color", group => group.color);
        this.nodeElements.style("--resource-node-tag-tint", node => `url(#resource-node-tag-tint-${node.id})`);
    }

    positionTagMenuCogs() {
        if (this.menuIcon) {
            this.tagControls?.each(function () {
                const controls = d3.select(this);
                const labelWidth = controls.select(".resource-tag-label").node().getComputedTextLength();
                controls.select(".resource-tag-menu-cog").attr("transform", `translate(${labelWidth + 18},-5)`);
                controls.select(".resource-tag-menu-bridge").attr("x", labelWidth);
            });
        }
    }

    getCollisionRadius(node) {
        return Math.min(90 + ((node.degree || 1) * 10), 180);
    }

    pullTagGroupsTogether = alpha => {
        for (const layout of this.tagLayouts) {
            const x = d3.mean(layout.nodes, node => node.x);
            const y = d3.mean(layout.nodes, node => node.y);
            for (const node of layout.nodes) {
                // Shared members target the average of their groups' actual centers, not just fixed anchors.
                const strength = 0.6 * alpha / node.tagAnchors.length;
                node.vx += (x - node.x) * strength;
                node.vy += (y - node.y) * strength;
            }
        }
    };

    separateTagGroups = () => {
        for (let i = 0; i < this.tagCollisionGroups.length; i++) {
            const a = this.tagCollisionGroups[i];
            for (let j = i + 1; j < this.tagCollisionGroups.length; j++) {
                const b = this.tagCollisionGroups[j];
                if (a.nodes.some(node => node.tagKeys.has(b.key))) {
                    continue;
                }
                const hullA = this.getTagHull(a, true);
                const hullB = this.getTagHull(b, true);
                const separation = this.getHullSeparation(hullA, hullB);
                if (!separation) {
                    continue;
                }
                const x = separation.x / separation.distance;
                const y = separation.y / separation.distance;
                const project = point => point[0] * x + point[1] * y;
                const [minA] = d3.extent(hullA, project);
                const [, maxB] = d3.extent(hullB, project);
                // Move only members on the contacting sides. Translating whole groups stretches shared
                // memberships and displaces linked members that weren't involved in the overlap.
                for (const node of a.nodes) {
                    const [min] = d3.extent(this.getPredictedNodeHull(node), project);
                    const push = Math.max(0, maxB + tagGroupGap - min + this.getTagPadding(a)) * 0.25;
                    node.vx += x * push;
                    node.vy += y * push;
                }
                for (const node of b.nodes) {
                    const [, max] = d3.extent(this.getPredictedNodeHull(node), project);
                    const push = Math.max(0, max + this.getTagPadding(b) - minA + tagGroupGap) * 0.25;
                    node.vx -= x * push;
                    node.vy -= y * push;
                }
            }
        }
    };

    repelUntaggedNodes = () => {
        for (const node of this.nodes) {
            if (node.tagKeys.size) {
                continue;
            }
            for (const group of this.outerTagGroups) {
                const separation = this.getHullSeparation(this.getPredictedNodeHull(node), this.getTagHull(group, true));
                if (!separation) {
                    continue;
                }
                const scale = 0.5 / (group.nodes.length + 1);
                node.vx += separation.x * scale * group.nodes.length;
                node.vy += separation.y * scale * group.nodes.length;
                // Let the group react too, so exclusion doesn't simply stretch the resource's links.
                for (const member of group.nodes) {
                    member.vx -= separation.x * scale;
                    member.vy -= separation.y * scale;
                }
            }
        }
    };

    getHullSeparation(a, b) {
        // The separating-axis test finds the shortest translation between convex envelopes, including
        // containment. The gap leaves room for the rendered spline to bow beyond the hull's straight edges.
        let separation = null;
        for (const polygon of [a, b]) {
            for (let i = 0; i < polygon.length; i++) {
                const p = polygon[i];
                const q = polygon[(i + 1) % polygon.length];
                const length = Math.hypot(q[0] - p[0], q[1] - p[1]);
                if (!length) {
                    continue;
                }
                const x = -(q[1] - p[1]) / length;
                const y = (q[0] - p[0]) / length;
                const project = point => point[0] * x + point[1] * y;
                const [minA, maxA] = d3.extent(a, project);
                const [minB, maxB] = d3.extent(b, project);
                const positive = maxB - minA + tagGroupGap;
                const negative = maxA - minB + tagGroupGap;
                if (positive <= 0 || negative <= 0) {
                    return null;
                }
                const distance = Math.min(positive, negative);
                if (!separation || distance < separation.distance) {
                    const direction = positive < negative ? 1 : -1;
                    separation = { x: x * direction * distance, y: y * direction * distance, distance };
                }
            }
        }
        return separation;
    }

    getPredictedNodeHull(node) {
        // Match forceCollide: anticipate motion and adjust velocities without cooling away the constraint.
        // https://d3js.org/d3-force/collide#collide_strength
        const left = node.x + node.vx + node.bounds.x;
        const top = node.y + node.vy + node.bounds.y;
        const right = left + node.bounds.width;
        const bottom = top + node.bounds.height;
        return [[left, top], [right, top], [right, bottom], [left, bottom]];
    }

    getTagPadding(group) {
        return 24 + group.level * 28;
    }

    getTagHull(group, anticipateMovement = false) {
        const padding = this.getTagPadding(group);
        const points = [];
        for (const node of group.nodes) {
            const bounds = node.bounds;
            const left = node.x + (anticipateMovement ? node.vx : 0) + bounds.x;
            const top = node.y + (anticipateMovement ? node.vy : 0) + bounds.y;
            const right = left + bounds.width;
            const bottom = top + bounds.height;
            // Sample a padded rectangle, including the resource's measured label. Unlike a hull of node centers,
            // this also encloses a single resource or two collinear resources.
            points.push([left - padding, top], [left, top - padding], [right, top - padding], [right + padding, top],
                [right + padding, bottom], [right, bottom + padding], [left, bottom + padding], [left - padding, bottom]);
        }
        // These are visual envelopes, not partitions: shared membership requires overlap, and a convex hull can
        // also cover unrelated resources between members.
        return d3.polygonHull(points);
    }

    updateResources(newResources) {
        const resourcesByName = new Map(newResources.map(resource => [resource.name, resource]));
        const hasStructureChanged = this.resourcesChanged(this.resources, resourcesByName);
        // Preserve the simulation's node order when the incoming order changes without a structural change.
        this.resources = hasStructureChanged ? newResources : this.resources.map(resource => resourcesByName.get(resource.name));
        const hasAddedNodes = this.updateNodes(this.resources);

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

        // Enter/leave keeps hover stable while moving between a node's child shapes.
        var newNodesContainer = newNodes
            .append("g")
            .attr("class", "resource-scale")
            .on('click', this.selectNode)
            .on('contextmenu', this.nodeContextMenu)
            .on('mouseenter', this.hoverNode)
            .on('mouseleave', this.unHoverNode);
        newNodesContainer
            .append("pattern")
            .attr("class", "resource-node-tag-pattern")
            .attr("id", node => `resource-node-tag-tint-${node.id}`)
            .attr("href", "#resource-node-tag-tint");
        newNodesContainer
            .append("circle")
            .attr("r", resourceNodeRadius)
            .attr("class", "resource-node");
        newNodesContainer
            .append("circle")
            .attr("r", resourceNodeRadius)
            .attr("class", "resource-node-tag-tint");
        newNodesContainer
            .append("circle")
            .attr("r", resourceNodeRadius)
            .attr("class", "resource-node-border");
        var iconTransform = newNodesContainer
            .append("g")
            .attr("transform", n => n.endpointText ? "translate(-24,-37)" : "translate(-24,-24)")
        var iconPath = iconTransform
            .append("path");
        iconPath
            .attr("fill", n => n.resourceIcon.color)
            .attr("d", n => n.resourceIcon.path)
            .attr("data-tooltip", n => n.resourceIcon.tooltip);

        // Icon paths could be mixed size. We need to transform icons to always be displayed at a consistent size.
        iconPath.each(function (d) {
            const iconSize = 48;

            const path = d3.select(this);
            const node = path.node();
            const bbox = node.getBBox();

            const available = Math.max(1, iconSize - 2);
            const largestDimension = Math.max(bbox.width, bbox.height);
            if (!Number.isFinite(largestDimension) || largestDimension <= 0) {
                path.attr("transform", null);
                return;
            }
            const scale = available / largestDimension;

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

        // Resource status
        var statusGroup = newNodesContainer
            .append("g")
            .attr("transform", "scale(1.6) translate(14,-34)");
        statusGroup
            .append("circle")
            .attr("r", 8)
            .attr("cy", 8)
            .attr("cx", 8)
            .attr("class", "resource-status-circle");
        statusGroup
            .append("path")
            .attr("class", "resource-status-path");

        var resourceNameGroup = newNodesContainer
            .append("g")
            .attr("transform", "translate(0,71)")
            .attr("class", "resource-name");
        resourceNameGroup
            .append("rect")
            .attr("class", "resource-name-hitbox");
        resourceNameGroup
            .append("text")
            .text(n => trimText(n.label, 30));
        resourceNameGroup.attr("data-tooltip", n => n.label);

        if (this.hiddenIcon) {
            var hiddenIconGroup = resourceNameGroup
                .append("g")
                .attr("class", "resource-hidden-icon")
                .style("display", n => n.isHidden ? null : "none");
            hiddenIconGroup
                .append("rect")
                .attr("class", "resource-hidden-icon-hitbox")
                .attr("x", -4)
                .attr("width", 20);
            hiddenIconGroup
                .append("path")
                .attr("d", this.hiddenIcon.path);
            hiddenIconGroup
                .append("title")
                .text(this.hiddenIcon.tooltip);
        }

        // Context menu affordance. A cog positioned on the circle rim directly below the status badge.
        // The status badge sits at the top-right via "scale(1.6) translate(14,-34)" (center ~(35,-42)),
        // so mirroring it vertically puts the cog at the bottom-right ~(35,43). Hidden until the node is
        // hovered (see .resource-menu-cog CSS); it makes the node's interactivity discoverable by opening
        // the same context menu as right-clicking the node.
        this.appendMenuCog(newNodesContainer, this.openResourceContextMenu)
            .attr("id", n => `resource-menu-cog-${n.id}`)
            .attr("transform", "translate(35,43)");

        newNodes.transition()
            .attr("opacity", 1);

        this.nodeElements = newNodes.merge(this.nodeElements);
        this.nodeElements.classed('resource-group-hidden', n => n.isHidden);

        // Set resource values that change.
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-name")
            .each(function (n) {
                const nameGroup = d3.select(this);
                const bounds = nameGroup.select("text").node().getBBox();
                const hitboxHeight = Math.max(bounds.height, 16);
                nameGroup.select(".resource-name-hitbox")
                    .attr("x", bounds.x)
                    .attr("y", bounds.y + (bounds.height - hitboxHeight) / 2)
                    .attr("width", bounds.width)
                    .attr("height", hitboxHeight);
                const hiddenIconGroup = nameGroup
                    .select(".resource-hidden-icon")
                    .style("display", n.isHidden ? null : "none");
                if (n.isHidden && !hiddenIconGroup.empty()) {
                    // Join the icon's hitbox to the measured label, including the gap between them.
                    hiddenIconGroup.attr("transform",
                        `translate(${bounds.x + bounds.width + 4},${bounds.y + (bounds.height - 16) / 2})`);
                    hiddenIconGroup.select(".resource-hidden-icon-hitbox")
                        .attr("y", (16 - hitboxHeight) / 2)
                        .attr("height", hitboxHeight);
                }
            });
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-menu-cog")
            .attr("aria-label", n => this.getResourceMenuLabel(n))
            .attr("data-tooltip", n => this.getResourceMenuLabel(n));
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-endpoint")
            .style("display", n => n.endpointText ? null : "none")
            .select("text")
            .text(n => trimText(n.endpointText, 15));
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-endpoint")
            .attr("data-tooltip", n => n.endpointText || "");
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-status-circle")
            .attr("data-tooltip", n => n.stateIcon.tooltip);
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-status-path")
            .attr("d", n => n.stateIcon.path)
            .attr("fill", n => n.stateIcon.color)
            .attr("data-tooltip", n => n.stateIcon.tooltip);

        this.measureNodeBounds();

        if (hasStructureChanged) {
            if (hasAddedNodes && !this.nodes.some(node => node.fx != null || node.fy != null)) {
                // Use D3's fresh starting positions instead of mixing the previous layout with newly placed nodes.
                for (const node of this.nodes) {
                    node.x = node.y = NaN;
                    node.vx = node.vy = 0;
                }
                this.hasFitGraph = false;
            }
            this.updateTagGroups();
            this.updateLinks(resourcesByName);
            this.simulation.stop().nodes(this.nodes).on('tick', this.onTick);
            this.linkForce.links(this.links);

            // Set alpha (give energy) and simulate the graph before rendering.
            // This prevents the graph from jumping around when loaded or changed.
            this.simulation.alpha(1);
            for (let i = 0; i < 300; i++) {
                this.simulation.tick();
            }
            this.simulation.restart();
        }

        // Redraw measured outlines after a display update without advancing the simulation.
        this.onTick();
        if (hasStructureChanged && !this.hasFitGraph) {
            this.hasFitGraph = this.fitGraph(false);
        }

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

    measureNodeBounds() {
        this.nodeElements.each(function (node) {
            const bounds = this.getBBox();
            // Hidden SVG elements report empty bounds. Keep the last measurement until resize can measure again.
            if (!node.bounds || (bounds.width > 0 && bounds.height > 0)) {
                node.bounds = bounds;
            }
        });
    }

    updateLinks(resourcesByName) {
        this.links = this.resources.flatMap(resource => resource.referencedNames
            .filter(name => resourcesByName.has(name))
            .map(name => ({
                id: `${resource.name}-${name}`,
                target: name,
                source: resource.name,
                strength: 0.7
            })));

        const linkElements = this.linkElementsG.selectAll("line").data(this.links, link => link.id);
        linkElements.exit().transition().attr("opacity", 0).remove();

        const newLinks = linkElements.enter().append("line")
            .attr("opacity", 0)
            .attr("class", "resource-link");
        newLinks.transition().attr("opacity", 1);

        this.linkElements = newLinks.merge(linkElements);
    }

    updateLinkMask() {
        const [minX = 0, maxX = 0] = d3.extent(this.nodes, node => node.x);
        const [minY = 0, maxY = 0] = d3.extent(this.nodes, node => node.y);
        const padding = resourceLinkMaskRadius * selectedNodeScale + 4;
        const x = minX - padding;
        const y = minY - padding;
        const width = maxX - minX + padding * 2;
        const height = maxY - minY + padding * 2;
        // Explicit graph-space bounds also work for horizontal/vertical links and nodes dragged outside the viewport.
        for (const element of [this.linkMask, this.linkMaskBackground]) {
            element
                .attr("x", x)
                .attr("y", y)
                .attr("width", width)
                .attr("height", height);
        }
        this.linkMaskNodes = this.linkMask.selectAll("circle")
            .data(this.nodes, node => node.id)
            .join("circle")
            .attr("fill", "black")
            .attr("cx", node => node.x)
            .attr("cy", node => node.y)
            .attr("r", node => resourceLinkMaskRadius * (this.nodeEquals(node, this.selectedNode) ? selectedNodeScale : 1));
    }

    onTick = () => {
        this.updateLinkMask();
        const outline = d3.line().curve(d3.curveCatmullRomClosed.alpha(0.5));
        this.tagElements?.attr("d", group => {
            const hull = this.getTagHull(group);
            group.path = outline(hull);
            const top = hull.reduce((a, b) => b[1] < a[1] || (b[1] === a[1] && b[0] < a[0]) ? b : a);
            group.labelTransform = `translate(${top[0]},${top[1] - 8})`;
            return group.path;
        });
        this.tagControls?.attr("transform", group => group.labelTransform);
        this.nodeTagElements?.attr("d", group => group.path);
        // Keep each pattern tile node-sized while sampling the corresponding area of the group fills.
        this.nodeElements.select(".resource-node-tag-pattern")
            .attr("viewBox", node => [node.x - resourceNodeRadius, node.y - resourceNodeRadius,
                resourceNodeRadius * 2, resourceNodeRadius * 2]);
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

    appendMenuCog(container, openMenu) {
        const cog = container.append("g")
            .attr("class", "resource-menu-cog")
            .attr("role", "button")
            .attr("tabindex", 0)
            .attr("aria-haspopup", "menu")
            .attr("aria-expanded", "false")
            .on("mousedown touchstart", event => event.stopPropagation())
            .on("click", event => {
                event.preventDefault();
                event.stopPropagation();
                return openMenu(event.currentTarget.__data__, event.clientX, event.clientY, event.currentTarget, null);
            })
            .on("keydown", event => {
                if (event.repeat || (event.key !== "Enter" && event.key !== " ")) {
                    return;
                }
                event.preventDefault();
                event.stopPropagation();
                const trigger = event.currentTarget;
                const bounds = trigger.getBoundingClientRect();
                return openMenu(trigger.__data__, Math.round(bounds.left + bounds.width / 2),
                    Math.round(bounds.top + bounds.height / 2), trigger, trigger.id);
            });
        cog.append("circle").attr("r", 14).attr("class", "resource-menu-cog-background");
        if (this.menuIcon) {
            cog.append("path")
                .attr("class", "resource-menu-cog-icon")
                .attr("d", this.menuIcon.path)
                .each(function () {
                    const bbox = this.getBBox();
                    const largestDimension = Math.max(bbox.width, bbox.height);
                    if (Number.isFinite(largestDimension) && largestDimension > 0) {
                        const cx = bbox.x + bbox.width / 2;
                        const cy = bbox.y + bbox.height / 2;
                        d3.select(this).attr("transform", `scale(${16 / largestDimension}) translate(${-cx},${-cy})`);
                    }
                });
        }
        return cog;
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
        event.stopPropagation();

        await this.openResourceContextMenu(data, event.clientX, event.clientY, null, null);
    };

    openResourceContextMenu = (node, clientX, clientY, trigger, focusElementId) =>
        this.openGraphContextMenu(trigger, "ResourceContextMenu", node.id, clientX, clientY, focusElementId);

    openTagContextMenu = (group, clientX, clientY, trigger) =>
        this.openGraphContextMenu(trigger, "TagGroupContextMenu", group.label, clientX, clientY);

    async openGraphContextMenu(trigger, method, ...args) {
        this.contextMenuTrigger?.setAttribute("aria-expanded", "false");
        this.contextMenuTrigger = trigger;
        this.contextMenuChanged(true);

        try {
            const opened = await this.resourcesInterop.invokeMethodAsync(method, ...args);
            if (opened === false && this.contextMenuTrigger === trigger) {
                this.contextMenuChanged(false);
            }
        } catch (error) {
            if (this.contextMenuTrigger === trigger) {
                this.contextMenuChanged(false);
            }
            throw error;
        }
    }

    contextMenuChanged = (open) => {
        this.openContextMenu = open;
        this.contextMenuTrigger?.setAttribute("aria-expanded", open ? "true" : "false");
        if (!open) {
            this.contextMenuTrigger = null;
            this.updateNodeHighlights(null);
        }
    };

    selectTagGroup = event => {
        event.preventDefault();
        event.stopPropagation();
        return this.resourcesInterop.invokeMethodAsync('SelectTagGroup', event.currentTarget.__data__.label, {
            ctrlKey: event.ctrlKey,
            metaKey: event.metaKey,
            shiftKey: event.shiftKey
        });
    };

    selectNode = (event) => {
        var data = event.target.__data__;

        // Always send the clicked on resource to the server. It will clear the selection if the same resource is clicked again.
        this.resourcesInterop.invokeMethodAsync('SelectResource', data.id);

        // Unscale the previous selected node.
        if (this.selectedNode) {
            changeScale(this, this.selectedNode.id, 1);
        }

        // Scale selected node if it is not the same as the previous selected node.
        var clearSelection = this.nodeEquals(data, this.selectedNode);
        if (!clearSelection) {
            changeScale(this, data.id, selectedNodeScale);
        }

        this.selectedNode = data;

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
        var mouseoverNode = event.currentTarget.__data__;

        this.updateNodeHighlights([mouseoverNode]);
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

    updateNodeHighlights = (mouseoverNodes, hoveredTagKey = null) => {
        for (const paths of [this.tagElements, this.nodeTagElements]) {
            paths?.classed("resource-tag-fill-hover", group => group.key === hoveredTagKey);
        }
        mouseoverNodes = mouseoverNodes || [];
        var mouseoverNeighbors = mouseoverNodes.flatMap(node => this.getNeighbors(node));
        var selectNeighbors = this.selectedNode ? this.getNeighbors(this.selectedNode) : [];
        var neighbors = [...mouseoverNeighbors, ...selectNeighbors];

        // we modify the styles to highlight selected nodes
        this.nodeElements.attr('class', (node) => {
            var classNames = ['resource-group'];
            if (node.isHidden) {
                classNames.push('resource-group-hidden');
            }
            if (mouseoverNodes.some(mouseoverNode => this.nodeEquals(node, mouseoverNode))) {
                classNames.push('resource-group-hover');
            }
            if (this.nodeEquals(node, this.selectedNode)) {
                classNames.push('resource-group-selected');
            }
            if (neighbors.indexOf(node.id) > -1) {
                classNames.push('resource-group-highlight');
            }
            return classNames.join(' ');
        });
        var highlightedNodes = this.selectedNode ? [...mouseoverNodes, this.selectedNode] : mouseoverNodes;
        this.linkElements.attr('class', link => this.getLinkClass(highlightedNodes, this.selectedNode, link));
        this.updateLinkMask();
    };
};
