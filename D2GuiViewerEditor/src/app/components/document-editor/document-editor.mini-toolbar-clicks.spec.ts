import { TestBed, ComponentFixture } from '@angular/core/testing';
import { of } from 'rxjs';
import { ActivatedRoute, Router } from '@angular/router';
import { DocumentEditorComponent } from './document-editor';
import { DocumentService } from '../../services/document.service';
import { DocumentStorageService } from '../../services/document-storage.service';
import { BuildInfoService } from '../../core/services/build-info.service';
import { MsalService } from '@azure/msal-angular';

const msalStub = { instance: { getActiveAccount: () => null, getAllAccounts: () => [] } };

describe('DocumentEditorComponent — mini-toolbar nie kradnie kliknięć', () => {
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

  function mouseDownOn(el: HTMLElement, detail = 1): { event: MouseEvent; prevented: () => boolean } {
    let prevented = false;
    const event = {
      target: el,
      detail,
      preventDefault: () => { prevented = true; },
    } as unknown as MouseEvent;
    return { event, prevented: () => prevented };
  }

  it('mousedown w tło paska (poza kontrolkami) zamyka pasek i NIE robi preventDefault', () => {
    component.showMiniToolbar.set(true);
    const row = document.createElement('div');
    row.className = 'mt-row';
    const { event, prevented } = mouseDownOn(row);

    component.onMiniToolbarMouseDown(event);

    expect(component.showMiniToolbar()).toBe(false);
    expect(prevented()).toBe(false);
  });

  it('dwuklik w SELECT paska zamyka pasek (celowanie w słowo pod paskiem)', () => {
    component.showMiniToolbar.set(true);
    const select = document.createElement('select');
    const { event, prevented } = mouseDownOn(select, 2);

    component.onMiniToolbarMouseDown(event);

    expect(component.showMiniToolbar()).toBe(false);
    expect(prevented()).toBe(false);
  });

  it('pojedynczy klik w BUTTON paska robi preventDefault (ochrona selekcji) i NIE zamyka paska', () => {
    component.showMiniToolbar.set(true);
    const btn = document.createElement('button');
    const inner = document.createElement('span');
    btn.appendChild(inner);
    const { event, prevented } = mouseDownOn(inner);

    component.onMiniToolbarMouseDown(event);

    expect(component.showMiniToolbar()).toBe(true);
    expect(prevented()).toBe(true);
  });

  it('pojedynczy klik w SELECT nie zamyka paska (normalne otwarcie dropdownu)', () => {
    component.showMiniToolbar.set(true);
    const select = document.createElement('select');
    const { event, prevented } = mouseDownOn(select, 1);

    component.onMiniToolbarMouseDown(event);

    expect(component.showMiniToolbar()).toBe(true);
    expect(prevented()).toBe(false);
  });

  it('keydown w edytorze chowa pasek (jak w Wordzie)', () => {
    component.showMiniToolbar.set(true);

    component.onEditorKeyDownHideMiniToolbar();

    expect(component.showMiniToolbar()).toBe(false);
  });
});
