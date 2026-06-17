import './styles.css';

const app = document.querySelector<HTMLDivElement>('#app');

if (app) {
  app.innerHTML = `
    <section class="hero">
      <p class="eyebrow">Aspire + Azure Container Apps sandboxes</p>
      <h1>Static site deployed into a sandbox</h1>
      <p>
        This Vite app is built locally, copied into an Azure sandbox, served by a
        generated Node.js static file server, and exposed through an anonymous
        sandbox port.
      </p>
    </section>
  `;
}
