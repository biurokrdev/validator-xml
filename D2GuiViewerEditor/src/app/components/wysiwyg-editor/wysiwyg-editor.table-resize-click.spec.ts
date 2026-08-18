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

describe('WysiwygEditorComponent — strefa resize vs PUSTE komórki (Problem 11 RC-A)', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;
  let editor: HTMLDivElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;

    editor = document.createElement('div');
    editor.className = 'editor-content';
    editor.innerHTML =
      '<table><tbody><tr><td id="empty"><br></td><td id="full">tekst</td></tr></tbody></table>';
    document.body.appendChild(editor);
    (component as any).getActiveEditor = () => editor;
  });

  afterEach(() => editor.remove());

  function mouseDown(cellId: string): { event: MouseEvent; prevented: () => boolean } {
    let prevented = false;
    const event = {
      target: editor.querySelector('#' + cellId),
      detail: 1,
      clientX: 100,
      clientY: 50,
      preventDefault: () => { prevented = true; },
      stopPropagation: () => {},
    } as unknown as MouseEvent;
    return { event, prevented: () => prevented };
  }

  function stubHit(hitAtThreshold: (threshold: number | undefined) => boolean): {
    thresholds: (number | undefined)[];
    resizeStarted: () => boolean;
  } {
    const thresholds: (number | undefined)[] = [];
    const hit = { type: 'row', table: editor.querySelector('table'), colIndex: 0, rowIndex: 0 };
    let started = false;
    (component as any).detectTableResizeHit = (_e: MouseEvent, threshold?: number) => {
      thresholds.push(threshold);
      return hitAtThreshold(threshold) ? hit : null;
    };
    (component as any).startTableResize = () => { started = true; };
    return { thresholds, resizeStarted: () => started };
  }

  it('pusta komórka: hit tylko w szerokim pasie (6px) → fall-through do stawiania karetki', () => {
    const probe = stubHit(threshold => threshold === undefined);
    const { event, prevented } = mouseDown('empty');

    (component as any).handleEditorMouseDown(event);

    expect(probe.resizeStarted()).toBe(false);
    expect(prevented()).toBe(false);
    expect(probe.thresholds.length).toBe(2);
    expect(probe.thresholds[0]).toBeUndefined();
    expect(probe.thresholds[1]).toBeLessThan(6);
  });

  it('pusta komórka przy samej krawędzi: hit także w ciasnym pasie → resize startuje', () => {
    const probe = stubHit(() => true);
    const { event, prevented } = mouseDown('empty');

    (component as any).handleEditorMouseDown(event);

    expect(probe.resizeStarted()).toBe(true);
    expect(prevented()).toBe(true);
  });

  it('niepusta komórka: bez ponownego testu z ciasnym progiem — zachowanie bez zmian', () => {
    const probe = stubHit(() => true);
    const { event, prevented } = mouseDown('full');

    (component as any).handleEditorMouseDown(event);

    expect(probe.resizeStarted()).toBe(true);
    expect(prevented()).toBe(true);
    expect(probe.thresholds).toEqual([undefined]);
  });
});
