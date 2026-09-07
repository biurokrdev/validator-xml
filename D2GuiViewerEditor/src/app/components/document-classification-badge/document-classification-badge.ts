import { Component, computed, input, ChangeDetectionStrategy } from '@angular/core';

@Component({
  selector: 'd2-document-classification-badge',
  standalone: true,
  templateUrl: './document-classification-badge.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './document-classification-badge.scss'
})
export class DocumentClassificationBadgeComponent {
  classification = input<string | null | undefined>(undefined);

  readonly value = computed(() => {
    const raw = this.classification();
    if (typeof raw !== 'string') return null;
    const trimmed = raw.trim();
    return trimmed.length > 0 ? trimmed : null;
  });

  readonly levelClass = computed(() => {
    const v = this.value();
    if (!v) return '';
    const known: Record<string, string> = {
      C1: 'lvl-c1',
      C2: 'lvl-c2',
      C3: 'lvl-c3',
      C4: 'lvl-c4'
    };
    return known[v.toUpperCase()] ?? 'lvl-unknown';
  });
}
