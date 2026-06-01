import { randomBytes, timingSafeEqual } from 'crypto';
import { getRunSessionInfo, getSupportedCapabilities } from '../capabilities';
import { generateToken } from '../utils/security';
import { DcpServerConnectionInfo, RunSessionInfo } from './types';

export interface TestRunSessionAcquireOptions {
    debug: boolean;
}

export interface AcquiredTestRunSession {
    id: string;
    sessionId: string;
    env: Record<string, string>;
}

export interface TestRunSessionLease {
    id: string;
    sessionId: string;
    token: string;
    expiresAt: number;
}

export interface TestRunSessionManagerOptions {
    leaseLifetimeMs?: number;
    now?: () => number;
}

export class TestRunSessionManager {
    private readonly leases = new Map<string, TestRunSessionLease>();
    private readonly leaseLifetimeMs?: number;
    private readonly now: () => number;

    constructor(
        private readonly connectionInfo: Pick<DcpServerConnectionInfo, 'address' | 'certificate'>,
        private readonly getSupportedLaunchConfigurations: () => string[] = getSupportedCapabilities,
        options: TestRunSessionManagerOptions = {}) {
        this.leaseLifetimeMs = options.leaseLifetimeMs;
        this.now = options.now ?? Date.now;
    }

    acquire(options: TestRunSessionAcquireOptions): AcquiredTestRunSession {
        this.removeExpiredLeases();

        const id = generateToken();
        const sessionId = `aspire-extension-test-run-${randomBytes(16).toString('hex')}`;
        const token = generateToken();
        const runSessionInfo: RunSessionInfo = {
            ...getRunSessionInfo(),
            supported_launch_configurations: this.getSupportedLaunchConfigurations()
        };

        this.leases.set(id, { id, sessionId, token, expiresAt: this.getExpiresAt() });

        return {
            id,
            sessionId,
            env: {
                DEBUG_SESSION_PORT: this.connectionInfo.address,
                DEBUG_SESSION_TOKEN: token,
                DEBUG_SESSION_SERVER_CERTIFICATE: this.connectionInfo.certificate,
                DCP_INSTANCE_ID_PREFIX: `${sessionId}-`,
                DEBUG_SESSION_RUN_MODE: options.debug ? 'Debug' : 'NoDebug',
                DEBUG_SESSION_INFO: JSON.stringify(runSessionInfo)
            }
        };
    }

    release(id: string): TestRunSessionLease | undefined {
        const lease = this.leases.get(id);
        this.leases.delete(id);
        return lease;
    }

    isActive(id: string): boolean {
        const lease = this.leases.get(id);
        if (!lease) {
            return false;
        }

        if (this.isExpired(lease)) {
            this.leases.delete(id);
            return false;
        }

        return true;
    }

    tryAuthorizeDcpRequest(dcpId: string, token: string): TestRunSessionLease | undefined {
        const lease = this.tryGetLeaseForDcpId(dcpId);
        if (!lease) {
            return undefined;
        }

        if (this.isExpired(lease)) {
            this.leases.delete(lease.id);
            return undefined;
        }

        const bearerTokenBuffer = Buffer.from(token);
        const expectedTokenBuffer = Buffer.from(lease.token);
        if (bearerTokenBuffer.length !== expectedTokenBuffer.length) {
            return undefined;
        }

        if (timingSafeEqual(bearerTokenBuffer, expectedTokenBuffer) === false) {
            return undefined;
        }

        return lease;
    }

    private tryGetLeaseForDcpId(dcpId: string): TestRunSessionLease | undefined {
        for (const lease of this.leases.values()) {
            if (dcpId.startsWith(`${lease.sessionId}-`)) {
                return lease;
            }
        }

        return undefined;
    }

    private removeExpiredLeases(): void {
        for (const lease of this.leases.values()) {
            if (this.isExpired(lease)) {
                this.leases.delete(lease.id);
            }
        }
    }

    private isExpired(lease: TestRunSessionLease): boolean {
        if (this.leaseLifetimeMs === undefined) {
            return false;
        }

        return this.now() >= lease.expiresAt;
    }

    private getExpiresAt(): number {
        return this.leaseLifetimeMs === undefined
            ? Number.POSITIVE_INFINITY
            : this.now() + this.leaseLifetimeMs;
    }
}
