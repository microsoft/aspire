// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { randomBytes } from "node:crypto";
import { promisify } from "node:util";
import * as oidc from "openid-client";
import express from "express";
import session from "express-session";

const scopes = ["openid", "profile"];
const randomToken = () => randomBytes(32).toString("base64url");

export function readConfiguration(environment)
{
    function required(name)
    {
        const value = environment[name];
        if (!value)
        {
            throw new Error(`Missing environment variable: ${name}`);
        }
        return value;
    }

    // The default connection name uses the resource name "entra-web" as ENTRA_WEB_.
    const instance = new URL(required("ENTRA_WEB_INSTANCE"));
    const baseUrl = new URL(required("NODE_WEB_BASE_URL"));
    if (instance.protocol !== "https:" || baseUrl.protocol !== "https:")
    {
        throw new Error("The authority and Node front end must use HTTPS.");
    }

    return {
        auth: {
            clientId: required("ENTRA_WEB_CLIENT_ID"),
            authority: new URL(required("ENTRA_WEB_TENANT_ID"), instance).href
        },
        redirectUri: new URL("/auth/redirect", baseUrl).href,
        postLogoutRedirectUri: new URL("/auth/signout-callback", baseUrl).href
    };
}

export async function createApp(configuration)
{
    // Entra accepts a tenant domain but advertises the canonical tenant GUID in its issuer.
    // Discover via the trusted tenant's document URL; ID-token validation still checks the discovered issuer.
    // https://github.com/panva/openid-client/blob/main/docs/functions/discovery.md
    const client = await oidc.discovery(
        new URL(`${configuration.auth.authority.replace(/\/$/, "")}/v2.0/.well-known/openid-configuration`),
        configuration.auth.clientId,
        { id_token_signed_response_alg: "RS256" },
        oidc.None(),
        { execute: [oidc.useIdTokenResponseType] });
    const app = express();
    const cookieOptions = {
        httpOnly: true,
        secure: new URL(configuration.redirectUri).protocol === "https:",
        // The ID token arrives via a cross-site form POST, which must include the sign-in session cookie.
        // https://learn.microsoft.com/entra/identity-platform/v2-oauth2-implicit-grant-flow
        sameSite: "none"
    };

    app.disable("x-powered-by");
    app.use((request, response, next) =>
    {
        response.set("Cache-Control", "no-store");
        response.set("Content-Security-Policy", "default-src 'none'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'");
        next();
    });
    app.get("/health", (request, response) => response.sendStatus(200));
    app.use(express.urlencoded({ extended: false, limit: "16kb" }));
    // MemoryStore and a per-process signing key are only for this local playground.
    // Restarting the Node app discards its sessions; production apps need a persistent session store.
    app.use(session({
        name: "entra-node.sid",
        secret: randomToken(),
        resave: false,
        saveUninitialized: false,
        cookie: { ...cookieOptions, maxAge: 60 * 60 * 1000 }
    }));

    app.get("/", (request, response) =>
    {
        const account = request.session.account;
        response.type("html").send(page(account
            ? `<p>Signed in as <strong>${escapeHtml(account.name)}</strong> (${escapeHtml(account.username)}).</p>
               <form method="post" action="/signout">
                   <input type="hidden" name="csrfToken" value="${request.session.csrfToken}">
                   <button type="submit">Sign out</button>
               </form>`
            : '<p>You are not signed in.</p><p><a href="/signin">Sign in with Microsoft Entra ID</a></p>'));
    });

    app.get("/signin", async (request, response) =>
    {
        await promisify(request.session.regenerate).call(request.session);
        const flow = { state: randomToken(), nonce: randomToken(), createdAt: Date.now() };
        request.session.authFlow = flow;

        const url = oidc.buildAuthorizationUrl(client, {
            scope: scopes.join(" "),
            redirect_uri: configuration.redirectUri,
            response_mode: "form_post",
            state: flow.state,
            nonce: flow.nonce
        });
        await promisify(request.session.save).call(request.session);
        response.redirect(url.href);
    });

    app.post("/auth/redirect", async (request, response) =>
    {
        const flow = request.session.authFlow;
        delete request.session.authFlow;
        if (!flow || request.body?.state !== flow.state || Date.now() - flow.createdAt > 10 * 60 * 1000)
        {
            return response.status(400).send("Invalid or expired sign-in state. Start sign-in again.");
        }
        if (request.body.error)
        {
            console.warn("Microsoft Entra ID sign-in was declined or failed.");
            return response.status(401).send("Microsoft Entra ID sign-in was declined or failed.");
        }
        if (typeof request.body.id_token !== "string" || !request.body.id_token)
        {
            return response.status(400).send("The sign-in response did not include an ID token.");
        }

        const callback = new Request(configuration.redirectUri, {
            method: "POST",
            headers: { "Content-Type": "application/x-www-form-urlencoded" },
            body: new URLSearchParams(request.body)
        });
        const claims = await oidc.implicitAuthentication(client, callback, flow.nonce, { expectedState: flow.state });
        const username = typeof claims.preferred_username === "string" ? claims.preferred_username : claims.sub;
        const name = typeof claims.name === "string" ? claims.name : username;

        await promisify(request.session.regenerate).call(request.session);
        request.session.account = { name, username };
        request.session.csrfToken = randomToken();
        await promisify(request.session.save).call(request.session);
        response.redirect("/");
    });

    app.post("/signout", async (request, response) =>
    {
        if (!request.session.csrfToken || request.body?.csrfToken !== request.session.csrfToken)
        {
            return response.status(400).send("Invalid sign-out antiforgery token.");
        }
        await promisify(request.session.destroy).call(request.session);
        response.clearCookie("entra-node.sid", cookieOptions);
        const logoutUrl = oidc.buildEndSessionUrl(client, {
            post_logout_redirect_uri: configuration.postLogoutRedirectUri,
            client_id: configuration.auth.clientId
        });
        response.redirect(logoutUrl.href);
    });
    app.get("/auth/signout-callback", (request, response) => response.redirect("/"));

    app.use((error, request, response, next) =>
    {
        // Authentication errors can contain identity details. Log the error code, not tokens or the full response.
        console.error("Node Entra authentication failed:", error.errorCode ?? error.code ?? error.name);
        response.status(500).send("Authentication failed. Check the Node app's console logs and Entra configuration.");
    });
    return app;
}

function escapeHtml(value)
{
    return value.replace(/[&<>"']/g, character => ({
        "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"
    })[character]);
}

function page(body)
{
    return `<!DOCTYPE html>
<html lang="en">
<head><meta charset="utf-8"><title>Node Entra ID playground</title></head>
<body><h1>Node Entra ID playground</h1>
<p>This Node app signs in independently using the same registration as the .NET web front end.</p>
${body}
</body></html>`;
}
