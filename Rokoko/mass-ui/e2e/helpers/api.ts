import { APIRequestContext, expect } from '@playwright/test';

export const API_URL = process.env['E2E_API_URL'] ?? 'http://localhost:5080';
const BASE = `${API_URL}/api/registered-numbers`;

/** Bezpośrednie wywołania API do przygotowania danych (arrange) i asercji stanu backendu. */
export async function apiImport(request: APIRequestContext, firstNumber: string, count: number, editor = 'e2e-seed') {
  const res = await request.post(`${BASE}/ranges/import`, {
    data: { firstNumber, count },
    headers: { 'X-Editor': editor },
  });
  expect(res.ok(), `import ${firstNumber} x${count}: ${res.status()}`).toBeTruthy();
  return res.json() as Promise<{ added: number; skipped: number; requested: number }>;
}

export async function apiChangeState(request: APIRequestContext, fullNumber: string, action: string, editor = 'e2e-seed') {
  const res = await request.post(`${BASE}/${fullNumber}/state`, {
    data: { action },
    headers: { 'X-Editor': editor },
  });
  expect(res.ok(), `state ${fullNumber} ${action}: ${res.status()}`).toBeTruthy();
  return res.json();
}

export async function apiGet(request: APIRequestContext, fullNumber: string) {
  const res = await request.get(`${BASE}/${fullNumber}`);
  return { status: res.status(), body: res.ok() ? await res.json() : await res.json().catch(() => null) };
}

export async function apiHealth(request: APIRequestContext) {
  const res = await request.get(`${API_URL}/health`);
  expect(res.ok(), `API niedostępne pod ${API_URL} - uruchom Mass.Api`).toBeTruthy();
  return res.json() as Promise<{ status: string; database: string }>;
}
