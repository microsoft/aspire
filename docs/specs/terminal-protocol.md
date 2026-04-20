# Aspire Terminal Protocol Specification

**Version:** 1
**Status:** Draft
**Issue:** https://github.com/microsoft/aspire/issues/16317
**DCP Issue:** https://github.com/microsoft/dcp/issues/6

## Overview

The Aspire Terminal Protocol defines a binary framing protocol for interactive terminal I/O over Unix domain sockets (UDS). It is used to stream PTY data between a terminal producer (DCP or a terminal host process) and a terminal consumer (Aspire Dashboard, Aspire CLI, or other clients).

The protocol is intentionally simple so that it can be implemented in both C# (Aspire) and Go (DCP) without shared code generation or IDL.

## Transport

- **Socket type:** Unix domain socket (`AF_UNIX`, `SOCK_STREAM`)
- **Byte order:** Big-endian (network byte order) for all multi-byte integers
- **Connection model:** A server listens on a UDS path. Clients connect to that path. Each connection is an independent terminal session.

## Frame Format

Every message on the wire is a **frame** with the following layout:

```
+--------+----------------+-----------------+
| Type   | Payload Length  | Payload         |
| 1 byte | 4 bytes (BE)   | 0..N bytes      |
+--------+----------------+-----------------+
```

| Field          | Size    | Description                                           |
|----------------|---------|-------------------------------------------------------|
| Type           | 1 byte  | Message type identifier (see table below)             |
| Payload Length | 4 bytes | Unsigned 32-bit big-endian integer, length of payload |
| Payload        | N bytes | Type-specific payload data                            |

### Maximum Frame Size

The maximum payload length is **1,048,576 bytes** (1 MiB). Receivers **must** reject frames with a payload length exceeding this limit and close the connection. This prevents memory exhaustion from malformed or malicious input.

### Partial Reads/Writes

Since UDS is a stream socket, a single `write()` does not guarantee a single `read()` on the other side. Implementations **must** handle partial reads by buffering until a complete frame header (5 bytes) and its full payload have been received.

## Message Types

| Type | Name        | Direction          | Payload                          |
|------|-------------|--------------------|----------------------------------|
| 0x01 | HELLO       | Server → Client    | Version + dimensions + flags     |
| 0x02 | DATA        | Either direction   | Raw terminal bytes               |
| 0x03 | RESIZE      | Either direction   | New dimensions                   |
| 0x04 | EXIT        | Server → Client    | Exit code + reason               |
| 0x05 | CLOSE       | Either direction   | *(empty — payload length is 0)*  |

### 0x01 — HELLO

Sent by the server as the **first frame** on every new connection. Establishes the protocol version and initial terminal dimensions.

```
+----------+--------+--------+--------+
| Version  | Cols   | Rows   | Flags  |
| 1 byte   | 2 BE   | 2 BE   | 1 byte |
+----------+--------+--------+--------+
```

| Field   | Size    | Description                                         |
|---------|---------|-----------------------------------------------------|
| Version | 1 byte  | Protocol version. Currently `0x01`.                 |
| Cols    | 2 bytes | Terminal width in columns (unsigned 16-bit BE)      |
| Rows    | 2 bytes | Terminal height in rows (unsigned 16-bit BE)        |
| Flags   | 1 byte  | Capability flags (see below)                        |

**Payload length:** 6 bytes (fixed).

**Flags:**

| Bit | Name           | Description                                        |
|-----|----------------|----------------------------------------------------|
| 0   | PTY            | `1` if the server has a real PTY allocated          |
| 1   | REPLAY         | `1` if the server will replay buffered terminal state after HELLO |
| 2-7 | *(reserved)*   | Must be `0`. Receivers must ignore unknown flags.  |

**Rules:**
- The server **must** send HELLO before any DATA frames.
- The server **must** buffer PTY output until HELLO has been written to the socket.
- The client **must** wait for HELLO before sending any frames.
- If the client receives a HELLO with an unsupported version, it **should** close the connection.

### 0x02 — DATA

