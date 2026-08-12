import { ComponentFixture, TestBed } from '@angular/core/testing';
import { StructureXmlViewerComponent } from './structure-xml-viewer';

describe('StructureXmlViewerComponent — nawigacja wyszukiwania', () => {
  let fixture: ComponentFixture<StructureXmlViewerComponent>;
  let component: StructureXmlViewerComponent;

  const xml = '<w:document><w:body><w:p><w:t>anchor</w:t></w:p><w:p><w:t>tekst</w:t></w:p><w:p><w:t>anchor</w:t></w:p></w:body></w:document>';

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [StructureXmlViewerComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(StructureXmlViewerComponent);
    component = fixture.componentInstance;
    component.content = { title: 'test', meta: 'meta', xml, highlightLine: null };
  });

  it('liczy trafienia i pokazuje pozycję bieżącego', () => {
    component.onSearchChange('anchor');

    expect(component.matchLineNumbers()).toHaveLength(2);
    expect(component.matchPositionLabel()).toBe('1 / 2');
  });

  it('skacze po trafieniach z zawijaniem na końcach listy', () => {
    component.onSearchChange('anchor');
    const [first, second] = component.matchLineNumbers();

    expect(component.currentMatchNumber()).toBe(first);

    component.goToMatch(1);
    expect(component.currentMatchNumber()).toBe(second);

    component.goToMatch(1);
    expect(component.currentMatchNumber()).toBe(first);

    component.goToMatch(-1);
    expect(component.currentMatchNumber()).toBe(second);
  });

  it('Enter idzie do następnego, Shift+Enter do poprzedniego', () => {
    component.onSearchChange('anchor');
    const [first, second] = component.matchLineNumbers();

    component.onSearchKeydown(new KeyboardEvent('keydown', { key: 'Enter' }));
    expect(component.currentMatchNumber()).toBe(second);

    component.onSearchKeydown(new KeyboardEvent('keydown', { key: 'Enter', shiftKey: true }));
    expect(component.currentMatchNumber()).toBe(first);
  });

  it('skok rozwija zwiniętą gałąź zasłaniającą trafienie', () => {
    const root = component.lines()[0];
    component.toggleFold(root);
    expect(component.visibleLines()).toHaveLength(1);

    component.onSearchChange('anchor');
    component.goToMatch(1);

    const target = component.currentMatchNumber()!;
    expect(component.visibleLines().some((line) => line.number === target)).toBe(true);
  });

  it('zmiana frazy wraca na pierwsze trafienie', () => {
    component.onSearchChange('anchor');
    component.goToMatch(1);

    component.onSearchChange('tekst');

    expect(component.matchPositionLabel()).toBe('1 / 1');
  });
});
