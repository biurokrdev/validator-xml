import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { AdminDocumentCompareComponent } from './admin-document-compare';
import {
  CompareOptions,
  DocumentComparisonReport,
  DocumentCompareService,
  DocumentDifference,
} from '../../../services/document-compare.service';

/**
 * „Porównanie dokumentów": dwa pliki muszą dotrzeć do API z flagami; wynik dzieli się na trzy kubełki
 * (do naprawy u nas / do sprawdzenia / nieistotne), w kubełku są przyczyny (grupy) od najpoważniejszego
 * skutku, każda rozwijana do stronicowanych wystąpień z podświetleniem zmienionych słów.
 */
function difference(overrides: Partial<DocumentDifference>): DocumentDifference {
  return {
    kind: 'TextChanged',
    category: 'Tekst',
    partPath: 'word/document.xml',
    leftPath: '/w:document[1]/w:body[1]/w:p[1]',
    rightPath: '/w:document[1]/w:body[1]/w:p[1]',
    leftLine: 3,
    rightLine: 3,
    name: null,
    leftValue: 'Umowa najmu',
    rightValue: 'Umowa sprzedaży',
    leftExcerpt: '<w:t>Umowa najmu</w:t>',
    rightExcerpt: '<w:t>Umowa sprzedaży</w:t>',
    excerptTruncated: false,
    leftContext: 'Umowa najmu',
    rightContext: 'Umowa sprzedaży',
    ...overrides,
  };
}

const report: DocumentComparisonReport = {
  left: { fileName: 'v1.docx', sizeInBytes: 1000, detectedFormat: 'docx', partCount: 9, notes: [] },
  right: { fileName: 'v2.docx', sizeInBytes: 1200, detectedFormat: 'docx', partCount: 10, notes: [] },
  ignoreRevisionIds: true,
  ignoreDocumentProperties: true,
  identical: false,
  totalDifferences: 4,
  truncated: false,
  ignoredAttributeCount: 4,
  countsByKind: { TextChanged: 1, AttributeValueChanged: 1, PartOnlyInRight: 1, ElementOnlyInLeft: 1 },
  countsByCategory: { Tekst: 1, 'Formatowanie znaku': 1, Style: 1, Ustawienia: 1 },
  parts: [
    { path: 'word/document.xml', status: 'Changed', isXml: true, differenceCount: 2, leftSize: 500, rightSize: 520, leftElementCount: 20, rightElementCount: 20, note: null },
    { path: 'word/styles.xml', status: 'OnlyInRight', isXml: true, differenceCount: 1, leftSize: null, rightSize: 300, leftElementCount: null, rightElementCount: null, note: null },
    { path: 'word/settings.xml', status: 'Changed', isXml: true, differenceCount: 1, leftSize: 900, rightSize: 700, leftElementCount: 30, rightElementCount: 28, note: null },
    { path: '_rels/.rels', status: 'Identical', isXml: true, differenceCount: 0, leftSize: 100, rightSize: 100, leftElementCount: null, rightElementCount: null, note: null },
  ],
  differences: [
    // do sprawdzenia: zmiana tekstu (heurystyka GUI: review)
    difference({}),
    // do sprawdzenia: w:val 24 → 28 (> 2 jednostki → review)
    difference({ kind: 'AttributeValueChanged', category: 'Formatowanie znaku', name: 'w:val', leftValue: '24', rightValue: '28', leftContext: null, rightContext: null }),
    // do naprawy u nas: część styles.xml tylko po zapisie (heurystyka: suspect → fix)
    difference({ kind: 'PartOnlyInRight', category: 'Style', partPath: 'word/styles.xml', leftPath: null, rightPath: null, leftValue: null, rightValue: '300 B', leftExcerpt: null, rightExcerpt: '<w:styles/>', leftContext: null, rightContext: null }),
    // do naprawy u nas z analizą backendu: w:compat zgubione przez writer, skutek inny PDF, wskaźnik do kodu
    difference({
      kind: 'ElementOnlyInLeft', category: 'Ustawienia', partPath: 'word/settings.xml',
      leftPath: '/w:settings[1]/w:compat[1]', rightPath: null, leftValue: null, rightValue: null,
      leftExcerpt: '<w:compat/>', rightExcerpt: null, leftContext: null, rightContext: null,
      analysis: { cause: 'PipelineRegenerated', impact: 'PdfDifference', explanation: 'Writer generuje settings.xml od nowa bez w:compat — Word otwiera plik w Trybie zgodności.', featureKey: 'settings', codePointer: 'HtmlToDocxConverter.PreserveSettings' },
    }),
  ],
  comparedAtUtc: '2026-09-23T10:00:00Z',
  durationMs: 120,
};

