import * as vscode from 'vscode';
import { getRunSessionInfo, getSupportedCapabilities } from '../capabilities';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import type { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { extensionLogOutputChannel } from '../utils/logging';
import type AspireRpcServer from '../server/AspireRpcServer';
import { generateToken } from '../utils/security';
import { DcpServerConnectionInfo, RunSessionInfo } from './types';
import { generateDcpIdPrefix } from './AspireDcpServer';
import type AspireDcpServer from './AspireDcpServer';

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

export interface TestRunSessionDebugSessionOptions {
    rpcServer: AspireRpcServer;
    dcpServer: AspireDcpServer;
    terminalProvider: AspireTerminalProvider;
    addAspireDebugSession: (session: AspireDebugSession) => void;
    removeAspireDebugSession: (session: AspireDebugSession) => void;
    getAspireDebugSession: (debugSessionId: string | null) => AspireDebugSession | null;
}

export class TestRunSessionManager {
    private readonly leases = new Map<string, TestRunSessionLease>();
    private readonly leaseLifetimeMs?: number;
    private readonly now: () => number;
    private connectionInfo?: DcpServerConnectionInfo;
    private debugSessionStartSubscription?: vscode.Disposable;
    private readonly leasedDebugSessionRemovers = new Map<string, () => void>();

    constructor(
        connectionInfo?: DcpServerConnectionInfo,
        private readonly getSupportedLaunchConfigurations: () => string[] = getSupportedCapabilities,
        options: TestRunSessionManagerOptions = {}) {
        this.connectionInfo = connectionInfo;
        this.leaseLifetimeMs = options.leaseLifetimeMs;
        this.now = options.now ?? Date.now;
    }

    initializeConnectionInfo(connectionInfo: DcpServerConnectionInfo): void {
        this.connectionInfo = connectionInfo;
    }

    listenForLeasedDebugSessions(options: TestRunSessionDebugSessionOptions): vscode.Disposable {
        this.debugSessionStartSubscription?.dispose();
        this.debugSessionStartSubscription = vscode.debug.onDidStartDebugSession(session => {
            const lease = this.tryGetLeaseForDebugSession(session);
            if (!lease || options.getAspireDebugSession(lease.sessionId)) {
                return;
            }

            const aspireDebugSession = new AspireDebugSession(
                session,
                options.rpcServer,
                options.dcpServer,
                options.terminalProvider,
                options.removeAspireDebugSession,
                lease.sessionId);

            options.addAspireDebugSession(aspireDebugSession);
            this.leasedDebugSessionRemovers.set(lease.id, () => options.removeAspireDebugSession(aspireDebugSession));
            extensionLogOutputChannel.info(`Registered leased Aspire debug session ${lease.sessionId} for VS Code debug session ${session.id}.`);
        });

        return this.debugSessionStartSubscription;
    }

    private tryGetLeaseForDebugSession(session: vscode.DebugSession): TestRunSessionLease | undefined {
        const dcpInstanceIdPrefix = session.configuration.env?.DCP_INSTANCE_ID_PREFIX;
        if (typeof dcpInstanceIdPrefix !== 'string') {
            return undefined;
        }

        return this.tryGetLeaseForSessionId(dcpInstanceIdPrefix.replace(/-$/, ''));
    }

    acquireTestRunSession(options: TestRunSessionAcquireOptions): AcquiredTestRunSession {
        if (!this.connectionInfo) {
            throw new Error('Test run session manager has not been initialized with DCP server connection information.');
        }

        this.removeExpiredLeases();

        const id = generateToken();
        const sessionId = generateDcpIdPrefix();
        const runSessionInfo: RunSessionInfo = {
            ...getRunSessionInfo(),
            supported_launch_configurations: this.getSupportedLaunchConfigurations()
        };

        this.leases.set(id, { id, sessionId, token: this.connectionInfo.token, expiresAt: this.getExpiresAt() });

        return {
            id,
            sessionId,
            env: {
                DEBUG_SESSION_PORT: this.connectionInfo.address,
                DEBUG_SESSION_TOKEN: this.connectionInfo.token,
                DEBUG_SESSION_SERVER_CERTIFICATE: this.connectionInfo.certificate,
                DCP_INSTANCE_ID_PREFIX: `${sessionId}-`,
                DEBUG_SESSION_RUN_MODE: options.debug ? 'Debug' : 'NoDebug',
                DEBUG_SESSION_INFO: JSON.stringify(runSessionInfo)
            }
        };
    }

    async releaseTestRunSession(id: string): Promise<void> {
        const lease = this.releaseLease(id);
        if (!lease) {
            return;
        }
    }

    private releaseLease(id: string): TestRunSessionLease | undefined {
        const lease = this.leases.get(id);
        this.leases.delete(id);
        this.removeLeasedDebugSession(id);
        return lease;
    }

    removeLeasedDebugSession(id: string): void {
        this.leasedDebugSessionRemovers.get(id)?.();
        this.leasedDebugSessionRemovers.delete(id);
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

    tryGetLeaseForDcpId(dcpId: string): TestRunSessionLease | undefined {
        for (const lease of this.leases.values()) {
            if (dcpId.startsWith(`${lease.sessionId}-`)) {
                if (this.isExpired(lease)) {
                    this.leases.delete(lease.id);
                    return undefined;
                }

                return lease;
            }
        }

        return undefined;
    }

    private tryGetLeaseForSessionId(sessionId: string): TestRunSessionLease | undefined {
        for (const lease of this.leases.values()) {
            if (lease.sessionId === sessionId) {
                if (this.isExpired(lease)) {
                    this.leases.delete(lease.id);
                    return undefined;
                }

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
