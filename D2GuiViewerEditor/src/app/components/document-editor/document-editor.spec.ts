import { TestBed, ComponentFixture } from '@angular/core/testing';
import { vi } from 'vitest';
import { of, throwError } from 'rxjs';
import { ActivatedRoute, Router } from '@angular/router';
import { DocumentEditorComponent } from './document-editor';
import { DocumentService, OpenDocumentError } from '../../services/document.service';
import { DocumentStorageService } from '../../services/document-storage.service';
import { BuildInfoService } from '../../core/services/build-info.service';
import { DocumentNavigationService } from '../../core/services/document-navigation.service';
import { LastHttpErrorService } from '../../core/services/last-http-error.service';
import { MsalService } from '@azure/msal-angular';

const msalStub = { instance: { getActiveAccount: () => null, getAllAccounts: () => [] } };

const TEST_DIALOG_PASSWORD = `test-${Math.random().toString(36).slice(2)}`;

describe('DocumentEditorComponent — ESC zamyka boczny panel', () => {
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

  function esc(): KeyboardEvent {
    const ev = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
    component.onEscapeKeydown(ev);
    return ev;
  }

  it('zamyka panel Wyszukiwania po ESC', () => {
    component.showFindReplace.set(true);
    const ev = esc();
    expect(component.showFindReplace()).toBe(false);
    expect(ev.defaultPrevented).toBe(true);
  });

  it('zamyka panel właściwości / stylizacji tabeli po ESC', () => {
    component.showTablePanel.set(true);
    const ev = esc();
    expect(component.showTablePanel()).toBe(false);
    expect(ev.defaultPrevented).toBe(true);
  });

  it('gdy żaden panel nie jest otwarty — ESC nie robi nic i nie blokuje zdarzenia', () => {
    component.showFindReplace.set(false);
    component.showTablePanel.set(false);
    const ev = esc();
    expect(component.showFindReplace()).toBe(false);
    expect(component.showTablePanel()).toBe(false);
    expect(ev.defaultPrevented).toBe(false);
  });

  it('nie zamyka panelu, gdy ESC obsłużył już bardziej szczegółowy handler (defaultPrevented)', () => {
    component.showFindReplace.set(true);
    const ev = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
    ev.preventDefault();
    component.onEscapeKeydown(ev);
    expect(component.showFindReplace()).toBe(true);
  });

  it('nie zamyka bocznego panelu, gdy otwarty jest dialog (dialog ma pierwszeństwo)', () => {
    component.showFindReplace.set(true);
    component.showInsertTableDialog.set(true);
    esc();
    expect(component.showFindReplace()).toBe(true);
  });

  it('ESC używa tej samej logiki zamykania co × — czyści stan wyszukiwania', () => {
    component.showFindReplace.set(true);
    component.findResultCount.set(5);
    component.findCurrentIndex.set(2);
    esc();
    expect(component.showFindReplace()).toBe(false);
    expect(component.findResultCount()).toBe(0);
    expect(component.findCurrentIndex()).toBe(-1);
  });

  it('zamknięcie panelu tabeli przez ESC oznacza go jako ręcznie zamknięty (jak ×)', () => {
    component.showTablePanel.set(true);
    esc();
    expect(component.showTablePanel()).toBe(false);
    expect(() => esc()).not.toThrow();
  });
});

describe('DocumentEditorComponent — przełączanie doku tabela ↔ wyszukiwanie', () => {
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

  const sync = () => (component as any).syncTablePanel();

  it('ponowne zaznaczenie tabeli przy otwartym wyszukiwaniu przełącza dok na formatowanie tabeli', () => {
    component.showFindReplace.set(true);
    component.isInTable.set(true);
    sync();
    expect(component.showTablePanel()).toBe(true);
    expect(component.showFindReplace()).toBe(false);
  });

  it('w tabeli (bez wyszukiwania) pokazuje panel tabeli', () => {
    component.showFindReplace.set(false);
    component.isInTable.set(true);
    sync();
    expect(component.showTablePanel()).toBe(true);
  });

  it('opuszczenie tabeli chowa panel tabeli i resetuje flagę ręcznego zamknięcia', () => {
    component.isInTable.set(true);
    sync();
    component.isInTable.set(false);
    sync();
    expect(component.showTablePanel()).toBe(false);
  });

  it('ręczne zamknięcie panelu tabeli (×) nie otwiera go ponownie póki karetka jest w tabeli', () => {
    component.isInTable.set(true);
    component.closeTablePanel();
    sync();
    expect(component.showTablePanel()).toBe(false);
  });

  it('dok ma jeden aktywny tryb — tabela i wyszukiwanie nie są widoczne jednocześnie', () => {
    component.showFindReplace.set(true);
    component.isInTable.set(true);
    sync();
    expect(component.showTablePanel() && component.showFindReplace()).toBe(false);
  });
});

describe('DocumentEditorComponent — widoczność przycisku „Zakończ" (canFinish)', () => {
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

  it('jest widoczny, gdy link zwrotny istnieje i jest poprawny', () => {
    component.returnUrl.set('https://app.example.com/return');
    expect(component.canFinish()).toBe(true);
  });

  it('nie jest renderowany, gdy link jest null', () => {
    component.returnUrl.set(null);
    expect(component.canFinish()).toBe(false);
  });

  it('nie jest renderowany, gdy link jest pustym stringiem', () => {
    component.returnUrl.set('');
    expect(component.canFinish()).toBe(false);
  });

  it('nie jest renderowany, gdy link to same białe znaki', () => {
    component.returnUrl.set('   ');
    expect(component.canFinish()).toBe(false);
  });

  it('nie jest renderowany, gdy link jest niepoprawnym URL', () => {
    component.returnUrl.set('not-a-url');
    expect(component.canFinish()).toBe(false);
  });
});

describe('DocumentEditorComponent — ESC zamyka edycję nagłówka/stopki', () => {
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

  function esc(): KeyboardEvent {
    const ev = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
    component.onEscapeKeydown(ev);
    return ev;
  }

  it('zamyka tryb edycji nagłówka po ESC (preventDefault + delegacja do editor)', () => {
    component.editingSection.set('header');
    let stopped = false;
    (component as any).editor = { stopEditingHeaderFooter: () => { stopped = true; } };

    const ev = esc();

    expect(stopped).toBe(true);
    expect(ev.defaultPrevented).toBe(true);
  });

  it('zamyka tryb edycji stopki po ESC', () => {
    component.editingSection.set('footer');
    let stopped = false;
    (component as any).editor = { stopEditingHeaderFooter: () => { stopped = true; } };

    const ev = esc();

    expect(stopped).toBe(true);
    expect(ev.defaultPrevented).toBe(true);
  });

  it('tryb nagłówka/stopki ma pierwszeństwo nad panelem wyszukiwania', () => {
    component.editingSection.set('header');
    component.showFindReplace.set(true);
    let stopped = false;
    (component as any).editor = { stopEditingHeaderFooter: () => { stopped = true; } };

    esc();

    expect(stopped).toBe(true);
    expect(component.showFindReplace()).toBe(true);
  });

  it('w trybie body ESC nie ruga editor.stopEditingHeaderFooter', () => {
    component.editingSection.set('body');
    let stopped = false;
    (component as any).editor = { stopEditingHeaderFooter: () => { stopped = true; } };

    const ev = esc();

    expect(stopped).toBe(false);
    expect(ev.defaultPrevented).toBe(false);
  });
});

