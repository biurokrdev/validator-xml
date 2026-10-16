import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ElementResult, LetterInspection } from '../../core/models';
import { LetterCheck } from './letter-check';

const UPLOAD = '/api/letter-inspections/upload';
const ORDER = '/api/letter-inspections/from-recipient';
const FORM = '/api/letter-inspections/from-form';

function element(overrides: Partial<ElementResult> = {}): ElementResult {
  return {
    status: 'Valid',
    windowName: 'okno adresata',
    windowArea: { left: 119, top: 54, width: 71, height: 30 },
    found: true,
    address: {
      lines: ['Pan Jan Kowalski', 'ul. Polna 1', '00-061 Warszawa'],
      textBounds: { left: 125, top: 60, width: 40, height: 12 },
      minFontSizePt: 10,
      maxFontSizePt: 10,
    },
    label: null,
    overflow: null,
    recipientMatch: 'NotChecked',
    reasons: [],
    ...overrides,
  };
}

function inspection(overrides: Partial<LetterInspection> = {}): LetterInspection {
  return {
    status: 'Valid',
    summary: 'okno adresata: OK',
    envelope: 'SingleWindow',
    document: { fileName: 'pismo.pdf', sourceFormat: 'Pdf', convertedToPdf: false, source: 'Upload' },
    profile: { name: 'C65, dwa okienka', confirmed: false, note: 'Profil demonstracyjny.' },
    page: { number: 1, count: 1, widthMm: 210, heightMm: 297 },
    address: element(),
    label: null,
    documentReasons: [],
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
    const q = <T extends HTMLElement = HTMLElement>(testId: string) => el.querySelector<T>(`[data-testid="${testId}"]`);
    const change = (testId: string) => {
      q(testId)!.dispatchEvent(new Event('change'));
      fixture.detectChanges();
    };
    const selectFile = (file: File) => {
      const input = q<HTMLInputElement>('file-input')!;
      Object.defineProperty(input, 'files', { value: [file], configurable: true });
      input.dispatchEvent(new Event('change'));
      fixture.detectChanges();
    };
    const type = (testId: string, value: string) => {
      const input = q<HTMLInputElement | HTMLTextAreaElement>(testId)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    };
    return { fixture, q, change, selectFile, type };
  }

  const pdf = (name = 'pismo.pdf') => new File(['%PDF-1.7'], name, { type: 'application/pdf' });

  it('starts with a single-window envelope and file upload selected', () => {
    const { q } = render();
    expect(q<HTMLInputElement>('envelope-SingleWindow')!.checked).toBe(true);
    expect(q<HTMLInputElement>('variant-upload')!.checked).toBe(true);
    expect(q('file-input')).toBeTruthy();
    expect(q('result')).toBeNull();
  });

  it('clears the form back to the defaults', () => {
    const { fixture, q, change, type } = render();
    change('variant-order');
    change('envelope-DoubleWindow');
    type('recipient', 'Pan Jan Kowalski\n00-061 Warszawa');
    type('registered-number', 'RR473124829PL');

    q('clear-button')!.click();
    fixture.detectChanges();
    expect(q<HTMLInputElement>('variant-upload')!.checked).toBe(true);
    expect(q<HTMLInputElement>('envelope-SingleWindow')!.checked).toBe(true);
    expect(q('recipient')).toBeNull();
  });

  it('uploads a PDF right after selection and shows the verdict', () => {
    const { fixture, q, change, selectFile } = render();
    change('envelope-SingleWindow');

    selectFile(pdf());

    expect(q('stage')?.textContent).toContain('Sprawdzanie dokumentu');
    const req = http.expectOne(UPLOAD);
    expect(req.request.method).toBe('POST');
    const body = req.request.body as FormData;
    expect((body.get('file') as File).name).toBe('pismo.pdf');
    expect(body.get('envelope')).toBe('SingleWindow');

    req.flush(inspection());
    fixture.detectChanges();

    expect(q('stage')).toBeNull();
    expect(q('result')?.getAttribute('data-status')).toBe('Valid');
    expect(q('result')?.textContent).toContain('Wynik: poprawny');
    expect(q('element-address')?.textContent).toContain('zgodności z konkretnym odbiorcą nie sprawdzano');
    expect(q('element-label')).toBeNull();

    // The form is hidden while the result is shown and comes back on "Weryfikuj kolejny".
    expect(q('file-input')).toBeNull();
    q('next-button')!.click();
    fixture.detectChanges();
    expect(q('result')).toBeNull();
    expect(q('file-input')).toBeTruthy();
    expect(q<HTMLInputElement>('envelope-SingleWindow')!.checked).toBe(true);
  });

  it('announces conversion for a DOCX upload and shows the converted document', () => {
    const { fixture, q, change, selectFile } = render();
    change('envelope-DoubleWindow');

    selectFile(new File(['PK'], 'pismo.docx'));

    expect(q('stage')?.textContent).toContain('Konwersja dokumentu');
    http.expectOne(UPLOAD).flush(
      inspection({
        envelope: 'DoubleWindow',
        document: { fileName: 'pismo.docx', sourceFormat: 'Docx', convertedToPdf: true, source: 'Upload' },
        label: element({
          windowName: 'okno nadawcy',
          address: null,
          label: { bounds: { left: 29.5, top: 65.5, width: 48, height: 12 }, widthPx: 1134, heightPx: 283, number: '00759007731512000621', humanReadableNumber: '(00)75900773 1 51200062 1', kind: 'Domestic', numberIsValid: true, symbology: 'GS1-128', barcodeModulePx: 5 },
        }),
      }),
    );
    fixture.detectChanges();

    expect(q('result')?.textContent).toContain('DOCX skonwertowany do PDF');
    expect(q('label-number')?.textContent).toContain('(00)75900773 1 51200062 1');
  });

  it('shows invalid and inconclusive verdicts with their reasons', () => {
    const { fixture, q, change, selectFile } = render();
    change('envelope-DoubleWindow');
    selectFile(pdf());
    http.expectOne(UPLOAD).flush(
      inspection({
        status: 'Invalid',
        envelope: 'DoubleWindow',
        address: element({ status: 'Invalid', overflow: { leftMm: 0, topMm: 0, rightMm: 9.7, bottomMm: 0, description: 'z prawej o 9,7 mm' }, reasons: [{ code: 'ADDRESS_OUTSIDE_WINDOW', severity: 'Error', message: 'Adres wystaje.' }] }),
        label: element({ status: 'Inconclusive', windowName: 'okno nadawcy', address: null, label: { bounds: { left: 29.5, top: 65.5, width: 48, height: 12 }, widthPx: 100, heightPx: 20, number: null, humanReadableNumber: null, kind: null, numberIsValid: false, symbology: null, barcodeModulePx: null }, reasons: [{ code: 'LABEL_UNREADABLE', severity: 'Inconclusive', message: 'Nie da się odczytać.' }] }),
      }),
    );
    fixture.detectChanges();

    expect(q('result')?.textContent).toContain('Wynik: niepoprawny');
    expect(q('element-address')?.getAttribute('data-status')).toBe('Invalid');
    expect(q('overflow')?.textContent).toContain('z prawej o 9,7 mm');
    expect(q('element-label')?.textContent).toContain('nierozstrzygnięty');
    expect(q('element-label')?.querySelector('[data-code="LABEL_UNREADABLE"]')).toBeTruthy();
  });

  it('orders a document with recipient data; the R number is sent only for two windows', () => {
    const { fixture, q, change, type } = render();
    change('variant-order');
    change('envelope-DoubleWindow');
    expect(q('registered-number')).toBeTruthy();
    expect(q<HTMLButtonElement>('order-button')!.disabled).toBe(true);

    type('recipient', 'Pan Jan Kowalski\nul. Polna 1\n00-061 Warszawa');
    type('registered-number', 'RR473124829PL');
    expect(q<HTMLButtonElement>('order-button')!.disabled).toBe(false);

    q('order-button')!.click();
    fixture.detectChanges();
    expect(q('stage')?.textContent).toContain('Pobieranie dokumentu');
    expect(q('order-button')).toBeNull();

    const req = http.expectOne(ORDER);
    expect(req.request.body).toEqual({ envelope: 'DoubleWindow', source: null, recipientLines: ['Pan Jan Kowalski', 'ul. Polna 1', '00-061 Warszawa'], registeredNumber: 'RR473124829PL' });
    req.flush(inspection({ envelope: 'DoubleWindow', address: element({ recipientMatch: 'Matched' }), label: element({ address: null }) }));
    fixture.detectChanges();

    expect(q('element-address')?.textContent).toContain('zgodna z podanymi danymi odbiorcy');
    expect(q('order-button')).toBeNull();

    q('next-button')!.click();
    fixture.detectChanges();
    expect(q<HTMLInputElement>('variant-order')!.checked).toBe(true);
    expect(q<HTMLInputElement>('envelope-DoubleWindow')!.checked).toBe(true);
    expect(q<HTMLTextAreaElement>('recipient')!.value).toBe('');
    expect(q<HTMLInputElement>('registered-number')!.value).toBe('');
  });

  it('hides and drops the R number for a single-window envelope', () => {
    const { q, change, type } = render();
    change('variant-order');
    change('envelope-DoubleWindow');
    type('recipient', 'Pan Jan Kowalski\n00-061 Warszawa');
    type('registered-number', '00759007731512000621');

    change('envelope-SingleWindow');
    expect(q('registered-number')).toBeNull();

    q('order-button')!.click();
    const req = http.expectOne(ORDER);
    expect(req.request.body).toEqual({ envelope: 'SingleWindow', source: null, recipientLines: ['Pan Jan Kowalski', '00-061 Warszawa'], registeredNumber: null });
    req.flush(inspection());
  });

  it('checks a form by GUID; the button waits for a valid identifier', () => {
    const { fixture, q, change, type } = render();
    change('variant-form');
    change('envelope-SingleWindow');
    expect(q<HTMLButtonElement>('form-button')!.disabled).toBe(true);

    type('form-id', 'nie-guid');
    expect(q<HTMLButtonElement>('form-button')!.disabled).toBe(true);
    type('form-id', ' 3F2504E0-4F89-11D3-9A0C-0305E82C3301 ');
    expect(q<HTMLButtonElement>('form-button')!.disabled).toBe(false);

    q('form-button')!.click();
    fixture.detectChanges();
    expect(q('stage')?.textContent).toContain('formularza');

    const req = http.expectOne(FORM);
    expect(req.request.body).toEqual({ envelope: 'SingleWindow', formId: '3F2504E0-4F89-11D3-9A0C-0305E82C3301' });
    req.flush(inspection({ document: { fileName: 'formularz.pdf', sourceFormat: 'Pdf', convertedToPdf: false, source: 'FormMock' } }));
    fixture.detectChanges();

    expect(q('result')?.textContent).toContain('Wynik: poprawny');
    expect(q('result')?.textContent).toContain('formularza');
  });

  it('shows a technical error separately from a verdict', () => {
    const { fixture, q, change, selectFile } = render();
    change('envelope-SingleWindow');
    selectFile(new File(['PK'], 'pismo.docx'));

    http.expectOne(UPLOAD).flush({ title: 'Konwersja DOCX do PDF jest niedostępna.', detail: 'Brak konwertera.', status: 503 }, { status: 503, statusText: 'Service Unavailable' });
    fixture.detectChanges();

    expect(q('error')?.textContent).toContain('Konwersja DOCX do PDF jest niedostępna.');
    expect(q('result')).toBeNull();
    expect(q('file-input')).toBeNull();

    q('back-button')!.click();
    fixture.detectChanges();
    expect(q('error')).toBeNull();
    expect(q('file-input')).toBeTruthy();
  });

  it('rejects files that are neither PDF nor DOCX without calling the API', () => {
    const { q, change, selectFile } = render();
    change('envelope-SingleWindow');

    selectFile(new File(['abc'], 'notatka.txt', { type: 'text/plain' }));

    http.expectNone(UPLOAD);
    expect(q('error')?.textContent).toContain('nie jest plikiem PDF ani DOCX');
  });
});
