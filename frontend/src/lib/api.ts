// HTTP-клиент API. Все изменяющие запросы несут заголовок X-Scada-Request (защита от CSRF).

export class ApiError extends Error {
  status: number;
  details?: unknown;
  constructor(status: number, message: string, details?: unknown) {
    super(message);
    this.status = status;
    this.details = details;
  }
}

let onUnauthorized: (() => void) | null = null;
export function setUnauthorizedHandler(fn: () => void) {
  onUnauthorized = fn;
}

async function request<T>(method: string, url: string, body?: unknown, raw = false): Promise<T> {
  const headers: Record<string, string> = { 'X-Scada-Request': '1' };
  let payload: BodyInit | undefined;
  if (body !== undefined) {
    if (typeof body === 'string') {
      payload = body;
      headers['Content-Type'] = 'text/plain; charset=utf-8';
    } else {
      payload = JSON.stringify(body);
      headers['Content-Type'] = 'application/json';
    }
  }
  const res = await fetch(url, { method, headers, body: payload, credentials: 'same-origin' });
  if (res.status === 401 && !url.endsWith('/auth/login') && !url.endsWith('/auth/me')) onUnauthorized?.();

  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    let details: unknown;
    try {
      details = await res.json();
      const d = details as { detail?: string; title?: string; errors?: string[] };
      message = d.detail || d.title || message;
      if (Array.isArray(d.errors) && d.errors.length && typeof d.errors[0] === 'string') message = d.errors.join('\n');
    } catch {
      /* тело не JSON */
    }
    throw new ApiError(res.status, message, details);
  }
  if (raw) return res as unknown as T;
  if (res.status === 204 || res.headers.get('content-length') === '0') return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const api = {
  get: <T>(url: string) => request<T>('GET', url),
  post: <T>(url: string, body?: unknown) => request<T>('POST', url, body ?? {}),
  put: <T>(url: string, body?: unknown) => request<T>('PUT', url, body ?? {}),
  del: <T>(url: string) => request<T>('DELETE', url),
};

export function qs(params: Record<string, string | number | boolean | null | undefined>): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== null && v !== undefined && v !== '') p.set(k, String(v));
  const s = p.toString();
  return s ? `?${s}` : '';
}

export function errorText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}