describe('AdminDocumentCompareComponent — kubełki i przyczyny', () => {
  let component: AdminDocumentCompareComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AdminDocumentCompareComponent],
      providers: [{ provide: DocumentCompareService, useValue: { compare: () => of(report) } }],
    }).compileComponents();

    component = TestBed.createComponent(AdminDocumentCompareComponent).componentInstance;
    component.onFileSelected('left', { target: { files: { item: () => new File(['a'], 'a.docx') }, value: '' } } as unknown as Event);
    component.onFileSelected('right', { target: { files: { item: () => new File(['b'], 'b.docx') }, value: '' } } as unknown as Event);
    component.compare();
  });

  it('dzieli różnice na trzy kubełki i domyślnie otwiera „do naprawy u nas”', () => {
    expect(component.bucketCounts()).toEqual({ fix: 2, review: 2, noise: 0 });
    expect(component.bucketGroupCounts()).toEqual({ fix: 2, review: 2, noise: 0 });
    expect(component.activeBucket()).toBe('fix');
    expect(component.bucketLabel('fix')).toBe('Do naprawy u nas');
  });

  it('w kubełku przyczyny idą od najpoważniejszego skutku, z tytułem po ludzku, częścią i wskaźnikiem do kodu', () => {
    const groups = component.visibleGroups();

    // w:compat (inny PDF) przed styles.xml (bez analizy → na końcu).
    expect(groups.map((group) => group.partPath)).toEqual(['word/settings.xml', 'word/styles.xml']);
    expect(component.describeGroup(groups[0])).toBe('Po zapisie brak: w:compat');
    expect(component.describePart(groups[0].partPath)).toBe('Ustawienia dokumentu');
    expect(groups[0].codePointer).toBe('HtmlToDocxConverter.PreserveSettings');
    expect(groups[0].worstImpact).toBe('PdfDifference');
    expect(component.describeGroup(groups[1])).toBe('Zapis dodał całą część, której nie było w oryginale');
    expect(component.visibleCount()).toBe(2);
  });

  it('przełącza kubełek i szuka w tytule, wartościach i akapicie', () => {
    component.selectBucket('review');
    expect(component.visibleGroups().map((group) => group.kind).sort()).toEqual(['AttributeValueChanged', 'TextChanged']);
    expect(component.describeGroup(component.visibleGroups().find((group) => group.kind === 'AttributeValueChanged')!)).toBe('Inna wartość w:p/@w:val: 24 → 28');

    component.setSearch('sprzedaży');
    expect(component.visibleGroups().map((group) => group.kind)).toEqual(['TextChanged']);

    component.setSearch('');
    component.selectBucket('noise');
    expect(component.visibleGroups()).toEqual([]);
  });

  it('rozwija grupę do wystąpień z indeksem w raporcie i stronicuje per grupa', () => {
    const group = component.visibleGroups()[0];

    expect(component.isGroupExpanded(group)).toBe(false);
    component.toggleGroup(group);
    expect(component.isGroupExpanded(group)).toBe(true);

    const items = component.itemsOf(group);
    expect(items).toHaveLength(1);
    expect(items[0].index).toBe(3);
    expect(component.describeDifference(items[0].difference)).toContain('w:compat');
    expect(component.hasMoreIn(group)).toBe(false);

    component.toggleGroup(group);
    expect(component.isGroupExpanded(group)).toBe(false);
  });

  it('kopiuje notatkę Markdown z kubełkami, przyczynami i wskaźnikiem do kodu', async () => {
    const written: string[] = [];
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText: (text: string) => { written.push(text); return Promise.resolve(); } },
      configurable: true,
    });

    await component.copySummary();

    expect(component.copiedNote()).toBe(true);
    expect(written[0]).toContain('## Do naprawy u nas (2 przyczyn, 2 różnic)');
    expect(written[0]).toContain('## Do sprawdzenia (2 przyczyn, 2 różnic)');
    expect(written[0]).toContain('### 1× Element tylko w oryginale · `w:compat` · word/settings.xml');
    expect(written[0]).toContain('- Kod: `HtmlToDocxConverter.PreserveSettings`');
    expect(written[0]).toContain('- 1× `24` → `28`');
  });
});

