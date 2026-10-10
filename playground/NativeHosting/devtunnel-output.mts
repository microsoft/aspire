export interface TunnelOutput { url?: string; ready?: boolean; disconnected?: boolean }

// Bounded subset of observed Dev Tunnels output, not the shipped parser:
//   Hosting port: 7007
//   Connect via browser: https://abc-7007.usw2.devtunnels.ms
//   Hosting port 7007 at https://abc-7007.usw2.devtunnels.ms/
//   Ready to accept connections for tunnel: abc.usw2
//   Connection to host tunnel relay closed.
// TCP/file byte batches may split lines or UTF-8. The caller owns that decoder.
export class TunnelOutputParser {
    private port: number | undefined;
    private pending = '';
    private readonly tunnelId: string;
    private readonly targetPort: number;
    private readonly localFixture: boolean;
    constructor(tunnelId: string, targetPort: number, localFixture = false) {
        this.tunnelId = tunnelId;
        this.targetPort = targetPort;
        this.localFixture = localFixture;
    }

    parse(chunk: string): TunnelOutput[] {
        this.pending += chunk;
        if (this.pending.length > 16384) throw new Error('Dev Tunnels output exceeded the bounded line buffer.');
        const lines = this.pending.split(/\r?\n/);
        this.pending = lines.pop()!;
        return lines.map(line => this.line(line));
    }

    private line(raw: string): TunnelOutput {
        const line = raw.replace(/\x1b\[[0-9;]*[A-Za-z]/g, '').trim();
        const hosting = /^Hosting port(?::|\s)\s*(\d+)(?:\s+at\s+(\S+))?$/i.exec(line);
        if (hosting) {
            this.port = Number(hosting[1]);
            if (this.port !== this.targetPort) throw new Error('Dev Tunnels reported an unexpected forwarded port.');
            if (hosting[2]) return { url: this.validate(hosting[2]) };
        }
        const browser = /^Connect via browser:\s*(\S+)$/i.exec(line);
        if (browser && this.port === this.targetPort) return { url: this.validate(browser[1]) };
        if (/^Ready to accept connections(?: for tunnel:?\s*(\S+))?\.?$/i.test(line)) {
            const id = /^Ready to accept connections for tunnel:?\s*(\S+)$/i.exec(line)?.[1];
            if (id && id !== this.tunnelId && !id.startsWith(`${this.tunnelId}.`)) throw new Error('Dev Tunnels reported a foreign tunnel identity.');
            return { ready: true };
        }
        if (/^Connection to host tunnel relay (closed|lost|disconnected)[.!]?$/i.test(line)) return { disconnected: true };
        if (/^Connection to host tunnel relay (restored|connected)[.!]?$/i.test(line)) return { ready: true };
        return {};
    }

    private validate(text: string) {
        const uri = new URL(text);
        const local = this.localFixture && uri.protocol === 'http:' && uri.hostname === '127.0.0.1';
        const external = uri.protocol === 'https:' && uri.port === '' && uri.hostname.endsWith('.devtunnels.ms') &&
            uri.hostname.split('.')[0].endsWith(`-${this.targetPort}`);
        if ((!local && !external) || uri.username || uri.password || uri.search || uri.hash || uri.pathname !== '/') {
            throw new Error('Dev Tunnels reported an invalid public endpoint.');
        }
        return uri.toString();
    }
}
