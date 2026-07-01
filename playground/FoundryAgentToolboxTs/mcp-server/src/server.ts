// Minimal MCP server protected by a bearer token. The Foundry Toolbox sends the token in the
// Authorization header on every request; the AppHost wires the same token into both this server
// (for validation) and the toolbox MCP tool definition (for outbound auth) via a single shared
// parameter.
//
// Transport: streamable HTTP, mounted at /mcp.
//   - POST /mcp        - JSON-RPC requests + tool invocations
//   - GET  /mcp        - SSE stream for server-initiated messages
//   - DELETE /mcp      - explicit session termination
//
// Tools exposed:
//   - lookup_employee(name): returns a fake directory record
//   - record_note(subject, body): records a note in-memory and returns the assigned id
//
// References:
//   - https://modelcontextprotocol.io/specification/2025-06-18/basic/transports#streamable-http
//   - https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/toolbox?pivots=javascript

import { randomUUID } from 'node:crypto';
import express, { type Request, type Response } from 'express';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StreamableHTTPServerTransport } from '@modelcontextprotocol/sdk/server/streamableHttp.js';
import { isInitializeRequest } from '@modelcontextprotocol/sdk/types.js';
import { z } from 'zod';

const PORT = Number(process.env.PORT ?? process.env.MCP_PORT ?? 5252);
const TOKEN = process.env.MCP_BEARER_TOKEN;

if (!TOKEN) {
    console.error('MCP_BEARER_TOKEN environment variable is required.');
    process.exit(1);
}

interface DirectoryEntry {
    readonly name: string;
    readonly title: string;
    readonly office: string;
    readonly email: string;
}

const directory: Record<string, DirectoryEntry> = {
    'ada lovelace': { name: 'Ada Lovelace', title: 'Principal Engineer', office: 'London', email: 'ada@contoso.example' },
    'grace hopper': { name: 'Grace Hopper', title: 'Distinguished Engineer', office: 'New York', email: 'grace@contoso.example' },
    'alan turing': { name: 'Alan Turing', title: 'Researcher', office: 'Cambridge', email: 'alan@contoso.example' }
};

interface Note {
    readonly id: string;
    readonly subject: string;
    readonly body: string;
    readonly createdAt: string;
}

const notes: Note[] = [];

function buildServer(): McpServer {
    const server = new McpServer(
        { name: 'directory-mcp', version: '1.0.0' },
        { capabilities: { tools: {} } });

    server.registerTool(
        'lookup_employee',
        {
            description: 'Look up an employee in the company directory by name. Returns title, office, and email.',
            inputSchema: { name: z.string().describe('The employee full name to look up.') }
        },
        async ({ name }) => {
            const entry = directory[name.trim().toLowerCase()];
            if (!entry) {
                return { content: [{ type: 'text', text: `No employee named '${name}' found in the directory.` }] };
            }
            return {
                content: [{
                    type: 'text',
                    text: `Name: ${entry.name}\nTitle: ${entry.title}\nOffice: ${entry.office}\nEmail: ${entry.email}`
                }]
            };
        });

    server.registerTool(
        'record_note',
        {
            description: 'Record a short note for the team. Returns the assigned note id.',
            inputSchema: {
                subject: z.string().describe('Short subject line.'),
                body: z.string().describe('Note body.')
            }
        },
        async ({ subject, body }) => {
            const note: Note = { id: randomUUID(), subject, body, createdAt: new Date().toISOString() };
            notes.push(note);
            return { content: [{ type: 'text', text: `Recorded note ${note.id} ('${subject}').` }] };
        });

    return server;
}

const app = express();
app.use(express.json());

// Unauthenticated liveness probe so the AppHost can mark the resource healthy without leaking
// the bearer token requirement onto the health-check path.
app.get('/health', (_req, res) => {
    res.status(200).json({ status: 'ok' });
});

