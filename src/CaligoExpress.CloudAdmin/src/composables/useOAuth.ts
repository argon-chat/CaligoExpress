import axios from "axios";
import { delay, logger } from "@argon/core";
import { ref, type Ref } from "vue";

// OIDC (PKCE) endpoints for operator SSO — configure at build time; without them the sign-in
// button has nowhere to redirect (use the worker's machine tokens / a private deployment instead).
const OAUTH_URL = import.meta.env.VITE_OAUTH_AUTHORIZE_URL ?? "";
const TOKEN_URL = import.meta.env.VITE_OAUTH_TOKEN_URL ?? "";

const CLIENT_ID = import.meta.env.VITE_OAUTH_CLIENT_ID ?? "";

const getRedirectUri = () => `${window.location.origin}/callback`;

// Everything persists to localStorage so the session survives reloads and browser restarts.
const LS = {
    access: "caligo.access_token",
    exp: "caligo.access_exp",
    refresh: "caligo.refresh_token",
    pkce: "caligo.pkce_verifier",
} as const;

function lsGet(k: string): string | null {
    try { return localStorage.getItem(k); } catch { return null; }
}
function lsSet(k: string, v: string): void {
    try { localStorage.setItem(k, v); } catch { /* private mode / quota */ }
}
function lsDel(k: string): void {
    try { localStorage.removeItem(k); } catch { /* ignore */ }
}

// In-memory mirror (reactive), seeded from localStorage so a page reload keeps you signed in.
const accessTokenRef = ref<string | null>(lsGet(LS.access));
let accessTokenExp: number = Number(lsGet(LS.exp) ?? "0") || 0;

// Lock to avoid concurrent refreshes.
let refreshPromise: Promise<boolean> | null = null;
let autoRefreshTimer: ReturnType<typeof setInterval> | null = null;

export function getAccessToken(): string | null {
    return accessTokenRef.value;
}

/** Reactive ref — use in computed() so it re-triggers on token change */
export function useAccessToken(): Ref<string | null> {
    return accessTokenRef;
}

function getRefreshToken(): string | null {
    const t = lsGet(LS.refresh);
    return t && t.length > 0 ? t : null;
}

