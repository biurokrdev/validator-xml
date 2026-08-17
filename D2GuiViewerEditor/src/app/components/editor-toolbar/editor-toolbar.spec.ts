import { TestBed, ComponentFixture } from '@angular/core/testing';
import { vi } from 'vitest';
import { EditorToolbarComponent } from './editor-toolbar';
import { EditorCommand, EditorState } from '../../models/document.model';

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

describe('EditorToolbarComponent — ENTER w polu rozmiaru czcionki', () => {
  let fixture: ComponentFixture<EditorToolbarComponent>;
  let component: EditorToolbarComponent;

  beforeEach(async () => {
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

    expect(prevented).toBe(true);              
    expect(blurSpy).not.toHaveBeenCalled();    

    vi.runAllTimers();                         
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

  it('przycisk jest renderowany i klik emituje toggleFormattingMarks', () => {
    component.readOnly = false;
    fixture.detectChanges();
    const emitted: string[] = [];
    component.command.subscribe((c: { command: string }) => emitted.push(c.command));

    const btn = marksBtn();
    expect(btn).toBeTruthy();
    btn!.click();

    expect(emitted).toContain('toggleFormattingMarks');
  });

  it('przycisk podświetla się według EditorState.formattingMarks', () => {
    component.editorState = {
      isModified: false, canUndo: false, canRedo: false, wordCount: 0,
      formattingMarks: true,
      currentFormatting: {
        bold: false, italic: false, underline: false,
        strikethrough: false, subscript: false, superscript: false,
      },
      currentStyle: {},
    };
    fixture.detectChanges();

    expect(marksBtn()!.classList.contains('active')).toBe(true);
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
