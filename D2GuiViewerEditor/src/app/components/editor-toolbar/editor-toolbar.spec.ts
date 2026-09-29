import { TestBed, ComponentFixture } from '@angular/core/testing';
import { vi } from 'vitest';
import { EditorToolbarComponent } from './editor-toolbar';
import { EditorCommand, EditorState } from '../../models/document.model';

/**
 * Testy przycisku „Cofnij" (undo) i „Ponów" (redo) w pasku narzędzi.
 *
 * Sprawdzają WIRING przycisku (widoczność, stan disabled/enabled, emitowaną komendę),
 * niezależnie od samego mechanizmu historii edytora (ten żyje w WysiwygEditorComponent
 * i opiera się na contenteditable/execCommand, których jsdom nie wspiera — patrz
 * scenariusz manualny w .ai/CHANGELOG.md).
 */
describe('EditorToolbarComponent — przyciski undo/redo', () => {
  let fixture: ComponentFixture<EditorToolbarComponent>;
  let component: EditorToolbarComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [EditorToolbarComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(EditorToolbarComponent);
    component = fixture.componentInstance;
  });

  function makeState(partial: Partial<EditorState>): EditorState {
    return {
      isModified: false,
      canUndo: false,
      canRedo: false,
      wordCount: 0,
      currentFormatting: {
        bold: false, italic: false, underline: false,
        strikethrough: false, subscript: false, superscript: false,
      },
      currentStyle: {},
      ...partial,
    };
  }

  function btn(titlePrefix: string): HTMLButtonElement {
    const el = Array.from(fixture.nativeElement.querySelectorAll('button'))
      .find((b) => (b as HTMLElement).getAttribute('title')?.startsWith(titlePrefix));
    return el as HTMLButtonElement;
  }

  it('renderuje przyciski Cofnij i Ponów w trybie edycji', () => {
    component.readOnly = false;
    fixture.detectChanges();
    expect(btn('Cofnij')).toBeTruthy();
    expect(btn('Ponów')).toBeTruthy();
  });

  it('Cofnij jest disabled, gdy nie ma czego cofnąć (canUndo=false)', () => {
    component.editorState = makeState({ canUndo: false });
    fixture.detectChanges();
    expect(btn('Cofnij').disabled).toBe(true);
  });

  it('Cofnij jest enabled po wykonaniu operacji (canUndo=true) i emituje komendę undo', () => {
    component.editorState = makeState({ canUndo: true });
    fixture.detectChanges();

    const undoBtn = btn('Cofnij');
    expect(undoBtn.disabled).toBe(false);

    const emitted: { command: EditorCommand; value?: string }[] = [];
    component.command.subscribe((e) => emitted.push(e));
    undoBtn.click();

    expect(emitted).toEqual([{ command: 'undo', value: undefined }]);
  });

  it('Ponów jest disabled, gdy redoStack jest pusty (canRedo=false)', () => {
    component.editorState = makeState({ canRedo: false });
    fixture.detectChanges();
    expect(btn('Ponów').disabled).toBe(true);
  });

  it('Ponów jest enabled po cofnięciu (canRedo=true) i emituje komendę redo', () => {
    component.editorState = makeState({ canRedo: true });
    fixture.detectChanges();

    const redoBtn = btn('Ponów');
    expect(redoBtn.disabled).toBe(false);

    const emitted: { command: EditorCommand; value?: string }[] = [];
    component.command.subscribe((e) => emitted.push(e));
    redoBtn.click();

    expect(emitted).toEqual([{ command: 'redo', value: undefined }]);
  });

  it('w trybie tylko-do-odczytu przyciski edycyjne (undo/redo) są ukryte', () => {
    component.readOnly = true;
    fixture.detectChanges();
    expect(btn('Cofnij')).toBeFalsy();
    expect(btn('Ponów')).toBeFalsy();
  });

  it('renderuje przycisk „Akapit" z aria-label i emituje openParagraph po kliknięciu', () => {
    component.readOnly = false;
    fixture.detectChanges();

    const akapit = btn('Akapit');
    expect(akapit).toBeTruthy();
    expect(akapit.getAttribute('aria-label')).toBe('Akapit');

    let opened = 0;
    component.openParagraph.subscribe(() => opened++);
    akapit.click();
    expect(opened).toBe(1);
  });
});

/**
 * Rozmiar czcionki z pola input — ENTER nie może kasować zaznaczonego tekstu.
 * Przyczyna: synchroniczny blur w trakcie ENTER → setFontSize przywraca zaznaczenie do edytora,
 * a domyślny ENTER kasuje je. Fix: preventDefault + odroczony blur (aplikacja przez blur).
 */
