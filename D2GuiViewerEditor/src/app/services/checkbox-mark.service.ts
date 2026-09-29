import { Injectable, computed, signal } from '@angular/core';

/** Znak, którym edytor oznacza ZAZNACZONE pole wyboru — w liście kontrolnej i w pojedynczym polu. */
export type CheckboxMark = 'cross' | 'check' | 'tick';

export interface CheckboxMarkDefinition {
  id: CheckboxMark;
  /** Nazwa w menu toolbara. */
  label: string;
  /** Glif Unicode pokazywany w edytorze i zapisywany dla list z punktorem w Unicode. */
  glyph: string;
  /**
   * Młodszy bajt znaku w foncie Wingdings — listy Worda trzymają punktor w PUA (0xF000 | kod) z fontem Wingdings,
   * więc zaznaczony punkt wraca do DOCX w tym samym kodowaniu co reszta listy.
   */
  wingdingsCode: number;
  /** Kod Unicode (hex) i font dla definicji stanu pojedynczego pola (w14:checkedState). */
  sdtHex: string;
  sdtFont: string;
}

/** Kolejność = kolejność w menu; pierwszy jest domyślny. */
export const CHECKBOX_MARKS: readonly CheckboxMarkDefinition[] = [
  { id: 'cross', label: 'Krzyżyk', glyph: '☒', wingdingsCode: 0xfd, sdtHex: '2612', sdtFont: 'MS Gothic' },
  { id: 'check', label: 'Ptaszek', glyph: '☑', wingdingsCode: 0xfe, sdtHex: '2611', sdtFont: 'Segoe UI Symbol' },
  { id: 'tick', label: 'Ptaszek bez ramki', glyph: '✔', wingdingsCode: 0xfc, sdtHex: '2714', sdtFont: 'Segoe UI Symbol' },
];

export const DEFAULT_CHECKBOX_MARK: CheckboxMark = 'cross';

const STORAGE_KEY = 'd2.editor.checkboxMark';

/**
 * Wybór znaku zaznaczenia pól wyboru. Jedno źródło dla toolbara (menu „Pola wyboru") i edytora
 * (odhaczanie punktu listy, wstawianie i przełączanie pojedynczego pola). Domyślnie krzyżyk w ramce —
 * tak jak domyślny formant pola wyboru w Wordzie. Wybór jest preferencją użytkownika (localStorage),
 * nie właściwością dokumentu: dokument niesie znak w samych polach.
 */
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
      // Brak dostępu do localStorage (tryb prywatny, polityka) — wybór działa do końca sesji.
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
