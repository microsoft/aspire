import { cellPoint, WheelAccumulator, SelectionGesture } from "./selection-input.js";
import { InputRoute, inputModifiers } from "./input-policy.js";
const buttons = [["left", 1], ["middle", 4], ["right", 2]];
const pointerButtons = ["left", "middle", "right"];
/** Capture input intent only; the server chooses and encodes the mouse protocol. */
export function captureMouse(canvas, send, focus, inspection = {}) {
    const listeners = new AbortController();
    const options = { signal: listeners.signal };
    let columns = 1, rows = 1;
    let tracking = 0;
    let pointerId = null;
    const pressed = new Set();
    let lastPoint;
    let pendingMove;
    let lastMove;
    let scheduled;
    const wheel = new WheelAccumulator();
    const gesture = new SelectionGesture();
    let wheelOwner;
    let pointerEvent;
    let autoScroll;
    let click = { count: 0, time: 0, x: -1, y: -1 };
    let selectionClick;
    let completedClick;
    let routedGesture;
    let contextMenuRoute;
    const state = () => ({ tracking, ...inspection.state?.() });
    function point(event, clamp = false) {
        return cellPoint(event, canvas.getBoundingClientRect(), columns, rows, clamp);
    }
    function updateAutoScroll() {
        clearTimeout(autoScroll);
        autoScroll = undefined;
        if (gesture.owner !== "local" || !pointerEvent)
            return;
        const bounds = canvas.getBoundingClientRect();
        const distance = pointerEvent.clientY < bounds.top ? pointerEvent.clientY - bounds.top
            : pointerEvent.clientY >= bounds.bottom ? pointerEvent.clientY - bounds.bottom + 1 : 0;
        if (!distance)
            return;
        autoScroll = setTimeout(() => {
            autoScroll = undefined;
            if (!pointerEvent)
                return;
            const position = point(pointerEvent, true);
            if (gesture.owner === "local" && position) {
                if (selectionClick)
                    selectionClick.dragged = true;
                click.count = 0;
                const lines = Math.sign(distance) * Math.min(8, Math.max(1, Math.ceil(Math.abs(distance) / (bounds.height / rows))));
                inspection.scroll?.(lines, gesture.scrollPoint(position));
                updateAutoScroll();
            }
        }, 60);
    }
    function flushMove() {
        if (scheduled !== undefined)
            cancelAnimationFrame(scheduled);
        scheduled = undefined;
        if (pendingMove)
            send(pendingMove);
        pendingMove = undefined;
    }
    function changeButtons(event, position) {
        for (const [button, mask] of buttons) {
            const down = (event.buttons & mask) !== 0;
            if (down === pressed.has(button))
                continue;
            flushMove();
            if (down)
                pressed.add(button);
            else
                pressed.delete(button);
            if (down || tracking !== 9)
                send({ type: "mouse", action: down ? "down" : "up", button, ...position });
            lastMove = undefined;
        }
    }
    function cancel(report = true, cancelled = true) {
        inspection.end?.(cancelled);
        clearTimeout(autoScroll);
        autoScroll = undefined;
        pointerEvent = undefined;
        selectionClick = undefined;
        completedClick = undefined;
        gesture.end();
        routedGesture = undefined;
        if (report)
            flushMove();
        else {
            if (scheduled !== undefined)
                cancelAnimationFrame(scheduled);
            scheduled = undefined;
            pendingMove = undefined;
        }
        if (report && tracking !== 9 && lastPoint) {
            for (const button of pressed)
                send({ type: "mouse", action: "up", button, ...lastPoint });
        }
        pressed.clear();
        const captured = pointerId;
        pointerId = null;
        if (captured !== null && canvas.hasPointerCapture(captured))
            canvas.releasePointerCapture(captured);
        lastMove = undefined;
        wheel.reset();
        wheelOwner = undefined;
    }
    canvas.addEventListener("pointerdown", event => {
        if (event.defaultPrevented && pointerId === null)
            return;
        if (event.pointerType !== "mouse" || ![0, 1, 2].includes(event.button))
            return;
        const position = point(event);
        if (!position)
            return;
        if (pointerId !== null) {
            if (routedGesture !== InputRoute.Browser)
                event.preventDefault();
            if ((gesture.owner === "app" || routedGesture === InputRoute.Application) && pointerId === event.pointerId)
                changeButtons(event, position);
            return;
        }
        focus();
        const input = { type: "pointer", button: pointerButtons[event.button],
            point: Object.freeze({ ...position }), ...inputModifiers(event) };
        const decision = inspection.resolve?.(input) ?? { route: InputRoute.Continue };
        const route = decision.action !== undefined ? InputRoute.Consume : decision.route;
        contextMenuRoute = event.button === 2 ? route : undefined;
        if (route !== InputRoute.Continue) {
            completedClick = selectionClick = undefined;
            click.count = 0;
            routedGesture = route;
            pointerId = event.pointerId;
            lastPoint = position;
            canvas.setPointerCapture(pointerId);
            if (route !== InputRoute.Browser)
                event.preventDefault();
            if (route === InputRoute.Application)
                flushMove();
            else {
                if (scheduled !== undefined)
                    cancelAnimationFrame(scheduled);
                scheduled = undefined;
                pendingMove = undefined;
            }
            if (route === InputRoute.Application)
                changeButtons(event, position);
            if (decision.action !== undefined)
                inspection.execute?.(decision, input);
            return;
        }
        // Keep focus on the hidden keyboard input instead of the canvas.
        event.preventDefault();
        if (event.metaKey)
            return;
        const now = performance.now();
        click.count = now - click.time < 500 && position.x === click.x && position.y === click.y
            ? click.count % 3 + 1 : 1;
        click = { count: click.count, time: now, x: position.x, y: position.y };
        const start = gesture.begin({ button: event.button, shiftKey: event.shiftKey, altKey: event.altKey,
            detail: event.detail || click.count }, position, state());
        if (!start)
            return;
        pointerId = event.pointerId;
        pointerEvent = event;
        lastPoint = position;
        completedClick = undefined;
        canvas.setPointerCapture(pointerId);
        if (start.owner === "local") {
            if (scheduled !== undefined)
                cancelAnimationFrame(scheduled);
            scheduled = undefined;
            pendingMove = undefined;
            lastMove = undefined;
            selectionClick = { point: position, mode: start.mode, extend: start.extend, dragged: false };
            inspection.begin?.(position, start);
        }
        else
            changeButtons(event, position);
    }, options);
    canvas.addEventListener("pointermove", event => {
        if (event.pointerType !== "mouse")
            return;
        if (pointerId === null && event.buttons)
            return;
        if (pointerId !== null && pointerId !== event.pointerId)
            return;
        const position = point(event, pointerId !== null);
        if (!position)
            return;
        lastPoint = position;
        if (routedGesture && routedGesture !== InputRoute.Application)
            return;
        if (gesture.owner === "local") {
            pointerEvent = event;
            const previous = gesture.endpoint;
            const endpoint = gesture.move(position);
            if (endpoint && previous && (endpoint.x !== previous.x || endpoint.y !== previous.y)) {
                click.count = 0;
                if (selectionClick)
                    selectionClick.dragged = true;
                inspection.extend?.(endpoint);
            }
            updateAutoScroll();
            return;
        }
        if (!tracking || (!routedGesture && gesture.owner === null && (state().historical || state().readOnly || event.shiftKey)))
            return;
        if (pointerId === event.pointerId)
            changeButtons(event, position);
        if (event.metaKey || (tracking !== 1003 && !(tracking === 1002 && pressed.size)))
            return;
        const button = buttons.find(([name]) => pressed.has(name))?.[0] ?? "none";
        const move = { type: "mouse", action: "move", button, ...position };
        const key = JSON.stringify(move);
        if (key === lastMove)
            return;
        lastMove = key;
        pendingMove = move;
        if (scheduled === undefined)
            scheduled = requestAnimationFrame(flushMove);
    }, options);
    canvas.addEventListener("pointerup", event => {
        if (pointerId !== event.pointerId)
            return;
        const position = point(event, true) ?? lastPoint;
        if (routedGesture) {
            if (routedGesture === InputRoute.Application && position)
                changeButtons(event, position);
            if (event.buttons === 0)
                cancel(routedGesture === InputRoute.Application);
            return;
        }
        if (gesture.owner === "local") {
            if (position && gesture.endpoint && (position.x !== gesture.endpoint.x || position.y !== gesture.endpoint.y)) {
                if (selectionClick)
                    selectionClick.dragged = true;
                const endpoint = gesture.move(position);
                if (endpoint)
                    inspection.extend?.(endpoint);
            }
            const completed = selectionClick;
            cancel(false, false);
            completedClick = completed;
            return;
        }
        if (position)
            changeButtons(event, position);
        if (!pressed.size)
            cancel();
    }, options);
    canvas.addEventListener("click", event => {
        const completed = completedClick;
        completedClick = undefined;
        if (!completed || completed.dragged || !Number.isInteger(event.detail) || event.detail < 1)
            return;
        click.count = event.detail;
        // Some browsers expose native multiclick counts only on click, not pointerdown.
        const mode = event.detail >= 3 ? "line" : event.detail === 2 ? "word" : "character";
        if (!completed.extend && completed.mode !== "rectangle" && mode !== completed.mode)
            inspection.begin?.(completed.point, { mode, extend: false });
    }, options);
    canvas.addEventListener("pointercancel", () => cancel(), options);
    canvas.addEventListener("lostpointercapture", () => {
        if (pointerId !== null)
            cancel();
    }, options);
    window.addEventListener("blur", () => cancel(), options);
    canvas.addEventListener("contextmenu", event => {
        if (contextMenuRoute !== undefined && contextMenuRoute !== InputRoute.Continue) {
            const route = contextMenuRoute;
            contextMenuRoute = undefined;
            if (route !== InputRoute.Browser)
                event.preventDefault();
            return;
        }
        if (tracking || gesture.owner === "local")
            event.preventDefault();
    }, options);
    canvas.addEventListener("wheel", event => {
        const input = { type: "wheel", deltaX: event.deltaX, deltaY: event.deltaY,
            deltaMode: event.deltaMode, point: point(event), ...inputModifiers(event) };
        const decision = pointerId === null
            ? inspection.resolve?.(input) ?? { route: InputRoute.Continue }
            : { route: routedGesture ?? InputRoute.Continue };
        if (decision.route === InputRoute.Browser)
            return;
        if (decision.action !== undefined || decision.route === InputRoute.Consume) {
            event.preventDefault();
            if (decision.action !== undefined)
                inspection.execute?.(decision, input);
            return;
        }
        // Ctrl+wheel (including trackpad pinch) remains browser zoom.
        if (decision.route !== InputRoute.Application && (event.ctrlKey || event.metaKey))
            return;
        const owner = decision.route === InputRoute.Application ? "app" : gesture.wheelOwner(event, state());
        if (owner === "app" && tracking === 9)
            return;
        const position = point(gesture.owner === "local" && pointerEvent ? pointerEvent : event, gesture.owner === "local");
        if (!position)
            return;
        event.preventDefault();
        flushMove();
        const bounds = canvas.getBoundingClientRect();
        if (wheelOwner !== owner)
            wheel.reset();
        wheelOwner = owner;
        const { x: horizontal, y: vertical } = wheel.take(event, bounds, rows);
        if (owner === "local") {
            if (vertical) {
                if (selectionClick)
                    selectionClick.dragged = true;
                click.count = 0;
                inspection.scroll?.(vertical, gesture.scrollPoint(position));
            }
            return;
        }
        const wheelDirections = [
            [vertical, "wheelUp", "wheelDown"],
            [horizontal, "wheelLeft", "wheelRight"]
        ];
        for (const [steps, negative, positive] of wheelDirections) {
            if (steps)
                send({
                    type: "mouse", action: "wheel", button: steps < 0 ? negative : positive,
                    count: Math.min(32, Math.abs(steps)), ...position
                });
        }
    }, { ...options, passive: false });
    return {
        update(nextColumns, nextRows, nextTracking) {
            if (columns !== nextColumns || rows !== nextRows || tracking !== nextTracking) {
                if (lastPoint) {
                    lastPoint = { ...lastPoint,
                        x: Math.min(lastPoint.x, nextColumns - 1),
                        y: Math.min(lastPoint.y, nextRows - 1)
                    };
                }
                // Application mode changes do not transfer ownership mid-gesture.
                if (columns !== nextColumns || rows !== nextRows)
                    cancel(nextTracking !== 0);
                columns = nextColumns;
                rows = nextRows;
                tracking = nextTracking;
            }
        },
        cancel() { cancel(); },
        dispose() {
            cancel(false);
            listeners.abort();
        }
    };
}
//# sourceMappingURL=mouse-input.js.map