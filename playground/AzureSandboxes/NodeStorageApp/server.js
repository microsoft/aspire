import http from 'node:http';
import { BlobServiceClient } from '@azure/storage-blob';
import { DefaultAzureCredential, ManagedIdentityCredential } from '@azure/identity';

const port = Number.parseInt(process.env.PORT ?? '8080', 10);
const blobServiceUri = process.env.BLOB_SERVICE_URI ?? process.env.ConnectionStrings__blobs;
const containerName = process.env.BLOB_CONTAINER_NAME ?? 'sandbox-data';

if (!blobServiceUri) {
  throw new Error('BLOB_SERVICE_URI or ConnectionStrings__blobs must be configured.');
}

const credential = process.env.AZURE_CLIENT_ID
  ? new ManagedIdentityCredential(process.env.AZURE_CLIENT_ID)
  : new DefaultAzureCredential();
const blobServiceClient = new BlobServiceClient(blobServiceUri, credential);
const containerClient = blobServiceClient.getContainerClient(containerName);

function html(strings, ...values) {
  return String.raw({ raw: strings }, ...values);
}

function escapeHtml(value) {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

function sendJson(response, statusCode, payload) {
  response.writeHead(statusCode, { 'content-type': 'application/json' });
  response.end(JSON.stringify(payload));
}

function sendHtml(response, markup) {
  response.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
  response.end(markup);
}

function readRequestBody(request) {
  return new Promise((resolve, reject) => {
    let body = '';
    request.setEncoding('utf8');
    request.on('data', chunk => {
      body += chunk;
      if (body.length > 64 * 1024) {
        reject(new Error('Request body is too large.'));
        request.destroy();
      }
    });
    request.on('end', () => resolve(body));
    request.on('error', reject);
  });
}

async function createBlob(content) {
  await containerClient.createIfNotExists();

  const blobName = `sandbox-${Date.now()}.txt`;
  const blobClient = containerClient.getBlockBlobClient(blobName);
  await blobClient.upload(content, Buffer.byteLength(content), {
    blobHTTPHeaders: {
      blobContentType: 'text/plain',
    },
  });

  return {
    containerName,
    blobName,
    content,
    url: blobClient.url,
  };
}

async function listBlobs() {
  await containerClient.createIfNotExists();

  const blobs = [];
  for await (const blob of containerClient.listBlobsFlat()) {
    blobs.push({
      name: blob.name,
      size: blob.properties.contentLength ?? 0,
      lastModified: blob.properties.lastModified?.toISOString() ?? null,
    });
  }

  return blobs
    .sort((left, right) => (right.lastModified ?? '').localeCompare(left.lastModified ?? ''))
    .slice(0, 25);
}

async function readBlob(blobName) {
  const blobClient = containerClient.getBlockBlobClient(blobName);
  const downloaded = await blobClient.downloadToBuffer();
  return downloaded.toString();
}

function renderHome() {
  const safeContainerName = escapeHtml(containerName);
  const safeBlobServiceUri = escapeHtml(blobServiceUri);

  return html`<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Azure Sandbox Blob Notes</title>
  <style>
    :root {
      color-scheme: light dark;
      font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      background: #0f172a;
      color: #e2e8f0;
    }

    body {
      margin: 0;
      min-height: 100vh;
      background:
        radial-gradient(circle at top left, rgba(56, 189, 248, 0.25), transparent 30rem),
        radial-gradient(circle at bottom right, rgba(34, 197, 94, 0.16), transparent 28rem),
        #0f172a;
    }

    main {
      box-sizing: border-box;
      width: min(980px, 100%);
      margin: 0 auto;
      padding: 48px 20px;
    }

    .hero {
      display: grid;
      gap: 16px;
      margin-bottom: 28px;
    }

    h1 {
      margin: 0;
      font-size: clamp(2rem, 7vw, 4.5rem);
      line-height: 0.95;
      letter-spacing: -0.06em;
    }

    .subtitle {
      max-width: 760px;
      color: #bae6fd;
      font-size: 1.12rem;
      line-height: 1.65;
    }

    .grid {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(280px, 0.75fr);
      gap: 18px;
    }

    @media (max-width: 820px) {
      .grid {
        grid-template-columns: 1fr;
      }
    }

    section, .status {
      border: 1px solid rgba(148, 163, 184, 0.24);
      border-radius: 24px;
      background: rgba(15, 23, 42, 0.72);
      box-shadow: 0 24px 70px rgba(2, 6, 23, 0.35);
      backdrop-filter: blur(18px);
    }

    section {
      padding: 22px;
    }

    label {
      display: block;
      margin-bottom: 10px;
      color: #cbd5e1;
      font-weight: 700;
    }

    textarea {
      box-sizing: border-box;
      width: 100%;
      min-height: 170px;
      resize: vertical;
      border: 1px solid rgba(148, 163, 184, 0.38);
      border-radius: 18px;
      padding: 16px;
      background: rgba(2, 6, 23, 0.62);
      color: #f8fafc;
      font: inherit;
      line-height: 1.5;
    }

    button {
      border: 0;
      border-radius: 999px;
      padding: 12px 18px;
      background: linear-gradient(135deg, #22c55e, #06b6d4);
      color: #031314;
      cursor: pointer;
      font-weight: 800;
    }

    button.secondary {
      background: rgba(148, 163, 184, 0.16);
      color: #e2e8f0;
      border: 1px solid rgba(148, 163, 184, 0.28);
    }

    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: 10px;
      align-items: center;
      margin-top: 14px;
    }

    .status {
      margin-bottom: 18px;
      padding: 16px 18px;
      color: #cbd5e1;
    }

    .meta {
      display: grid;
      gap: 8px;
      color: #94a3b8;
      font-size: 0.92rem;
      word-break: break-word;
    }

    .blob-list {
      display: grid;
      gap: 10px;
      margin-top: 14px;
    }

    .blob {
      display: grid;
      gap: 6px;
      border: 1px solid rgba(148, 163, 184, 0.18);
      border-radius: 16px;
      padding: 12px;
      background: rgba(2, 6, 23, 0.38);
    }

    .blob button {
      justify-self: start;
      padding: 8px 12px;
      font-size: 0.88rem;
    }

    pre {
      overflow: auto;
      min-height: 80px;
      margin: 14px 0 0;
      border-radius: 16px;
      padding: 14px;
      background: rgba(2, 6, 23, 0.74);
      color: #bfdbfe;
      white-space: pre-wrap;
    }
  </style>
</head>
<body>
  <main>
    <div class="hero">
      <h1>Blob notes from an Azure sandbox</h1>
      <p class="subtitle">
        This Node.js app is running in an ACA sandbox. It uses a managed identity to create,
        list, upload, and read blobs from Azure Storage.
      </p>
    </div>

    <div id="status" class="status">Loading storage state...</div>

    <div class="grid">
      <section>
        <label for="note">Write a note to Azure Blob Storage</label>
        <textarea id="note">Hello from an interactive Node.js app running in an Azure sandbox.</textarea>
        <div class="actions">
          <button id="save">Save note as blob</button>
          <button id="refresh" class="secondary">Refresh blobs</button>
        </div>
        <pre id="preview">Select a blob to read it back.</pre>
      </section>

      <section>
        <div class="meta">
          <strong>Storage container</strong>
          <span>${safeContainerName}</span>
          <strong>Blob service URI</strong>
          <span>${safeBlobServiceUri}</span>
        </div>
        <div id="blobs" class="blob-list"></div>
      </section>
    </div>
  </main>

  <script>
    const status = document.querySelector('#status');
    const blobs = document.querySelector('#blobs');
    const preview = document.querySelector('#preview');
    const note = document.querySelector('#note');

    function setStatus(message, failed = false) {
      status.textContent = message;
      status.style.borderColor = failed ? 'rgba(248, 113, 113, 0.65)' : 'rgba(148, 163, 184, 0.24)';
      status.style.color = failed ? '#fecaca' : '#cbd5e1';
    }

    async function parseJson(response) {
      const text = await response.text();
      const value = text ? JSON.parse(text) : {};
      if (!response.ok) {
        throw new Error(value.error || response.statusText);
      }

      return value;
    }

    async function refreshBlobs() {
      try {
        setStatus('Loading blobs from Azure Storage...');
        const items = await parseJson(await fetch('/api/blobs'));
        blobs.replaceChildren(...items.map(item => {
          const row = document.createElement('div');
          row.className = 'blob';
          row.innerHTML = '<strong></strong><span></span><button class="secondary">Read blob</button>';
          row.querySelector('strong').textContent = item.name;
          row.querySelector('span').textContent = item.size + ' bytes' + (item.lastModified ? ' · ' + new Date(item.lastModified).toLocaleString() : '');
          row.querySelector('button').addEventListener('click', () => readBlob(item.name));
          return row;
        }));
        setStatus(items.length ? 'Loaded ' + items.length + ' blob(s).' : 'No blobs yet. Save a note to create one.');
      } catch (error) {
        setStatus(error.message, true);
      }
    }

    async function saveNote() {
      try {
        setStatus('Writing note to Azure Storage...');
        const result = await parseJson(await fetch('/api/blobs', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ content: note.value }),
        }));
        preview.textContent = result.content;
        setStatus('Created ' + result.blobName + ' in ' + result.containerName + '.');
        await refreshBlobs();
      } catch (error) {
        setStatus(error.message, true);
      }
    }

    async function readBlob(name) {
      try {
          setStatus('Reading ' + name + ' from Azure Storage...');
          const result = await parseJson(await fetch('/api/blobs/' + encodeURIComponent(name)));
        preview.textContent = result.content;
          setStatus('Read ' + name + '.');
      } catch (error) {
        setStatus(error.message, true);
      }
    }

    document.querySelector('#save').addEventListener('click', saveNote);
    document.querySelector('#refresh').addEventListener('click', refreshBlobs);
    refreshBlobs();
  </script>
</body>
</html>`;
}

const server = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url ?? '/', `http://${request.headers.host ?? 'localhost'}`);

    if (url.pathname === '/') {
      sendHtml(response, renderHome());
      return;
    }

    if (url.pathname === '/storage') {
      const result = await createBlob(`Hello from Node.js and Azure Storage at ${new Date().toISOString()}`);
      const content = await readBlob(result.blobName);
      sendJson(response, 200, { ...result, content });
      return;
    }

    if (url.pathname === '/webhook' && request.method === 'POST') {
      const body = await readRequestBody(request);
      sendJson(response, 202, {
        accepted: true,
        contentType: request.headers['content-type'] ?? null,
        bodyLength: body.length,
      });
      return;
    }

    if (url.pathname === '/api/blobs' && request.method === 'GET') {
      sendJson(response, 200, await listBlobs());
      return;
    }

    if (url.pathname === '/api/blobs' && request.method === 'POST') {
      const body = await readRequestBody(request);
      const payload = body ? JSON.parse(body) : {};
      const content = typeof payload.content === 'string' && payload.content.trim()
        ? payload.content
        : `Hello from Node.js and Azure Storage at ${new Date().toISOString()}`;
      sendJson(response, 201, await createBlob(content));
      return;
    }

    if (url.pathname.startsWith('/api/blobs/') && request.method === 'GET') {
      const blobName = decodeURIComponent(url.pathname.slice('/api/blobs/'.length));
      sendJson(response, 200, {
        blobName,
        content: await readBlob(blobName),
      });
      return;
    }

    response.writeHead(404, { 'content-type': 'text/plain' });
    response.end('Not found');
  } catch (error) {
    console.error(error);
    sendJson(response, 500, { error: error instanceof Error ? error.message : String(error) });
  }
});

server.listen(port, '0.0.0.0', () => {
  console.log(`Node storage app listening on port ${port}`);
});