describe('AdminDocumentCompareComponent', () => {
  let fixture: ComponentFixture<AdminDocumentCompareComponent>;
  let component: AdminDocumentCompareComponent;
  let calls: { left: string; right: string; options: CompareOptions }[];
  let api: { compare: (left: File, right: File, options: CompareOptions) => unknown };

  beforeEach(async () => {
    calls = [];
    api = {
      compare: (left: File, right: File, options: CompareOptions) => {
        calls.push({ left: left.name, right: right.name, options });
        return of(report);
      },
    };

    await TestBed.configureTestingModule({
      imports: [AdminDocumentCompareComponent],
      providers: [{ provide: DocumentCompareService, useValue: api }],
    }).compileComponents();

    fixture = TestBed.createComponent(AdminDocumentCompareComponent);
    component = fixture.componentInstance;
  });

  function select(side: 'left' | 'right', name: string): void {
    component.onFileSelected(side, { target: { files: { item: () => new File(['x'], name) }, value: '' } } as unknown as Event);
  }

  function compareBoth(): void {
    select('left', 'v1.docx');
    select('right', 'v2.docx');
    component.compare();
  }

  it('nie porównuje bez dwóch plików', () => {
    select('left', 'v1.docx');
    component.compare();

    expect(calls).toHaveLength(0);
    expect(component.canCompare()).toBe(false);
  });

  it('wysyła oba pliki z flagami pomijania', () => {
    component.ignoreRevisionIds.set(false);

    compareBoth();

    expect(calls).toEqual([{ left: 'v1.docx', right: 'v2.docx', options: { ignoreRevisionIds: false, ignoreDocumentProperties: true } }]);
    expect(component.report()?.totalDifferences).toBe(4);
    expect(component.changedParts().map((part) => part.path)).toEqual(['word/document.xml', 'word/styles.xml', 'word/settings.xml']);
  });

  it('zamienia pliki miejscami', () => {
    select('left', 'v1.docx');
    select('right', 'v2.docx');

    component.swapFiles();
    component.compare();

    expect(component.leftFileName()).toBe('v2.docx');
    expect(calls[0].left).toBe('v2.docx');
  });

  it('podświetla zmienione słowa po obu stronach', () => {
    compareBoth();

    const diff = component.valueDiff(report.differences[0])!;
    expect(diff.left.map((segment) => segment.kind)).toEqual(['same', 'removed']);
    expect(diff.right.map((segment) => segment.kind)).toEqual(['same', 'added']);
    expect(component.valueDiff(report.differences[2])).toBeNull();
  });

  it('stronicuje wystąpienia w grupie po 50', () => {
    const many: DocumentComparisonReport = {
      ...report,
      differences: Array.from({ length: 120 }, (_, index) => difference({ leftValue: `L${index}`, rightValue: `R${index}` })),
    };
    api.compare = () => of(many);

    compareBoth();
    component.selectBucket('review');

    const group = component.visibleGroups()[0];
    expect(group.count).toBe(120);
    expect(component.itemsOf(group)).toHaveLength(50);
    expect(component.hasMoreIn(group)).toBe(true);
    component.showMoreIn(group);
    expect(component.itemsOf(group)).toHaveLength(100);
    component.showMoreIn(group);
    expect(component.itemsOf(group)).toHaveLength(120);
    expect(component.hasMoreIn(group)).toBe(false);
  });

  it('rozwija wycinek XML per wystąpienie (po indeksie w raporcie)', () => {
    compareBoth();

    component.toggleExcerpt(1);
    expect(component.isExcerptExpanded(1)).toBe(true);
    expect(component.isExcerptExpanded(0)).toBe(false);
  });

  it('pokazuje komunikat backendu przy błędzie', () => {
    api.compare = () => throwError(() => new HttpErrorResponse({ status: 400, error: { error: 'Wymagane są dwa pliki.' } }));

    compareBoth();

    expect(component.report()).toBeNull();
    expect(component.error()).toContain('dwa pliki');
  });

  it('tłumaczy rodzaje, przyczyny i skutki na etykiety', () => {
    expect(component.kindLabel('ElementOnlyInRight')).toBe('Element tylko w porównywanym');
    expect(component.causeLabel('PipelineUnsupported')).toBe('nasz pipeline tego nie obsługuje');
    expect(component.impactLabel('PdfDifference')).toBe('inny PDF');
    expect(component.isOneSided('AttributeOnlyInLeft')).toBe(true);
    expect(component.isOneSided('TextChanged')).toBe(false);
  });
});
