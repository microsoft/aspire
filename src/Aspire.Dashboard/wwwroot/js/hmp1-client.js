// Hex1b Muxer Protocol (HMP) v1 client over WebSocket. Pure JS,
// no dependencies. Speaks the same wire format as
// src/Hex1b/Hmp1/Hmp1Protocol.cs in the Hex1b package the Aspire
// dashboard's terminal endpoint forwards through. Frames:
//   [type:1B][length:4B LE][payload:N bytes]
// JSON payloads are camelCase to match Hmp1JsonContext.cs.
//
// This file is a port of
//   samples/WebMuxerDemo/wwwroot/js/hmp1-client.js
// from the Hex1b repo (microsoft/hex1b) at version 0.147.0.
// Keep the surface in sync with that file when possible so future
// upstream changes can be replayed verbatim. The dashboard does not
// add any dashboard-specific UI hooks here — that's all in
// TerminalView.razor.js, which imports this module.

const FrameType = Object.freeze({
  Hello: 0x01,
  StateSync: 0x02,
  Output: 0x03,
  Input: 0x04,
  Resize: 0x05,
  Exit: 0x06,
  RequestPrimary: 0x07,
  RoleChange: 0x08,
  PeerJoin: 0x09,
  PeerLeave: 0x0a,
  ClientHello: 0x0b,
});

const HEADER_SIZE = 5;

// Hard cap on a single HMP frame payload. The wire format uses a 4-byte LE
// signed length, so the spec maximum is ~2GiB - in practice nothing legit
// emits more than a few hundred KB at a time (Output frames are flushed
// frequently, JSON control frames are small). Anything wildly larger is
// either corruption, version skew, or a malicious upstream trying to OOM
// the browser. Refuse and let the caller close the WS so the reconnect
// path takes over.
const MAX_FRAME_PAYLOAD = 16 * 1024 * 1024;

// FrameBuffer accumulates incoming WS payloads and yields complete HMP1
// frames. WebSocket message boundaries do NOT align with HMP1 frame
// boundaries (especially when the server batches Output frames, which
// it does aggressively), so we buffer.
class FrameBuffer {
  constructor() {
    this._chunks = [];
    this._totalLength = 0;
  }

  push(arrayBuffer) {
    const view = new Uint8Array(arrayBuffer);
    this._chunks.push(view);
    this._totalLength += view.byteLength;
  }

  *drain() {
    while (true) {
      const frame = this._tryReadOne();
      if (frame === null) {
        return;
      }
      yield frame;
    }
  }

  _tryReadOne() {
    if (this._totalLength < HEADER_SIZE) {
      return null;
    }
    const header = this._peek(HEADER_SIZE);
    const dv = new DataView(header.buffer, header.byteOffset, HEADER_SIZE);
    const type = dv.getUint8(0);
    const length = dv.getInt32(1, true);
    // Reject negative lengths (would desync the buffer because
    // `total < HEADER_SIZE`) and absurdly large lengths (would attempt to
    // allocate a multi-GiB Uint8Array which either throws inside the
    // `message` handler - silently wedging the client - or OOMs the tab).
    if (length < 0 || length > MAX_FRAME_PAYLOAD) {
      throw new Error(`HMP1 protocol error: invalid frame length ${length} (type=${type}).`);
    }
    const total = HEADER_SIZE + length;
    if (this._totalLength < total) {
      return null;
    }
    const payload = this._take(total).slice(HEADER_SIZE);
    return { type, payload };
  }

  _peek(n) {
    return this._concat(n, /* consume */ false);
  }

  _take(n) {
    return this._concat(n, /* consume */ true);
  }

