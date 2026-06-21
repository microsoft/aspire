import * as vscode from 'vscode';
import { getRunSessionInfo, getSupportedCapabilities } from '../capabilities';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import type { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { extensionLogOutputChannel } from '../utils/logging';
import { testRunSessionManagerNotInitialized } from '../loc/strings';
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

interface TestRunSessionLease {
    id: string;
    sessionId: string;
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
    private connectionInfo?: DcpServerConnectionInfo;
    private debugSessionSubscription?: vscode.Disposable;
    private readonly leasedDebugSessionDisposers = new Map<string, () => void>();

    constructor(
        connectionInfo?: DcpServerConnectionInfo,
        private readonly getSupportedLaunchConfigurations: () => string[] = getSupportedCapabilities) {
        this.connectionInfo = connectionInfo;
    }

    initializeConnectionInfo(connectionInfo: DcpServerConnectionInfo): void {
        this.connectionInfo = connectionInfo;
    }

    listenForLeasedDebugSessions(options: TestRunSessionDebugSessionOptions): vscode.Disposable {
        this.debugSessionSubscription?.dispose();
        const startSubscription = vscode.debug.onDidStartDebugSession(session => {
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
            this.leasedDebugSessionDisposers.set(lease.id, () => aspireDebugSession.dispose());
            extensionLogOutputChannel.info(`Registered leased Aspire debug session ${lease.sessionId} for VS Code debug session ${session.id}.`);
        });
        const terminateSubscription = vscode.debug.onDidTerminateDebugSession(session => {
            const lease = this.tryGetLeaseForDebugSession(session);
            if (lease) {
                this.releaseLease(lease.id);
            }
        });
        this.debugSessionSubscription = vscode.Disposable.from(startSubscription, terminateSubscription);

        return this.debugSessionSubscription;
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
            throw new Error(testRunSessionManagerNotInitialized);
        }

        const id = generateToken();
        const sessionId = generateDcpIdPrefix();
        const runSessionInfo: RunSessionInfo = {
            ...getRunSessionInfo(),
            supported_launch_configurations: this.getSupportedLaunchConfigurations()
        };

        this.leases.set(id, { id, sessionId });

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

    releaseTestRunSession(id: string): Promise<void> {
        this.releaseLease(id);

        return Promise.resolve();
    }

    private releaseLease(id: string): TestRunSessionLease | undefined {
        const lease = this.leases.get(id);
        this.leases.delete(id);
        this.removeLeasedDebugSession(id);
        return lease;
    }

    private removeLeasedDebugSession(id: string): void {
        const dispose = this.leasedDebugSessionDisposers.get(id);
        this.leasedDebugSessionDisposers.delete(id);
        dispose?.();
    }

    private tryGetLeaseForSessionId(sessionId: string): TestRunSessionLease | undefined {
        for (const lease of this.leases.values()) {
            if (lease.sessionId === sessionId) {
                return lease;
            }
        }

        return undefined;
    }
}
