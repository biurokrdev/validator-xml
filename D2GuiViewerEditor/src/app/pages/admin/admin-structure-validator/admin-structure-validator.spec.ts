import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { AdminStructureValidatorComponent } from './admin-structure-validator';
import {
  DocumentSection,
  PackageDiagnostics,
  SchemaIssues,
  StructureElement,
  StructureInspectionSummary,
  StructureValidatorService,
} from '../../../services/structure-validator.service';

function element(
  id: string,
  depth: number,
  overrides: Partial<StructureElement> = {},
): StructureElement {
  return {
    id,
    parentId: null,
    depth,
    partPath: 'word/document.xml',
    xmlName: 'w:p',
    category: 'Paragraph',
    displayName: 'Akapit',
    preview: null,
    searchText: 'w:p | akapit | paragraph | word/document.xml',
    severity: 'None',
    issueCount: 0,
    hasChildren: false,
    ...overrides,
  };
}

const summary: StructureInspectionSummary = {
  inspectionId: 'insp-1',
  fileName: 'test.docx',
  fileSizeInBytes: 1024,
  mainDocumentPartPath: 'word/document.xml',
  expiresAtUtc: '2026-08-11T12:00:00Z',
  elementCount: 5,
  errorCount: 1,
  warningCount: 2,
  infoCount: 3,
  schemaIssueCount: 0,
  packageIssueCount: 1,
  sectionCount: 1,
  elementsTruncated: false,
  parts: [
    {
      path: 'word/document.xml',
      contentType: 'application/xml',
      uncompressedSize: 10,
      compressedSize: 5,
      elementCount: 5,
    },
  ],
  categories: ['Paragraph', 'AnchoredDrawing'],
};

const elements: StructureElement[] = [
  element('p0', 0, { xmlName: 'w:document', displayName: 'w:document', hasChildren: true }),
  element('p0-0', 1, { parentId: 'p0', xmlName: 'w:body', displayName: 'w:body', hasChildren: true }),
  element('p0-0.0', 2, { parentId: 'p0-0', hasChildren: true }),
  element('p0-0.0.0', 3, {
    parentId: 'p0-0.0',
    xmlName: 'w:r',
    displayName: 'Run',
    preview: 'Pierwszy akapit',
    searchText: 'w:r | run | run | word/document.xml | pierwszy akapit',
  }),
  element('p0-0.1', 2, {
    parentId: 'p0-0',
    xmlName: 'wp:anchor',
    displayName: 'Grafika pływająca (wp:anchor)',
    category: 'AnchoredDrawing',
    severity: 'Warning',
    issueCount: 2,
    searchText: 'wp:anchor | grafika pływająca | anchoreddrawing | word/document.xml | drawing_behind_document',
  }),
];

const sections: DocumentSection[] = [
  {
    number: 1,
    sectionPropertiesElementId: 'p0-0.1',
    displayPath: '/w:document[1]/w:body[1]/w:sectPr[1]',
    firstPageDifferent: false,
    evenAndOddHeaders: false,
    headerFooterBindings: [],
    issues: [],
  },
];

const packageDiagnostics: PackageDiagnostics = {
  mainDocumentPartPath: 'word/document.xml',
  issues: [],
  entries: [],
  supportedSchemaTargets: ['Microsoft365', 'Office2013'],
};

const schemaIssues: SchemaIssues = { targetVersion: 'Microsoft365', totalCount: 0, issues: [] };