  _concat(n, consume) {
    if (n === 0) {
      return new Uint8Array(0);
    }
    const out = new Uint8Array(n);
    let written = 0;
    let chunkIndex = 0;
    while (written < n && chunkIndex < this._chunks.length) {
      const chunk = this._chunks[chunkIndex];
      const need = n - written;
      const take = Math.min(need, chunk.byteLength);
      out.set(chunk.subarray(0, take), written);
      written += take;
      chunkIndex += 1;
    }
    if (consume) {
      // Discard fully-consumed chunks; keep the partial tail of the last one.
      let consumed = n;
      while (consumed > 0 && this._chunks.length > 0) {
        const chunk = this._chunks[0];
        if (chunk.byteLength <= consumed) {
          consumed -= chunk.byteLength;
          this._chunks.shift();
        } else {
          this._chunks[0] = chunk.subarray(consumed);
          consumed = 0;
        }
      }
      this._totalLength -= n;
    }
    return out;
  }
}

const textEncoder = new TextEncoder();
const textDecoder = new TextDecoder("utf-8", { fatal: false });

function buildFrame(type, payload) {
  const len = payload ? payload.byteLength : 0;
  const out = new Uint8Array(HEADER_SIZE + len);
  out[0] = type;
  new DataView(out.buffer).setInt32(1, len, /* littleEndian */ true);
  if (len > 0) {
    out.set(payload, HEADER_SIZE);
  }
  return out;
}

function buildJsonFrame(type, obj) {
  return buildFrame(type, textEncoder.encode(JSON.stringify(obj)));
}

function buildResizePayload(cols, rows) {
  const out = new Uint8Array(8);
  const dv = new DataView(out.buffer);
  dv.setInt32(0, cols, true);
  dv.setInt32(4, rows, true);
  return out;
}

function parseResize(payload) {
  if (payload.byteLength < 8) {
    return { cols: 0, rows: 0 };
  }
  const dv = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  return { cols: dv.getInt32(0, true), rows: dv.getInt32(4, true) };
}

function parseJson(payload) {
  if (payload.byteLength === 0) {
    return null;
  }
  return JSON.parse(textDecoder.decode(payload));
}

// Hmp1Client connects to the dashboard's /api/terminal WebSocket
// endpoint, which forwards verbatim to the upstream Aspire.TerminalHost
// over the resource's per-replica consumer UDS. From the upstream's
// perspective this client is just another HMP v1 peer in its
// multi-head roster. Take-control / role-change / state-replay all
// flow through end-to-end without any dashboard-side translation.
//
// Event hooks (set as properties; null-tolerant, single-cast):
//   onOpen()                   — WS open
//   onScreenBytes(uint8array)  — Output / StateSync bytes for the terminal
//   onHello(payload)           — first contact: peerId, primaryPeerId,
//                                width, height, peers
//   onRoleChange(payload)      — primary changed (carries new primaryPeerId
//                                and the size that comes with it)
//   onPeerJoin(payload)        — peer roster delta
//   onPeerLeave(payload)       — peer roster delta
//   onResize(cols, rows)       — producer broadcast new dims (echo of
//                                accepted Resize)
//   onExit(code)               — workload exited
//   onClose(event)             — WS closed
export class Hmp1Client {
  constructor({ url, displayName, defaultRole }) {
    this._url = url;
    this._displayName = displayName ?? "browser";
    this._defaultRole = defaultRole ?? "secondary";
    this._buffer = new FrameBuffer();
    this._ws = null;

    this.peerId = null;
    this.primaryPeerId = null;
    this.width = 0;
    this.height = 0;
    this.peers = []; // [{ peerId, displayName }]

    this.onOpen = null;
    this.onScreenBytes = null;
    this.onHello = null;
    this.onRoleChange = null;
    this.onPeerJoin = null;
    this.onPeerLeave = null;
    this.onResize = null;
    this.onExit = null;
    this.onClose = null;
  }

  get isPrimary() {
    return this.peerId !== null && this.primaryPeerId === this.peerId;
  }

