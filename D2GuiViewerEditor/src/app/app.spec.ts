import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withXhr } from '@angular/common/http';
import { MsalService, MsalBroadcastService } from '@azure/msal-angular';
import { InteractionStatus } from '@azure/msal-browser';
import { of } from 'rxjs';
import { App } from './app';
import { BuildInfoService } from './core/services/build-info.service';
import { ConnectionStatusService } from './core/services/connection-status.service';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([]),
        provideHttpClient(withXhr()),
        {
          provide: MsalService,
          useValue: {
            instance: {
              enableAccountStorageEvents: () => {},
              getAllAccounts: () => [],
              getActiveAccount: () => null,
              setActiveAccount: () => {},
            },
          },
        },
        { provide: MsalBroadcastService, useValue: { inProgress$: of(InteractionStatus.None) } },
        { provide: BuildInfoService, useValue: { environment: signal('DEV') } },
        { provide: ConnectionStatusService, useValue: { isOffline: signal(false), dismiss: () => {} } },
      ],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('mounts the global banners container at the top of the shell', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('d2-global-banners')).not.toBeNull();
  });

  it('should render the root view', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('router-outlet')).not.toBeNull();
  });

  it('pokazuje startowy wskaźnik ładowania do aktywacji pierwszego widoku (MSAL + guardy)', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.app-boot-loading')).not.toBeNull();
    expect(compiled.textContent).toContain('Ładowanie aplikacji');

    fixture.componentInstance.onOutletActivate();
    fixture.detectChanges();
    expect(compiled.querySelector('.app-boot-loading')).toBeNull();
  });
});