describe('AdminStructureValidatorComponent', () => {
  let fixture: ComponentFixture<AdminStructureValidatorComponent>;
  let component: AdminStructureValidatorComponent;
  let api: Record<string, unknown>;
  let schemaTargetCalls: (string | undefined)[];
  let deletedInspections: string[];

  beforeEach(async () => {
    schemaTargetCalls = [];
    deletedInspections = [];

    api = {
      analyze: () => of(summary),
      getElements: () => of(elements),
      getSections: () => of(sections),
      getPackageDiagnostics: () => of(packageDiagnostics),
      getSchemaIssues: (_id: string, targetVersion?: string) => {
        schemaTargetCalls.push(targetVersion);
        return of({ ...schemaIssues, targetVersion: targetVersion ?? 'Microsoft365' });
      },
      getElementDetails: (_id: string, elementId: string) =>
        of({
          id: elementId,
          parentId: null,
          depth: 0,
          partPath: 'word/document.xml',
          displayPath: '/w:document[1]',
          xmlName: 'w:document',
          localName: 'document',
          namespaceUri: 'http://schemas.openxmlformats.org/wordprocessingml/2006/main',
          category: 'Document',
          displayName: 'w:document',
          preview: null,
          attributes: [],
          properties: [],
          relationships: [],
          issues: [],
          editorCompatibility: [],
        }),
      getElementXml: (_id: string, elementId: string) =>
        of({
          elementId,
          partPath: 'word/document.xml',
          displayPath: '/w:document[1]',
          xml: '<w:document/>',
          sourceLine: 1,
        }),
      getPartXml: (_id: string, partPath: string, highlightElementId?: string) =>
        of({
          partPath,
          xml: '<w:document/>',
          highlightElementId: highlightElementId ?? null,
          highlightLine: highlightElementId ? 1 : null,
        }),
      deleteInspection: (id: string) => {
        deletedInspections.push(id);
        return of(void 0);
      },
    };

    await TestBed.configureTestingModule({
      imports: [AdminStructureValidatorComponent],
      providers: [{ provide: StructureValidatorService, useValue: api }],
    }).compileComponents();

    fixture = TestBed.createComponent(AdminStructureValidatorComponent);
    component = fixture.componentInstance;
  });

  function analyzeWith(file = new File(['x'], 'test.docx')): void {
    component.onFileSelected({
      target: { files: { item: () => file }, value: '' },
    } as unknown as Event);
    component.analyze();
  }

  it('wczytuje podsumowanie, drzewo, sekcje i diagnostykę pakietu', () => {
    analyzeWith();

    expect(component.summary()?.inspectionId).toBe('insp-1');
    expect(component.elements()).toHaveLength(5);
    expect(component.sections()).toHaveLength(1);
    expect(component.packageDiagnostics()?.mainDocumentPartPath).toBe('word/document.xml');
    expect(component.visibleElements()).toHaveLength(5);
  });

  it('odrzuca plik o rozszerzeniu innym niż .docx bez wywołania API', () => {
    let called = false;
    api['analyze'] = () => {
      called = true;
      return of(summary);
    };

    analyzeWith(new File(['x'], 'dokument.pdf'));

    expect(called).toBe(false);
    expect(component.error()).toContain('DOCX');
  });

  it('ukrywa potomków zwiniętego węzła', () => {
    analyzeWith();

    component.toggleCollapse(elements[2], new Event('click'));

    expect(component.visibleElements().map((item) => item.id)).toEqual([
      'p0',
      'p0-0',
      'p0-0.0',
      'p0-0.1',
    ]);
  });

  it('filtruje zachowując przodków trafienia', () => {
    analyzeWith();

    component.setSearch('anchor');

    expect(component.visibleElements().map((item) => item.id)).toEqual(['p0', 'p0-0', 'p0-0.1']);
  });

  it('filtruje po poziomie diagnostyki i po „tylko z problemami”', () => {
    analyzeWith();

    component.setSeverity('Warning');
    expect(component.visibleElements().map((item) => item.id)).toEqual(['p0', 'p0-0', 'p0-0.1']);

    component.setSeverity('All');
    component.toggleProblemsOnly();
    expect(component.visibleElements().map((item) => item.id)).toEqual(['p0', 'p0-0', 'p0-0.1']);
  });

  it('renderuje tylko okno wierszy wokół pozycji scrolla', () => {
    analyzeWith();

    expect(component.windowedElements().length).toBeLessThanOrEqual(component.visibleElements().length);
    expect(component.totalListHeight()).toBe(component.visibleElements().length * component.rowHeight);
  });

  it('skok do elementu czyści filtry, zaznacza element i pozycjonuje okno wirtualizacji', () => {
    analyzeWith();
    component.setSearch('nie-istnieje');

    component.goToElement('p0-0.1');

    expect(component.hasFilters()).toBe(false);
    expect(component.selectedId()).toBe('p0-0.1');
    expect(component.windowedElements().some((item) => item.id === 'p0-0.1')).toBe(true);
  });

  it('zmiana profilu Open XML odpytuje backend o wyniki dla tej wersji', () => {
    analyzeWith();
    schemaTargetCalls.length = 0;

    component.changeSchemaTarget('Office2013');

    expect(schemaTargetCalls).toEqual(['Office2013']);
    expect(component.schemaTarget()).toBe('Office2013');
  });

  it('zwalnia poprzednią analizę przed wczytaniem kolejnego pliku', () => {
    analyzeWith();

    analyzeWith(new File(['y'], 'drugi.docx'));

    expect(deletedInspections).toEqual(['insp-1']);
  });

  it('klik w plakietkę poziomu filtruje drzewo, ponowny klik zdejmuje filtr', () => {
    analyzeWith();

    component.toggleSeverityBadge('Warning');
    expect(component.severityFilter()).toBe('Warning');
    expect(component.visibleElements().map((item) => item.id)).toEqual(['p0', 'p0-0', 'p0-0.1']);

    component.toggleSeverityBadge('Warning');
    expect(component.severityFilter()).toBe('All');
    expect(component.visibleElements()).toHaveLength(5);
  });

  it('Enter zaznacza element, a strzałki zwijają i rozwijają gałąź', () => {
    analyzeWith();
    const body = elements[1];

    component.onRowKeydown(new KeyboardEvent('keydown', { key: 'ArrowLeft' }), body);
    expect(component.isCollapsed(body)).toBe(true);
    expect(component.visibleElements().map((item) => item.id)).toEqual(['p0', 'p0-0']);

    component.onRowKeydown(new KeyboardEvent('keydown', { key: 'ArrowRight' }), body);
    expect(component.isCollapsed(body)).toBe(false);

    component.onRowKeydown(new KeyboardEvent('keydown', { key: 'Enter' }), body);
    expect(component.selectedId()).toBe('p0-0');
  });

  it('pokazuje komunikat backendu, gdy analiza wygasła', () => {
    api['analyze'] = () =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 404,
            error: { error: 'Analiza dokumentu wygasła lub nie istnieje. Wczytaj dokument ponownie.' },
          }),
      );

    analyzeWith();

    expect(component.summary()).toBeNull();
    expect(component.error()).toContain('wygasła');
  });
});
