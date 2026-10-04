import { expect, test } from '@playwright/test';
import { apiChangeState, apiGet, apiHealth, apiImport } from './helpers/api';
import { domesticRange, internationalRange } from './helpers/numbers';

/**
 * E2E-LST: lista numerów, filtry, stronicowanie, statystyki.
 * E2E-STA: zmiana stanu numeru z poziomu listy.
 * Odpowiada przypadkom manualnym TM-LST-* i TM-STA-* w Mass/TESTS.md.
 */
test.describe('Lista numerów i zmiana stanu', () => {
  test.beforeAll(async ({ request }) => {
    await apiHealth(request);
  });

  test('E2E-LST-01 zaimportowane numery pojawiają się na liście po filtrze fragmentu', async ({ page, request }) => {
    const range = domesticRange(3);
    await apiImport(request, range.first, 3);

    await page.goto('/');
    await page.getByTestId('filter-search').fill(range.first.slice(0, 18)); // wspólny prefiks + początek seriala
    await page.getByTestId('filter-apply').click();

    const rows = page.getByTestId('numbers-table').locator('tbody tr[data-testid^="row-"]');
    await expect(rows).toHaveCount(3);
    await expect(page.getByTestId('list-summary')).toContainText('Znaleziono 3 numerów');
    await expect(page.getByTestId(`row-${range.numbers[0]}`)).toHaveAttribute('data-state', 'Available');
  });

  test('E2E-LST-02 filtr typu i stanu zawęża listę', async ({ page, request }) => {
    const dom = domesticRange(2);
    const intl = internationalRange(2);
    await apiImport(request, dom.first, 2);
    await apiImport(request, intl.first, 2);
    await apiChangeState(request, intl.numbers[0], 'Use');

    await page.goto('/');
    await page.getByTestId('filter-type').selectOption('International');
    await page.getByTestId('filter-state').selectOption('Used');
    await page.getByTestId('filter-search').fill(intl.numbers[0].slice(0, 9));
    await page.getByTestId('filter-apply').click();

    const rows = page.getByTestId('numbers-table').locator('tbody tr[data-testid^="row-"]');
    await expect(rows).toHaveCount(1);
    await expect(rows.first()).toContainText(intl.numbers[0]);
    await expect(rows.first().getByTestId('cell-state')).toHaveText('Użyty');

    await page.getByTestId('filter-reset').click();
    await expect(page.getByTestId('filter-search')).toHaveValue('');
  });

  test('E2E-LST-03 stronicowanie: 30 numerów przy 25 na stronie daje 2 strony', async ({ page, request }) => {
    const range = domesticRange(30);
    await apiImport(request, range.first, 30);

    await page.goto('/');
    await page.getByTestId('filter-search').fill(range.first.slice(0, 16));
    await page.getByTestId('filter-apply').click();

    await expect(page.getByTestId('pager-info')).toHaveText('1 / 2');
    await expect(page.getByTestId('pager-prev')).toBeDisabled();
    await page.getByTestId('pager-next').click();
    await expect(page.getByTestId('pager-info')).toHaveText('2 / 2');
    const rows = page.getByTestId('numbers-table').locator('tbody tr[data-testid^="row-"]');
    await expect(rows).toHaveCount(5);
    await expect(page.getByTestId('pager-next')).toBeDisabled();
  });

  test('E2E-LST-04 statystyki rosną po imporcie', async ({ page, request }) => {
    await page.goto('/');
    const stat = page.getByTestId('stat-Domestic');
    const before = Number(await stat.getByTestId('stat-available').textContent());

    const range = domesticRange(4);
    await apiImport(request, range.first, 4);
    await page.reload();

    await expect(stat.getByTestId('stat-available')).toHaveText(String(before + 4));
  });

  test('E2E-STA-01 Dostępny -> Zarezerwuj -> Oznacz jako użyty; użyty nie ma akcji', async ({ page, request }) => {
    const range = domesticRange(1);
    await apiImport(request, range.first, 1);
    const n = range.numbers[0];

    await page.goto('/');
    await page.getByTestId('editor-input').fill('tester-e2e');
    await page.getByTestId('filter-search').fill(n);
    await page.getByTestId('filter-apply').click();

    const row = page.getByTestId(`row-${n}`);
    await expect(row.getByTestId('action-Reserve')).toBeVisible();
    await row.getByTestId('action-Reserve').click();
    await expect(page.getByTestId('list-notice')).toContainText('Zarezerwowany');
    await expect(page.getByTestId(`row-${n}`)).toHaveAttribute('data-state', 'Reserved');

    await page.getByTestId(`row-${n}`).getByTestId('action-Use').click();
    await expect(page.getByTestId(`row-${n}`)).toHaveAttribute('data-state', 'Used');
    await expect(page.getByTestId(`row-${n}`).locator('button')).toHaveCount(0);
    await expect(page.getByTestId(`row-${n}`)).toContainText('tester-e2e');

    const { body } = await apiGet(request, n);
    expect(body.state).toBe('Used');
    expect(body.editor).toBe('tester-e2e');
  });

  test('E2E-STA-02 Zwolnij przywraca Dostępny', async ({ page, request }) => {
    const range = domesticRange(1);
    await apiImport(request, range.first, 1);
    const n = range.numbers[0];
    await apiChangeState(request, n, 'Reserve');

    await page.goto('/');
    await page.getByTestId('filter-search').fill(n);
    await page.getByTestId('filter-apply').click();

    await page.getByTestId(`row-${n}`).getByTestId('action-Release').click();
    await expect(page.getByTestId(`row-${n}`)).toHaveAttribute('data-state', 'Available');
  });

  test('E2E-STA-03 Anuluj wymaga potwierdzenia; po anulowaniu brak akcji', async ({ page, request }) => {
    const range = domesticRange(1);
    await apiImport(request, range.first, 1);
    const n = range.numbers[0];

    await page.goto('/');
    await page.getByTestId('filter-search').fill(n);
    await page.getByTestId('filter-apply').click();

    // odrzucenie potwierdzenia -> bez zmian
    page.once('dialog', (d) => d.dismiss());
    await page.getByTestId(`row-${n}`).getByTestId('action-Cancel').click();
    await expect(page.getByTestId(`row-${n}`)).toHaveAttribute('data-state', 'Available');

    // potwierdzenie -> Anulowany
    page.once('dialog', (d) => d.accept());
    await page.getByTestId(`row-${n}`).getByTestId('action-Cancel').click();
    await expect(page.getByTestId(`row-${n}`)).toHaveAttribute('data-state', 'Cancelled');
    await expect(page.getByTestId(`row-${n}`).locator('button')).toHaveCount(0);
  });

  test('E2E-STA-04 konflikt: numer zmieniony w tle daje komunikat 409 zamiast cichej zmiany', async ({ page, request }) => {
    const range = domesticRange(1);
    await apiImport(request, range.first, 1);
    const n = range.numbers[0];

    await page.goto('/');
    await page.getByTestId('filter-search').fill(n);
    await page.getByTestId('filter-apply').click();
    await expect(page.getByTestId(`row-${n}`).getByTestId('action-Reserve')).toBeVisible();

    // ktoś inny w międzyczasie użył numeru
    await apiChangeState(request, n, 'Use');

    await page.getByTestId(`row-${n}`).getByTestId('action-Reserve').click();
    await expect(page.getByTestId('list-error')).toContainText('niedozwolone');
  });

  test('E2E-STA-05 "Pobierz kolejny numer" rezerwuje najniższy dostępny numer typu', async ({ page, request }) => {
    await page.goto('/');
    const button = page.getByTestId('acquire-International');
    const intl = internationalRange(1);
    await apiImport(request, intl.first, 1);
    await page.reload();

    await expect(button).toBeEnabled();
    await button.click();
    await expect(page.getByTestId('list-notice')).toContainText('Pobrano i zarezerwowano numer');
    const notice = (await page.getByTestId('list-notice').textContent()) ?? '';
    const acquired = notice.match(/R[A-Z]\d{9}[A-Z]{2}/)?.[0];
    expect(acquired).toBeTruthy();

    const { body } = await apiGet(request, acquired!);
    expect(body.state).toBe('Reserved');
  });
});
