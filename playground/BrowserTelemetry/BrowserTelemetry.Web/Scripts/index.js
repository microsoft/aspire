import {
  ConsoleSpanExporter,
  SimpleSpanProcessor,
} from '@opentelemetry/sdk-trace-base';
import { WebTracerProvider } from '@opentelemetry/sdk-trace-web';
import { DocumentLoadInstrumentation } from '@opentelemetry/instrumentation-document-load';
import { ZoneContextManager } from '@opentelemetry/context-zone';
import { registerInstrumentations } from '@opentelemetry/instrumentation';
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-http';
import { resourceFromAttributes } from '@opentelemetry/resources';

export function initializeTelemetry(otlpUrl, headers, resourceAttributes) {
    const otlpOptions = {
        url: `${otlpUrl}/v1/traces`,
        headers: parseDelimitedValues(headers)
    };

    var attributes = parseDelimitedValues(resourceAttributes);
    attributes['service.name'] = 'browser';

    const provider = new WebTracerProvider({
        resource: resourceFromAttributes(attributes),
        spanProcessors: [
            new SimpleSpanProcessor(new ConsoleSpanExporter()),
            new SimpleSpanProcessor(new OTLPTraceExporter(otlpOptions))
        ]
    });

    provider.register({
        // Changing default contextManager to use ZoneContextManager - supports asynchronous operations - optional
        contextManager: new ZoneContextManager(),
    });

    // Registering instrumentations
    registerInstrumentations({
        instrumentations: [new DocumentLoadInstrumentation()],
    });
}

document.addEventListener('DOMContentLoaded', () => {
    wireButton('emit-console-log', () => {
        setStatus('Emitted console.log.');
        console.log('BrowserTelemetry demo: console.log fired from the tracked browser.');
    });

    wireButton('emit-console-warn', () => {
        setStatus('Emitted console.warn.');
        console.warn('BrowserTelemetry demo: console.warn fired from the tracked browser.');
    });

    wireButton('emit-console-error', () => {
        setStatus('Emitted console.error.');
        console.error('BrowserTelemetry demo: console.error fired from the tracked browser.');
    });

    wireButton('emit-unhandled-exception', () => {
        setStatus('Scheduling an unhandled exception.');
        window.setTimeout(() => {
            throw new Error('BrowserTelemetry demo: unhandled exception from tracked browser.');
        }, 50);
    });

    wireButton('emit-unhandled-rejection', () => {
        setStatus('Scheduling an unhandled promise rejection.');
        Promise.reject(new Error('BrowserTelemetry demo: unhandled promise rejection from tracked browser.'));
    });

    wireButton('emit-successful-fetch', async () => {
        setStatus('Issuing a successful fetch request.');
        const response = await fetch(`${window.location.pathname}?browserNetworkSuccess=${Date.now()}`, {
            cache: 'no-store',
            headers: {
                'x-browser-telemetry-demo': 'success'
            }
        });

        setStatus(`Successful fetch completed with status ${response.status}.`);
        console.info(`BrowserTelemetry demo: successful fetch completed with status ${response.status}.`);
    });

    wireButton('emit-failing-fetch', async () => {
        setStatus('Issuing a failing fetch request.');

        try {
            await fetch(`https://127.0.0.1:1/browser-network-failure?ts=${Date.now()}`, {
                cache: 'no-store'
            });
        } catch (error) {
            setStatus('Failing fetch triggered.');
            console.warn('BrowserTelemetry demo: failing fetch triggered for tracked browser network capture.', error);
        }
    });

    window.setTimeout(() => {
        setStatus('Tracked browser demo ready.');
        console.info('BrowserTelemetry demo: page loaded and ready for tracked browser logging.');
    }, 250);
});

function parseDelimitedValues(s) {
    const o = {};
    if (!s || !s.trim()) {
        return o;
    }

    s.split(',').forEach(header => {
        const separatorIndex = header.indexOf('=');
        if (separatorIndex === -1) {
            return;
        }

        const key = header.slice(0, separatorIndex).trim();
        if (!key) {
            return;
        }

        const value = header.slice(separatorIndex + 1).trim();
        o[key] = value;
    });

    return o;
}

function wireButton(id, callback) {
    const button = document.getElementById(id);
    if (!button) {
        return;
    }

    button.addEventListener('click', callback);
}

function setStatus(message) {
    const status = document.getElementById('browser-log-status');
    if (!status) {
        return;
    }

    status.textContent = message;
}