describe('EditorToolbarComponent — ENTER w polu rozmiaru czcionki', () => {
  let fixture: ComponentFixture<EditorToolbarComponent>;
  let component: EditorToolbarComponent;

  beforeEach(async () => {
    localStorage.removeItem('d2.editor.checkboxMark');
    await TestBed.configureTestingModule({ imports: [EditorToolbarComponent] }).compileComponents();
    fixture = TestBed.createComponent(EditorToolbarComponent);
    component = fixture.componentInstance;
  });

  it('ENTER blokuje domyślną akcję i odracza blur (nie aplikuje synchronicznie)', () => {
    vi.useFakeTimers();
    const input = document.createElement('input');
    input.value = '20';
    let prevented = false;
    const blurSpy = vi.spyOn(input, 'blur');
    const ev = { preventDefault: () => (prevented = true), target: input } as unknown as Event;

    component.onFontSizeInputEnter(ev);

    expect(prevented).toBe(true);              // domyślny ENTER zablokowany
    expect(blurSpy).not.toHaveBeenCalled();    // blur NIE jest synchroniczny (w trakcie ENTER)

    vi.runAllTimers();                         // dopiero po zdarzeniu ENTER
    expect(blurSpy).toHaveBeenCalledTimes(1);
    vi.useRealTimers();
  });

  it('blur (klik poza pole) aplikuje rozmiar od razu (emituje fontSizeChange)', () => {
    const input = document.createElement('input');
    input.value = '24';
    let emitted: number | undefined;
    component.fontSizeChange.subscribe(v => (emitted = v));

    component.onFontSizeInputBlur({ target: input } as unknown as Event);

    expect(emitted).toBe(24);
  });
});

/**
 * „Pokaż wszystko" (¶) — przycisk ZNÓW UKRYTY (2026-08-18, decyzja użytkownika;
 * wcześniej ukryty 2026-08-10, przywrócony 2026-08-14). Funkcja żyje pod
 * Ctrl+Shift+8; stan isFormattingMarksActive nadal czytany (gotowość na powrót).
 */
describe('EditorToolbarComponent — przycisk „Pokaż wszystko"', () => {
  let fixture: ComponentFixture<EditorToolbarComponent>;
  let component: EditorToolbarComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [EditorToolbarComponent] }).compileComponents();
    fixture = TestBed.createComponent(EditorToolbarComponent);
    component = fixture.componentInstance;
  });

  function marksBtn(): HTMLButtonElement | undefined {
    return Array.from(fixture.nativeElement.querySelectorAll('button'))
      .find((b) => (b as HTMLElement).getAttribute('title')?.startsWith('Pokaż wszystko')) as
      HTMLButtonElement | undefined;
  }

  it('przycisk NIE jest renderowany (ukryty decyzją użytkownika 2026-08-18)', () => {
    component.readOnly = false;
    fixture.detectChanges();

    expect(marksBtn()).toBeUndefined();
  });

  it('ukryte są też checkbox-punktor i „Więcej narzędzi" (ta sama decyzja)', () => {
    component.readOnly = false;
    fixture.detectChanges();
    const titles = Array.from(fixture.nativeElement.querySelectorAll('button'))
      .map((b) => (b as HTMLElement).getAttribute('title') ?? '');

    expect(titles.some((t) => t.startsWith('Przełącz punktor'))).toBe(false);
    expect(titles).not.toContain('Więcej narzędzi');
  });

  it('stan z EditorState.formattingMarks jest nadal czytany (gotowość na przywrócenie)', () => {
    expect(component.isFormattingMarksActive()).toBe(false);
    component.editorState = {
      isModified: false, canUndo: false, canRedo: false, wordCount: 0,
      formattingMarks: true,
      currentFormatting: {
        bold: false, italic: false, underline: false,
        strikethrough: false, subscript: false, superscript: false,
      },
      currentStyle: {},
    };
    expect(component.isFormattingMarksActive()).toBe(true);
  });
});

/**
 * Menu „Pola wyboru" (zgłoszenie: „element w toolbarze totalnie niezrozumiały"): dwie sekcje — „Wstaw"
 * (lista kontrolna / pojedyncze pole) i „Pole pod kursorem" (zaznacz / odznacz). Pozycja przełączania
 * mówi, CO zrobi (etykieta ze stanu pola pod karetką) i jest wyszarzona, gdy pod kursorem nie ma pola.
 */
