import { TestBed, ComponentFixture } from '@angular/core/testing';
import { vi } from 'vitest';
import { WysiwygEditorComponent } from './wysiwyg-editor';


describe('WysiwygEditorComponent — pastePlainTextAt (Wklej bez formatowania)', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;
  let editor: HTMLDivElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [WysiwygEditorComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;

    editor = document.createElement('div');
    editor.setAttribute('contenteditable', 'true');
    document.body.appendChild(editor);
    (component as any).getActiveEditor = () => editor;
    
    (component as any).onContentChange = vi.fn();
    (component as any).isSelectionInEditor = (sel: Selection) =>
      !!sel.anchorNode && editor.contains(sel.anchorNode);
  });

  afterEach(() => {
    editor.remove();
    window.getSelection()?.removeAllRanges();
  });

  
  function caretIn(node: Node, offset: number): Range {
    const range = document.createRange();
    range.setStart(node, offset);
    range.collapse(true);
    const sel = window.getSelection()!;
    sel.removeAllRanges();
    sel.addRange(range);
    return range;
  }

  it('inserts plain text at the captured TARGET bookmark, not the live selection', () => {
    
    editor.innerHTML =
      '<p><b style="color:red">SOURCE</b></p><p id="target"><br></p>';
    const target = editor.querySelector('#target')!;

    
    const bookmark = document.createRange();
    bookmark.setStart(target, 0);
    bookmark.collapse(true);

    
    const sourceText = editor.querySelector('b')!.firstChild!;
    caretIn(sourceText, 6);

    component.pastePlainTextAt(bookmark, 'plain');

    
    expect(target.textContent).toContain('plain');
    expect(editor.querySelector('b')!.textContent).toBe('SOURCE');
  });

  it('drops source formatting: inserted run has no bold/color/font marks', () => {
    editor.innerHTML = '<p id="target"><br></p>';
    const target = editor.querySelector('#target')!;
    const bookmark = document.createRange();
    bookmark.setStart(target, 0);
    bookmark.collapse(true);

    component.pastePlainTextAt(bookmark, 'hello');

    
    const inserted = [...target.childNodes].find(
      (n) => n.nodeType === Node.TEXT_NODE && n.textContent === 'hello',
    );
    expect(inserted).toBeTruthy();
    expect(target.querySelector('b, strong, span[style], font')).toBeNull();
  });

  it('escapes an inline formatting run at the caret (drops inherited color/bold)', () => {
    
    
    
    editor.innerHTML =
      '<p id="target">a<b style="color:red"><span style="color:red">RUN</span></b>b</p>';
    const runText = editor.querySelector('span')!.firstChild!; // "RUN"
    const bookmark = document.createRange();
    bookmark.setStart(runText, 1); // between R|UN, deep inside <b><span style=color>
    bookmark.collapse(true);

    component.pastePlainTextAt(bookmark, 'plain');

    
    const inserted = [...editor.querySelectorAll('#target')][0]!;
    const walker = document.createTreeWalker(inserted, NodeFilter.SHOW_TEXT);
    let plainNode: Node | null = null;
    while (walker.nextNode()) {
      if (walker.currentNode.textContent === 'plain') { plainNode = walker.currentNode; break; }
    }
    expect(plainNode).toBeTruthy();
    let ancestor = plainNode!.parentElement;
    while (ancestor && ancestor.id !== 'target') {
      expect(['B', 'STRONG', 'FONT'].includes(ancestor.tagName)).toBe(false);
      expect(ancestor.getAttribute('style')).toBeNull();
      ancestor = ancestor.parentElement;
    }
    
    expect(editor.textContent).toBe('aRplainUNb');
  });

  it('escapes the run even when the plain paste REPLACES the whole colored run', () => {
    
    
    editor.innerHTML = '<p id="target"><span style="color:red">RED</span></p>';
    const runText = editor.querySelector('span')!.firstChild!;
    const bookmark = document.createRange();
    bookmark.setStart(runText, 0);
    bookmark.setEnd(runText, 3); // whole "RED"

    component.pastePlainTextAt(bookmark, 'plain');

    expect(editor.textContent).toBe('plain');
    
    const target = editor.querySelector('#target')!;
    const styledSpan = target.querySelector('span[style]');
    expect(styledSpan?.textContent ?? '').not.toContain('plain');
  });

  it('replaces a non-empty selection', () => {
    editor.innerHTML = '<p id="target">keepXXXkeep</p>';
    const target = editor.querySelector('#target')!;
    const textNode = target.firstChild!;
    const bookmark = document.createRange();
    bookmark.setStart(textNode, 4); // before XXX
    bookmark.setEnd(textNode, 7); // after XXX

    component.pastePlainTextAt(bookmark, 'YES');

    expect(target.textContent).toBe('keepYESkeep');
  });

  it('leaves the caret directly after the inserted text', () => {
    editor.innerHTML = '<p id="target"><br></p>';
    const target = editor.querySelector('#target')!;
    const bookmark = document.createRange();
    bookmark.setStart(target, 0);
    bookmark.collapse(true);

    component.pastePlainTextAt(bookmark, 'abc');

    
    
    
    const caret: Range = (component as any).savedSelection;
    expect(caret).toBeTruthy();
    expect(caret.collapsed).toBe(true);
    const after = document.createRange();
    after.setStartAfter(target.firstChild!); // the inserted 'abc' text node
    after.collapse(true);
    expect(caret.compareBoundaryPoints(Range.START_TO_START, after)).toBe(0);
  });

  it('multi-line text becomes separate PARAGRAPHS, not soft <br> breaks (Word Keep Text Only)', () => {
    editor.innerHTML = '<p id="target"><br></p>';
    const target = editor.querySelector('#target')!;
    const bookmark = document.createRange();
    bookmark.setStart(target, 0);
    bookmark.collapse(true);

    component.pastePlainTextAt(bookmark, 'line1\nline2');

    const paras = Array.from(editor.querySelectorAll('p'));
    expect(paras.length).toBe(2);
    expect(paras[0].textContent).toBe('line1');
    expect(paras[1].textContent).toBe('line2');
    
    expect(paras[0].querySelector('br')).toBeNull();
  });

  it('multi-line paste mid-paragraph splits the host and keeps the tail after the last line', () => {
    editor.innerHTML = '<p id="target" style="text-align:center">ABCD</p>';
    const target = editor.querySelector('#target')!;
    const bookmark = document.createRange();
    bookmark.setStart(target.firstChild!, 2); // AB|CD
    bookmark.collapse(true);

    component.pastePlainTextAt(bookmark, 'one\ntwo\nthree');

    const paras = Array.from(editor.querySelectorAll('p'));
    expect(paras.map(p => p.textContent)).toEqual(['ABone', 'two', 'threeCD']);
    
    expect(paras[1].getAttribute('style')).toBe('text-align:center');
    expect(paras[2].getAttribute('style')).toBe('text-align:center');
    
    expect(paras[1].id).toBe('');
    expect(paras[2].id).toBe('');
  });

  it('blank line in a multi-line paste becomes an empty paragraph', () => {
    editor.innerHTML = '<p id="target"><br></p>';
    const target = editor.querySelector('#target')!;
    const bookmark = document.createRange();
    bookmark.setStart(target, 0);
    bookmark.collapse(true);

    component.pastePlainTextAt(bookmark, 'a\n\nb');

    const paras = Array.from(editor.querySelectorAll('p'));
    expect(paras.map(p => p.textContent)).toEqual(['a', '', 'b']);
    
    expect(paras[1].querySelector('br')).not.toBeNull();
  });

  it('multi-line paste into a list item creates sibling list items', () => {
    editor.innerHTML = '<ul><li id="target">punkt</li></ul>';
    const target = editor.querySelector('#target')!;
    const bookmark = document.createRange();
    bookmark.setStart(target.firstChild!, 5);
    bookmark.collapse(true);

    component.pastePlainTextAt(bookmark, 'x\ny');

    const items = Array.from(editor.querySelectorAll('li'));
    expect(items.map(li => li.textContent)).toEqual(['punktx', 'y']);
    expect(editor.querySelectorAll('ul').length).toBe(1);
  });

  it('keeps HTML-looking text literal (no injection)', () => {
    editor.innerHTML = '<p id="target"><br></p>';
    const target = editor.querySelector('#target')!;
    const bookmark = document.createRange();
    bookmark.setStart(target, 0);
    bookmark.collapse(true);

    component.pastePlainTextAt(bookmark, '<img src=x onerror=alert(1)>');

    expect(target.querySelector('img')).toBeNull();
    expect(target.textContent).toContain('<img src=x onerror=alert(1)>');
  });

  it('produces exactly one content change (one undo entry)', () => {
    editor.innerHTML = '<p id="target"><br></p>';
    const target = editor.querySelector('#target')!;
    const bookmark = document.createRange();
    bookmark.setStart(target, 0);
    bookmark.collapse(true);

    component.pastePlainTextAt(bookmark, 'once');

    expect((component as any).onContentChange).toHaveBeenCalledTimes(1);
  });

  it('aborts safely when the bookmark no longer lives in the editor', () => {
    editor.innerHTML = '<p id="target">unchanged</p>';
    
    const orphan = document.createElement('p');
    orphan.textContent = 'gone';
    const bookmark = document.createRange();
    bookmark.setStart(orphan.firstChild!, 0);
    bookmark.collapse(true);

    expect(() => component.pastePlainTextAt(bookmark, 'x')).not.toThrow();
    expect(editor.textContent).toBe('unchanged');
    expect((component as any).onContentChange).not.toHaveBeenCalled();
  });

  it('is a no-op for empty/whitespace-only clipboard text', () => {
    editor.innerHTML = '<p id="target">stay</p>';
    const target = editor.querySelector('#target')!;
    const bookmark = document.createRange();
    bookmark.setStart(target.firstChild!, 0);
    bookmark.collapse(true);

    component.pastePlainTextAt(bookmark, '   \n  ');

    expect(target.textContent).toBe('stay');
    expect((component as any).onContentChange).not.toHaveBeenCalled();
  });

  it('captureSelectionBookmark clones the live editor selection', () => {
    editor.innerHTML = '<p id="target">abc</p>';
    const textNode = editor.querySelector('#target')!.firstChild!;
    const live = caretIn(textNode, 1);

    const bookmark = component.captureSelectionBookmark();
    expect(bookmark).not.toBeNull();
    expect(bookmark!.startContainer).toBe(textNode);
    expect(bookmark!.startOffset).toBe(1);
    
    live.setStart(textNode, 3);
    expect(bookmark!.startOffset).toBe(1);
  });
});