Carries raw terminal bytes in either direction.

- **Server → Client:** PTY output (stdout/stderr interleaved, as a real terminal would produce). These are raw bytes including ANSI escape sequences, UTF-8 text, and control characters.
- **Client → Server:** User input (keystrokes, mouse events encoded as ANSI escape sequences). These are raw bytes as a terminal emulator would generate.

**Payload:** Variable-length raw bytes. Minimum 1 byte. Maximum 1 MiB.

**Rules:**
- DATA frames carry opaque byte streams. Neither side should assume anything about framing alignment with respect to UTF-8 characters, escape sequences, or line boundaries. A single escape sequence may span multiple DATA frames, and a single DATA frame may contain multiple escape sequences.
- Empty DATA frames (payload length 0) are permitted but have no effect.

### 0x03 — RESIZE

Notifies the other side of a terminal dimension change.

```
+--------+--------+
| Cols   | Rows   |
| 2 BE   | 2 BE   |
+--------+--------+
```

| Field | Size    | Description                                    |
|-------|---------|------------------------------------------------|
| Cols  | 2 bytes | New terminal width in columns (unsigned 16-bit BE) |
| Rows  | 2 bytes | New terminal height in rows (unsigned 16-bit BE)   |

**Payload length:** 4 bytes (fixed).

**Direction:**
- **Client → Server:** The client's display area changed (e.g., browser window resized, CLI terminal resized via `SIGWINCH`). The server should forward this to the PTY via `TIOCSWINSZ` / `SIGWINCH`.
- **Server → Client:** The server-side terminal dimensions changed (informational). The client should update its terminal emulator dimensions.

### 0x04 — EXIT

Sent by the server when the underlying process terminates.

```
+-----------+--------+
| Exit Code | Reason |
| 4 bytes   | 1 byte |
+-----------+--------+
```

| Field     | Size    | Description                                      |
|-----------|---------|--------------------------------------------------|
| Exit Code | 4 bytes | Signed 32-bit big-endian integer. Process exit code. `-1` if unknown. |
| Reason    | 1 byte  | Termination reason (see below)                   |

**Payload length:** 5 bytes (fixed).

**Reason values:**

| Value | Name        | Description                                      |
|-------|-------------|--------------------------------------------------|
| 0x00  | EXITED      | Process exited normally                          |
| 0x01  | KILLED      | Process was killed (e.g., `SIGKILL`, `TerminateProcess`) |
| 0x02  | DETACHED    | Server is detaching from the PTY (process may continue) |
| 0x03  | ERROR       | Server encountered an unrecoverable error        |

**Rules:**
- After sending EXIT, the server **may** send a CLOSE frame or simply close the socket.
- After receiving EXIT, the client **should** stop sending DATA/RESIZE frames.

### 0x05 — CLOSE

Graceful session shutdown. Either side may send this to indicate it is done.

**Payload length:** 0 bytes (fixed).

**Rules:**
- After sending CLOSE, the sender **must not** send any further frames.
- After receiving CLOSE, the receiver **should** close its end of the socket.
- A CLOSE without a preceding EXIT from the server indicates an abnormal disconnection (e.g., the server crashed).

## Connection Lifecycle

### Normal Session

```
Client                          Server
  |                                |
  |-------- connect() ----------->|
  |                                |
  |<------- HELLO(v1, 80x24) -----|  (server sends HELLO first)
  |<------- DATA(buffered state) -|  (optional: replay if REPLAY flag set)
  |<------- DATA(live output) ----|
  |                                |
  |-------- DATA(keystroke) ----->|
  |<------- DATA(echo + output) --|
  |                                |
  |-------- RESIZE(120x40) ----->|  (client window resized)
  |<------- DATA(repaint) --------|
  |                                |
  |<------- EXIT(0, EXITED) ------|  (process exited)
  |<------- CLOSE ----------------|
  |-------- CLOSE --------------->|
  |                                |
```

### Client-Initiated Disconnect

