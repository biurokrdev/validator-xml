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
    finding({ code: 'DOC_ALT_CHUNK_PRESENT', severity: 'Warning', stage: 'Structure', pdfImpact: 'Likely' }),
    finding({ code: 'DOC_COMMENTS_PRESENT', severity: 'Info', stage: 'Structure' }),
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

  it('wczytuje raport i grupuje ustalenia etapami w kolejności plik → struktura', () => {
    analyzeWith();

    expect(component.report()?.verdict).toBe('NeedsRepair');
    expect(component.findingGroups().map((group) => group.stage)).toEqual(['File', 'Structure']);
    expect(component.stageCounts().Structure).toBe(2);
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
    expect(component.visibleFindings()).toHaveLength(3);
  });

  it('filtruje po wpływie na Worda i na PDF', () => {
    analyzeWith();

    component.setImpact('Word');
    expect(component.visibleFindings().map((item) => item.code)).toEqual(['ZIP_ENTRY_CRC_MISMATCH']);

    component.setImpact('Pdf');
    expect(component.visibleFindings().map((item) => item.code)).toEqual([
      'ZIP_ENTRY_CRC_MISMATCH',
      'DOC_ALT_CHUNK_PRESENT',
    ]);
    expect(component.pdfImpactCount()).toBe(2);
  });

  it('filtruje po etapie i czyści wszystkie filtry naraz', () => {
    analyzeWith();

    component.setStage('Structure');
    component.setSeverity('Info');
    expect(component.visibleFindings().map((item) => item.code)).toEqual(['DOC_COMMENTS_PRESENT']);

    component.clearFilters();
    expect(component.hasFilters()).toBe(false);
    expect(component.visibleFindings()).toHaveLength(3);
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
