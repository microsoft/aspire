import "./styles.css";

const app = document.querySelector<HTMLDivElement>("#app");

if (!app) {
    throw new Error("Missing app root element.");
}

app.innerHTML = `
  <section class="hero">
    <p class="eyebrow">Aspire + Azure Functions</p>
    <h1>TypeScript Functions with Azure Storage</h1>
    <p>
      Call the Aspire-modeled TypeScript Azure Function and write a sample blob
      to the configured Azure Storage container.
    </p>
  </section>
  <form id="blob-form" class="card">
    <label for="name">Name</label>
    <div class="form-row">
      <input id="name" name="name" value="Aspire" autocomplete="off" />
      <button type="submit">Write blob</button>
    </div>
  </form>
  <pre id="result" class="result" aria-live="polite">Ready.</pre>
`;

const form = document.querySelector<HTMLFormElement>("#blob-form");
const input = document.querySelector<HTMLInputElement>("#name");
const result = document.querySelector<HTMLPreElement>("#result");

if (!form || !input || !result) {
    throw new Error("Missing frontend controls.");
}

form.addEventListener("submit", async (event) => {
    event.preventDefault();

    const name = input.value.trim() || "Aspire";
    result.textContent = "Writing blob...";

    try {
        const response = await fetch(`/api/storageHttpTrigger?name=${encodeURIComponent(name)}`);
        const body = await response.json();

        if (!response.ok) {
            throw new Error(body.error ?? `Function returned HTTP ${response.status}.`);
        }

        result.textContent = JSON.stringify(body, null, 2);
    } catch (error) {
        result.textContent = error instanceof Error ? error.message : String(error);
    }
});