```
Client                          Server
  |                                |
  |-------- CLOSE --------------->|  (user detached)
  |                                |  (server may keep process running)
```

### Server Crash

```
Client                          Server
  |                                |
  |<------- [socket closed] ------|  (no CLOSE/EXIT — abnormal)
  |                                |
```

## Error Handling

| Condition                        | Action                                    |
|----------------------------------|-------------------------------------------|
| Unknown message type received    | Close socket immediately (protocol error) |
| Payload length exceeds 1 MiB     | Close socket immediately (protocol error) |
| HELLO not received as first frame| Close socket immediately (protocol error) |
| Unsupported protocol version     | Close socket (version mismatch)           |
| Socket read returns 0 bytes      | Peer disconnected, clean up               |
| Socket write fails with EPIPE    | Peer disconnected, clean up               |
| Fixed-length payload has wrong size | Close socket (protocol error)           |

## Implementation Notes

### For DCP (Go)

- When `terminal.enabled = true` on an Executable/Container spec, DCP allocates a PTY and starts the UDS server at `terminal.socketPath`.
- DCP writes `HELLO` to each new client connection, then streams PTY output as `DATA` frames.
- DCP reads `DATA` frames from the client and writes them to the PTY's stdin.
- DCP reads `RESIZE` frames and calls `ioctl(TIOCSWINSZ)` on the PTY master.
- When the process exits, DCP sends `EXIT` followed by `CLOSE`.

### For Aspire Terminal Host (C#)

- The terminal host is used for non-DCP resources. It runs Hex1b with a custom `IHex1bTerminalPresentationAdapter` that implements the server side of this protocol.
- The adapter listens on the UDS, sends `HELLO`, bridges `DATA` frames to/from Hex1b's presentation interface, and forwards `RESIZE` events.

### For Aspire Dashboard (C#/Blazor)

- The Dashboard connects to the UDS (directly or via backchannel proxy), receives `HELLO`, and configures xterm.js with the initial dimensions.
- `DATA` frames from the server are written to xterm.js for rendering.
- Keystrokes from xterm.js are sent as `DATA` frames to the server.
- Browser resize events trigger `RESIZE` frames.

### For Aspire CLI (C#)

- The CLI connects to the UDS, receives `HELLO`, puts the local terminal in raw mode, and forwards bytes bidirectionally.
- `SIGWINCH` triggers a `RESIZE` frame to the server.
- `Ctrl+]` detaches by sending `CLOSE`.

## Wire Examples

### HELLO frame (version 1, 120 columns, 30 rows, PTY flag set)

```
01                     # Type: HELLO
00 00 00 06            # Payload length: 6
01                     # Version: 1
00 78                  # Cols: 120
00 1E                  # Rows: 30
01                     # Flags: PTY=1
```

### DATA frame (5 bytes: "Hello")

```
02                     # Type: DATA
00 00 00 05            # Payload length: 5
48 65 6C 6C 6F         # "Hello"
```

### RESIZE frame (200 columns, 50 rows)

```
03                     # Type: RESIZE
00 00 00 04            # Payload length: 4
00 C8                  # Cols: 200
00 32                  # Rows: 50
```

### EXIT frame (exit code 0, reason EXITED)

```
04                     # Type: EXIT
00 00 00 05            # Payload length: 5
00 00 00 00            # Exit code: 0
00                     # Reason: EXITED
```

### CLOSE frame

```
05                     # Type: CLOSE
00 00 00 00            # Payload length: 0
```

## Future Considerations

- **Multiple clients:** The current design is single-client per UDS. Supporting multiple concurrent viewers would require a fan-out layer (e.g., the terminal host, or a backchannel proxy). The protocol itself does not need to change.
- **Authentication:** If the UDS needs access control beyond filesystem permissions, a future version could add an AUTH message type after HELLO.
- **Compression:** For high-throughput scenarios, a future version could negotiate compression in the HELLO flags.
- **Flow control:** The current design has no backpressure mechanism. If a client cannot keep up with output, it will buffer or lose frames. A future version could add windowed flow control.
