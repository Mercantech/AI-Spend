const AUTH_ISSUER = import.meta.env.VITE_AUTH_ISSUER || 'https://auth.mercantec.tech'
const CLIENT_ID = import.meta.env.VITE_AUTH_CLIENT_ID || 'ai-spend-web'
const REDIRECT_URI =
  import.meta.env.VITE_AUTH_REDIRECT_URI || `${window.location.origin}/callback`

const ACCESS_KEY = 'aispend_access'
const REFRESH_KEY = 'aispend_refresh'
const VERIFIER_KEY = 'aispend_verifier'
const STATE_KEY = 'aispend_state'

function base64UrlEncode(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer)
  let str = ''
  bytes.forEach((b) => {
    str += String.fromCharCode(b)
  })
  return btoa(str).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

function randomString(length = 64): string {
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~'
  const array = new Uint8Array(length)
  crypto.getRandomValues(array)
  return Array.from(array, (x) => chars[x % chars.length]).join('')
}

async function sha256(input: string): Promise<string> {
  const data = new TextEncoder().encode(input)
  const hash = await crypto.subtle.digest('SHA-256', data)
  return base64UrlEncode(hash)
}

export function getAccessToken(): string | null {
  return sessionStorage.getItem(ACCESS_KEY)
}

export function getRefreshToken(): string | null {
  return sessionStorage.getItem(REFRESH_KEY)
}

export function clearTokens(): void {
  sessionStorage.removeItem(ACCESS_KEY)
  sessionStorage.removeItem(REFRESH_KEY)
}

function storeTokens(access: string, refresh?: string | null): void {
  sessionStorage.setItem(ACCESS_KEY, access)
  if (refresh) sessionStorage.setItem(REFRESH_KEY, refresh)
}

export function isLoggedIn(): boolean {
  return !!getAccessToken()
}

export async function startLogin(): Promise<void> {
  const verifier = randomString(64)
  const state = randomString(32)
  const challenge = await sha256(verifier)
  sessionStorage.setItem(VERIFIER_KEY, verifier)
  sessionStorage.setItem(STATE_KEY, state)

  const params = new URLSearchParams({
    response_type: 'code',
    client_id: CLIENT_ID,
    redirect_uri: REDIRECT_URI,
    state,
    code_challenge: challenge,
    code_challenge_method: 'S256',
  })

  window.location.assign(`${AUTH_ISSUER}/oauth/authorize?${params}`)
}

export async function handleCallback(): Promise<void> {
  const url = new URL(window.location.href)
  const code = url.searchParams.get('code')
  const state = url.searchParams.get('state')
  const expected = sessionStorage.getItem(STATE_KEY)
  const verifier = sessionStorage.getItem(VERIFIER_KEY)

  if (!code || !state || !expected || state !== expected || !verifier) {
    throw new Error('Ugyldig OAuth-callback')
  }

  const body = new URLSearchParams({
    grant_type: 'authorization_code',
    code,
    redirect_uri: REDIRECT_URI,
    client_id: CLIENT_ID,
    code_verifier: verifier,
  })

  const res = await fetch(`${AUTH_ISSUER}/oauth/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body,
  })

  if (!res.ok) {
    throw new Error(`Token-udveksling fejlede (${res.status})`)
  }

  const data = await res.json()
  storeTokens(data.access_token, data.refresh_token)
  sessionStorage.removeItem(VERIFIER_KEY)
  sessionStorage.removeItem(STATE_KEY)
}

export async function refreshAccessToken(): Promise<string | null> {
  const refresh = getRefreshToken()
  if (!refresh) return null

  const body = new URLSearchParams({
    grant_type: 'refresh_token',
    refresh_token: refresh,
    client_id: CLIENT_ID,
  })

  const res = await fetch(`${AUTH_ISSUER}/oauth/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body,
  })

  if (!res.ok) {
    clearTokens()
    return null
  }

  const data = await res.json()
  storeTokens(data.access_token, data.refresh_token ?? refresh)
  return data.access_token as string
}

export function logoutLocal(): void {
  clearTokens()
  window.location.assign('/login')
}
