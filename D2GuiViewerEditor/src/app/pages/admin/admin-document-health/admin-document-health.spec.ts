import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AdminDocumentHealthComponent } from './admin-document-health';
import {
  DocumentHealthReport,
  DocumentHealthService,
  HealthFinding,
} from '../../../services/document-health.service';

/**
 * „Kondycja dokumentu": raport ma dać się przefiltrować po poziomie, etapie i wpływie (Word/PDF),
 * flaga prób konwersji musi dotrzeć do API, a błąd backendu ma pokazać komunikat, nie pustą stronę.
 */
function finding(overrides: Partial<HealthFinding>): HealthFinding {
  return {
    code: 'X',
    severity: 'Info',
    stage: 'Structure',
    title: 'tytuł',
    description: 'opis',
    location: null,
    wordImpact: 'None',
    pdfImpact: 'None',
    remedy: null,
    appSupport: 'Unknown',
    appNote: null,
    ...overrides,
  };
}

const report: DocumentHealthReport = {
  fileName: 'test.docx',
  fileSizeInBytes: 2048,
  detectedFormat: 'docx',
  mainDocumentPartPath: 'word/document.xml',
  verdict: 'NeedsRepair',
  verdictSummary: 'Word zażąda naprawy.',
  wordOpen: 'Repair',
  wordOpenSummary: 'Naprawa.',
  pdfConversion: 'Likely',
  pdfConversionSummary: 'Prawdopodobny błąd.',
  errorCount: 1,
  warningCount: 1,
  infoCount: 1,
  findingsTruncated: false,
  findings: [
    finding({ code: 'ZIP_ENTRY_CRC_MISMATCH', severity: 'Error', stage: 'File', wordImpact: 'CannotOpen', pdfImpact: 'Blocking' }),
    finding({
      code: 'DOC_ALT_CHUNK_PRESENT', severity: 'Warning', stage: 'Structure', pdfImpact: 'Likely',
      appSupport: 'Unsupported', appNote: 'Reader nie obsługuje w:altChunk.',
    }),
    finding({ code: 'DOC_COMMENTS_PRESENT', severity: 'Info', stage: 'Structure' }),
    finding({ code: 'APP_FEATURE_UNSUPPORTED', severity: 'Warning', stage: 'Application', pdfImpact: 'Possible', appSupport: 'Unsupported', appNote: 'n' }),
  ],
  probes: [
    { id: 'sdk-open', name: 'SDK', description: 'd', status: 'Passed', durationMs: 12, message: 'ok', details: null },
    { id: 'pdf-conversion', name: 'PDF', description: 'd', status: 'Failed', durationMs: 1500, message: '503', details: 'stack' },
  ],
  statistics: {
    packageEntries: 10, xmlParts: 6, imageParts: 1, imageBytes: 1024, elements: 100, paragraphs: 5, tables: 1,
    maxTableNesting: 1, drawings: 1, fields: 0, sections: 1, footnotes: 0, endnotes: 0, comments: 2,
    trackedRevisions: 0, contentControls: 0, altChunks: 1, embeddedFonts: 0,
  },
  coverage: [
    {
      featureKey: 'tables', label: 'Tabele', sourceCount: 1, sampleLocation: null, reader: 'Full', editor: 'Full', writer: 'Full',
      roundTripCount: 1, roundTrip: 'Preserved', status: 'Supported', note: 'ok', codePointer: null,
    },
    {
      featureKey: 'alt-chunk', label: 'Treść dołączona przez w:altChunk', sourceCount: 1, sampleLocation: 'word/document.xml:1 — /w:altChunk[1]',
      reader: 'Unsupported', editor: 'Unsupported', writer: 'Unsupported', roundTripCount: 0, roundTrip: 'Lost', status: 'Unsupported',
      note: 'Brak gałęzi AltChunk.', codePointer: 'DocxToHtmlConverter',
    },
  ],
  coverageSummary: '2 konstrukcji w dokumencie: 1 obsługiwanych w pełni; 1 nieobsługiwanych (Treść dołączona przez w:altChunk).',
  analyzedAtUtc: '2026-09-23T10:00:00Z',
  durationMs: 2000,
};

