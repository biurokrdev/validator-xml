import { expect, test } from '@playwright/test';
import { apiGet, apiHealth, apiImport } from './helpers/api';
import { corruptCheckDigit, domesticRange, internationalRange } from './helpers/numbers';

/**
 * E2E-IMP: zasilenie puli przedziałem numerów + weryfikacja duplikatów przed importem.
 * Odpowiada przypadkom manualnym TM-IMP-01..07 w Mass/TESTS.md.
 */
test.describe('Zasilenie puli przedziałem', () => {
  test.beforeAll(async ({ request }) => {
    await apiHealth(request);
  });

  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.getByTestId('nav-import').click();
    await expect(page.getByRole('heading', { name: 'Zasilenie puli przedziałem numerów' })).toBeVisible();
  });

  test('E2E-IMP-01 przycisk Zasil jest nieaktywny do czasu sprawdzenia przedziału', async ({ page }) => {
    await expect(page.getByTestId('import-submit')).toBeDisabled();
    await expect(page.getByTestId('import-check')).toBeDisabled(); // brak pierwszego numeru

    await page.getByTestId('import-first').fill(domesticRange(1).first);
    await expect(page.getByTestId('import-check')).toBeEnabled();
    await expect(page.getByTestId('import-submit')).toBeDisabled();
  });

  test('E2E-IMP-02 krajowy przedział (pierwszy numer + ilość): sprawdzenie bez duplikatów i zasilenie', async ({ page, request }) => {
    const range = domesticRange(5);

    await page.getByTestId('import-first').fill(range.first);
    await page.getByTestId('import-count').fill('5');
    await page.getByTestId('import-check').click();

    const result = page.getByTestId('check-result');
    await expect(result).toBeVisible();
    await expect(page.getByTestId('check-requested')).toHaveText('5');
    await expect(page.getByTestId('check-new')).toHaveText('5');
    await expect(page.getByTestId('check-existing')).toHaveText('0');
    await expect(page.getByTestId('check-ok')).toBeVisible();
    await expect(page.getByTestId('check-first')).toHaveText(range.numbers[0]);
    await expect(page.getByTestId('check-last')).toHaveText(range.numbers[4]);

    await page.getByTestId('import-submit').click();
    await expect(page.getByTestId('import-result')).toBeVisible();
    await expect(page.getByTestId('import-added')).toHaveText('5');
    await expect(page.getByTestId('import-skipped')).toHaveText('0');

    // backend: wszystkie numery są w puli i są dostępne
    for (const n of range.numbers) {
      const { status, body } = await apiGet(request, n);
      expect(status).toBe(200);
      expect(body.state).toBe('Available');
    }
  });

  test('E2E-IMP-03 zagraniczny przedział podany jako pierwszy i ostatni numer', async ({ page, request }) => {
    const range = internationalRange(3);

    await page.getByTestId('import-first').fill(range.first);
    await page.getByTestId('import-mode').selectOption('last');
    await page.getByTestId('import-last').fill(range.numbers[2]);
    await page.getByTestId('import-check').click();

    await expect(page.getByTestId('check-requested')).toHaveText('3');
    await page.getByTestId('import-submit').click();
    await expect(page.getByTestId('import-added')).toHaveText('3');

    const { body } = await apiGet(request, range.numbers[1]);
    expect(body.type).toBe('International');
  });

  test('E2E-IMP-04 weryfikacja duplikatów: część przedziału już w puli', async ({ page, request }) => {
    const range = domesticRange(7);
    await apiImport(request, range.first, 5); // 5 z 7 już mamy

    await page.getByTestId('import-first').fill(range.first);
    await page.getByTestId('import-count').fill('7');
    await page.getByTestId('import-check').click();

    await expect(page.getByTestId('check-new')).toHaveText('2');
    await expect(page.getByTestId('check-existing')).toHaveText('5');
    await expect(page.getByTestId('check-duplicated-some')).toBeVisible();

    const rows = page.getByTestId('check-existing-table').locator('tbody tr');
    await expect(rows).toHaveCount(5);
    await expect(rows.first()).toContainText(range.numbers[0]);
    await expect(rows.first()).toContainText('e2e-seed');

    await page.getByTestId('import-submit').click();
    await expect(page.getByTestId('import-added')).toHaveText('2');
    await expect(page.getByTestId('import-skipped')).toHaveText('5');
  });

  test('E2E-IMP-05 weryfikacja duplikatów: cały przedział już w puli blokuje zasilenie', async ({ page, request }) => {
    const range = domesticRange(3);
    await apiImport(request, range.first, 3);

    await page.getByTestId('import-first').fill(range.first);
    await page.getByTestId('import-count').fill('3');
    await page.getByTestId('import-check').click();

    await expect(page.getByTestId('check-duplicated-all')).toBeVisible();
    await expect(page.getByTestId('check-new')).toHaveText('0');
    await expect(page.getByTestId('import-submit')).toBeDisabled();
  });

  test('E2E-IMP-06 błędna cyfra kontrolna pierwszego numeru daje czytelny błąd', async ({ page }) => {
    await page.getByTestId('import-first').fill(corruptCheckDigit(domesticRange(1).first));
    await page.getByTestId('import-count').fill('10');
    await page.getByTestId('import-check').click();

    await expect(page.getByTestId('import-error')).toContainText('Błędna cyfra kontrolna');
    await expect(page.getByTestId('check-result')).toHaveCount(0);
    await expect(page.getByTestId('import-submit')).toBeDisabled();
  });

  test('E2E-IMP-07 zmiana przedziału po sprawdzeniu unieważnia wynik i blokuje Zasil', async ({ page }) => {
    const range = domesticRange(2);
    await page.getByTestId('import-first').fill(range.first);
    await page.getByTestId('import-count').fill('2');
    await page.getByTestId('import-check').click();
    await expect(page.getByTestId('import-submit')).toBeEnabled();

    await page.getByTestId('import-count').fill('3');
    await expect(page.getByTestId('import-submit')).toBeDisabled();
    await expect(page.getByText('Przedział zmienił się po sprawdzeniu')).toBeVisible();
  });

  test('E2E-IMP-08 przedział z różnych pul (pierwszy krajowy, ostatni zagraniczny) jest odrzucany', async ({ page }) => {
    await page.getByTestId('import-first').fill(domesticRange(1).first);
    await page.getByTestId('import-mode').selectOption('last');
    await page.getByTestId('import-last').fill(internationalRange(1).first);
    await page.getByTestId('import-check').click();

    await expect(page.getByTestId('import-error')).toContainText('różnych pul');
  });
});