export async function generatePKCE() {
    const randomBytes = new Uint8Array(32);
    crypto.getRandomValues(randomBytes);

    const codeVerifier = btoa(String.fromCharCode(...randomBytes))
        .replace(/\+/g, "-")
        .replace(/\//g, "_")
        .replace(/=/g, "");

    const encoder = new TextEncoder();
    const data = encoder.encode(codeVerifier);

    const digest = await crypto.subtle.digest("SHA-256", data);
    const hashArray = Array.from(new Uint8Array(digest));
    const codeChallenge = btoa(String.fromCharCode(...hashArray))
        .replace(/\+/g, "-")
        .replace(/\//g, "_")
        .replace(/=/g, "");

    return { codeVerifier, codeChallenge };
}

// Call before every API request.
export async function ensureValidToken(): Promise<string | null> {
    if (isTokenExpired()) {
        logger.warn("Token expired, refreshing before request...");
        const ok = await ensureTokenRefreshed();
        if (!ok) {
            logger.error("Failed to refresh token");
            return null;
        }
    }
    return accessTokenRef.value;
}

export async function ensureAuthenticated() {
    if (!accessTokenRef.value || isTokenExpired()) {
        // access_token may be missing/expired after a reload — recover via the persisted refresh_token.
        if (getRefreshToken()) {
            logger.warn("Access token missing or expired, attempting refresh...");
            const ok = await ensureTokenRefreshed();
            if (ok) {
                startTokenAutoRefresh();
                return;
            }
        }

        logger.warn("No valid tokens, redirecting to OAuth...");
        await redirectToOAuth();
        return;
    }

    startTokenAutoRefresh();
}

async function redirectToOAuth() {
    const { codeVerifier, codeChallenge } = await generatePKCE();
    lsSet(LS.pkce, codeVerifier);

    const params = new URLSearchParams({
        client_id: CLIENT_ID,
        redirect_uri: getRedirectUri(),
        response_type: "code",
        scope: "openid identity offline_access",
        code_challenge: codeChallenge,
        code_challenge_method: "S256",
    });

    window.location.href = `${OAUTH_URL}?${params.toString()}`;
    await delay(5000);
}

export async function exchangeCodeForToken(code: string) {
    const codeVerifier = lsGet(LS.pkce);
    if (!codeVerifier) throw new Error("PKCE verifier missing");

    const form = new URLSearchParams({
        grant_type: "authorization_code",
        code,
        redirect_uri: getRedirectUri(),
        client_id: CLIENT_ID,
        code_verifier: codeVerifier,
    });

    logger.info(`OAuth: exchanging authorization code for tokens (redirect_uri=${getRedirectUri()})`);
    const res = await axios.post(TOKEN_URL, form);
    const data = res.data;
    if (!data.access_token) throw new Error("OAuth error: no access_token returned");

    logger.info(
        `OAuth: tokens received — access=${tokenTail(data.access_token)} expires_in=${data.expires_in ?? "?"}s, ` +
        `offline/refresh=${data.refresh_token ? `yes (${tokenTail(data.refresh_token)})` : "no"}, scope="${data.scope ?? "?"}"`,
    );

    saveTokens(data);
    lsDel(LS.pkce);
}

/** Safe token preview for logs: first 6 + last 4 chars, never the full secret. */
function tokenTail(t?: string): string {
    return t && t.length > 12 ? `${t.slice(0, 6)}…${t.slice(-4)}` : t ? "short" : "none";
}

function saveTokens(data: any) {
    const now = Math.floor(Date.now() / 1000);

    accessTokenRef.value = data.access_token;
    accessTokenExp = now + (data.expires_in ?? 3600);

    lsSet(LS.access, data.access_token);
    lsSet(LS.exp, String(accessTokenExp));

    // The token endpoint may omit refresh_token on a refresh — keep the existing one instead of
    // overwriting it with an empty value (that was wiping the session).
    if (data.refresh_token) lsSet(LS.refresh, data.refresh_token);
}

/** Clears all persisted auth state (sign out). */
export function clearTokens(): void {
    accessTokenRef.value = null;
    accessTokenExp = 0;
    lsDel(LS.access);
    lsDel(LS.exp);
    lsDel(LS.refresh);
    if (autoRefreshTimer) {
        clearInterval(autoRefreshTimer);
        autoRefreshTimer = null;
    }
}

export function isTokenExpired(): boolean {
    if (!accessTokenExp) return true;
    const now = Math.floor(Date.now() / 1000);
    return now >= accessTokenExp - 60; // refresh 60s early
}

export function startTokenAutoRefresh() {
    if (autoRefreshTimer) return; // already running
    autoRefreshTimer = setInterval(async () => {
        if (isTokenExpired() && getRefreshToken()) {
            await ensureTokenRefreshed();
        }
    }, 30_000);
}

export async function ensureTokenRefreshed(): Promise<boolean> {
    if (refreshPromise) return await refreshPromise;

    refreshPromise = refreshAccessToken();
    try {
        return await refreshPromise;
    } finally {
        refreshPromise = null;
    }
}

export async function refreshAccessToken(): Promise<boolean> {
    const refreshToken = getRefreshToken();
    if (!refreshToken) {
        logger.warn("No refresh_token");
        return false;
    }

    const form = new URLSearchParams({
        grant_type: "refresh_token",
        refresh_token: refreshToken,
        client_id: CLIENT_ID,
    });

    try {
        logger.info(`OAuth: refreshing access token via refresh_token (${tokenTail(refreshToken)})...`);
        const res = await axios.post(TOKEN_URL, form);
        const data = res.data;
        if (!data.access_token) return false;

        logger.info(
            `OAuth: refreshed — access=${tokenTail(data.access_token)} expires_in=${data.expires_in ?? "?"}s, ` +
            `refresh rotated=${data.refresh_token ? "yes" : "no (kept existing)"}`,
        );
        saveTokens(data);
        return true;
    } catch (err) {
        logger.error("Token refresh failed", err);
        return false;
    }
}

/** Whether the user is authenticated (no redirect). */
export function isAuthenticated(): boolean {
    if (getRefreshToken()) return true; // can be restored via refresh
    return !!accessTokenRef.value && !isTokenExpired();
}
