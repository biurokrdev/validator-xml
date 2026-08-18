import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';
import { ensureBulletMarkers, bulletGlyphFromContract } from '../../core/utils/list-label.util';

const PUA_UNCHECKED = String.fromCharCode(0xf071);
const PUA_CHECKED = String.fromCharCode(0xf0fe);

describe('WysiwygEditorComponent — punktory-checkboxy', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;
  let editor: HTMLDivElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;

    editor = document.createElement('div');
    editor.className = 'editor-content';
    document.body.appendChild(editor);
    (component as any).getActiveEditor = () => editor;
    (component as any).isSelectionInEditor = () => true;
    (component as any).onContentChange = () => {};
  });

  afterEach(() => editor.remove());

  function checkboxList(): void {
    editor.innerHTML =
      `<ul data-num-id="5" data-abstract-num-id="3" data-ilvl="0" data-num-fmt="bullet"` +
      ` data-lvl-text="${PUA_UNCHECKED}" data-bullet-font="Wingdings">` +
      '<li id="a"><span class="list-marker" contenteditable="false">❑</span>Alfa</li>' +
      '<li id="b"><span class="list-marker" contenteditable="false">❑</span>Beta</li>' +
      '<li id="c"><span class="list-marker" contenteditable="false">❑</span>Gamma</li></ul>';
  }

  function caretInLi(id: string): void {
    const li = editor.querySelector(`#${id}`)!;
    const textNode = li.lastChild!;
    const range = document.createRange();
    range.setStart(textNode, 1);
    range.collapse(true);
    const sel = window.getSelection()!;
    sel.removeAllRanges();
    sel.addRange(range);
  }

  it('przełącza punktor środkowego li na odhaczony — tylko ten punkt, w kodowaniu PUA', () => {
    checkboxList();
    caretInLi('b');

    component.toggleCheckboxBullet();

    const lists = Array.from(editor.querySelectorAll('ul'));
    expect(lists.length).toBe(3);

    const solo = editor.querySelector('#b')!.parentElement!;
    expect(solo.getAttribute('data-lvl-text')).toBe(PUA_CHECKED);
    expect(solo.getAttribute('data-lvl-override')).toBe('1');
    expect(solo.getAttribute('data-bullet-font')).toBe('Wingdings');
    expect(solo.getAttribute('data-num-id')).not.toBe('5');
    expect(editor.querySelector('#b .list-marker')!.textContent).toBe('☑');

    const head = editor.querySelector('#a')!.parentElement!;
    const tail = editor.querySelector('#c')!.parentElement!;
    expect(head.getAttribute('data-num-id')).toBe('5');
    expect(tail.getAttribute('data-num-id')).toBe('5');
    expect(head.getAttribute('data-lvl-text')).toBe(PUA_UNCHECKED);
    expect(editor.querySelector('#a .list-marker')!.textContent).toBe('❑');
  });

  it('ponowne przełączenie wraca do ORYGINALNEGO pustego znaku (❑, nie generycznego ☐)', () => {
    checkboxList();
    caretInLi('b');
    component.toggleCheckboxBullet();

    caretInLi('b');
    component.toggleCheckboxBullet();

    const solo = editor.querySelector('#b')!.parentElement!;
    expect(solo.getAttribute('data-lvl-text')).toBe(PUA_UNCHECKED);
    expect(editor.querySelector('#b .list-marker')!.textContent).toBe('❑');
  });

  it('lista ze zwykłą kropką nie jest ruszana (punktor nie jest checkboxem)', () => {
    editor.innerHTML =
      '<ul data-num-id="9" data-ilvl="0" data-num-fmt="bullet" data-lvl-text="•">' +
      '<li id="a"><span class="list-marker">•</span>Alfa</li></ul>';
    caretInLi('a');

    component.toggleCheckboxBullet();

    expect(editor.querySelectorAll('ul').length).toBe(1);
    expect(editor.querySelector('ul')!.getAttribute('data-lvl-text')).toBe('•');
  });

  it('ensureBulletMarkers syntetyzuje marker z kontraktu, gdy nie ma wzorca w kontenerze', () => {
    editor.innerHTML =
      `<ul data-num-id="5" data-ilvl="0" data-num-fmt="bullet"` +
      ` data-lvl-text="${PUA_UNCHECKED}" data-bullet-font="Wingdings">` +
      '<li>bez markera</li></ul>';

    ensureBulletMarkers([editor]);

    const marker = editor.querySelector<HTMLElement>('li > span.list-marker');
    expect(marker).not.toBeNull();
    expect(marker!.textContent).toBe('❑');
    expect(marker!.getAttribute('contenteditable')).toBe('false');
  });

  it('bulletGlyphFromContract mapuje PUA bez fontu i glify wprost', () => {
    expect(bulletGlyphFromContract(PUA_CHECKED, null)).toBe('☑');
    expect(bulletGlyphFromContract(String.fromCharCode(0xf0a8), 'Wingdings')).toBe('☐');
    expect(bulletGlyphFromContract('▪', 'Arial')).toBe('▪');
    expect(bulletGlyphFromContract(String.fromCharCode(0xf099), null)).toBe('•');
  });
});
