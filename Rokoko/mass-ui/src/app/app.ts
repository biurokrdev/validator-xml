import { NgTemplateOutlet } from '@angular/common';
import { Component, TemplateRef, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RegisteredNumbersService } from './core/registered-numbers.service';
import { ImportRange } from './features/import-range/import-range';
import { NumberCheck } from './features/number-check/number-check';
import { NumbersList } from './features/numbers-list/numbers-list';

/** Sekcje jednej strony. Przycisk w nagłówku pokazuje odpowiedni ng-template. */
export type Section = 'numbers' | 'import' | 'check';

@Component({
  selector: 'app-root',
  imports: [NgTemplateOutlet, FormsModule, NumbersList, ImportRange, NumberCheck],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  protected readonly api = inject(RegisteredNumbersService);

  protected readonly sections: readonly { id: Section; label: string }[] = [
    { id: 'numbers', label: 'Lista' },
    { id: 'import', label: 'Zasilenie' },
    { id: 'check', label: 'Sprawdź numer' },
  ];

  /** Aktywna sekcja; domyślnie lista. */
  protected readonly active = signal<Section>('numbers');

  private readonly numbersTpl = viewChild.required<TemplateRef<unknown>>('numbersTpl');
  private readonly importTpl = viewChild.required<TemplateRef<unknown>>('importTpl');
  private readonly checkTpl = viewChild.required<TemplateRef<unknown>>('checkTpl');

  protected show(section: Section): void {
    this.active.set(section);
  }

  protected activeTemplate(): TemplateRef<unknown> {
    switch (this.active()) {
      case 'import':
        return this.importTpl();
      case 'check':
        return this.checkTpl();
      default:
        return this.numbersTpl();
    }
  }
}