describe('EditorToolbarComponent — menu pól wyboru', () => {
  let fixture: ComponentFixture<EditorToolbarComponent>;
  let component: EditorToolbarComponent;
  let emitted: { command: EditorCommand; value?: string }[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [EditorToolbarComponent] }).compileComponents();
    fixture = TestBed.createComponent(EditorToolbarComponent);
    component = fixture.componentInstance;
    emitted = [];
    component.command.subscribe((event) => emitted.push(event));
  });

  function open(formatting: Partial<EditorState['currentFormatting']>): void {
    component.editorState = {
      isModified: false, canUndo: false, canRedo: false, wordCount: 0,
      currentFormatting: {
        bold: false, italic: false, underline: false,
        strikethrough: false, subscript: false, superscript: false,
        ...formatting,
      },
      currentStyle: {},
    };
    component.toggleCheckboxMenu();
    fixture.detectChanges();
  }

  function options(): HTMLButtonElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('.checkbox-option')) as HTMLButtonElement[];
  }

  function title(option: HTMLElement): string {
    return (option.querySelector('.checkbox-option-title')!.textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  it('dzieli menu na „Wstaw" i „Pole pod kursorem", każda pozycja ma nazwę i opis', () => {
    open({});

    const sections = Array.from(fixture.nativeElement.querySelectorAll('.checkbox-menu-section'))
      .map((el) => (el as HTMLElement).textContent?.trim());
    expect(sections).toEqual(['Wstaw', 'Pole pod kursorem', 'Znak zaznaczenia']);
    expect(options().map(title)).toEqual(['Lista kontrolna', 'Pojedyncze pole wyboru', 'Zaznacz pole']);
    options().forEach((option) =>
      expect((option.querySelector('.checkbox-option-hint')!.textContent ?? '').trim().length).toBeGreaterThan(20));
  });

  it('bez pola pod kursorem „Zaznacz pole" jest wyszarzone i mówi, co zrobić', () => {
    open({ checkboxState: null });

    const toggle = options()[2];
    expect(toggle.disabled).toBe(true);
    expect(toggle.textContent).toContain('ustaw kursor w punkcie listy kontrolnej');
  });

  it('etykieta przełączania wynika ze stanu pola: puste → „Zaznacz pole", odhaczone → „Odznacz pole"', () => {
    open({ checkboxList: true, checkboxState: 'unchecked' });
    expect(options()[2].disabled).toBe(false);
    expect(title(options()[2])).toBe('Zaznacz pole');

    component.toggleCheckboxMenu();
    open({ checkboxList: true, checkboxState: 'checked' });
    expect(title(options()[2])).toBe('Odznacz pole');

    options()[2].click();
    expect(emitted).toEqual([{ command: 'toggleCheckboxBullet', value: undefined }]);
    expect(component.showCheckboxMenu()).toBe(false);
  });

  it('lista kontrolna pod kursorem jest oznaczona jako włączona i opisuje, że klik ją zdejmie', () => {
    open({ checkboxList: true, checkboxState: 'unchecked' });

    const list = options()[0];
    expect(list.classList.contains('selected')).toBe(true);
    expect(list.getAttribute('aria-checked')).toBe('true');
    expect(list.textContent).toContain('włączona');
    expect(list.textContent).toContain('z powrotem na zwykłe akapity');
  });

  it('sekcja „Znak zaznaczenia": krzyżyk domyślnie, wybór zapamiętany, zastosowany i widoczny w pozycji „Zaznacz pole"', () => {
    open({ checkboxList: true, checkboxState: 'unchecked' });

    const sections = Array.from(fixture.nativeElement.querySelectorAll('.checkbox-menu-section'))
      .map((el) => (el as HTMLElement).textContent?.trim());
    expect(sections).toEqual(['Wstaw', 'Pole pod kursorem', 'Znak zaznaczenia']);

    const marks = (): HTMLButtonElement[] => Array.from(fixture.nativeElement.querySelectorAll('.checkbox-mark'));
    expect(marks().map((mark) => mark.querySelector('.checkbox-mark-glyph')!.textContent)).toEqual(['☒', '☑', '✔']);
    expect(marks().map((mark) => mark.getAttribute('aria-checked'))).toEqual(['true', 'false', 'false']);
    expect(options()[2].querySelector('.checkbox-option-glyph')!.textContent).toBe('☒');

    marks()[1].click();
    fixture.detectChanges();

    expect(emitted).toEqual([{ command: 'applyCheckboxMark', value: undefined }]);
    expect(marks().map((mark) => mark.getAttribute('aria-checked'))).toEqual(['false', 'true', 'false']);
    expect(options()[2].querySelector('.checkbox-option-glyph')!.textContent).toBe('☑');
    expect(localStorage.getItem('d2.editor.checkboxMark')).toBe('check');
    // Menu zostaje otwarte — widać, co jest wybrane.
    expect(component.showCheckboxMenu()).toBe(true);
  });

  it('pozycje „Wstaw" emitują komendy edytora i zamykają menu', () => {
    open({});
    options()[0].click();
    component.toggleCheckboxMenu();
    fixture.detectChanges();
    options()[1].click();

    expect(emitted.map((event) => event.command)).toEqual(['checkboxList', 'insertCheckboxControl']);
    expect(component.showCheckboxMenu()).toBe(false);
  });
});
