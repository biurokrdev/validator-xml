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
  totalDifferences: 3,
  truncated: false,
  ignoredAttributeCount: 4,
  countsByKind: { TextChanged: 1, AttributeValueChanged: 1, PartOnlyInRight: 1 },
  countsByCategory: { Tekst: 1, 'Formatowanie znaku': 1, Style: 1 },
  parts: [
    { path: 'word/document.xml', status: 'Changed', isXml: true, differenceCount: 2, leftSize: 500, rightSize: 520, leftElementCount: 20, rightElementCount: 20, note: null },
    { path: 'word/styles.xml', status: 'OnlyInRight', isXml: true, differenceCount: 1, leftSize: null, rightSize: 300, leftElementCount: null, rightElementCount: null, note: null },
    { path: '_rels/.rels', status: 'Identical', isXml: true, differenceCount: 0, leftSize: 100, rightSize: 100, leftElementCount: null, rightElementCount: null, note: null },
  ],
  differences: [
    difference({}),
    difference({ kind: 'AttributeValueChanged', category: 'Formatowanie znaku', name: 'w:val', leftValue: '24', rightValue: '28', leftContext: null, rightContext: null }),
    difference({ kind: 'PartOnlyInRight', category: 'Style', partPath: 'word/styles.xml', leftPath: null, rightPath: null, leftValue: null, rightValue: '300 B', leftExcerpt: null, rightExcerpt: '<w:styles/>', leftContext: null, rightContext: null }),
  ],
  comparedAtUtc: '2026-09-23T10:00:00Z',
  durationMs: 120,
};

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
    expect(component.report()?.totalDifferences).toBe(3);
    expect(component.changedParts().map((part) => part.path)).toEqual(['word/document.xml', 'word/styles.xml']);
  });

  it('zamienia pliki miejscami', () => {
    select('left', 'v1.docx');
    select('right', 'v2.docx');

    component.swapFiles();
    component.compare();

    expect(component.leftFileName()).toBe('v2.docx');
    expect(calls[0].left).toBe('v2.docx');
  });

  it('filtruje po rodzaju, kategorii, części i tekście', () => {
    compareBoth();

    component.setKind('TextChanged');
    expect(component.visibleDifferences()).toHaveLength(1);
    component.setKind('All');

    component.setCategory('Style');
    expect(component.visibleDifferences().map((item) => item.kind)).toEqual(['PartOnlyInRight']);
    component.setCategory('All');

    component.togglePart(report.parts[0]);
    expect(component.visibleDifferences()).toHaveLength(2);
    component.togglePart(report.parts[0]);

    component.setSearch('sprzedaży');
    expect(component.visibleDifferences()).toHaveLength(1);

    component.clearFilters();
    expect(component.hasFilters()).toBe(false);
    expect(component.visibleDifferences()).toHaveLength(3);
  });

  it('podświetla zmienione słowa po obu stronach', () => {
    compareBoth();

    const diff = component.valueDiff(report.differences[0])!;
    expect(diff.left.map((segment) => segment.kind)).toEqual(['same', 'removed']);
    expect(diff.right.map((segment) => segment.kind)).toEqual(['same', 'added']);
    expect(component.valueDiff(report.differences[2])).toBeNull();
  });

  it('stronicuje długie listy', () => {
    const many: DocumentComparisonReport = {
      ...report,
      differences: Array.from({ length: 250 }, (_, index) => difference({ leftValue: `L${index}`, rightValue: `R${index}` })),
    };
    api.compare = () => of(many);

    compareBoth();

    expect(component.pagedDifferences()).toHaveLength(100);
    expect(component.hasMore()).toBe(true);
    component.showMore();
    expect(component.pagedDifferences()).toHaveLength(200);
  });

  it('rozwija wycinek XML per różnica', () => {
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

  it('tłumaczy rodzaje i statusy na etykiety', () => {
    expect(component.kindLabel('ElementOnlyInRight')).toBe('Element tylko w prawym');
    expect(component.statusLabel('Unreadable')).toBe('nieczytelna');
    expect(component.isOneSided('AttributeOnlyInLeft')).toBe(true);
    expect(component.isOneSided('TextChanged')).toBe(false);
  });
});
