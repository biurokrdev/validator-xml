import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { App } from './app';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
  });

  function render() {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('should create the app', () => {
    expect(render().fixture.componentInstance).toBeTruthy();
  });

  it('should show the numbers list by default', () => {
    const { el } = render();
    expect(el.querySelector('main')?.getAttribute('data-section')).toBe('numbers');
    expect(el.querySelector('app-numbers-list')).toBeTruthy();
    expect(el.querySelector('app-import-range')).toBeNull();
  });

  it('should switch section on button click', () => {
    const { fixture, el } = render();
    (el.querySelector('[data-testid="nav-import"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(el.querySelector('main')?.getAttribute('data-section')).toBe('import');
    expect(el.querySelector('app-import-range')).toBeTruthy();
    expect(el.querySelector('app-numbers-list')).toBeNull();
  });
});
