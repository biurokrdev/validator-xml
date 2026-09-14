import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { finalize } from 'rxjs';
import {
  ALLOWED_ACTIONS,
  POOL_STATES,
  POOL_STATE_LABEL,
  POOL_TYPES,
  POOL_TYPE_LABEL,
  PagedResponse,
  PoolState,
  PoolStatistics,
  PoolType,
  RegisteredNumber,
  STATE_ACTION_LABEL,
  StateAction,
} from '../../core/models';
import { RegisteredNumbersService } from '../../core/registered-numbers.service';

/** Lista numerów R z filtrami, stronicowaniem, statystykami i zmianą stanu. */
@Component({
  selector: 'app-numbers-list',
  imports: [FormsModule, DatePipe],
  templateUrl: './numbers-list.html',
  styleUrl: './numbers-list.scss',
})
export class NumbersList {
  private readonly api = inject(RegisteredNumbersService);

  protected readonly types = POOL_TYPES;
  protected readonly states = POOL_STATES;
  protected readonly typeLabel = POOL_TYPE_LABEL;
  protected readonly stateLabel = POOL_STATE_LABEL;
  protected readonly actionLabel = STATE_ACTION_LABEL;

  // filtry
  protected readonly type = signal<PoolType | ''>('');
  protected readonly state = signal<PoolState | ''>('');
  protected readonly search = signal('');
  protected readonly page = signal(1);
  protected readonly pageSize = signal(25);

  // dane
  protected readonly result = signal<PagedResponse<RegisteredNumber> | null>(null);
  protected readonly stats = signal<PoolStatistics[]>([]);
  protected readonly loading = signal(false);
  protected readonly busyNumber = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);

  protected readonly items = computed(() => this.result()?.items ?? []);
  protected readonly totalPages = computed(() => this.result()?.totalPages ?? 0);

  /** Dostępne numery łącznie w obu pulach. */
  protected readonly totalAvailable = computed(() => this.stats().reduce((sum, s) => sum + s.available, 0));

  /** Polska liczba mnoga: 1 numer, 2-4 numery, 5+ numerów (z wyjątkiem 12-14). */
  protected plural(n: number, one: string, few: string, many: string): string {
    const abs = Math.abs(n);
    if (abs === 1) return one;
    const lastTwo = abs % 100;
    const last = abs % 10;
    if (last >= 2 && last <= 4 && (lastTwo < 12 || lastTwo > 14)) return few;
    return many;
  }

  constructor() {
    this.reload();
  }

  protected actionsFor(item: RegisteredNumber): readonly StateAction[] {
    return ALLOWED_ACTIONS[item.state];
  }

  protected applyFilters(): void {
    this.page.set(1);
    this.reload();
  }

  protected resetFilters(): void {
    this.type.set('');
    this.state.set('');
    this.search.set('');
    this.applyFilters();
  }

  protected goTo(page: number): void {
    if (page < 1 || page > this.totalPages()) return;
    this.page.set(page);
    this.reload();
  }

  protected reload(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api
      .list({
        type: this.type() || null,
        state: this.state() || null,
        search: this.search() || null,
        page: this.page(),
        pageSize: this.pageSize(),
      })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (r) => this.result.set(r),
        error: (e) => this.error.set(RegisteredNumbersService.errorMessage(e)),
      });

    this.api.stats().subscribe({
      next: (s) => this.stats.set(s),
      error: () => this.stats.set([]),
    });
  }

  protected change(item: RegisteredNumber, action: StateAction): void {
    if (action === 'Cancel' && !confirm(`Anulować numer ${item.fullNumber}? Operacji nie można cofnąć.`)) {
      return;
    }

    this.busyNumber.set(item.fullNumber);
    this.error.set(null);
    this.notice.set(null);

    this.api
      .changeState(item.fullNumber, action)
      .pipe(finalize(() => this.busyNumber.set(null)))
      .subscribe({
        next: (updated) => {
          this.notice.set(`${updated.fullNumber}: ${this.stateLabel[updated.state]}`);
          this.reload();
        },
        error: (e) => this.error.set(RegisteredNumbersService.errorMessage(e)),
      });
  }

  protected acquire(type: PoolType): void {
    this.error.set(null);
    this.notice.set(null);

    this.api.acquireNext(type).subscribe({
      next: (n) => {
        this.notice.set(`Pobrano i zarezerwowano numer ${n.fullNumber}.`);
        this.reload();
      },
      error: (e) => this.error.set(RegisteredNumbersService.errorMessage(e)),
    });
  }
}
