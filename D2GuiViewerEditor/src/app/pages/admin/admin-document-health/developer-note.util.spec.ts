import { describe, expect, it } from 'vitest';
import { buildDeveloperNote } from './developer-note.util';
import {
  DocumentHealthReport,
  HealthFinding,
  ImplementationCoverageItem,
} from '../../../services/document-health.service';

function coverage(overrides: Partial<ImplementationCoverageItem>): ImplementationCoverageItem {
  return {
    featureKey: 'tables',
    label: 'Tabele',
    sourceCount: 2,
    sampleLocation: 'word/document.xml:1 — /w:document[1]/w:body[1]/w:tbl[1]',
    reader: 'Full',
    editor: 'Full',
    writer: 'Full',
    roundTripCount: 2,
    roundTrip: 'Preserved',
    status: 'Supported',
    note: 'Pełny round-trip.',
    codePointer: 'DocxToHtmlConverter.AppendTableCellHtml',
    ...overrides,
  };
}

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
  fileName: 'umowa.docx',
  fileSizeInBytes: 4096,
  detectedFormat: 'docx',
  mainDocumentPartPath: 'word/document.xml',
  verdict: 'Warnings',
  verdictSummary: 'ok',
  wordOpen: 'Ok',
  wordOpenSummary: 'ok',
  pdfConversion: 'AtRisk',
  pdfConversionSummary: 'ok',
  errorCount: 0,
  warningCount: 2,
  infoCount: 1,
  findingsTruncated: false,
  findings: [
    finding({
      code: 'APP_ROUND_TRIP_LOSS',
      stage: 'Application',
      severity: 'Warning',
      title: 'Round-trip edytora gubi: Przypisy dolne',
      remedy: 'Zbadaj HtmlToDocxConverter.AddFootnotes',
    }),
    finding({
      code: 'DOC_ALT_CHUNK_PRESENT',
      severity: 'Warning',
      title: 'Treść osadzona przez w:altChunk',
      appSupport: 'Unsupported',
      appNote: 'Reader nie obsługuje w:altChunk.',
      location: 'word/document.xml:1 — /w:document[1]/w:body[1]/w:altChunk[1]',
    }),
    finding({ code: 'DOC_STYLE_MISSING', title: 'Styl', appSupport: 'Full', appNote: 'Fallback do stylu domyślnego.' }),
  ],
  probes: [
    { id: 'round-trip', name: 'Round-trip', description: 'd', status: 'Failed', durationMs: 5, message: 'InvalidOperationException: boom', details: 'stack\nline2' },
    { id: 'sdk-open', name: 'SDK', description: 'd', status: 'Passed', durationMs: 5, message: 'ok', details: null },
  ],
  statistics: {
    packageEntries: 1, xmlParts: 1, imageParts: 0, imageBytes: 0, elements: 1, paragraphs: 1, tables: 2, maxTableNesting: 1,
    drawings: 0, fields: 0, sections: 1, footnotes: 1, endnotes: 0, comments: 0, trackedRevisions: 0, contentControls: 0,
    altChunks: 1, embeddedFonts: 0,
  },
  coverage: [
    coverage({}),
    coverage({ featureKey: 'footnotes', label: 'Przypisy dolne', sourceCount: 1, roundTripCount: 0, roundTrip: 'Lost', status: 'UnexpectedLoss', note: 'Model boczny.', codePointer: 'HtmlToDocxConverter.AddFootnotes' }),
    coverage({ featureKey: 'alt-chunk', label: 'Treść dołączona przez w:altChunk', reader: 'Unsupported', editor: 'Unsupported', writer: 'Unsupported', roundTripCount: 0, roundTrip: 'Lost', status: 'Unsupported', note: 'Brak gałęzi AltChunk.', codePointer: null }),
    coverage({ featureKey: 'lists', label: 'Listy', writer: 'Partial', status: 'Partial', note: 'GAP-L8.', roundTripCount: null, roundTrip: 'NotVerified' }),
  ],
  coverageSummary: '4 konstrukcji: 1 obsługiwana; 1 częściowo; 1 nieobsługiwana; 1 NIEOCZEKIWANA strata.',
  analyzedAtUtc: '2026-09-26T10:00:00Z',
  durationMs: 10,
};

describe('buildDeveloperNote', () => {
  const note = buildDeveloperNote(report);

  it('zaczyna od nieoczekiwanych strat, potem braki, potem obsługa częściowa — bez konstrukcji obsługiwanych', () => {
    const loss = note.indexOf('NIEOCZEKIWANA UTRATA');
    const unsupported = note.indexOf('### brak obsługi');
    const partial = note.indexOf('### obsługa częściowa');

    expect(loss).toBeGreaterThan(-1);
    expect(unsupported).toBeGreaterThan(loss);
    expect(partial).toBeGreaterThan(unsupported);
    expect(note).not.toContain('**Tabele**');
  });

  it('podaje liczniki round-tripu, poziomy per komponent i wskaźnik do kodu', () => {
    expect(note).toContain('round-trip: 1 → 0 (Lost)');
    expect(note).toContain('reader pełna / edytor pełna / writer pełna');
    expect(note).toContain('Kod: HtmlToDocxConverter.AddFootnotes');
    expect(note).toContain('round-trip: nie uruchomiono');
  });

  it('wymienia ustalenia etapu Aplikacja z remedium oraz problemy pliku z notatką o naszym zachowaniu', () => {
    expect(note).toContain('**APP_ROUND_TRIP_LOSS** (Warning)');
    expect(note).toContain('Co zrobić: Zbadaj HtmlToDocxConverter.AddFootnotes');
    expect(note).toContain('**DOC_ALT_CHUNK_PRESENT** (Warning, obsługa: brak)');
    expect(note).toContain('U nas: Reader nie obsługuje w:altChunk.');
    expect(note).not.toContain('DOC_STYLE_MISSING');
  });

  it('cytuje nieudane próby ze szczegółami w bloku kodu, pomija udane', () => {
    expect(note).toContain('**Round-trip** [Failed]: InvalidOperationException: boom');
    expect(note).toContain('  stack');
    expect(note).not.toContain('**SDK**');
  });

  it('bez luk pisze to wprost', () => {
    const clean = buildDeveloperNote({ ...report, coverage: [coverage({})], findings: [], probes: [] });

    expect(clean).toContain('Brak — każda konstrukcja z rejestru użyta w dokumencie jest obsługiwana w pełni.');
    expect(clean).not.toContain('## Próby przetworzenia');
  });
});
