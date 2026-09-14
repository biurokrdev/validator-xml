import { defineConfig, devices } from '@playwright/test';

/**
 * Testy e2e panelu administracji numerami R.
 *
 * Wymagania:
 *  - API uruchomione osobno: `dotnet run --project ../Mass.Api` z Database:Provider=InMemory
 *    (appsettings.Development.json) lub na bazie testowej PostgreSQL. Adres: E2E_API_URL (domyślnie http://localhost:5080).
 *  - Front: Playwright sam uruchomi `ng serve` (webServer), chyba że już działa na E2E_BASE_URL.
 *
 * Testy generują unikalne przedziały numerów (losowy prefiks/serial), więc mogą działać
 * wielokrotnie na tej samej bazie bez czyszczenia danych.
 */
export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  expect: { timeout: 7_000 },
  fullyParallel: false, // wspólna pula w API; scenariusze zmieniają jej stan
  workers: 1,
  retries: process.env['CI'] ? 1 : 0,
  reporter: process.env['CI'] ? [['list'], ['html', { open: 'never' }]] : [['list']],
  use: {
    baseURL: process.env['E2E_BASE_URL'] ?? 'http://localhost:4200',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    locale: 'pl-PL',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'npx ng serve --port 4200',
    url: 'http://localhost:4200',
    reuseExistingServer: true,
    timeout: 120_000,
  },
});
