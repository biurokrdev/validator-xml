import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — resize tabeli vs zaznaczanie tekstu', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;
  let editor: HTMLDivElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;

    editor = document.createElement('div');
    editor.className = 'editor-content';
    editor.innerHTML = '<table><tbody><tr><td id="c">tekst w komorce</td></tr></tbody></table>';
    document.body.appendChild(editor);
    (component as any).getActiveEditor = () => editor;
  });

  afterEach(() => editor.remove());

  function mouseDown(detail: number): { event: MouseEvent; prevented: () => boolean } {
    let prevented = false;
    const event = {
      target: editor.querySelector('#c'),
      detail,
      clientX: 100,
      clientY: 50,
      preventDefault: () => { prevented = true; },
      stopPropagation: () => {},
    } as unknown as MouseEvent;
    return { event, prevented: () => prevented };
  }

  it('dwuklik (detail=2) omija strefę resize — mousedown idzie do zaznaczania tekstu', () => {
    const hit = { type: 'row', table: editor.querySelector('table'), colIndex: 0, rowIndex: 0 };
    let resizeStarted = false;
    (component as any).detectTableResizeHit = () => hit;
    (component as any).startTableResize = () => { resizeStarted = true; };

    const { event, prevented } = mouseDown(2);
    (component as any).handleEditorMouseDown(event);

    expect(resizeStarted).toBe(false);
    expect(prevented()).toBe(false);
  });

  it('pojedynczy klik (detail=1) w strefie nadal startuje resize', () => {
    const hit = { type: 'row', table: editor.querySelector('table'), colIndex: 0, rowIndex: 0 };
    let resizeStarted = false;
    (component as any).detectTableResizeHit = () => hit;
    (component as any).startTableResize = () => { resizeStarted = true; };

    const { event, prevented } = mouseDown(1);
    (component as any).handleEditorMouseDown(event);

    expect(resizeStarted).toBe(true);
    expect(prevented()).toBe(true);
  });

  it('klik w strefie bez ruchu myszy NIE commituje (bez dirty/undo od nieudanych kliknięć)', () => {
    let contentChanged = 0;
    (component as any).onContentChange = () => { contentChanged++; };
    const table = editor.querySelector('table') as HTMLTableElement;
    const hit = { type: 'row', table, colIndex: 0, rowIndex: 0 };
    const { event } = mouseDown(1);

    (component as any).startTableResize(hit, event);
    document.dispatchEvent(new MouseEvent('mouseup'));

    expect(contentChanged).toBe(0);
    expect(document.body.classList.contains('table-resizing')).toBe(false);
  });

  it('realne przeciągnięcie commituje (onContentChange po mousemove z deltą)', () => {
    let contentChanged = 0;
    (component as any).onContentChange = () => { contentChanged++; };
    const table = editor.querySelector('table') as HTMLTableElement;
    const hit = { type: 'row', table, colIndex: 0, rowIndex: 0 };
    const { event } = mouseDown(1);

    (component as any).startTableResize(hit, event);
    document.dispatchEvent(new MouseEvent('mousemove', { clientX: 100, clientY: 80 }));
    document.dispatchEvent(new MouseEvent('mouseup'));

    expect(contentChanged).toBe(1);
  });

  it('punkt nad glifami tekstu wyłącza strefę resize (dolna połowa liter przy tcMar=0)', () => {
    (component as any)._pointOverText = () => true;
    const eventLike = {
      target: editor.querySelector('#c'),
      clientX: 100,
      clientY: 50,
    } as unknown as MouseEvent;

    expect((component as any).detectTableResizeHit(eventLike)).toBeNull();
  });
});
