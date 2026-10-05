import { ChildProcess, spawn } from 'child_process';
import { extensionLogOutputChannel } from './logging';

/**
 * Spawn options that make a child process the leader of its own process group, so
 * {@link terminateProcessTree} can signal the whole group.
 *
 * Only applied on POSIX: on Windows `detached` allocates a new console window, and the process tree is
 * terminated with `taskkill /t` instead, which needs nothing at spawn time.
 */
export function processGroupSpawnOptions(): { detached: boolean } {
    return { detached: process.platform !== 'win32' };
}

/**
 * Terminates a child process together with every process it spawned.
 *
 * `ChildProcess.kill()` signals only the direct child. That is not enough for tools that fan out into
 * their own subprocesses — killing `cargo` leaves `rustc`, the linker and any build scripts running, and
 * those are what hold the target-directory lock that the cleanup exists to release.
 *
 * @param child The process to terminate.
 * @param force Whether to kill outright rather than asking the process to stop.
 * @returns Whether a signal was delivered. `false` means the caller should fall back to its own cleanup.
 */
export function terminateProcessTree(child: ChildProcess, force: boolean = false): boolean {
    const signal = force ? 'SIGKILL' : 'SIGTERM';

    // Without a pid there is no tree to walk — the process either never started or has already been
    // reaped — so fall back to signalling the child directly.
    if (child.pid === undefined) {
        return child.kill(force ? signal : undefined);
    }

    if (process.platform === 'win32') {
        // Windows has no process groups to signal, so shell out to taskkill, whose /t walks the tree.
        const args = ['/pid', String(child.pid), '/t'];
        if (force) {
            args.push('/f');
        }

        const taskkill = spawn('taskkill.exe', args, { stdio: 'ignore', windowsHide: true });
        taskkill.on('error', error => {
            extensionLogOutputChannel.warn(`Failed to stop process tree for PID ${child.pid}: ${error}`);
            child.kill();
        });
        // taskkill can start and still fail, most often with access denied, which would otherwise leave the
        // tree running. Signalling the direct child is the best that can be done from here, and is harmless
        // when the nonzero code was the benign 128 for a process that had already exited.
        taskkill.on('close', code => {
            if (code !== 0) {
                extensionLogOutputChannel.warn(`taskkill exited with code ${code} for PID ${child.pid}; falling back to killing the direct child.`);
                child.kill();
            }
        });
        taskkill.unref();

        return true;
    }

    // A negative pid signals the whole process group. The group only exists when the child was spawned
    // with processGroupSpawnOptions(); otherwise the kill fails with ESRCH and the direct child is
    // signalled instead. A group id always equals its leader's pid, so this can never hit an unrelated
    // group by accident.
    try {
        process.kill(-child.pid, signal);
        return true;
    } catch {
        return child.kill(signal);
    }
}
