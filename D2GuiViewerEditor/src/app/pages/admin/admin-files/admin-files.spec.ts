import { TestBed, ComponentFixture } from '@angular/core/testing';
import { vi } from 'vitest';
import { Observable, of, throwError } from 'rxjs';
import { AdminFilesComponent } from './admin-files';
import { AdminService, DocumentListItem, DocumentVersionListItem } from '../../../services/admin.service';

function doc(masterId: string, name: string, mimeType: string): DocumentListItem {
  return {
    masterId,
    name,
    mimeType,
    createdAt: '2026-07-22T12:26:00Z',
    activeVersionId: `active-${masterId}`,
    versionNumber: 1,
    status: 'Saved',
    lastModifiedBy: null,
  };
}

describe('AdminFilesComponent — filtry Master/Version ID i rozszerzeń', () => {
  let fixture: ComponentFixture<AdminFilesComponent>;
  let component: AdminFilesComponent;
  let versionCalls: string[];
  let deleteCalls: string[];
  let deleteResult: Observable<void>;

  const docs = [
    doc('master-aaa', 'umowa.docx',
      'application/vnd.openxmlformats-officedocument.wordprocessingml.document'),
    doc('master-bbb', 'stary.doc', 'application/msword'),
    doc('master-ccc', 'pismo.pdf', 'application/pdf'),
  ];
  const versionsByMaster: Record<string, DocumentVersionListItem[]> = {
    'master-aaa': [{
      versionId: 'ver-hidden-123', versionNumber: 2, createdAt: '2026-07-22T12:30:00Z',
      createdBy: 'x', isActive: false, sizeInBytes: 100,
    }],
    'master-bbb': [],
    'master-ccc': [],
  };

  beforeEach(async () => {
    versionCalls = [];
    deleteCalls = [];
    deleteResult = of(void 0);
    await TestBed.configureTestingModule({
      imports: [AdminFilesComponent],
      providers: [{
        provide: AdminService,
        useValue: {
          getAllDocuments: () => of(docs),
          getDocumentVersions: (masterId: string) => {
            versionCalls.push(masterId);
            return of(versionsByMaster[masterId] ?? []);
          },
          deleteDocument: (masterId: string) => {
            deleteCalls.push(masterId);
            return deleteResult;
          },
        },
      }],
    }).compileComponents();
    fixture = TestBed.createComponent(AdminFilesComponent);
    component = fixture.componentInstance;
    component.ngOnInit();
  });

  it('filtr ID znajduje po MasterID', () => {
    component.setFilter('id', 'master-bbb');
    expect(component.documents().map(d => d.masterId)).toEqual(['master-bbb']);
  });

  it('filtr ID znajduje po VersionID aktywnej wersji', () => {
    component.setFilter('id', 'active-master-ccc');
    expect(component.documents().map(d => d.masterId)).toEqual(['master-ccc']);
  });

  it('filtr ID dociąga wersje RAZ i znajduje po VersionID nieaktywnej wersji', () => {
    component.setFilter('id', 'ver-hidden');
    expect(component.documents().map(d => d.masterId)).toEqual(['master-aaa']);
    
    component.setFilter('id', 'ver-hidden-1');
    expect(versionCalls.filter(m => m === 'master-aaa').length).toBe(1);
  });

  it('filtr rozszerzenia rozróżnia doc od docx (etykieta „Word" tego nie umiała)', () => {
    component.setFilter('extension', 'doc');
    expect(component.documents().map(d => d.masterId)).toEqual(['master-bbb']);

    component.setFilter('extension', 'docx');
    expect(component.documents().map(d => d.masterId)).toEqual(['master-aaa']);

    component.setFilter('extension', 'pdf');
    expect(component.documents().map(d => d.masterId)).toEqual(['master-ccc']);
  });

  it('opcje selecta = rozszerzenia obecne na liście (posortowane)', () => {
    expect(component.availableExtensions()).toEqual(['doc', 'docx', 'pdf']);
  });

  it('rozszerzenie liczone z nazwy pliku, fallback z MIME; chip pokazuje uppercase', () => {
    expect(component.fileExtension({ name: 'x.DOCX', mimeType: 'application/pdf' })).toBe('docx');
    expect(component.fileExtension({ name: '', mimeType: 'application/msword' })).toBe('doc');
    expect(component.extensionLabel(docs[2])).toBe('PDF');
  });

  describe('trwałe usuwanie pozycji (GCS + baza)', () => {
    function firstDoc() {
      return component.documents()[0];
    }

    it('po potwierdzeniu woła API i zdejmuje pozycję z listy', () => {
      vi.spyOn(window, 'confirm').mockReturnValue(true);
      const before = component.documents().length;

      component.deleteDocument(firstDoc(), new Event('click'));

      expect(deleteCalls).toEqual(['master-aaa']);
      expect(component.documents().length).toBe(before - 1);
      expect(component.notice()).toContain('Usunięto');
    });

    it('anulowanie potwierdzenia NIE woła API', () => {
      vi.spyOn(window, 'confirm').mockReturnValue(false);

      component.deleteDocument(firstDoc(), new Event('click'));

      expect(deleteCalls).toEqual([]);
      expect(component.documents().length).toBe(3);
    });

    it('409 (wysyłka w toku) pokazuje komunikat serwera, pozycja zostaje', () => {
      vi.spyOn(window, 'confirm').mockReturnValue(true);
      deleteResult = throwError(() => ({
        error: { error: 'Nie można usunąć dokumentu w stanie Sending: wysyłka jest w toku.' },
      })) as never;

      component.deleteDocument(firstDoc(), new Event('click'));

      expect(component.documents().length).toBe(3);
      expect(component.notice()).toContain('wysyłka jest w toku');
      expect(component.deletingId()).toBeNull();
    });
  });
});
