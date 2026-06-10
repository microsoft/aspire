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
      Call the Aspire-modeled TypeScript Azure Function to write a sample blob
      immediately, or enqueue background work for a storage queue-triggered
      function to process asynchronously.
    </p>
  </section>
  <form id="work-form" class="card">
    <label for="name">Name</label>
    <div class="form-row">
      <input id="name" name="name" value="Aspire" autocomplete="off" />
      <button type="submit">Write blob now</button>
      <button id="queue-button" type="button">Queue background work</button>
    </div>
  </form>
  <pre id="result" class="result" aria-live="polite">Ready.</pre>
`;

const form = document.querySelector<HTMLFormElement>("#work-form");
const input = document.querySelector<HTMLInputElement>("#name");
const queueButton = document.querySelector<HTMLButtonElement>("#queue-button");
const result = document.querySelector<HTMLPreElement>("#result");

if (!form || !input || !queueButton || !result) {
    throw new Error("Missing frontend controls.");
}

const formElement = form;
const inputElement = input;
const queueButtonElement = queueButton;
const resultElement = result;

formElement.addEventListener("submit", async (event) => {
    event.preventDefault();

    await callFunction("storageHttpTrigger", "Writing blob...");
});

queueButtonElement.addEventListener("click", async () => {
    await callFunction("enqueueBackgroundWork", "Queueing background work...");
});

async function callFunction(functionName: string, pendingMessage: string): Promise<void> {
    const name = inputElement.value.trim() || "Aspire";
    resultElement.textContent = pendingMessage;

    try {
        const response = await fetch(`/api/${functionName}?name=${encodeURIComponent(name)}`);
        const body = await response.json();

        if (!response.ok) {
            throw new Error(body.error ?? `Function returned HTTP ${response.status}.`);
        }

        resultElement.textContent = JSON.stringify(body, null, 2);
    } catch (error) {
        resultElement.textContent = error instanceof Error ? error.message : String(error);
    }
}