describe('DocumentEditorComponent — koordynacja panelu Nagłówek/Stopka z Find/Tables', () => {
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

  it('panel nagłówka/stopki jest otwarty, gdy editingSection != body i brak innych paneli', () => {
    component.editingSection.set('header');
    component.showFindReplace.set(false);
    component.showTablePanel.set(false);
    expect(component.showHeaderFooterPanel()).toBe(true);
  });

  it('panel nagłówka/stopki ustępuje miejsca panelowi „Znajdź", gdy oba są aktywne', () => {
    component.editingSection.set('header');
    component.showFindReplace.set(true);
    expect(component.showHeaderFooterPanel()).toBe(false);
  });

  it('panel nagłówka/stopki ustępuje miejsca panelowi tabeli', () => {
    component.editingSection.set('footer');
    component.showTablePanel.set(true);
    expect(component.showHeaderFooterPanel()).toBe(false);
  });

  it('po zamknięciu „Znajdź" panel nagłówka wraca, jeżeli edycja trwa', () => {
    component.editingSection.set('header');
    component.showFindReplace.set(true);
    expect(component.showHeaderFooterPanel()).toBe(false);

    component.showFindReplace.set(false);

    expect(component.showHeaderFooterPanel()).toBe(true);
  });

  it('zamknięcie edycji (editingSection = body) zamyka panel', () => {
    component.editingSection.set('header');
    expect(component.showHeaderFooterPanel()).toBe(true);

    component.editingSection.set('body');

    expect(component.showHeaderFooterPanel()).toBe(false);
  });

  it('panel obrazu ma pierwszeństwo nad panelem nagłówka/stopki', () => {
    component.editingSection.set('header');
    component.selectedImage.set({
      widthPx: 100, heightPx: 50, aspectRatio: 2, alignment: null, positionMode: 'inline',
      border: { enabled: false, color: '#000000', widthPx: 1, style: 'solid' },
      crop: { left: 0, right: 0, top: 0, bottom: 0 },
    });
    expect(component.showImagePanel()).toBe(true);
    expect(component.showHeaderFooterPanel()).toBe(false);
  });
});

describe('DocumentEditorComponent — menu „Pomoc" i akcja „Zgłoś"', () => {
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
        { provide: BuildInfoService, useValue: {
            buildNumber: () => '1.0.0',
            environment: () => 'TEST',
        } },
        { provide: MsalService, useValue: msalStub },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(DocumentEditorComponent);
    component = fixture.componentInstance;
  });

  it('toggleHelpMenu otwiera dropdown „Pomoc" i zamyka inne menu', () => {
    component.showViewMenu.set(true);

    component.toggleHelpMenu();

    expect(component.showHelpMenu()).toBe(true);
    expect(component.showViewMenu()).toBe(false);
  });

  it('drugi toggleHelpMenu zamyka dropdown', () => {
    component.toggleHelpMenu();
    expect(component.showHelpMenu()).toBe(true);

    component.toggleHelpMenu();

    expect(component.showHelpMenu()).toBe(false);
  });

  it('closeAllMenus zamyka również „Pomoc"', () => {
    component.showHelpMenu.set(true);

    component.closeAllMenus();

    expect(component.showHelpMenu()).toBe(false);
  });

  it('openReportEmail() zamyka otwarte menu „Pomoc" (akcja zamyka dropdown jak inne menu)', () => {
    component.showHelpMenu.set(true);
    const origOpen = window.open;
    (window as any).open = () => null;

    try {
      component.openReportEmail();
      expect(component.showHelpMenu()).toBe(false);
    } finally {
      (window as any).open = origOpen;
    }
  });

  function captureMailBody(): string {
    let captured = '';
    const origOpen = window.open;
    (window as any).open = (url: string) => { captured = url; return null; };
    try {
      component.openReportEmail();
    } finally {
      (window as any).open = origOpen;
    }
    const body = /[?&]body=([^&]*)/.exec(captured)?.[1] ?? '';
    return decodeURIComponent(body);
  }

  it('openReportEmail() wstawia poprawny Master ID i Version ID w treści maila (Problem 5)', () => {
    component.documentMasterId.set('master-123');
    component.documentVersionId.set('version-456');

    const body = captureMailBody();

    expect(body).toContain('Master ID');
    expect(body).toContain('master-123');
    expect(body).toContain('Version ID');
    expect(body).toContain('version-456');
    expect(body).not.toMatch(/Version ID\s*:\s*—/);
  });

  it('openReportEmail() używa fallbacku „—" dla Version ID tylko gdy wersja faktycznie nie istnieje', () => {
    component.documentMasterId.set('master-123');
    component.documentVersionId.set(null);

    const body = captureMailBody();

    expect(body).toMatch(/Version ID\s*:\s*—/);
  });

  function captureMailUrl(): string {
    let captured = '';
    const origOpen = window.open;
    (window as any).open = (url: string) => { captured = url; return null; };
    try {
      component.openReportEmail();
    } finally {
      (window as any).open = origOpen;
    }
    return captured;
  }

  it('openReportEmail() ustawia adresata wsparcia z konfiguracji', () => {
    const url = captureMailUrl();
    expect(url).toMatch(/^mailto:test@testowy\.pl\?/);
  });

  it('openReportEmail() dołącza rozszerzoną diagnostykę (przeglądarka/połączenie/viewport)', () => {
    const body = captureMailBody();

    expect(body).toContain('Przeglądarka');
    expect(body).toContain('Połączenie');
    expect(body).toContain('Viewport');
    expect(body).toContain('Wersja aplikacji');
  });

  it('openReportEmail() dołącza ostatni błąd HTTP, gdy istnieje', () => {
    TestBed.inject(LastHttpErrorService).record({
      status: 500,
      method: 'POST',
      url: '/api/documents/save',
      detail: 'Wystąpił nieoczekiwany błąd',
      at: '14.07.2026, 12:00:00',
    });

    const body = captureMailBody();

    expect(body).toContain('Ostatni błąd');
    expect(body).toContain('HTTP 500 POST /api/documents/save');
    expect(body).toContain('Wystąpił nieoczekiwany błąd');
  });

  it('openReportEmail() pomija sekcję ostatniego błędu, gdy sesja bez błędów', () => {
    TestBed.inject(LastHttpErrorService).clear();

    const body = captureMailBody();

    expect(body).not.toContain('Ostatni błąd');
  });

  it('otwarcie dokumentu loguje RAZ do konsoli te same dane diagnostyczne co „Zgłoś problem"', () => {
    component.documentMasterId.set('master-777');
    component.documentVersionId.set('version-888');
    const infoSpy = vi.spyOn(console, 'info').mockImplementation(() => {});

    try {
      (component as unknown as { logOpenDiagnostics: () => void }).logOpenDiagnostics();

      expect(infoSpy).toHaveBeenCalledTimes(1);
      const msg = infoSpy.mock.calls[0][0] as string;
      expect(msg).toContain('[open]');
      expect(msg).toContain('Master ID');
      expect(msg).toContain('master-777');
      expect(msg).toContain('Version ID');
      expect(msg).toContain('version-888');
      expect(msg).toContain('Wersja aplikacji');
      expect(msg).toContain('Przeglądarka');
    } finally {
      infoSpy.mockRestore();
    }
  });

  it('copyDiagnostics() kopiuje blok diagnostyczny do schowka', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    const origClipboard = navigator.clipboard;
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    try {
      component.copyDiagnostics();
      await Promise.resolve();

      expect(writeText).toHaveBeenCalledTimes(1);
      const text = writeText.mock.calls[0][0] as string;
      expect(text).toContain('Wersja aplikacji');
      expect(text).toContain('Przeglądarka');
    } finally {
      Object.defineProperty(navigator, 'clipboard', { value: origClipboard, configurable: true });
    }
  });
});

