import { TestBed, ComponentFixture } from '@angular/core/testing';
import { of } from 'rxjs';
import { ActivatedRoute, Router } from '@angular/router';
import { DocumentEditorComponent } from './document-editor';
import { DocumentService } from '../../services/document.service';
import { DocumentStorageService } from '../../services/document-storage.service';
import { BuildInfoService } from '../../core/services/build-info.service';
import { MsalService } from '@azure/msal-angular';
import { EditorState } from '../../models/document.model';

const msalStub = { instance: { getActiveAccount: () => null, getAllAccounts: () => [] } };

describe('DocumentEditorComponent — mini-toolbar font = źródło prawdy', () => {
  let fixture: ComponentFixture<DocumentEditorComponent>;
  let component: DocumentEditorComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DocumentEditorComponent],
      providers: [
        { provide: DocumentService, useValue: { getTemplates: () => of([]) } },
        { provide: DocumentStorageService, useValue: {} },
        { provide: Router, useValue: { navigate: () => {} } },
        { provide: ActivatedRoute, useValue: { queryParams: of({}) } },
        { provide: BuildInfoService, useValue: {} },
        { provide: MsalService, useValue: msalStub },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(DocumentEditorComponent);
    component = fixture.componentInstance;
  });

  function setSelectionFont(fontFamily?: string, fontSize?: number): void {
    const state: EditorState = {
      isModified: false,
      canUndo: false,
      canRedo: false,
      wordCount: 0,
      currentFormatting: {
        bold: false,
        italic: false,
        underline: false,
        strikethrough: false,
        subscript: false,
        superscript: false,
      },
      currentStyle: { fontFamily, fontSize },
    };
    component.editorState.set(state);
  }

  it('pokazuje firmowy font dokumentu („Doc2 Me") zamiast fałszywego Calibri', () => {
    setSelectionFont('Doc2 Me', 12);

    expect(component.miniToolbarFontFamily()).toBe('Doc2 Me');
    
    expect(component.miniToolbarFontOptions()).toContain('Doc2 Me');
    
    expect(
      component.miniToolbarFontOptions().filter((f) => f === 'Doc2 Me').length,
    ).toBe(1);
  });

  it('nie wprowadza duplikatu dla fontu, który już jest na liście (Calibri)', () => {
    setSelectionFont('Calibri', 11);

    expect(component.miniToolbarFontFamily()).toBe('Calibri');
    const options = component.miniToolbarFontOptions();
    expect(options.filter((f) => f === 'Calibri').length).toBe(1);
    
    expect(options.length).toBe(component.commonFonts().length);
  });

  it('mini-toolbar i główny toolbar zgadzają się dla tej samej selekcji', () => {
    
    setSelectionFont('times new roman', 14);
    expect(component.miniToolbarFontFamily()).toBe('Times New Roman');
    expect(component.miniToolbarFontOptions()).toContain('Times New Roman');
  });

  it('gdy selekcja nie ma jawnego fontu — spada do domyślnego (Calibri), bez pustki', () => {
    setSelectionFont(undefined, undefined);
    expect(component.miniToolbarFontFamily()).toBe('Calibri');
    expect(component.miniToolbarFontOptions()).toContain('Calibri');
  });
});
