import { Injectable, computed, signal } from '@angular/core';

export type CheckboxMark = 'cross' | 'check' | 'tick';

export interface CheckboxMarkDefinition {
  id: CheckboxMark;
  label: string;
  glyph: string;
  wingdingsCode: number;
  sdtHex: string;
  sdtFont: string;
}

export const CHECKBOX_MARKS: readonly CheckboxMarkDefinition[] = [
  { id: 'cross', label: 'Krzyżyk', glyph: '☒', wingdingsCode: 0xfd, sdtHex: '2612', sdtFont: 'MS Gothic' },
  { id: 'check', label: 'Ptaszek', glyph: '☑', wingdingsCode: 0xfe, sdtHex: '2611', sdtFont: 'Segoe UI Symbol' },
  { id: 'tick', label: 'Ptaszek bez ramki', glyph: '✔', wingdingsCode: 0xfc, sdtHex: '2714', sdtFont: 'Segoe UI Symbol' },
];

export const DEFAULT_CHECKBOX_MARK: CheckboxMark = 'cross';

const STORAGE_KEY = 'd2.editor.checkboxMark';

@Injectable({ providedIn: 'root' })
export class CheckboxMarkService {
  private readonly _mark = signal<CheckboxMark>(this.read());

  readonly marks = CHECKBOX_MARKS;
  readonly mark = this._mark.asReadonly();
  readonly definition = computed<CheckboxMarkDefinition>(() => definitionOf(this._mark()));

  set(mark: CheckboxMark): void {
    if (!CHECKBOX_MARKS.some((candidate) => candidate.id === mark)) {
      return;
    }

    this._mark.set(mark);

    try {
      localStorage.setItem(STORAGE_KEY, mark);
    } catch {
    }
  }

  private read(): CheckboxMark {
    try {
      const stored = localStorage.getItem(STORAGE_KEY);
      return CHECKBOX_MARKS.some((candidate) => candidate.id === stored) ? (stored as CheckboxMark) : DEFAULT_CHECKBOX_MARK;
    } catch {
      return DEFAULT_CHECKBOX_MARK;
    }
  }
}

export function definitionOf(mark: CheckboxMark): CheckboxMarkDefinition {
  return CHECKBOX_MARKS.find((candidate) => candidate.id === mark) ?? CHECKBOX_MARKS[0];
}
