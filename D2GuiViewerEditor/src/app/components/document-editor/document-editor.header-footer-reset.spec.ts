import { TestBed, ComponentFixture } from '@angular/core/testing';
import { of } from 'rxjs';
import { ActivatedRoute, Router } from '@angular/router';
import { DocumentEditorComponent } from './document-editor';
import { DocumentService } from '../../services/document.service';
import { DocumentStorageService } from '../../services/document-storage.service';
import { BuildInfoService } from '../../core/services/build-info.service';
import { buildInfoStub } from '../../testing/build-info.stub';
import { MsalService } from '@azure/msal-angular';
import { DocumentContent } from '../../models/document.model';

const msalStub = { instance: { getActiveAccount: () => null, getAllAccounts: () => [] } };

function makeContent(overrides: Partial<DocumentContent>): DocumentContent {
  return {
    html: '<p>Treść</p>',
    metadata: {} as DocumentContent['metadata'],
    images: [],
    styles: [],
    ...overrides,
  };
}

describe('DocumentEditorComponent — reset nagłówka/stopki między dokumentami', () => {
  let fixture: ComponentFixture<DocumentEditorComponent>;
  let component: DocumentEditorComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DocumentEditorComponent],
      providers: [
        { provide: DocumentService, useValue: { getTemplates: () => of([]), saveDocument: () => of(new Blob()) } },
        {
          provide: DocumentStorageService,
          useValue: { updateDocumentVersion: () => of({}), saveDocumentVersion: () => of({}) },
        },
        { provide: Router, useValue: { navigate: () => {} } },
        { provide: ActivatedRoute, useValue: { queryParams: of({}) } },
        { provide: BuildInfoService, useValue: buildInfoStub() },
        { provide: MsalService, useValue: msalStub },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(DocumentEditorComponent);
    component = fixture.componentInstance;
  });

  it('dokument bez nagłówka/stopki zeruje warianty first-page i even z poprzedniego', () => {
    const apply = (c: DocumentContent) => (component as unknown as {
      _applyLoadedContent(content: DocumentContent, fileName: string): void;
    })._applyLoadedContent(c, 'doc.docx');

    apply(makeContent({
      header: {
        html: '<p>Nagłówek A</p>', height: 1.27,
        differentFirstPage: true, firstPageHtml: '<p>Nagłówek pierwszej strony A</p>',
        differentOddEven: true, evenHtml: '<p>Nagłówek parzysty A</p>',
      },
      footer: {
        html: '<p>Stopka A</p>', height: 1.27,
        differentFirstPage: true, firstPageHtml: '<p>Stopka pierwszej strony A</p>',
      },
    }));

    expect(component.headerContent().firstPageHtml).toBe('<p>Nagłówek pierwszej strony A</p>');
    expect(component.footerContent().firstPageHtml).toBe('<p>Stopka pierwszej strony A</p>');

    apply(makeContent({ html: '<p>Dokument B</p>' }));

    expect(component.headerContent().html).toBe('');
    expect(component.headerContent().firstPageHtml).toBe('');
    expect(component.headerContent().differentFirstPage).toBe(false);
    expect(component.headerContent().evenHtml).toBe('');
    expect(component.headerContent().differentOddEven).toBe(false);

    expect(component.footerContent().html).toBe('');
    expect(component.footerContent().firstPageHtml).toBe('');
    expect(component.footerContent().differentFirstPage).toBe(false);
  });
});
