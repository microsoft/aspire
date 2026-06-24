import './styles.css';

type BackendMessage = {
  message: string;
  computeEnvironment: string;
  time: string;
};

const app = document.querySelector<HTMLDivElement>('#app');

async function loadBackendMessage(): Promise<void> {
  if (!app) {
    return;
  }

  try {
    const response = await fetch('/api/message');
    if (!response.ok) {
      throw new Error(`Backend request failed with HTTP ${response.status}`);
    }

    const result = await response.json() as BackendMessage;
    app.innerHTML = `
      <main class="card">
        <p class="eyebrow">Aspire mixed compute environments</p>
        <h1>Vite in Sandbox, C# in App Service</h1>
        <p class="lead">
          This static Vite frontend is deployed to an ACA sandbox. Its <code>/api</code>
          calls are reverse-proxied to a C# backend deployed to Azure App Service.
        </p>
        <dl>
          <dt>Backend message</dt>
          <dd>${escapeHtml(result.message)}</dd>
          <dt>Backend compute environment</dt>
          <dd>${escapeHtml(result.computeEnvironment)}</dd>
          <dt>Backend UTC time</dt>
          <dd>${escapeHtml(result.time)}</dd>
        </dl>
      </main>
    `;
  } catch (error) {
    app.innerHTML = `
      <main class="card error">
        <p class="eyebrow">Backend call failed</p>
        <h1>Could not reach /api/message</h1>
        <p>${escapeHtml(error instanceof Error ? error.message : String(error))}</p>
      </main>
    `;
  }
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

void loadBackendMessage();
