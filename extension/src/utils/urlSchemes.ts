// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/**
 * URL schemes that terminals and browsers don't support opening as links.
 * This is a deny list because custom schemes could hand off the link to an app
 * registered with the OS. For example, vscode://.
 *
 * Mirrors the dashboard's KnownUnsupportedUrlSchemes (src/Shared/KnownUnsupportedUrlSchemes.cs).
 */
const unsupportedSchemes = new Set([
    'gopher',
    'ws',
    'wss',
    'news',
    'nntp',
    'telnet',
    'tcp',
    'redis',
    'rediss',
]);

/**
 * Returns true when the URL scheme is known to be linkable (i.e. not in the unsupported set).
 */
export function isLinkableUrl(url: string): boolean {
    try {
        const parsed = new URL(url);
        return !unsupportedSchemes.has(parsed.protocol.replace(/:$/, '').toLowerCase());
    } catch {
        return false;
    }
}