describe('AdminDocumentHealthComponent', () => {
  let fixture: ComponentFixture<AdminDocumentHealthComponent>;
  let component: AdminDocumentHealthComponent;
  let analyzeCalls: { name: string; probes: boolean }[];
  let api: { analyze: (file: File, probes: boolean) => unknown };

  beforeEach(async () => {
    analyzeCalls = [];
    api = {
      analyze: (file: File, probes: boolean) => {
        analyzeCalls.push({ name: file.name, probes });
        return of(report);
      },
    };

    await TestBed.configureTestingModule({
      imports: [AdminDocumentHealthComponent],
      providers: [provideRouter([]), { provide: DocumentHealthService, useValue: api }],
    }).compileComponents();

    fixture = TestBed.createComponent(AdminDocumentHealthComponent);
    component = fixture.componentInstance;
  });

  function analyzeWith(file = new File(['x'], 'test.docx')): void {
    component.onFileSelected({ target: { files: { item: () => file }, value: '' } } as unknown as Event);
    component.analyze();
  }

  it('wczytuje raport i grupuje ustalenia etapami w kolejności plik → struktura → nasza implementacja', () => {
    analyzeWith();

    expect(component.report()?.verdict).toBe('NeedsRepair');
    expect(component.findingGroups().map((group) => group.stage)).toEqual(['File', 'Structure', 'Application']);
    expect(component.stageCounts().Structure).toBe(2);
    expect(component.stageCounts().Application).toBe(1);
    expect(component.appWarningCount()).toBe(1);
  });

  it('pokrycie domyślnie pokazuje tylko luki, przełącznik odsłania konstrukcje obsługiwane', () => {
    analyzeWith();

    expect(component.visibleCoverage().map((item) => item.featureKey)).toEqual(['alt-chunk']);
    expect(component.roundTripRan()).toBe(true);

    component.setCoverageFilter('All');
    expect(component.visibleCoverage()).toHaveLength(2);
    expect(component.roundTripLabel(component.visibleCoverage()[1])).toContain('UTRACONE');
    expect(component.coverageStatusLabel('UnexpectedLoss')).toBe('nieoczekiwana utrata');
  });

  it('kopiuje notatkę dla programisty jako Markdown z lukami i notatką „u nas”', async () => {
    analyzeWith();
    const written: string[] = [];
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText: (text: string) => { written.push(text); return Promise.resolve(); } },
      configurable: true,
    });

    await component.copyDeveloperNote();

    expect(component.copiedNote()).toBe(true);
    expect(written[0]).toContain('# Kondycja dokumentu — notatka dla programisty');
    expect(written[0]).toContain('**Treść dołączona przez w:altChunk** `alt-chunk`');
    expect(written[0]).toContain('U nas: Reader nie obsługuje w:altChunk.');
    expect(written[0]).not.toContain('**Tabele**');
  });

  it('etykieta „u nas” istnieje tylko dla znanych poziomów obsługi', () => {
    expect(component.appSupportLabel('Unknown')).toBeNull();
    expect(component.appSupportLabel('Unsupported')).toBe('U nas: brak obsługi');
    expect(component.supportLevelLabel('PassThrough')).toBe('pass-through');
  });

  it('przekazuje do API flagę prób konwersji', () => {
    component.includeProbes.set(false);

    analyzeWith();

    expect(analyzeCalls).toEqual([{ name: 'test.docx', probes: false }]);
  });

  it('nie odrzuca pliku o innym rozszerzeniu — format rozpoznaje backend', () => {
    analyzeWith(new File(['x'], 'udaje-docx.html'));

    expect(analyzeCalls).toHaveLength(1);
    expect(component.error()).toBeNull();
  });

  it('filtruje po poziomie i ponowny klik zdejmuje filtr', () => {
    analyzeWith();

    component.setSeverity('Error');
    expect(component.visibleFindings().map((item) => item.code)).toEqual(['ZIP_ENTRY_CRC_MISMATCH']);

    component.setSeverity('Error');
    expect(component.visibleFindings()).toHaveLength(4);
  });

  it('filtruje po wpływie na Worda i na PDF', () => {
    analyzeWith();

    component.setImpact('Word');
    expect(component.visibleFindings().map((item) => item.code)).toEqual(['ZIP_ENTRY_CRC_MISMATCH']);

    component.setImpact('Pdf');
    expect(component.visibleFindings().map((item) => item.code)).toEqual([
      'ZIP_ENTRY_CRC_MISMATCH',
      'DOC_ALT_CHUNK_PRESENT',
      'APP_FEATURE_UNSUPPORTED',
    ]);
    expect(component.pdfImpactCount()).toBe(3);
  });

  it('filtruje po etapie i czyści wszystkie filtry naraz', () => {
    analyzeWith();

    component.setStage('Structure');
    component.setSeverity('Info');
    expect(component.visibleFindings().map((item) => item.code)).toEqual(['DOC_COMMENTS_PRESENT']);

    component.clearFilters();
    expect(component.hasFilters()).toBe(false);
    expect(component.visibleFindings()).toHaveLength(4);
  });

  it('rozwija i zwija szczegóły próby', () => {
    analyzeWith();
    const probe = report.probes[1];

    component.toggleProbe(probe);
    expect(component.isProbeExpanded(probe)).toBe(true);

    component.toggleProbe(probe);
    expect(component.isProbeExpanded(probe)).toBe(false);
  });

  it('kopiuje pełny raport jako JSON do schowka', async () => {
    analyzeWith();
    const written: string[] = [];
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText: (text: string) => { written.push(text); return Promise.resolve(); } },
      configurable: true,
    });

    await component.copyReport();

    expect(component.copied()).toBe(true);
    expect(JSON.parse(written[0]).fileName).toBe('test.docx');
  });

  it('pokazuje komunikat backendu przy błędzie analizy', () => {
    api.analyze = () =>
      throwError(() => new HttpErrorResponse({ status: 400, error: { error: 'Plik przekracza limit 26214400 bajtów.' } }));

    analyzeWith();

    expect(component.report()).toBeNull();
    expect(component.error()).toContain('przekracza limit');
  });

  it('tłumaczy werdykty i wpływy na etykiety', () => {
    expect(component.verdictLabel('Corrupt')).toBe('Plik uszkodzony');
    expect(component.wordOpenLabel('Repair')).toBe('Zażąda naprawy');
    expect(component.pdfLabel('Blocked')).toBe('Zablokowana');
    expect(component.wordImpactLabel('None')).toBeNull();
    expect(component.pdfImpactLabel('Likely')).toContain('prawdopodobny');
    expect(component.formatDuration(1500)).toBe('1.5 s');
  });
});
