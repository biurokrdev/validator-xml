import { expect, test } from '@playwright/test';
import { apiChangeState, apiHealth, apiImport } from './helpers/api';
import { corruptCheckDigit, domesticRange, internationalRange } from './helpers/numbers';

/**
 * E2E-CHK: sprawdzenie pojedynczego numeru (format, cyfra kontrolna, obecność w puli, stan).
 * Odpowiada przypadkom TM-CHK-* w Mass/TESTS.md.
 */
test.describe('Sprawdzenie numeru', () => {
  test.beforeAll(async ({ request }) => {
    await apiHealth(request);
  });

  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.getByTestId('nav-check').click();
  });

  test('E2E-CHK-01 numer z puli pokazuje stan', async ({ page, request }) => {
    const range = internationalRange(1);
    await apiImport(request, range.first, 1);
    await apiChangeState(request, range.first, 'Use');

    await page.getByTestId('check-input').fill(range.first);
    await page.getByTestId('check-submit').click();

    await expect(page.getByTestId('check-in-pool')).toBeVisible();
    await expect(page.getByTestId('check-state')).toHaveText('Użyty');
    await expect(page.getByTestId('check-number-result')).toHaveAttribute('data-valid', 'true');
  });

  test('E2E-CHK-02 poprawny numer spoza puli daje ostrzeżenie', async ({ page }) => {
    await page.getByTestId('check-input').fill(domesticRange(1).first);
    await page.getByTestId('check-submit').click();

    await expect(page.getByTestId('check-not-in-pool')).toBeVisible();
    await expect(page.getByTestId('check-number-result')).toHaveAttribute('data-valid', 'false');
  });

  test('E2E-CHK-03 błędna cyfra kontrolna i zły format są odrzucane', async ({ page }) => {
    await page.getByTestId('check-input').fill(corruptCheckDigit(internationalRange(1).first));
    await page.getByTestId('check-submit').click();
    await expect(page.getByTestId('check-invalid')).toContainText('Błędna cyfra kontrolna S10');

    await page.getByTestId('check-input').fill('ABC123');
    await page.getByTestId('check-submit').click();
    await expect(page.getByTestId('check-invalid')).toContainText('Nieznany format');
  });

  test('E2E-CHK-04 zapis z nawiasem i spacjami jest normalizowany', async ({ page, request }) => {
    const range = domesticRange(1);
    await apiImport(request, range.first, 1);
    const n = range.first;
    const spaced = `(00) ${n[2]} ${n.slice(3, 10)} ${n[10]} ${n.slice(11, 19)} ${n[19]}`;

    await page.getByTestId('check-input').fill(spaced);
    await page.getByTestId('check-submit').click();

    await expect(page.getByTestId('check-in-pool')).toContainText(n);
  });
});
