import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { DashboardComponent } from './dashboard';
import { DocumentService } from '../../services/document.service';
import { DocumentStorageService } from '../../services/document-storage.service';
import { DocumentNavigationService } from '../../core/services/document-navigation.service';
import { ResourceAccessService } from '../../core/services/resource-access.service';
import { MsalService } from '@azure/msal-angular';

function setup(canUseEditor: boolean, documentStorageServiceOverride?: Record<string, unknown>) {
  const documentService = { newDocument: vi.fn().mockReturnValue(of({ html: '', metadata: '' })) };
  const documentStorageService = documentStorageServiceOverride ?? {};
  const documentNavigation = { navigateToEditableDocument: vi.fn(), navigateToDocument: vi.fn() };
  const resourceAccess = { hasAccessToResource: vi.fn().mockReturnValue(of(canUseEditor)) };
  const msal = { instance: { getActiveAccount: () => null, getAllAccounts: () => [] } };

  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      { provide: DocumentService, useValue: documentService },
      { provide: DocumentStorageService, useValue: documentStorageService },
      { provide: DocumentNavigationService, useValue: documentNavigation },
      { provide: ResourceAccessService, useValue: resourceAccess },
      { provide: MsalService, useValue: msal },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new DashboardComponent());
  return { component, documentService, documentNavigation };
}

async function pickFile(component: DashboardComponent, file: File): Promise<void> {
  const originalCreateElement = document.createElement.bind(document);
  const spy = vi.spyOn(document, 'createElement').mockImplementation((tag: string) => {
    const el = originalCreateElement(tag);
    if (tag === 'input') {
      Object.defineProperty(el, 'files', { value: [file], configurable: true });
      (el as HTMLInputElement).click = () => {};
    }
    return el;
  });
  component.openFile();
  const input = spy.mock.results[spy.mock.results.length - 1].value as HTMLInputElement;
  spy.mockRestore();
  await input.onchange?.({ target: input } as unknown as Event);
}

describe('DashboardComponent editor gating', () => {
  it('exposes canUseEditor from the "editor" resource', () => {
    expect(setup(true).component.canUseEditor()).toBe(true);
    expect(setup(false).component.canUseEditor()).toBe(false);
  });

  it('blocks newDocument when the user lacks the editor resource', () => {
    const { component, documentService } = setup(false);
    component.newDocument();
    expect(documentService.newDocument).not.toHaveBeenCalled();
    expect(component.errorMessage()).toBe('Nie masz uprawnień do edycji dokumentów.');
  });

  it('allows newDocument when the user has the editor resource', () => {
    const { component, documentService } = setup(true);
    component.newDocument();
    expect(documentService.newDocument).toHaveBeenCalled();
  });
});

describe('DashboardComponent openFile — .doc support', () => {
  it('uploads a .doc file as application/msword and navigates to the editable document', async () => {
    const uploadDocument = vi.fn().mockReturnValue(of({ masterId: 'master-1' }));
    const saveDocumentVersion = vi.fn().mockReturnValue(of({ versionId: 'version-1' }));
    const fileToBase64 = vi.fn().mockResolvedValue('ZmFrZQ==');
    const { component, documentNavigation } = setup(true, {
      uploadDocument, saveDocumentVersion, fileToBase64,
    });

    const file = new File(['legacy binary content'], 'umowa.doc', { type: 'application/msword' });
    await pickFile(component, file);

    expect(uploadDocument).toHaveBeenCalledWith(
      expect.objectContaining({ name: 'umowa.doc', mimeType: 'application/msword' })
    );
    expect(saveDocumentVersion).toHaveBeenCalledWith('master-1', { content: 'ZmFrZQ==' });
    expect(documentNavigation.navigateToEditableDocument).toHaveBeenCalledWith('master-1', 'version-1', { openedFileName: 'umowa.doc' });
    expect(component.errorMessage()).toBeNull();
  });

  it('rejects extensions other than .docx/.doc/.pdf', async () => {
    const uploadDocument = vi.fn();
    const { component } = setup(true, { uploadDocument });

    const file = new File(['x'], 'notatka.txt', { type: 'text/plain' });
    await pickFile(component, file);

    expect(uploadDocument).not.toHaveBeenCalled();
    expect(component.errorMessage()).toBe('Obsługiwane są pliki DOCX, DOC i PDF.');
  });
});
