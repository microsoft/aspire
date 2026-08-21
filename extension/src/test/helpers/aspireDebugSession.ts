import * as sinon from 'sinon';
import { AspireDebugSession } from '../../debugger/AspireDebugSession';
import { AspireExtensionEnvironment } from '../../utils/cliPathEnvironment';

/**
 * Creates an {@link AspireDebugSession} test double.
 *
 * `sinon.createStubInstance` replaces prototype *methods* but leaves accessors alone, so a bare
 * stub instance still runs the real `aspireExtensionEnvironment` getter. That getter delegates to
 * the terminal provider, which a stub instance never receives, so reading it throws. AppHost
 * launches read the accessor while building the debug configuration, so define it as an own
 * property that shadows the prototype accessor.
 */
export function createFakeAspireDebugSession(
    aspireExtensionEnvironment?: AspireExtensionEnvironment,
): sinon.SinonStubbedInstance<AspireDebugSession> {
    const fake = sinon.createStubInstance(AspireDebugSession);
    Object.defineProperty(fake, 'aspireExtensionEnvironment', {
        value: aspireExtensionEnvironment,
        configurable: true,
        writable: true,
    });

    return fake;
}
