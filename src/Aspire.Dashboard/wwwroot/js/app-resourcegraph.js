import './d3.v7.min.js'

let resourceGraph = null;

export function initializeResourcesGraph(resourcesInterop) {
    resourceGraph = new ResourceGraph(resourcesInterop);
    resourceGraph.resize();

    const observer = new ResizeObserver(function () {
        resourceGraph.resize();
    });

    for (const child of document.getElementsByClassName('resources-summary-layout')) {
        observer.observe(child);
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

class ResourceGraph {
    constructor(resourcesInterop) {
        this.resources = [];
        this.resourcesInterop = resourcesInterop;
        this.openContextMenu = false;

        this.nodes = [];
        this.links = [];
        this.initialFitDone = false;

        this.svg = d3.select('.resource-graph');
        this.baseGroup = this.svg.append("g");

        // Enable zoom + pan
        // https://www.d3indepth.com/zoom-and-pan/
        // scaleExtent limits zoom to reasonable values
        this.zoom = d3.zoom().scaleExtent([0.2, 4]).on('zoom', (event) => {
            this.baseGroup.attr('transform', event.transform);
        });
        this.svg.call(this.zoom);

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
            .force("collide", d3.forceCollide(function (d) {
                var degree = d.degree || 1;

                // scale collide radius with degree: 90 for low degree, up to 180 for high degree
                return Math.min(90 + (degree * 10), 180);
            }).iterations(10))
            .force("x", d3.forceX().strength(0.2))
            .force("y", d3.forceY().strength(0.4))
            .force("center", d3.forceCenter().strength(0.01));

        // Drag start is trigger on mousedown from click.
        // Only change the state of the simulation when the drag event is triggered.
        var dragActive = false;
        var dragged = false;
        this.dragDrop = d3.drag().on('start', (event) => {
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

        var defs = this.svg.append("defs");
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
            .attr("fill", "var(--fill-color)");

        highlightedPattern
            .append("line")
            .attr("x1", "0")
            .attr("y", "0")
            .attr("x2", "0")
            .attr("y2", "17.5")
            .attr("stroke", "var(--neutral-fill-secondary-hover)")
            .attr("stroke-width", "15");

        this.linkElementsG = this.baseGroup.append("g").attr("class", "links");
        this.nodeElementsG = this.baseGroup.append("g").attr("class", "nodes");

        this.initializeButtons();
    }

    initializeButtons() {
        d3.select('.graph-zoom-in').on("click", () => this.zoomIn());
        d3.select('.graph-zoom-out').on("click", () => this.zoomOut());
        d3.select('.graph-reset').on("click", () => this.resetZoomAndPan());
    }

    resetZoomAndPan() {
        this.svg.transition().call(this.zoom.transform, d3.zoomIdentity);
    }

    // Fit the whole graph within the viewport and center it. The viewBox is centered
    // on the origin ([-w/2,-h/2,w,h]), so fitting means scaling the node bounding box
    // (plus padding) to fit the viewport, then translating its center to the origin.
    // Returns false (without changing the view) when the viewport or layout isn't
    // measurable yet, so the caller can retry on a later render.
    zoomToFit(padding = 60) {
        if (!this.nodes || this.nodes.length === 0) {
            return false;
        }

        var container = document.querySelector(".resources-summary-layout");
        if (!container) {
            return false;
        }

        var width = container.clientWidth;
        var height = Math.max(container.clientHeight - 50, 0);
        if (width === 0 || height === 0) {
            return false;
        }

        // Nodes are drawn as circles around their (x,y); pad by an approximate node
        // radius so the outermost nodes and their labels aren't clipped at the edge.
        var nodeRadius = 48;
        var minX = d3.min(this.nodes, d => d.x) - nodeRadius;
        var maxX = d3.max(this.nodes, d => d.x) + nodeRadius;
        var minY = d3.min(this.nodes, d => d.y) - nodeRadius;
        var maxY = d3.max(this.nodes, d => d.y) + nodeRadius;

        var boxWidth = maxX - minX;
        var boxHeight = maxY - minY;
        if (boxWidth === 0 || boxHeight === 0) {
            return false;
        }

        var midX = (minX + maxX) / 2;
        var midY = (minY + maxY) / 2;

        // Clamp to the zoom scaleExtent ([0.2, 4]) and avoid magnifying a tiny graph
        // past 1:1, which would look unnaturally zoomed-in on first display.
        var scale = Math.min((width - padding * 2) / boxWidth, (height - padding * 2) / boxHeight);
        scale = Math.max(0.2, Math.min(scale, 1));

        // d3.zoomIdentity.scale(s).translate(-midX,-midY) maps the box center to (0,0),
        // which is the viewport center given the origin-centered viewBox.
        var transform = d3.zoomIdentity.scale(scale).translate(-midX, -midY);
        this.svg.call(this.zoom.transform, transform);
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

    resize() {
        var container = document.querySelector(".resources-summary-layout");
        if (container) {
            var width = container.clientWidth;
            var height = Math.max(container.clientHeight - 50, 0);
            this.svg.attr("viewBox", [-width / 2, -height / 2, width, height]);
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
        if (r1.referencedNames.length !== r2.referencedNames.length) {
            return false;
        }
        for (var i = 0; i < r1.referencedNames.length; i++) {
            if (r1.referencedNames[i] !== r2.referencedNames[i]) {
                return false;
            }
        }

        return true;
    }

    iconEqual(i1, i2) {
        if (i1.path !== i2.path) {
            return false;
        }
        if (i1.svg !== i2.svg) {
            return false;
        }
        if (i1.usesFill !== i2.usesFill || i1.name !== i2.name || i1.variant !== i2.variant) {
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
        const existingNodes = this.nodes || []; // Ensure nodes is initialized
        const updatedNodes = [];

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
                // Update existing node without replacing it
                updatedNodes.push({
                    ...existingNode,
                    label: resource.displayName,
                    endpointUrl: resource.endpointUrl,
                    endpointText: resource.endpointText,
                    resourceIcon: createIcon(resource.resourceIcon),
                    stateIcon: createIcon(resource.stateIcon),
                    degree: degree
                });
            } else {
                // Add new resource
                updatedNodes.push({
                    id: resource.name,
                    label: resource.displayName,
                    endpointUrl: resource.endpointUrl,
                    endpointText: resource.endpointText,
                    resourceIcon: createIcon(resource.resourceIcon),
                    stateIcon: createIcon(resource.stateIcon),
                    degree: degree
                });
            }
        });

        this.nodes = updatedNodes;

        function createIcon(resourceIcon) {
            return {
                path: resourceIcon.path,
                svg: resourceIcon.svg,
                usesFill: resourceIcon.usesFill,
                name: resourceIcon.name,
                variant: resourceIcon.variant,
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
        for (var i = 0; i < newResources.length; i++) {
            var resource = newResources[i];

            var resourceLinks = resource.referencedNames
                .filter((referencedName) => {
                    return newResources.some(r => r.name === referencedName);
                })
                .map((referencedName, index) => {
                    return {
                        id: `${resource.name}-${referencedName}`,
                        target: referencedName,
                        source: resource.name,
                        strength: 0.7
                    };
                });

            this.links.push(...resourceLinks);
        }

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
        // Deck resource-type icons are multi-element, stroke-based SVGs (unlike the single filled path
        // used for the state icon), so render their inner markup into a group via innerHTML and stroke it.
        var iconGroup = iconTransform
            .append("g")
            .attr("fill", n => n.resourceIcon.usesFill ? n.resourceIcon.color : "none")
            .attr("stroke-linecap", "round")
            .attr("stroke-linejoin", "round")
            .attr("data-icon-name", n => n.resourceIcon.name)
            .attr("data-icon-variant", n => n.resourceIcon.variant);
        iconGroup
            .html(n => n.resourceIcon.svg)
            .attr("stroke", n => n.resourceIcon.usesFill ? "none" : n.resourceIcon.color);
        iconGroup
            .append("title")
            .text(n => n.resourceIcon.tooltip);

        // Icon viewBoxes could be mixed size. Scale each icon to a consistent display size, and divide
        // the stroke width back out by the scale so every icon keeps a uniform ~2px stroke weight.
        iconGroup.each(function (d) {
            const iconSize = 48;

            const group = d3.select(this);
            const node = group.node();
            const bbox = node.getBBox();

            const available = Math.max(1, iconSize - 2);
            const scale = available / Math.max(bbox.width, bbox.height);

            const cx = bbox.x + bbox.width / 2;
            const cy = bbox.y + bbox.height / 2;

            // apply scaling & centering inside this group
            group.attr("transform",
                `translate(${iconSize / 2},${iconSize / 2}) scale(${scale}) translate(${-cx},${-cy})`);
            group.attr("stroke-width", 2 / scale);
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

        newNodes.transition()
            .attr("opacity", 1);

        this.nodeElements = newNodes.merge(this.nodeElements);

        // Set resource values that change.
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
            // Inline style (not attr) so it wins over the .resource-status-circle CSS fill rule.
            .style("fill", n => n.stateIcon.color)
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

        this.simulation
            .nodes(this.nodes)
            .on('tick', this.onTick);

        this.simulation.force("link").links(this.links);
        if (hasStructureChanged) {
            this.simulation.stop();

            // Set alpha (give energy) and simulate the graph before rendering.
            // This prevents the graph from jumping around when loaded or changed.
            this.simulation.alpha(1);
            for (let i = 0; i < 300; i++) {
                this.simulation.tick();
            }
        }

        this.simulation.restart();

        // On first display, fit the whole graph within the viewport and center it,
        // rather than showing it at 1:1 scale where larger graphs overflow or sit
        // off-center until the user manually zooms/pans. Only auto-fit once so we
        // don't fight the user's own zoom/pan on later updates; zoomToFit reports
        // whether it could measure the viewport so we retry on a subsequent render.
        if (!this.initialFitDone && this.zoomToFit()) {
            this.initialFitDone = true;
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

        this.openContextMenu = true;

        try {
            // Wait for method completion. It completes when the context menu is closed.
            await this.resourcesInterop.invokeMethodAsync('ResourceContextMenu', data.id, window.innerWidth, window.innerHeight, event.clientX, event.clientY);
        } finally {
            this.openContextMenu = false;

            // Unselect the node when the context menu is closed to reset mouseover state.
            this.updateNodeHighlights(null);
        }
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
            changeScale(this, data.id, 1.2);
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
        var mouseoverNode = event.target.__data__;

        this.updateNodeHighlights(mouseoverNode);
    }

    unHoverNode = (event) => {
        // Don't unhover the selected node when the context menu is open.
        // This is done to keep the node selected until the context menu is closed.
        if (!this.openContextMenu) {
            this.updateNodeHighlights(null);
        }
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