  connect() {
    const ws = new WebSocket(this._url);
    ws.binaryType = "arraybuffer";
    this._ws = ws;

    ws.addEventListener("open", () => {
      this._send(buildJsonFrame(FrameType.ClientHello, {
        displayName: this._displayName,
        defaultRole: this._defaultRole,
      }));
      if (this.onOpen) this.onOpen();
    });

    ws.addEventListener("message", (ev) => {
      this._buffer.push(ev.data);
      try {
        for (const frame of this._buffer.drain()) {
          this._dispatch(frame);
        }
      } catch (err) {
        // A protocol-level fault (invalid length prefix, malformed JSON in a
        // control frame) leaves the buffer permanently desynced. Closing the
        // WS triggers the dashboard's reconnect path, which resets state.
        // Use code 1002 (protocol error) so server-side logs can distinguish
        // this from a clean client-initiated close.
        if (this.onError) { try { this.onError(err); } catch { /* ignore */ } }
        try { ws.close(1002, "HMP1 protocol error"); } catch { /* ignore */ }
      }
    });

    ws.addEventListener("close", (ev) => {
      this._ws = null;
      if (this.onClose) this.onClose(ev);
    });

    ws.addEventListener("error", () => {
      // close event will fire next.
    });
  }

  close() {
    if (this._ws) {
      try { this._ws.close(); } catch { /* ignore */ }
      this._ws = null;
    }
  }

  sendInput(bytes) {
    if (!this._ws || this._ws.readyState !== WebSocket.OPEN) return;
    const buf = typeof bytes === "string" ? textEncoder.encode(bytes) : bytes;
    this._send(buildFrame(FrameType.Input, buf));
  }

  // Only sent if we are primary — otherwise the server silently drops it.
  // Callers may always invoke sendResize and rely on isPrimary gating.
  sendResize(cols, rows) {
    if (!this.isPrimary) return;
    if (!this._ws || this._ws.readyState !== WebSocket.OPEN) return;
    this._send(buildFrame(FrameType.Resize, buildResizePayload(cols, rows)));
  }

  requestPrimary(cols, rows) {
    if (!this._ws || this._ws.readyState !== WebSocket.OPEN) return;
    this._send(buildJsonFrame(FrameType.RequestPrimary, { cols, rows }));
  }

  _send(bytes) {
    this._ws.send(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength));
  }

  _dispatch(frame) {
    switch (frame.type) {
      case FrameType.Hello: {
        const p = parseJson(frame.payload);
        this.peerId = p.peerId ?? null;
        this.primaryPeerId = p.primaryPeerId ?? null;
        this.width = p.width ?? 0;
        this.height = p.height ?? 0;
        this.peers = Array.isArray(p.peers) ? p.peers.slice() : [];
        if (this.onHello) this.onHello(p);
        break;
      }
      case FrameType.StateSync:
      case FrameType.Output: {
        if (this.onScreenBytes && frame.payload.byteLength > 0) {
          this.onScreenBytes(frame.payload);
        }
        break;
      }
      case FrameType.Resize: {
        const r = parseResize(frame.payload);
        this.width = r.cols;
        this.height = r.rows;
        if (this.onResize) this.onResize(r.cols, r.rows);
        break;
      }
      case FrameType.RoleChange: {
        const p = parseJson(frame.payload);
        this.primaryPeerId = p.primaryPeerId ?? null;
        this.width = p.width ?? this.width;
        this.height = p.height ?? this.height;
        if (this.onRoleChange) this.onRoleChange(p);
        break;
      }
      case FrameType.PeerJoin: {
        const p = parseJson(frame.payload);
        // Only add if not already present (defensive vs. replays).
        if (!this.peers.some(x => x.peerId === p.peerId)) {
          this.peers.push({ peerId: p.peerId, displayName: p.displayName });
        }
        if (this.onPeerJoin) this.onPeerJoin(p);
        break;
      }
      case FrameType.PeerLeave: {
        const p = parseJson(frame.payload);
        this.peers = this.peers.filter(x => x.peerId !== p.peerId);
        if (this.onPeerLeave) this.onPeerLeave(p);
        break;
      }
      case FrameType.Exit: {
        let code = 0;
        if (frame.payload.byteLength >= 4) {
          code = new DataView(frame.payload.buffer, frame.payload.byteOffset, 4).getInt32(0, true);
        }
        if (this.onExit) this.onExit(code);
        break;
      }
      default:
        // Unknown frame types are ignored (forward compatibility).
        break;
    }
  }
}
