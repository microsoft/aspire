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
    private readonly leasedDebugSessions = new Map<string, AspireDebugSession>();
    // VS Code's termination event can race the test API's explicit release. Both callers must join
    // the same ordered stop so the API does not report completion while session removal is pending.
    private readonly leaseReleasePromises = new Map<string, Promise<TestRunSessionLease | undefined>>();

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
            this.leasedDebugSessions.set(lease.id, aspireDebugSession);
            extensionLogOutputChannel.info(`Registered leased Aspire debug session ${lease.sessionId} for VS Code debug session ${session.id}.`);
        });
        const terminateSubscription = vscode.debug.onDidTerminateDebugSession(session => {
            const lease = this.tryGetLeaseForDebugSession(session);
            if (lease) {
                this.leasedDebugSessions.get(lease.id)?.recordParentDebugSessionTermination();
                void this.releaseLease(lease.id).catch(error => {
                    extensionLogOutputChannel.warn(`Failed to stop leased Aspire debug session ${lease.sessionId}: ${String(error)}`);
                });
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

    async releaseTestRunSession(id: string): Promise<void> {
        await this.releaseLease(id);
    }

    private releaseLease(id: string): Promise<TestRunSessionLease | undefined> {
        const existingRelease = this.leaseReleasePromises.get(id);
        if (existingRelease) {
            return existingRelease;
        }

        // Defer the stop until the single-flight promise is registered. A stop can synchronously
        // raise the parent termination event, which re-enters releaseLease and must join this
        // release rather than starting another stop.
        const release = Promise.resolve().then(() => this.releaseLeaseCore(id));
        this.leaseReleasePromises.set(id, release);
        const clearRelease = () => {
            if (this.leaseReleasePromises.get(id) === release) {
                this.leaseReleasePromises.delete(id);
            }
        };
        void release.then(clearRelease, clearRelease);

        return release;
    }

    private async releaseLeaseCore(id: string): Promise<TestRunSessionLease | undefined> {
        const lease = this.leases.get(id);
        await this.stopLeasedDebugSession(id);
        this.leases.delete(id);

        return lease;
    }

    private async stopLeasedDebugSession(id: string): Promise<void> {
        const debugSession = this.leasedDebugSessions.get(id);
        await debugSession?.stopDebugging();
        this.leasedDebugSessions.delete(id);
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