// TEMPORARY: log inbound auth header presence so we can see in the ACA log stream whether the
// Foundry data plane forwards the Authorization header set by the toolbox. Masked so the secret
// doesn't actually leave the container. Remove before committing.
function maskToken(value: string | undefined): string {
    if (!value) return '<missing>';
    const stripped = value.replace(/^Bearer\s+/i, '');
    if (stripped.length <= 8) return 'Bearer ***';
    return `Bearer ${stripped.slice(0, 4)}...${stripped.slice(-4)}`;
}

// Bearer token middleware. The token comes from MCP_BEARER_TOKEN; the AppHost forwards the same
// value to the Toolbox so its outbound calls authenticate cleanly.
app.use((req, res, next) => {
    const auth = req.header('authorization') ?? '';
    console.log(
        `[auth] ${req.method} ${req.path} auth=${maskToken(auth)} ` +
        `ua=${req.header('user-agent') ?? '<missing>'} ` +
        `src=${req.header('x-app-source') ?? req.header('x-ms-source') ?? '<missing>'}`
    );
    if (auth !== `Bearer ${TOKEN}`) {
        res.status(401).json({ error: 'unauthorized', message: 'Missing or invalid bearer token.' });
        return;
    }
    next();
});

// One transport per MCP session. The session id arrives in the Mcp-Session-Id header on every
// request after initialization (per the Streamable HTTP spec).
const transports = new Map<string, StreamableHTTPServerTransport>();

async function handleMcpPost(req: Request, res: Response): Promise<void> {
    const sessionHeader = req.header('mcp-session-id');
    let transport = sessionHeader ? transports.get(sessionHeader) : undefined;

    if (!transport) {
        // Either the very first request (initialize) or a request with a stale/missing session id.
        if (!isInitializeRequest(req.body)) {
            res.status(400).json({
                jsonrpc: '2.0',
                error: { code: -32000, message: 'Bad Request: No valid MCP session.' },
                id: null
            });
            return;
        }

        transport = new StreamableHTTPServerTransport({
            sessionIdGenerator: () => randomUUID(),
            onsessioninitialized: (id) => {
                transports.set(id, transport!);
            }
        });

        // Clean up server-side state when the client disconnects so transports doesn't grow unbounded.
        transport.onclose = () => {
            if (transport!.sessionId) {
                transports.delete(transport!.sessionId);
            }
        };

        const server = buildServer();
        await server.connect(transport);
    }

    await transport.handleRequest(req, res, req.body);
}

async function handleMcpSessionRequest(req: Request, res: Response): Promise<void> {
    const sessionHeader = req.header('mcp-session-id');
    const transport = sessionHeader ? transports.get(sessionHeader) : undefined;
    if (!transport) {
        res.status(400).send('Invalid or missing Mcp-Session-Id header.');
        return;
    }
    await transport.handleRequest(req, res);
}

app.post('/mcp', (req, res) => {
    handleMcpPost(req, res).catch((err) => {
        console.error('Error handling MCP POST', err);
        if (!res.headersSent) {
            res.status(500).json({
                jsonrpc: '2.0',
                error: { code: -32603, message: 'Internal server error' },
                id: null
            });
        }
    });
});

app.get('/mcp', (req, res) => {
    handleMcpSessionRequest(req, res).catch((err) => {
        console.error('Error handling MCP GET', err);
        if (!res.headersSent) {
            res.status(500).send('Internal server error');
        }
    });
});

app.delete('/mcp', (req, res) => {
    handleMcpSessionRequest(req, res).catch((err) => {
        console.error('Error handling MCP DELETE', err);
        if (!res.headersSent) {
            res.status(500).send('Internal server error');
        }
    });
});

app.listen(PORT, () => {
    console.log(`Directory MCP server listening on http://0.0.0.0:${PORT}/mcp`);
    console.log('Health endpoint: /health');
    console.log('Authorization: Bearer <token> required for /mcp');
});
