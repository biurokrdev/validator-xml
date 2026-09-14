import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { finalize } from 'rxjs';
import {
  POOL_STATE_LABEL,
  POOL_TYPE_LABEL,
  RangeCheckResponse,
  RangeImportResponse,
  RangeRequest,
} from '../../core/models';
import { RegisteredNumbersService } from '../../core/registered-numbers.service';

type RangeMode = 'count' | 'last';

/**
 * Zasilenie puli przedziałem numerów. Przepływ: wpisz przedział -> "Sprawdź" (co nowe, co już mamy)
 * -> "Zasil" (dostępne dopiero po sprawdzeniu, żeby nie importować w ciemno).
 */
@Component({
  selector: 'app-import-range',
  imports: [FormsModule, DatePipe],
  templateUrl: './import-range.html',
  styleUrl: './import-range.scss',
})
export class ImportRange {
  private readonly api = inject(RegisteredNumbersService);

  protected readonly typeLabel = POOL_TYPE_LABEL;
  protected readonly stateLabel = POOL_STATE_LABEL;

  // formularz
  protected readonly firstNumber = signal('');
  protected readonly mode = signal<RangeMode>('count');
  protected readonly count = signal<number | null>(1000);
  protected readonly lastNumber = signal('');

  // wynik
  protected readonly check = signal<RangeCheckResponse | null>(null);
  protected readonly imported = signal<RangeImportResponse | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly checking = signal(false);
  protected readonly importing = signal(false);

  /** Podpis przedziału, dla którego wykonano sprawdzenie; po zmianie pól sprawdzenie traci ważność. */
  private readonly checkedFor = signal<string | null>(null);

  protected readonly request = computed<RangeRequest>(() => ({
    firstNumber: this.firstNumber().trim(),
    count: this.mode() === 'count' ? this.count() : null,
    lastNumber: this.mode() === 'last' ? this.lastNumber().trim() || null : null,
  }));

  protected readonly formValid = computed(() => {
    const r = this.request();
    if (!r.firstNumber) return false;
    return this.mode() === 'count' ? (r.count ?? 0) >= 1 : !!r.lastNumber;
  });

  protected readonly checkIsCurrent = computed(() => this.checkedFor() === JSON.stringify(this.request()));

  protected readonly canImport = computed(
    () => this.checkIsCurrent() && !!this.check() && !this.check()!.isFullyDuplicated && !this.importing(),
  );

  protected onCheck(): void {
    if (!this.formValid()) return;
    this.error.set(null);
    this.imported.set(null);
    this.checking.set(true);

    const req = this.request();
    this.api
      .checkRange(req)
      .pipe(finalize(() => this.checking.set(false)))
      .subscribe({
        next: (r) => {
          this.check.set(r);
          this.checkedFor.set(JSON.stringify(req));
        },
        error: (e) => {
          this.check.set(null);
          this.checkedFor.set(null);
          this.error.set(RegisteredNumbersService.errorMessage(e));
        },
      });
  }

  protected onImport(): void {
    if (!this.canImport()) return;
    this.error.set(null);
    this.importing.set(true);

    this.api
      .importRange(this.request())
      .pipe(finalize(() => this.importing.set(false)))
      .subscribe({
        next: (r) => {
          this.imported.set(r);
          this.check.set(null);
          this.checkedFor.set(null);
        },
        error: (e) => this.error.set(RegisteredNumbersService.errorMessage(e)),
      });
  }

  protected resetForm(): void {
    this.firstNumber.set('');
    this.mode.set('count');
    this.count.set(1000);
    this.lastNumber.set('');
    this.check.set(null);
    this.checkedFor.set(null);
    this.imported.set(null);
    this.error.set(null);
  }
}