describe('DocumentEditorComponent — dialog hasła: anulowanie (Problem 1)', () => {
  let fixture: ComponentFixture<DocumentEditorComponent>;
  let component: DocumentEditorComponent;
  let navigateSpy: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    navigateSpy = vi.fn();
    await TestBed.configureTestingModule({
      imports: [DocumentEditorComponent],
      providers: [
        { provide: DocumentService, useValue: { getTemplates: () => of([]) } },
        { provide: DocumentStorageService, useValue: {} },
        { provide: Router, useValue: { navigate: navigateSpy } },
        { provide: ActivatedRoute, useValue: { queryParams: of({}) } },
        { provide: BuildInfoService, useValue: {} },
        { provide: MsalService, useValue: msalStub },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(DocumentEditorComponent);
    component = fixture.componentInstance;
  });

  it('Anuluj bez returnUrl → przekierowanie na dashboard (nie zostawia pustego dokumentu)', () => {
    component.returnUrl.set(null);
    component.showPasswordDialog.set(true);

    component.cancelPasswordDialog();

    expect(component.showPasswordDialog()).toBe(false);
    expect(navigateSpy).toHaveBeenCalledWith(['/']);
  });

  it('Anuluj z returnUrl → przepływ powrotu (zamknięcie karty), bez nawigacji na dashboard', () => {
    component.returnUrl.set('https://app.example.com/return');
    component.showPasswordDialog.set(true);
    const origClose = window.close;
    const closeSpy = vi.fn();
    (window as any).close = closeSpy;

    try {
      component.cancelPasswordDialog();
    } finally {
      (window as any).close = origClose;
    }

    expect(component.showPasswordDialog()).toBe(false);
    expect(closeSpy).toHaveBeenCalled();
    expect(navigateSpy).not.toHaveBeenCalled();
  });
});

describe('DocumentEditorComponent — userDownload (widoczność „Pobierz dokument")', () => {
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
        { provide: BuildInfoService, useValue: { buildNumber: () => '1', environment: () => 'TEST' } },
        { provide: MsalService, useValue: msalStub },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(DocumentEditorComponent);
    component = fixture.componentInstance;
  });

  it('default ⇒ canUserDownload=false (brak metadanych)', () => {
    expect(component.canUserDownload()).toBe(false);
  });

  it('userDownload=true ⇒ canUserDownload=true', () => {
    component.userDownload.set(true);
    expect(component.canUserDownload()).toBe(true);
  });

  it('userDownload=false ⇒ canUserDownload=false', () => {
    component.userDownload.set(false);
    expect(component.canUserDownload()).toBe(false);
  });

  it('downloadDocument() bez canUserDownload pokazuje błąd i nie wywołuje API', () => {
    let downloadCalls = 0;
    (component as any).documentStorageService = {
      downloadEditedDocument: () => { downloadCalls++; return { subscribe: () => {} }; }
    };
    component.documentMasterId.set('master-1');
    component.userDownload.set(false);

    component.downloadDocument();

    expect(downloadCalls).toBe(0);
    expect(component.errorMessage()).not.toBeNull();
  });
});

describe('DocumentEditorComponent — showSaveState (widoczność autozapisu + przycisku „Zapisz")', () => {
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
        { provide: BuildInfoService, useValue: { buildNumber: () => '1', environment: () => 'TEST' } },
        { provide: MsalService, useValue: msalStub },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(DocumentEditorComponent);
    component = fixture.componentInstance;
  });

  it('default ⇒ showSaveState=true (oba elementy widoczne)', () => {
    expect(component.showSaveState()).toBe(true);
  });

  it('showSaveState=false ⇒ sygnał false (ukrywa autozapis i „Zapisz")', () => {
    component.showSaveState.set(false);
    expect(component.showSaveState()).toBe(false);
  });
});

describe('DocumentEditorComponent — rozmiar strony (PageSize round-trip)', () => {
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

  it('buildSaveRequest niesie wczytany pageSize', () => {
    component.documentPageSize.set({ widthCm: 29.7, heightCm: 21, orientation: 'landscape' });

    const req = (component as any).buildSaveRequest();

    expect(req.pageSize).toEqual({ widthCm: 29.7, heightCm: 21, orientation: 'landscape' });
  });

  it('bez wczytanego pageSize request ma undefined (backend → fallback A4)', () => {
    const req = (component as any).buildSaveRequest();

    expect(req.pageSize).toBeUndefined();
  });

  it('buildSaveRequest niesie masterId → backend zapisuje przez pass-through (style tabel przeżywają)', () => {
    component.documentMasterId.set('m-42');

    const req = (component as any).buildSaveRequest();

    expect(req.masterId).toBe('m-42');
  });

  it('bez masterId (np. nowy dokument) request ma masterId undefined → regeneracja', () => {
    component.documentMasterId.set(null);

    const req = (component as any).buildSaveRequest();

    expect(req.masterId).toBeUndefined();
  });

  it('buildSaveRequest niesie EFEKTYWNY format numeracji przypisów (plik = ekran)', () => {
    component.footnoteNumberFormat.set('upperRoman');
    component.endnoteNumberFormat.set('decimal');
    let req = (component as any).buildSaveRequest();
    expect(req.footnoteNumberFormat).toBe('upperRoman');
    expect(req.endnoteNumberFormat).toBe('decimal');

    component.footnoteNumberFormat.set(null);
    component.endnoteNumberFormat.set(null);
    req = (component as any).buildSaveRequest();
    expect(req.footnoteNumberFormat).toBe('decimal');
    expect(req.endnoteNumberFormat).toBe('lowerRoman');
  });
});

describe('DocumentEditorComponent — „Ustaw jako domyślne" akapitu (Doc2-PAR-007)', () => {
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

  it('NIE resetuje formularza do wartości bazowych (sedno regresji)', () => {
    component.paragraphData.alignment = 'center';
    component.paragraphData.spaceAfter = 24;
    component.paragraphData.lineSpacingValue = 2;

    component.setParagraphAsDefault();

    expect(component.paragraphData.alignment).toBe('center');
    expect(component.paragraphData.spaceAfter).toBe(24);
    expect(component.paragraphData.lineSpacingValue).toBe(2);
  });

  it('zapisany default seeduje dialog, gdy nie ma aktywnej selekcji', () => {
    component.paragraphData.alignment = 'right';
    component.paragraphData.spaceBefore = 12;
    component.setParagraphAsDefault();

    component.paragraphData.alignment = 'left';
    component.paragraphData.spaceBefore = 0;
    (component as any).readCurrentParagraphSettings();

    expect(component.paragraphData.alignment).toBe('right');
    expect(component.paragraphData.spaceBefore).toBe(12);
  });

  it('default jest niezależną kopią — późniejsza edycja formularza go nie zmienia', () => {
    component.paragraphData.indentLeft = 3;
    component.setParagraphAsDefault();

    component.paragraphData.indentLeft = 99;
    (component as any).readCurrentParagraphSettings();

    expect(component.paragraphData.indentLeft).toBe(3);
  });
});

describe('DocumentEditorComponent — pozycja menu kontekstowego (Doc2-UI-006)', () => {
  let fixture: ComponentFixture<DocumentEditorComponent>;
  let component: DocumentEditorComponent;
  let origW: number;
  let origH: number;

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
    origW = window.innerWidth;
    origH = window.innerHeight;
  });

  afterEach(() => {
    Object.defineProperty(window, 'innerWidth', { value: origW, configurable: true });
    Object.defineProperty(window, 'innerHeight', { value: origH, configurable: true });
  });

  function ctxEventAt(clientX: number, clientY: number): MouseEvent {
    const target = document.createElement('div');
    target.className = 'paper-container';
    document.body.appendChild(target);
    return { target, clientX, clientY, preventDefault: () => {} } as unknown as MouseEvent;
  }

  it('na niskim oknie clamp nie daje ujemnego y (menu nie zasłania toolbara)', () => {
    Object.defineProperty(window, 'innerWidth', { value: 1200, configurable: true });
    Object.defineProperty(window, 'innerHeight', { value: 300, configurable: true });

    component.onContextMenu(ctxEventAt(600, 280));

    expect(component.contextMenuY()).toBeGreaterThanOrEqual(8);
    expect(component.showContextMenu()).toBe(true);
  });

  it('przy prawej krawędzi przesuwa menu w lewo, by zmieściło się w viewport', () => {
    Object.defineProperty(window, 'innerWidth', { value: 1000, configurable: true });
    Object.defineProperty(window, 'innerHeight', { value: 900, configurable: true });

    component.onContextMenu(ctxEventAt(995, 100));

    expect(component.contextMenuX()).toBeLessThanOrEqual(1000 - 260 - 8);
    expect(component.contextMenuX()).toBeGreaterThanOrEqual(8);
  });

  it('w typowym miejscu otwiera się pod kursorem (bez zbędnego przesuwania)', () => {
    Object.defineProperty(window, 'innerWidth', { value: 1600, configurable: true });
    Object.defineProperty(window, 'innerHeight', { value: 1000, configurable: true });

    component.onContextMenu(ctxEventAt(400, 200));

    expect(component.contextMenuX()).toBe(400);
    expect(component.contextMenuY()).toBe(200);
  });
});

