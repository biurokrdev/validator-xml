import { TestBed, ComponentFixture } from '@angular/core/testing';
import { vi } from 'vitest';
import { of } from 'rxjs';
import { ActivatedRoute, Router } from '@angular/router';
import { DocumentEditorComponent } from './document-editor';
import { DocumentService } from '../../services/document.service';
import { DocumentStorageService } from '../../services/document-storage.service';
import { BuildInfoService } from '../../core/services/build-info.service';
import { buildInfoStub } from '../../testing/build-info.stub';
import { MsalService } from '@azure/msal-angular';
import { SaveDocumentRequest } from '../../models/document.model';

const msalStub = { instance: { getActiveAccount: () => null, getAllAccounts: () => [] } };

describe('DocumentEditorComponent — zapis przypisów', () => {
  let fixture: ComponentFixture<DocumentEditorComponent>;
  let component: DocumentEditorComponent;
  let saveSpy: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    saveSpy = vi.fn().mockReturnValue(of(new Blob(['docx'])));

    await TestBed.configureTestingModule({
      imports: [DocumentEditorComponent],
      providers: [
        { provide: DocumentService, useValue: { getTemplates: () => of([]), saveDocument: saveSpy } },
        {
          provide: DocumentStorageService,
          useValue: {
            updateDocumentVersion: () => of({}),
            saveDocumentVersion: () => of({}),
          },
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

  it('wysyła zmieniony model przypisów w żądaniu zapisu', () => {
    component.documentMasterId.set('master-1');
    component.documentVersionId.set('version-1');
    component.documentContent.set('<p>Treść z odwołaniem' +
      '<sup class="footnote-ref" data-footnote-id="fn-1">1</sup>.</p>');

    component.footnotes.set([{ id: 'fn-1', html: '<p>Zmieniona treść przypisu.</p>' }]);

    component.saveDocument();

    expect(saveSpy).toHaveBeenCalledTimes(1);
    const request = saveSpy.mock.calls[0][0] as SaveDocumentRequest;
    expect(request.footnotes).toEqual([{ id: 'fn-1', html: '<p>Zmieniona treść przypisu.</p>' }]);
    expect(request.html).toContain('data-footnote-id="fn-1"');
  });

  it('dokument bez przypisów nie wysyła footnotes', () => {
    component.documentMasterId.set('master-2');
    component.documentContent.set('<p>Bez przypisów.</p>');
    component.footnotes.set(null);

    component.saveDocument();

    const request = saveSpy.mock.calls[0][0] as SaveDocumentRequest;
    expect(request.footnotes).toBeUndefined();
  });
});
