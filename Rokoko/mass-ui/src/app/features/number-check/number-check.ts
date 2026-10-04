import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { finalize } from 'rxjs';
import { POOL_STATE_LABEL, POOL_TYPE_LABEL, ValidationResponse } from '../../core/models';
import { RegisteredNumbersService } from '../../core/registered-numbers.service';

/** Sprawdzenie pojedynczego numeru: format, cyfra kontrolna, obecność w puli i stan. */
@Component({
  selector: 'app-number-check',
  imports: [FormsModule],
  templateUrl: './number-check.html',
  styleUrl: './number-check.scss',
})
export class NumberCheck {
  private readonly api = inject(RegisteredNumbersService);

  protected readonly typeLabel = POOL_TYPE_LABEL;
  protected readonly stateLabel = POOL_STATE_LABEL;

  protected readonly number = signal('');
  protected readonly result = signal<ValidationResponse | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  protected onCheck(): void {
    const value = this.number().trim();
    if (!value) return;

    this.busy.set(true);
    this.error.set(null);

    this.api
      .validate(value)
      .pipe(finalize(() => this.busy.set(false)))
      .subscribe({
        next: (r) => this.result.set(r),
        error: (e) => {
          this.result.set(null);
          this.error.set(RegisteredNumbersService.errorMessage(e));
        },
      });
  }
}