describe('DocumentEditorComponent — flow „Zakończ" (synchroniczna wysyłka)', () => {
  let fixture: ComponentFixture<DocumentEditorComponent>;
  let component: DocumentEditorComponent;
  let finishCalls: number;
  let finishResult: { next?: unknown; error?: unknown };
  let abortCalls: number;
  let continueCalls: number;
  let originalWindowClose: typeof window.close;
  let openResult: any;

  const deliveredOk = { deliveryId: 'd-1', status: 'Sent', documentStatus: 'Sent', delivered: true, error: null };
  const deliveryFailed = { deliveryId: 'd-1', status: 'RetryScheduled', documentStatus: 'DeliveryFailed', delivered: false, error: 'boom' };

  beforeEach(async () => {
    finishCalls = 0;
    abortCalls = 0;
    continueCalls = 0;
    finishResult = { next: deliveredOk };
    openResult = of({ html: '<p>ok</p>', metadata: {}, images: [], styles: [] });

    const storageMock = {
      finishAndSend: () => {
        finishCalls++;
        return finishResult.error
          ? throwError(() => finishResult.error)
          : of(finishResult.next);
      },
      abortSend: () => { abortCalls++; return of({ masterId: 'm-1', documentStatus: 'SendAborted', deliveryStatus: 'Cancelled' }); },
      continueDelivery: () => { continueCalls++; return of({ masterId: 'm-1', documentStatus: 'Queued', deliveryId: 'd-1', deliveryStatus: 'Pending' }); },
    };

    await TestBed.configureTestingModule({
      imports: [DocumentEditorComponent],
      providers: [
        { provide: DocumentService, useValue: {
            getTemplates: () => of([]),
            saveDocument: () => of(new Blob(['<p></p>'], { type: 'text/html' })),
            openDocument: () => openResult,
        } },
        { provide: DocumentStorageService, useValue: storageMock },
        { provide: Router, useValue: { navigate: () => {} } },
        { provide: ActivatedRoute, useValue: { queryParams: of({}) } },
        { provide: BuildInfoService, useValue: {} },
        { provide: MsalService, useValue: msalStub },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(DocumentEditorComponent);
    component = fixture.componentInstance;

    component.documentMasterId.set('m-1');
    component.documentVersionId.set('v-1');
    component.returnUrl.set('https://app.example.com/return');
    (component as any).blobToBase64 = () => Promise.resolve('PHA+PC9wPg==');

    originalWindowClose = window.close;
    (window as any).close = () => { };
  });

  afterEach(() => {
    (window as any).close = originalWindowClose;
    component.ngOnDestroy();
  });

  it('kliknięcie „Zakończ" pokazuje komunikat „Trwa wysyłanie dokumentu ..."', () => {
    component.finishDocument();

    expect(component.showSendingModal()).toBe(true);
    expect(component.showSendErrorModal()).toBe(false);
    expect(component.isFinishing()).toBe(true);
  });

  it('udana pierwsza próba → status „Sent", zamknięcie karty i stan końcowy', async () => {
    const origClose = window.close;
    let closeAttempts = 0;
    (window as any).close = () => { closeAttempts++; };

    try {
      component.autoSaveEnabled.set(true);
      component.finishDocument();

      await vi.waitFor(() => expect(component.workFinished()).toBe(true));

      expect(component.deliveryStatus()).toBe('Sent');
      expect(component.showSendingModal()).toBe(false);
      expect(component.showSendErrorModal()).toBe(false);
      expect(component.autoSaveEnabled()).toBe(false);
      expect(closeAttempts).toBe(1);
    } finally {
      (window as any).close = origClose;
    }
  });

  it('nieudana pierwsza próba → modal „Wystąpiły problemy..." z wyborem', async () => {
    finishResult = { next: deliveryFailed };

    component.finishDocument();

    await vi.waitFor(() => expect(component.showSendErrorModal()).toBe(true));
    expect(component.showSendingModal()).toBe(false);
    expect(component.workFinished()).toBe(false);
  });

  it('błąd HTTP pierwszej próby też pokazuje modal problemu', async () => {
    finishResult = { error: new Error('network') };

    component.finishDocument();

    await vi.waitFor(() => expect(component.showSendErrorModal()).toBe(true));
    expect(component.showSendingModal()).toBe(false);
  });

  it('„Przerwij" anuluje wysyłkę i cofa do edytora (bez zamknięcia karty)', async () => {
    const origClose = window.close;
    let closeAttempts = 0;
    (window as any).close = () => { closeAttempts++; };
    vi.spyOn(component as any, 'showSuccess').mockImplementation(() => {});

    try {
      finishResult = { next: deliveryFailed };
      component.finishDocument();
      await vi.waitFor(() => expect(component.showSendErrorModal()).toBe(true));

      component.abortSend();

      expect(abortCalls).toBe(1);
      expect(component.showSendErrorModal()).toBe(false);
      expect(component.isFinishing()).toBe(false);
      expect(component.workFinished()).toBe(false);
      expect(closeAttempts).toBe(0);
    } finally {
      (window as any).close = origClose;
    }
  });

  it('„Kontynuuj wysyłkę w tle" zamyka kartę i wchodzi w stan końcowy', async () => {
    const origClose = window.close;
    let closeAttempts = 0;
    (window as any).close = () => { closeAttempts++; };

    try {
      finishResult = { next: deliveryFailed };
      component.finishDocument();
      await vi.waitFor(() => expect(component.showSendErrorModal()).toBe(true));

      component.continueSendInBackground();

      expect(continueCalls).toBe(1);
      expect(component.showSendErrorModal()).toBe(false);
      expect(component.workFinished()).toBe(true);
      expect(closeAttempts).toBe(1);
    } finally {
      (window as any).close = origClose;
    }
  });

  it('wielokrotne kliknięcie „Zakończ" nie tworzy wielu równoległych flow', async () => {
    component.finishDocument();
    component.finishDocument();
    component.finishDocument();

    await vi.waitFor(() => expect(finishCalls).toBe(1));
  });

  it('w trybie podglądu (readOnly) „Zakończ" nie pokazuje żadnego modala', () => {
    component.readOnly.set(true);

    component.finishDocument();

    expect(component.showSendingModal()).toBe(false);
    expect(component.showSendErrorModal()).toBe(false);
    expect(component.isFinishing()).toBe(false);
  });

  describe('dokument chroniony przed edycją (isReadOnlyProtected)', () => {
    const content = (isReadOnlyProtected: boolean) => ({
      html: '<p>Plik tylko do odczytu</p>',
      metadata: {},
      images: [],
      styles: [],
      isReadOnlyProtected
    });

    it('_applyLoadedContent ustawia stan blokady, ale sam nie emituje komunikatu', () => {
      const showError = vi.spyOn(component as any, 'showError');
      const showSuccess = vi.spyOn(component as any, 'showSuccess');
      const showInfo = vi.spyOn(component as any, 'showInfo');

      (component as any)._applyLoadedContent(content(true), 'chroniony.docx');

      expect(component.documentEditProtected()).toBe(true);
      expect(component.editingDisabled()).toBe(true);
      expect(showError).not.toHaveBeenCalled();
      expect(showSuccess).not.toHaveBeenCalled();
      expect(showInfo).not.toHaveBeenCalled();
    });

    it('otwarcie chronionego dokumentu pokazuje JEDEN komunikat read-only jako INFO, bez sukcesu i błędu', () => {
      openResult = of(content(true));

      (component as any)._convertAndLoad(new File(['x'], 'chroniony.docx'), 'chroniony.docx', undefined, true);

      expect(component.documentEditProtected()).toBe(true);
      expect(component.infoMessage()).toContain('tylko do odczytu');
      expect(component.errorMessage()).toBeNull();
      expect(component.successMessage()).toBeNull();
    });

    it('ponowne wczytanie tego samego chronionego dokumentu znów daje INFO, nie błąd (13865553)', () => {
      openResult = of(content(true));
      const file = new File(['x'], 'chroniony.docx');

      (component as any)._convertAndLoad(file, 'chroniony.docx', undefined, true);
      (component as any)._convertAndLoad(file, 'chroniony.docx', undefined, true);

      expect(component.infoMessage()).toContain('tylko do odczytu');
      expect(component.errorMessage()).toBeNull();
    });

    it('otwarcie zwykłego dokumentu pokazuje sukces bez komunikatu read-only', () => {
      openResult = of(content(false));

      (component as any)._convertAndLoad(new File(['x'], 'zwykly.docx'), 'zwykly.docx', undefined, true);

      expect(component.documentEditProtected()).toBe(false);
      expect(component.successMessage()).toContain('Otwarto dokument');
      expect(component.errorMessage()).toBeNull();
    });

    it('showError zdejmuje wcześniejszy komunikat sukcesu (i odwrotnie)', () => {
      (component as any).showSuccess('Otwarto dokument: x');
      (component as any).showError('Tylko do odczytu');
      expect(component.successMessage()).toBeNull();
      expect(component.errorMessage()).toBe('Tylko do odczytu');

      (component as any).showSuccess('Zapisano');
      expect(component.errorMessage()).toBeNull();
      expect(component.successMessage()).toBe('Zapisano');
    });

    it('zapis chronionego dokumentu jest zablokowany (persistDocument nie jest wołany)', () => {
      vi.spyOn(component as any, 'showError').mockImplementation(() => {});
      const persist = vi.spyOn(component as any, 'persistDocument');

      (component as any)._applyLoadedContent(content(true), 'chroniony.docx');
      component.saveDocument();

      expect(persist).not.toHaveBeenCalled();
    });

    it('kolejny, niechroniony dokument zdejmuje blokadę', () => {
      (component as any)._applyLoadedContent(content(true), 'chroniony.docx');
      (component as any)._applyLoadedContent(content(false), 'zwykly.docx');

      expect(component.documentEditProtected()).toBe(false);
    });
  });

  it('wynik ze statusem „Sending" (delivered=false) NIE pokazuje modalu błędu', async () => {
    finishResult = { next: { deliveryId: 'd-1', status: 'Sending', documentStatus: 'Sending', delivered: false, error: null } };

    component.finishDocument();

    await vi.waitFor(() => expect(finishCalls).toBe(1));
    expect(component.showSendErrorModal()).toBe(false);
    expect(component.showSendingModal()).toBe(true);
    expect(component.deliveryStatus()).toBe('Sending');
    expect(component.workFinished()).toBe(false);
  });

  it('„Przerwij wysyłkę" w trakcie wysyłki anuluje (abortSend) i nie pokazuje błędu', async () => {
    vi.spyOn(component as any, 'showSuccess').mockImplementation(() => {});

    component.finishDocument();
    expect(component.showSendingModal()).toBe(true);

    component.cancelSend();

    await vi.waitFor(() => expect(abortCalls).toBe(1));
    expect(component.showSendingModal()).toBe(false);
    expect(component.showSendErrorModal()).toBe(false);
    expect(component.deliveryStatus()).toBe('Cancelled');
    expect(component.isFinishing()).toBe(false);
  });

  it('„Zamknij" w trakcie wysyłki zamyka okno bez błędu, a spóźniony błąd nie wyskakuje', async () => {
    finishResult = { next: deliveryFailed };

    component.finishDocument();
    component.closeSendingModal();

    expect(component.showSendingModal()).toBe(false);
    expect(component.workFinished()).toBe(true);

    await vi.waitFor(() => expect(finishCalls).toBe(1));
    expect(component.showSendErrorModal()).toBe(false);
  });
});

describe('DocumentEditorComponent — „Pobierz oryginał dokumentu"', () => {
  let component: DocumentEditorComponent;
  let baseVersionCalls: number;

  beforeEach(async () => {
    baseVersionCalls = 0;
    const storageMock = {
      downloadBaseVersion: () => { baseVersionCalls++; return of(new Blob(['orig'], { type: 'application/octet-stream' })); },
    };

    await TestBed.configureTestingModule({
      imports: [DocumentEditorComponent],
      providers: [
        { provide: DocumentService, useValue: { getTemplates: () => of([]) } },
        { provide: DocumentStorageService, useValue: storageMock },
        { provide: Router, useValue: { navigate: () => {} } },
        { provide: ActivatedRoute, useValue: { queryParams: of({}) } },
        { provide: BuildInfoService, useValue: {} },
        { provide: MsalService, useValue: msalStub },
      ],
    }).compileComponents();
    component = TestBed.createComponent(DocumentEditorComponent).componentInstance;
  });

  it('plik wczytany z dysku → opcja dostępna', () => {
    component.loadedFromDisk.set(true);
    component.userDownload.set(false);
    expect(component.canDownloadOriginal()).toBe(true);
  });

  it('dokument z aplikacji zewn. z userDownload=true → opcja dostępna', () => {
    component.loadedFromDisk.set(false);
    component.userDownload.set(true);
    expect(component.canDownloadOriginal()).toBe(true);
  });

  it('dokument z aplikacji zewn. bez userDownload → opcja niedostępna', () => {
    component.loadedFromDisk.set(false);
    component.userDownload.set(false);
    expect(component.canDownloadOriginal()).toBe(false);
  });

  it('pobranie oryginału z dysku oddaje wczytany plik (bez wołania API)', () => {
    const saveSpy = vi.spyOn(component as any, 'saveBlobToDisk').mockImplementation(() => {});
    vi.spyOn(component as any, 'showSuccess').mockImplementation(() => {});
    const file = new File(['orig'], 'oryginal.docx');
    component.loadedFromDisk.set(true);
    (component as any).diskOriginalFile = file;

    component.downloadOriginalDocument();

    expect(saveSpy).toHaveBeenCalledWith(file, 'oryginal.docx');
    expect(baseVersionCalls).toBe(0);
  });

  it('pobranie oryginału z aplikacji zewn. pobiera wersję bazową (v1) z API', () => {
    const saveSpy = vi.spyOn(component as any, 'saveBlobToDisk').mockImplementation(() => {});
    vi.spyOn(component as any, 'showSuccess').mockImplementation(() => {});
    component.loadedFromDisk.set(false);
    component.userDownload.set(true);
    component.documentMasterId.set('m-1');

    component.downloadOriginalDocument();

    expect(baseVersionCalls).toBe(1);
    expect(saveSpy).toHaveBeenCalled();
  });
});

describe('DocumentEditorComponent — dialog hasła', () => {
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

  it('confirm bez hasła nie zamyka i pokazuje błąd', () => {
    component.showPasswordDialog.set(true);
    component.passwordDialogValue = '';

    component.confirmPasswordDialog();

    expect(component.showPasswordDialog()).toBe(true);
    expect(component.passwordDialogError()).toBeTruthy();
  });

  it('cancel zamyka dialog i czyści stan', () => {
    component.showPasswordDialog.set(true);
    component.passwordDialogValue = TEST_DIALOG_PASSWORD;
    component.passwordDialogError.set('err');

    component.cancelPasswordDialog();

    expect(component.showPasswordDialog()).toBe(false);
    expect(component.passwordDialogValue).toBe('');
    expect(component.passwordDialogError()).toBeNull();
  });

  it('confirm z hasłem zamyka dialog', () => {
    component.showPasswordDialog.set(true);
    component.passwordDialogValue = TEST_DIALOG_PASSWORD;

    component.confirmPasswordDialog();

    expect(component.showPasswordDialog()).toBe(false);
  });
});

describe('DocumentEditorComponent — dialog hasła wyzwalany przy konwersji', () => {
  let fixture: ComponentFixture<DocumentEditorComponent>;
  let component: DocumentEditorComponent;
  let openCalls: any[][];
  let openResult: any;

  beforeEach(async () => {
    openCalls = [];
    openResult = of({ html: '', metadata: {}, styles: [] });
    await TestBed.configureTestingModule({
      imports: [DocumentEditorComponent],
      providers: [
        { provide: DocumentService, useValue: {
            getTemplates: () => of([]),
            openDocument: (...args: any[]) => { openCalls.push(args); return openResult; }
        } },
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

  it('PASSWORD_REQUIRED z /open otwiera dialog hasła', () => {
    openResult = throwError(() => new OpenDocumentError('zabezpieczony', 'PASSWORD_REQUIRED'));

    (component as any)._convertAndLoad(new File([], 'tajne.docx'), 'tajne.docx');

    expect(component.showPasswordDialog()).toBe(true);
  });

  it('zatwierdzenie hasła ponawia konwersję z podanym hasłem', () => {
    openResult = throwError(() => new OpenDocumentError('zabezpieczony', 'PASSWORD_REQUIRED'));
    (component as any)._convertAndLoad(new File([], 'tajne.docx'), 'tajne.docx');

    openResult = of({ html: '<p>ok</p>', metadata: {}, styles: [] });
    component.passwordDialogValue = TEST_DIALOG_PASSWORD;
    component.confirmPasswordDialog();

    expect(openCalls[openCalls.length - 1][1]).toBe(TEST_DIALOG_PASSWORD);
    expect(component.showPasswordDialog()).toBe(false);
  });

  it('WRONG_PASSWORD pokazuje dialog z komunikatem błędu', () => {
    openResult = throwError(() => new OpenDocumentError('złe', 'WRONG_PASSWORD'));

    (component as any)._convertAndLoad(new File([], 'tajne.docx'), 'tajne.docx');

    expect(component.showPasswordDialog()).toBe(true);
    expect(component.passwordDialogError()).toBeTruthy();
  });
});

describe('DocumentEditorComponent — globalne skróty edycji (Ctrl+Z/Y/X/C/V)', () => {
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

  function ctrlKey(key: string, opts: { target?: EventTarget; shift?: boolean; alt?: boolean } = {}): KeyboardEvent {
    let prevented = false;
    const ev = {
      key,
      ctrlKey: true,
      metaKey: false,
      altKey: !!opts.alt,
      shiftKey: !!opts.shift,
      target: opts.target ?? document.body,
      preventDefault: () => { prevented = true; },
      get defaultPrevented() { return prevented; },
    } as unknown as KeyboardEvent;
    component.onGlobalKeydown(ev);
    return ev;
  }

  function editableDiv(): HTMLElement {
    const div = document.createElement('div');
    div.setAttribute('contenteditable', 'true');
    document.body.appendChild(div);
    return div;
  }

  it('Ctrl+Z z fokusem poza kartką → cofnij (custom undo, nie natywny)', () => {
    const undo = vi.spyOn(component, 'undo').mockImplementation(() => {});
    const ev = ctrlKey('z');
    expect(undo).toHaveBeenCalledTimes(1);
    expect(ev.defaultPrevented).toBe(true);
  });

  it('Ctrl+Shift+Z → ponów', () => {
    const redo = vi.spyOn(component, 'redo').mockImplementation(() => {});
    ctrlKey('Z', { shift: true });
    expect(redo).toHaveBeenCalledTimes(1);
  });

  it('Ctrl+Y → ponów', () => {
    const redo = vi.spyOn(component, 'redo').mockImplementation(() => {});
    ctrlKey('y');
    expect(redo).toHaveBeenCalledTimes(1);
  });

  it('Ctrl+X → wytnij, Ctrl+C → kopiuj, Ctrl+V → wklej', () => {
    const cut = vi.spyOn(component, 'cut').mockImplementation(() => {});
    const copy = vi.spyOn(component, 'copy').mockImplementation(() => {});
    const paste = vi.spyOn(component, 'paste').mockImplementation(() => {});

    ctrlKey('x');
    ctrlKey('c');
    ctrlKey('v');

    expect(cut).toHaveBeenCalledTimes(1);
    expect(copy).toHaveBeenCalledTimes(1);
    expect(paste).toHaveBeenCalledTimes(1);
  });

  it('wewnątrz contenteditable NIE przechwytuje (obsługa należy do edytora/przeglądarki)', () => {
    const undo = vi.spyOn(component, 'undo').mockImplementation(() => {});
    const paste = vi.spyOn(component, 'paste').mockImplementation(() => {});
    const target = editableDiv();

    try {
      const evZ = ctrlKey('z', { target });
      const evV = ctrlKey('v', { target });
      expect(undo).not.toHaveBeenCalled();
      expect(paste).not.toHaveBeenCalled();
      expect(evZ.defaultPrevented).toBe(false);
      expect(evV.defaultPrevented).toBe(false);
    } finally {
      target.remove();
    }
  });

  it('w polu formularza (INPUT) NIE przechwytuje — natywne undo/wklejanie działa', () => {
    const undo = vi.spyOn(component, 'undo').mockImplementation(() => {});
    const input = document.createElement('input');
    document.body.appendChild(input);

    try {
      const ev = ctrlKey('z', { target: input });
      expect(undo).not.toHaveBeenCalled();
      expect(ev.defaultPrevented).toBe(false);
    } finally {
      input.remove();
    }
  });

  it('AltGr (Ctrl+Alt) nie wyzwala skrótów — polskie znaki ż/ź/ć są bezpieczne', () => {
    const undo = vi.spyOn(component, 'undo').mockImplementation(() => {});
    const cut = vi.spyOn(component, 'cut').mockImplementation(() => {});

    ctrlKey('z', { alt: true });
    ctrlKey('x', { alt: true });

    expect(undo).not.toHaveBeenCalled();
    expect(cut).not.toHaveBeenCalled();
  });

  it('w trybie read-only Ctrl+Z/X/V są nieaktywne, ale Ctrl+C (kopiuj) działa', () => {
    component.readOnly.set(true);
    const undo = vi.spyOn(component, 'undo').mockImplementation(() => {});
    const cut = vi.spyOn(component, 'cut').mockImplementation(() => {});
    const paste = vi.spyOn(component, 'paste').mockImplementation(() => {});
    const copy = vi.spyOn(component, 'copy').mockImplementation(() => {});

    ctrlKey('z');
    ctrlKey('x');
    ctrlKey('v');
    ctrlKey('c');

    expect(undo).not.toHaveBeenCalled();
    expect(cut).not.toHaveBeenCalled();
    expect(paste).not.toHaveBeenCalled();
    expect(copy).toHaveBeenCalledTimes(1);
  });

  it('Ctrl+F nadal otwiera wyszukiwanie (bez regresji), także z fokusem w input', () => {
    const open = vi.spyOn(component, 'openFindReplace').mockImplementation(() => {});
    const input = document.createElement('input');
    document.body.appendChild(input);

    try {
      const ev = ctrlKey('f', { target: input });
      expect(open).toHaveBeenCalledTimes(1);
      expect(ev.defaultPrevented).toBe(true);
    } finally {
      input.remove();
    }
  });
});

describe('DocumentEditorComponent — otwarcie PDF z edytora → podgląd (/viewer)', () => {
  let component: DocumentEditorComponent;
  let navigateToDocument: ReturnType<typeof vi.fn>;
  let uploadDocument: ReturnType<typeof vi.fn>;
  let router: { navigate: ReturnType<typeof vi.fn> };

  async function setup(uploadResult: unknown): Promise<void> {
    navigateToDocument = vi.fn();
    uploadDocument = vi.fn().mockReturnValue(uploadResult);
    router = { navigate: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [DocumentEditorComponent],
      providers: [
        { provide: DocumentService, useValue: { getTemplates: () => of([]) } },
        {
          provide: DocumentStorageService,
          useValue: {
            fileToBase64: vi.fn().mockResolvedValue('cGRm'),
            uploadDocument,
          },
        },
        { provide: DocumentNavigationService, useValue: { navigateToDocument } },
        { provide: Router, useValue: router },
        { provide: ActivatedRoute, useValue: { queryParams: of({}) } },
        { provide: BuildInfoService, useValue: {} },
        { provide: MsalService, useValue: msalStub },
      ],
    }).compileComponents();
    component = TestBed.createComponent(DocumentEditorComponent).componentInstance;
  }

  it('wgrywa PDF i nawiguje do podglądu przez DocumentNavigationService (nie /pdf-maintenance)', async () => {
    await setup(of({ masterId: 'master-1', versionId: 'v-1' }));

    await (component as any).openPdfInViewer(new File([], 'umowa.pdf'));

    expect(uploadDocument).toHaveBeenCalledWith({
      name: 'umowa.pdf',
      mimeType: 'application/pdf',
      content: 'cGRm',
    });
    expect(navigateToDocument).toHaveBeenCalledWith('master-1', 'application/pdf');
    expect(router.navigate).not.toHaveBeenCalled();
  });

  it('błąd uploadu → komunikat błędu, bez nawigacji, spinner zdjęty', async () => {
    await setup(throwError(() => new Error('boom')));

    await (component as any).openPdfInViewer(new File([], 'umowa.pdf'));

    expect(navigateToDocument).not.toHaveBeenCalled();
    expect(component.isLoading()).toBe(false);
    expect(component.errorMessage()).toContain('PDF');
  });
});

describe('DocumentEditorComponent — otwarcie DOCX z edytora → nowy dokument + nawigacja', () => {
  let component: DocumentEditorComponent;
  let navigateToEditableDocument: ReturnType<typeof vi.fn>;
  let uploadDocument: ReturnType<typeof vi.fn>;
  let saveDocumentVersion: ReturnType<typeof vi.fn>;
  let convertAndLoad: ReturnType<typeof vi.fn>;

  async function setup(uploadResult: unknown): Promise<void> {
    navigateToEditableDocument = vi.fn();
    uploadDocument = vi.fn().mockReturnValue(uploadResult);
    saveDocumentVersion = vi.fn().mockReturnValue(of({ versionId: 'v-new' }));
    await TestBed.configureTestingModule({
      imports: [DocumentEditorComponent],
      providers: [
        { provide: DocumentService, useValue: { getTemplates: () => of([]) } },
        {
          provide: DocumentStorageService,
          useValue: { fileToBase64: vi.fn().mockResolvedValue('ZG9jeA=='), uploadDocument, saveDocumentVersion },
        },
        { provide: DocumentNavigationService, useValue: { navigateToEditableDocument, navigateToDocument: vi.fn() } },
        { provide: Router, useValue: { navigate: vi.fn() } },
        { provide: ActivatedRoute, useValue: { queryParams: of({}) } },
        { provide: BuildInfoService, useValue: {} },
        { provide: MsalService, useValue: msalStub },
      ],
    }).compileComponents();
    component = TestBed.createComponent(DocumentEditorComponent).componentInstance;
    convertAndLoad = vi.fn();
    (component as any)._convertAndLoad = convertAndLoad;
  }

  it('DOCX: upload (v1) + wersja edytowalna (v2) + nawigacja na nowe id; bez wczytywania do bieżącego widoku', async () => {
    await setup(of({ masterId: 'm-new' }));
    component.documentMasterId.set('m-old');
    component.documentVersionId.set('v-old');

    await (component as any).openDocxAsNewDocument(new File(['x'], 'umowa.docx', { type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document' }));

    expect(uploadDocument).toHaveBeenCalledWith({
      name: 'umowa.docx',
      mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
      content: 'ZG9jeA==',
    });
    expect(saveDocumentVersion).toHaveBeenCalledWith('m-new', { content: 'ZG9jeA==' });
    expect(navigateToEditableDocument).toHaveBeenCalledWith('m-new', 'v-new');
    expect(convertAndLoad).not.toHaveBeenCalled();
    expect(component.documentMasterId()).toBe('m-old');
    expect(component.documentVersionId()).toBe('v-old');
  });

  it('.doc dostaje application/msword (jak dashboard)', async () => {
    await setup(of({ masterId: 'm-new' }));
    await (component as any).openDocxAsNewDocument(new File(['x'], 'stary.doc'));
    expect(uploadDocument.mock.calls[0][0].mimeType).toBe('application/msword');
  });

  it('błąd uploadu → komunikat, bez nawigacji, spinner zdjęty', async () => {
    await setup(throwError(() => new Error('boom')));
    await (component as any).openDocxAsNewDocument(new File(['x'], 'umowa.docx'));
    expect(navigateToEditableDocument).not.toHaveBeenCalled();
    expect(component.isLoading()).toBe(false);
    expect(component.errorMessage()).toContain('dokumentu');
  });
});

describe('DocumentEditorComponent — kontekst tabeli: strony, martwy DOM, determinizm (Problem 11)', () => {
  let fixture: ComponentFixture<DocumentEditorComponent>;
  let component: DocumentEditorComponent;
  const mounted: HTMLElement[] = [];

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

  afterEach(() => {
    mounted.splice(0).forEach(el => el.remove());
    window.getSelection()?.removeAllRanges();
  });

  function mount(el: HTMLElement): HTMLElement {
    document.body.appendChild(el);
    mounted.push(el);
    return el;
  }

  function caretIn(node: Node, offset = 0): void {
    const sel = window.getSelection()!;
    const range = document.createRange();
    range.setStart(node, offset);
    range.collapse(true);
    sel.removeAllRanges();
    sel.addRange(range);
  }

  it('detectTableContext widzi tabelę na NIEaktywnej stronie (findEditorContentContaining)', () => {
    const page1 = mount(document.createElement('div'));
    const page2 = mount(document.createElement('div'));
    page2.innerHTML = '<table><tbody><tr><td>x</td></tr></tbody></table>';
    (component as any).editor = {
      findEditorContentContaining: (n: Node) => (page2.contains(n) ? page2 : null),
      editorContent: { nativeElement: page1 },
    };
    caretIn(page2.querySelector('td')!.firstChild!);

    (component as any).detectTableContext();

    expect(component.isInTable()).toBe(true);
    expect(component.activeTable()).toBe(page2.querySelector('table'));
    expect(component.showTablePanel()).toBe(true);
  });

  it('odłączona activeTable + selekcja poza edytorem → kontekst czyszczony (bez martwego DOM)', () => {
    const detachedTable = document.createElement('table') as HTMLTableElement;
    const detachedCell = document.createElement('td') as HTMLTableCellElement;
    component.activeTable.set(detachedTable);
    component.activeTableCell.set(detachedCell);
    component.isInTable.set(true);
    component.showTablePanel.set(true);
    window.getSelection()?.removeAllRanges();

    (component as any).detectTableContext();

    expect(component.isInTable()).toBe(false);
    expect(component.activeTable()).toBeNull();
    expect(component.activeTableCell()).toBeNull();
    expect(component.showTablePanel()).toBe(false);
  });

  it('podłączona activeTable + selekcja poza edytorem → kontekst zachowany (anty-migotanie)', () => {
    const table = mount(document.createElement('table')) as HTMLTableElement;
    component.activeTable.set(table);
    component.isInTable.set(true);
    component.showTablePanel.set(true);
    window.getSelection()?.removeAllRanges();

    (component as any).detectTableContext();

    expect(component.isInTable()).toBe(true);
    expect(component.activeTable()).toBe(table);
    expect(component.showTablePanel()).toBe(true);
  });

  it('applyInsertTable → po zamknięciu dialogu detectTableContext jest wywoływany', async () => {
    (component as any).editor = {
      insertTable: vi.fn(),
      editorContent: { nativeElement: document.createElement('div') },
    };
    vi.spyOn(component as any, 'applyTableAutoFit').mockImplementation(() => {});
    const detectSpy = vi.spyOn(component as any, 'detectTableContext').mockImplementation(() => {});
    component.showInsertTableDialog.set(true);

    component.applyInsertTable();
    expect(component.showInsertTableDialog()).toBe(false);
    await Promise.resolve();

    expect(detectSpy).toHaveBeenCalled();
  });

  it('onInsertTable (szybkie wstawianie) też wywołuje detectTableContext po wstawieniu', async () => {
    (component as any).editor = {
      insertTable: vi.fn(),
      editorContent: { nativeElement: document.createElement('div') },
    };
    vi.spyOn(component as any, 'applyTableAutoFit').mockImplementation(() => {});
    const detectSpy = vi.spyOn(component as any, 'detectTableContext').mockImplementation(() => {});

    component.onInsertTable('3x3');
    await Promise.resolve();

    expect(detectSpy).toHaveBeenCalled();
  });
});

describe('DocumentEditorComponent — malarz formatów', () => {
  let component: DocumentEditorComponent;
  let applied: unknown[];

  const surface = (cls: string) => {
    const host = document.createElement('div');
    host.className = cls;
    const child = document.createElement('span');
    child.textContent = 'tekst do pomalowania';
    host.appendChild(child);
    document.body.appendChild(host);
    return child;
  };

  const selectInside = (el: Node, collapsed = false) => {
    const range = document.createRange();
    range.selectNodeContents(el);
    if (collapsed) range.collapse(true);
    vi.spyOn(window, 'getSelection').mockReturnValue({
      rangeCount: 1,
      isCollapsed: collapsed,
      getRangeAt: () => range,
    } as unknown as Selection);
  };

  const mouseUpOn = (target: Element | null) =>
    component.onFormatPainterMouseUp({ target } as unknown as MouseEvent);

  beforeEach(async () => {
    applied = [];
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
    component = TestBed.createComponent(DocumentEditorComponent).componentInstance;

    (component as any).editor = {
      getCurrentFormatting: () => ({ bold: true, fontSize: 15 }),
      applyFormatting: (f: unknown) => applied.push(f),
    };
    (component as any).toolbar = {
      formatPainterActive: () => (component as any)._painterArmed ?? false,
      applyFormatPainter: () => { (component as any)._painterArmed = false; component.onPasteFormat(); },
      deactivateFormatPainter: () => { (component as any)._painterArmed = false; },
    };
  });

  afterEach(() => {
    vi.restoreAllMocks();
    document.body.innerHTML = '';
  });

  const arm = () => { component.onCopyFormat(); (component as any)._painterArmed = true; };

  it('zaznaczenie w treści przy uzbrojonym pędzlu APLIKUJE formatowanie (regresja: nic się nie działo)', () => {
    arm();
    const el = surface('editor-content');
    selectInside(el);

    mouseUpOn(el);

    expect(applied).toHaveLength(1);
    expect(applied[0]).toEqual({ bold: true, fontSize: 15 });
  });

  it('puszczenie myszy poza tekstem, ale z zaznaczeniem w treści → maluje', () => {
    arm();
    const el = surface('editor-content');
    selectInside(el);
    const margin = document.createElement('div');
    margin.className = 'page';
    document.body.appendChild(margin);

    mouseUpOn(margin);

    expect(applied).toHaveLength(1);
  });

  it('działa także w paśmie nagłówka/stopki', () => {
    arm();
    const el = surface('header-editor-content');
    selectInside(el);

    mouseUpOn(el);

    expect(applied).toHaveLength(1);
  });

  it('zaznaczenie poza powierzchnią edytowalną nie maluje', () => {
    arm();
    const outside = document.createElement('p');
    outside.textContent = 'poza edytorem';
    document.body.appendChild(outside);
    selectInside(outside);

    mouseUpOn(outside);

    expect(applied).toHaveLength(0);
  });

  it('zaznaczenie przez granicę stron (dwa .editor-content) maluje', () => {
    arm();
    const p1 = surface('editor-content');
    const p2 = surface('editor-content');
    const range = document.createRange();
    range.setStart(p1.firstChild!, 0);
    range.setEnd(p2.firstChild!, 3);
    vi.spyOn(window, 'getSelection').mockReturnValue({
      rangeCount: 1, isCollapsed: false, getRangeAt: () => range,
    } as unknown as Selection);

    mouseUpOn(p2);

    expect(applied).toHaveLength(1);
  });

  it('pędzel rozbraja się po jednym użyciu (jak w Wordzie)', () => {
    arm();
    const el = surface('editor-content');
    selectInside(el);

    mouseUpOn(el);
    mouseUpOn(el);

    expect(applied).toHaveLength(1);
  });

  it('puszczenie myszy NA PASKU narzędzi to klik uzbrajający — nie maluje', () => {
    arm();
    const el = surface('editor-content');
    selectInside(el);
    const toolbarEl = document.createElement('d2-editor-toolbar');
    const btn = document.createElement('button');
    toolbarEl.appendChild(btn);
    document.body.appendChild(toolbarEl);

    mouseUpOn(btn);

    expect(applied).toHaveLength(0);
  });

  it('zwinięta karetka (sam klik, bez zaznaczenia) nie maluje', () => {
    arm();
    const el = surface('editor-content');
    selectInside(el, true);

    mouseUpOn(el);

    expect(applied).toHaveLength(0);
  });

  it('dokument w trybie tylko do odczytu nie daje się pomalować', () => {
    arm();
    component.readOnly.set(true);
    const el = surface('editor-content');
    selectInside(el);

    mouseUpOn(el);

    expect(applied).toHaveLength(0);
  });

  it('wejściem jest onCellMouseUp — delegacja do malarza musi zostać', () => {
    arm();
    const el = surface('editor-content');
    selectInside(el);

    component.onCellMouseUp({ target: el } as unknown as MouseEvent);

    expect(applied).toHaveLength(1);
  });

  it('nieuzbrojony pędzel ignoruje zaznaczenia', () => {
    component.onCopyFormat();
    const el = surface('editor-content');
    selectInside(el);

    mouseUpOn(el);

    expect(applied).toHaveLength(0);
  });

  it('klik w pędzel BEZ zaznaczenia rozbraja przycisk i mówi dlaczego', () => {
    (component as any).editor.getCurrentFormatting = () => null;
    const err = vi.spyOn(component as any, 'showError').mockImplementation(() => {});
    (component as any)._painterArmed = true;

    component.onCopyFormat();

    expect((component as any)._painterArmed).toBe(false);
    expect(err).toHaveBeenCalled();
  });
});
