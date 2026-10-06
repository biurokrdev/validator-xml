import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { LetterInspection } from '../../core/models';
import { LetterCheck } from './letter-check';

const URL = '/api/letter-inspections';

function inspection(overrides: Partial<LetterInspection> = {}): LetterInspection {
  return {
    fileName: 'pismo.pdf',
    envelope: 'SingleWindow',
    isValid: true,
    isReadable: true,
    summary: 'okno adresata: OK',
    page: { number: 1, count: 1, widthMm: 210, heightMm: 297 },
    windows: [
      {
        kind: 'Recipient',
        name: 'okno adresata',
        found: true,
        isValid: true,
        area: { left: 119, top: 54, width: 71, height: 30 },
        clearanceMm: 1,
        content: 'Address',
        label: null,
        address: {
          lines: ['Pan Jan Kowalski', 'ul. Polna 1', '00-061 Warszawa'],
          textBounds: { left: 125, top: 60, width: 40, height: 12 },
          minFontSizePt: 10,
          maxFontSizePt: 10,
        },
        overflow: null,
        findings: [],
      },
    ],
    documentFindings: [],
    preview: null,
    ...overrides,
  };
}

describe('LetterCheck', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [LetterCheck],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function render() {
    const fixture = TestBed.createComponent(LetterCheck);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const q = (testId: string) => el.querySelector<HTMLElement>(`[data-testid="${testId}"]`);

    const select = (file: File) => {
      const input = q('file-input') as HTMLInputElement;
      Object.defineProperty(input, 'files', { value: [file], configurable: true });
      input.dispatchEvent(new Event('change'));
      fixture.detectChanges();
    };

    return { fixture, q, select };
  }

  const pdf = (name = 'pismo.pdf') => new File(['%PDF-1.7'], name, { type: 'application/pdf' });

  it('sends the selected PDF to the API and shows a positive verdict', () => {
    const { fixture, q, select } = render();

    select(pdf());

    const req = http.expectOne(URL);
    expect(req.request.method).toBe('POST');
    const body = req.request.body as FormData;
    expect((body.get('file') as File).name).toBe('pismo.pdf');
    expect(body.get('envelope')).toBe('SingleWindow');
    expect(q('check-progress')).toBeTruthy();

    req.flush(inspection());
    fixture.detectChanges();

    expect(q('verdict')?.getAttribute('data-valid')).toBe('true');
    expect(q('verdict')?.textContent).toContain('Pismo nadaje się do koperty.');
    expect(q('address')?.textContent).toContain('00-061 Warszawa');
    expect(q('check-progress')).toBeNull();
  });

  it('shows findings and overflow for an invalid letter', () => {
    const { fixture, q, select } = render();
    select(pdf());

    const base = inspection();
    http.expectOne(URL).flush(
      inspection({
        isValid: false,
        summary: 'okno adresata: błąd (wystaje z prawej o 9,7 mm)',
        windows: [
          {
            ...base.windows[0],
            isValid: false,
            overflow: { leftMm: 0, topMm: 0, rightMm: 9.7, bottomMm: 0, description: 'z prawej o 9,7 mm' },
            findings: [{ code: 'ADDRESS_OUTSIDE_WINDOW', severity: 'Error', message: 'Adres wystaje poza okno.' }],
          },
        ],
      }),
    );
    fixture.detectChanges();

    expect(q('verdict')?.getAttribute('data-valid')).toBe('false');
    expect(q('overflow')?.textContent).toContain('z prawej o 9,7 mm');
    expect(q('window-Recipient')?.querySelector('[data-code="ADDRESS_OUTSIDE_WINDOW"]')).toBeTruthy();
  });

  it('shows the registered label window without address data', () => {
    const { fixture, q, select } = render();
    select(pdf());

    const base = inspection();
    http.expectOne(URL).flush(
      inspection({
        envelope: 'DoubleWindow',
        windows: [
          base.windows[0],
          {
            kind: 'Sender',
            name: 'okno nadawcy',
            found: true,
            isValid: true,
            area: { left: 28, top: 64, width: 51, height: 15 },
            clearanceMm: 1,
            content: 'RegisteredLabel',
            address: null,
            label: { bounds: { left: 29.5, top: 65.5, width: 48, height: 12 }, positionEstimated: false },
            overflow: null,
            findings: [],
          },
        ],
      }),
    );
    fixture.detectChanges();

    const labelWindow = q('window-Sender')!;
    expect(labelWindow.querySelector('[data-testid="label-info"]')?.textContent).toContain('48 × 12 mm');
    expect(labelWindow.querySelector('[data-testid="address"]')).toBeNull();
    expect(labelWindow.querySelector('.badge.ok')).toBeTruthy();
  });

  it('says the registered label is missing', () => {
    const { fixture, q, select } = render();
    select(pdf());

    const base = inspection();
    http.expectOne(URL).flush(
      inspection({
        envelope: 'DoubleWindow',
        isValid: false,
        windows: [
          base.windows[0],
          {
            kind: 'Sender',
            name: 'okno nadawcy',
            found: false,
            isValid: false,
            area: { left: 28, top: 64, width: 51, height: 15 },
            clearanceMm: 1,
            content: 'RegisteredLabel',
            address: null,
            label: null,
            overflow: null,
            findings: [{ code: 'LABEL_NOT_FOUND', severity: 'Error', message: 'Nie znaleziono nalepki R.' }],
          },
        ],
      }),
    );
    fixture.detectChanges();

    expect(q('window-Sender')?.textContent).toContain('brak nalepki R');
    expect(q('window-Sender')?.querySelector('[data-code="LABEL_NOT_FOUND"]')).toBeTruthy();
  });

  it('rejects a non-PDF file without calling the API', () => {
    const { q, select } = render();

    select(new File(['abc'], 'notatka.docx', { type: 'application/msword' }));

    http.expectNone(URL);
    expect(q('check-error')?.textContent).toContain('nie jest plikiem PDF');
    expect(q('file-name')).toBeNull();
  });

  it('re-checks the loaded file when the envelope type changes', () => {
    const { fixture, q, select } = render();
    select(pdf());
    http.expectOne(URL).flush(inspection());
    fixture.detectChanges();

    (q('envelope-DoubleWindow') as HTMLInputElement).dispatchEvent(new Event('change'));
    fixture.detectChanges();

    const req = http.expectOne(URL);
    expect((req.request.body as FormData).get('envelope')).toBe('DoubleWindow');
    expect(q('verdict')).toBeNull();
    req.flush(inspection({ envelope: 'DoubleWindow' }));
  });

  it('shows the API error message', () => {
    const { fixture, q, select } = render();
    select(pdf());

    http
      .expectOne(URL)
      .flush(
        { title: 'Nie można przyjąć pliku.', detail: 'Plik nie jest dokumentem PDF.', status: 400 },
        { status: 400, statusText: 'Bad Request' },
      );
    fixture.detectChanges();

    expect(q('check-error')?.textContent).toContain('Plik nie jest dokumentem PDF.');
    expect(q('verdict')).toBeNull();
  });
});
