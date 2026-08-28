import { TestBed, ComponentFixture } from '@angular/core/testing';
import { vi } from 'vitest';
import { of, throwError } from 'rxjs';
import { Router } from '@angular/router';
import { AdminDeliveriesComponent } from './admin-deliveries';
import { DeliveryListItem, DeliveryStatus, DocumentStorageService } from '../../../services/document-storage.service';

function item(status: DeliveryStatus, id = 'd1'): DeliveryListItem {
  return {
    deliveryId: id, documentId: 'doc1', status, attemptCount: 0,
    createdAt: new Date().toISOString(), lastAttemptAt: null, nextAttemptAt: null,
    deadlineAt: new Date().toISOString(), lastError: null, lockedUntil: null,
    lockedBy: null, sourceVersionId: 'v1', recipientUrl: 'https://x',
    corporateKey: null,
  };
}

describe('AdminDeliveriesComponent — Anuluj / Wznów / autoodświeżanie', () => {
  let fixture: ComponentFixture<AdminDeliveriesComponent>;
  let component: AdminDeliveriesComponent;
  let storage: { getDeliveries: ReturnType<typeof vi.fn>; retryDelivery: ReturnType<typeof vi.fn>; cancelDelivery: ReturnType<typeof vi.fn>; updateDeliveryRecipientUrl: ReturnType<typeof vi.fn>; downloadDeliveryFile: ReturnType<typeof vi.fn>; };
  let navigateSpy: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    storage = {
      getDeliveries: vi.fn(() => of([])),
      retryDelivery: vi.fn(() => of({ deliveryId: 'd1', status: 'Pending' as DeliveryStatus })),
      cancelDelivery: vi.fn(() => of({ deliveryId: 'd1', status: 'Cancelled' as DeliveryStatus })),
      updateDeliveryRecipientUrl: vi.fn(() => of({ deliveryId: 'd1', recipientUrl: 'https://new.example.com', status: 'Pending' as DeliveryStatus })),
      downloadDeliveryFile: vi.fn(() => of(new Blob(['docx'], { type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document' }))),
    };
    navigateSpy = vi.fn();
    await TestBed.configureTestingModule({
      imports: [AdminDeliveriesComponent],
      providers: [
        { provide: DocumentStorageService, useValue: storage },
        { provide: Router, useValue: {
            navigate: navigateSpy,
            createUrlTree: (commands: unknown[], opts: { queryParams?: Record<string, string> }) =>
              ({ commands, queryParams: opts?.queryParams ?? {} }),
            serializeUrl: (tree: { commands: unknown[]; queryParams: Record<string, string> }) => {
              const qs = Object.entries(tree.queryParams).map(([k, v]) => `${k}=${v}`).join('&');
              return `${(tree.commands as string[]).join('/')}${qs ? `?${qs}` : ''}`;
            },
          } },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(AdminDeliveriesComponent);
    component = fixture.componentInstance;
  });

  it('canResume dla Cancelled/RetryScheduled/DeadLettered/FailedPermanently, nie dla Sending/Sent/Pending', () => {
    expect(component.canResume(item('Cancelled'))).toBe(true);
    expect(component.canResume(item('RetryScheduled'))).toBe(true);
    expect(component.canResume(item('DeadLettered'))).toBe(true);
    expect(component.canResume(item('FailedPermanently'))).toBe(true);
    expect(component.canResume(item('Sending'))).toBe(false);
    expect(component.canResume(item('Sent'))).toBe(false);
    expect(component.canResume(item('Pending'))).toBe(false);
  });

  it('canCancel tylko dla Pending i RetryScheduled', () => {
    expect(component.canCancel(item('Pending'))).toBe(true);
    expect(component.canCancel(item('RetryScheduled'))).toBe(true);
    expect(component.canCancel(item('Sending'))).toBe(false);
    expect(component.canCancel(item('Sent'))).toBe(false);
    expect(component.canCancel(item('Cancelled'))).toBe(false);
  });

  it('cancel() woła cancelDelivery i ustawia komunikat', () => {
    const ev = { stopPropagation: vi.fn() } as unknown as Event;
    component.cancel(item('Pending'), ev);

    expect(storage.cancelDelivery).toHaveBeenCalledWith('d1');
    expect(component.cancelingId()).toBeNull();
    expect(component.notice()).toContain('anulowane');
  });

  it('resume() woła retryDelivery i ustawia komunikat', () => {
    const ev = { stopPropagation: vi.fn() } as unknown as Event;
    component.resume(item('Cancelled'), ev);

    expect(storage.retryDelivery).toHaveBeenCalledWith('d1');
    expect(component.notice()).toContain('wznowione');
  });

  it('błąd akcji ustawia actionError, NIE error (tabela nie znika)', () => {
    storage.retryDelivery.mockReturnValueOnce(throwError(() => ({ error: { error: 'boom z backendu' } })));
    const ev = { stopPropagation: vi.fn() } as unknown as Event;

    component.resume(item('DeadLettered'), ev);

    expect(component.actionError()).toBe('boom z backendu');
    expect(component.error()).toBeNull();
    expect(component.dismissActionError).toBeTypeOf('function');
    component.dismissActionError();
    expect(component.actionError()).toBeNull();
  });

  it('copyEditLink kopiuje sformatowany link do edycji (z versionId) do schowka', async () => {
    const writeText = vi.fn((_url: string) => Promise.resolve());
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const ev = { stopPropagation: vi.fn() } as unknown as Event;
    const d = { ...item('Sent'), documentId: 'master-7', sourceVersionId: 'ver-9' };

    component.copyEditLink(d, ev);

    expect(writeText).toHaveBeenCalledTimes(1);
    const url = writeText.mock.calls[0][0] as string;
    expect(url).toContain('/editor?masterId=master-7&versionId=ver-9');
    expect(url).toMatch(/^https?:\/\//);
    await Promise.resolve();
    expect(component.notice()).toContain('Skopiowano');
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it('copyEditLink bez sourceVersionId pomija versionId w linku', () => {
    const writeText = vi.fn((_url: string) => Promise.resolve());
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const ev = { stopPropagation: vi.fn() } as unknown as Event;
    const d = { ...item('Sent'), documentId: 'master-7', sourceVersionId: '' };

    component.copyEditLink(d, ev);

    const url = writeText.mock.calls[0][0] as string;
    expect(url).toContain('/editor?masterId=master-7');
    expect(url).not.toContain('versionId=');
  });

  it('canEdit dla wszystkiego poza Sent/Sending', () => {
    expect(component.canEdit(item('Pending'))).toBe(true);
    expect(component.canEdit(item('RetryScheduled'))).toBe(true);
    expect(component.canEdit(item('DeadLettered'))).toBe(true);
    expect(component.canEdit(item('Cancelled'))).toBe(true);
    expect(component.canEdit(item('Sent'))).toBe(false);
    expect(component.canEdit(item('Sending'))).toBe(false);
  });

  it('startEdit ładuje bieżący adres do formularza', () => {
    const ev = { stopPropagation: vi.fn() } as unknown as Event;
    const it1 = { ...item('Pending'), recipientUrl: 'https://old.example.com' };
    component.startEdit(it1, ev);

    expect(component.editingId()).toBe('d1');
    expect(component.editUrlValue()).toBe('https://old.example.com');
  });

  it('saveEdit z poprawnym URL woła serwis, zamyka modal i ustawia komunikat', () => {
    const ev = { stopPropagation: vi.fn() } as unknown as Event;
    component.startEdit(item('Pending'), ev);
    component.editUrlValue.set('https://new.example.com');

    component.saveEdit();

    expect(storage.updateDeliveryRecipientUrl).toHaveBeenCalledWith('d1', 'https://new.example.com');
    expect(component.editingId()).toBeNull();
    expect(component.notice()).toContain('adres odbiorcy');
  });

  it('saveEdit z niepoprawnym URL nie woła serwisu i pokazuje błąd', () => {
    const ev = { stopPropagation: vi.fn() } as unknown as Event;
    component.startEdit(item('Pending'), ev);
    component.editUrlValue.set('zły-adres');

    component.saveEdit();

    expect(storage.updateDeliveryRecipientUrl).not.toHaveBeenCalled();
    expect(component.editError()).toBeTruthy();
    expect(component.editingId()).toBe('d1');
  });

  it('toggleAutoRefresh przełącza flagę', () => {
    expect(component.autoRefresh()).toBe(true);
    component.toggleAutoRefresh();
    expect(component.autoRefresh()).toBe(false);
  });

  it('statusLabel/statusClass obsługują Cancelled', () => {
    expect(component.statusLabel('Cancelled')).toBe('Anulowano');
    expect(component.statusClass('Cancelled')).toBe('status-cancelled');
  });

  it('ngOnDestroy odsubskrybowuje autoodświeżanie (brak wycieku timera)', () => {
    component.ngOnInit();
    component.ngOnDestroy();
    expect(() => component.ngOnDestroy()).not.toThrow();
  });

  it('„Pobierz plik” pobiera SNAPSHOT zadania (deliveries/{id}/download), nie wersję dokumentu, i nazywa plik .docx', () => {
    const trigger = vi.spyOn(component as any, 'triggerDownload').mockImplementation(() => undefined);
    const ev = { stopPropagation: vi.fn() } as unknown as Event;

    component.downloadFile(item('Sent', 'delivery-1234'), ev);

    expect(storage.downloadDeliveryFile).toHaveBeenCalledWith('delivery-1234');
    expect(trigger).toHaveBeenCalledTimes(1);
    const [, fileName, mime] = trigger.mock.calls[0] as [Blob, string, string];
    expect(fileName).toMatch(/^wyslany_.*\.docx$/);
    expect(mime).toContain('wordprocessingml');
    expect(component.downloadingId()).toBeNull();
    expect((ev.stopPropagation as unknown as ReturnType<typeof vi.fn>)).toHaveBeenCalled();
  });

  it('błąd pobrania snapshotu trafia do actionError (baner), a stan „pobieranie” jest zdejmowany', () => {
    storage.downloadDeliveryFile = vi.fn(() => throwError(() => ({ error: { error: 'plik jest niespójny' } })));
    vi.spyOn(component as any, 'triggerDownload').mockImplementation(() => undefined);

    component.downloadFile(item('Sent', 'd9'), { stopPropagation: vi.fn() } as unknown as Event);

    expect(component.downloadingId()).toBeNull();
    expect(component.actionError()).toContain('plik jest niespójny');
    expect(component.error()).toBeNull();
  });
});
