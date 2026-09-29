export type DiffSegmentKind = 'same' | 'removed' | 'added';

export interface DiffSegment {
  text: string;
  kind: DiffSegmentKind;
}

/** Powyżej tylu komórek tablicy LCS rezygnujemy z podświetlania (wynik: cała lewa usunięta, cała prawa dodana). */
const MAX_CELLS = 250_000;

function tokenize(text: string): string[] {
  return text.match(/\s+|[^\s]+/g) ?? [];
}

function push(target: DiffSegment[], text: string, kind: DiffSegmentKind): void {
  const last = target[target.length - 1];

  if (last && last.kind === kind) {
    last.text += text;
  } else {
    target.push({ text, kind });
  }
}

/**
 * Różnica dwóch tekstów na poziomie słów (LCS): segmenty `same` / `removed` (tylko w lewym) /
 * `added` (tylko w prawym). Lewa strona renderuje `same` + `removed`, prawa `same` + `added`.
 */
export function diffWords(left: string, right: string): DiffSegment[] {
  if (left === right) {
    return left ? [{ text: left, kind: 'same' }] : [];
  }

  const a = tokenize(left);
  const b = tokenize(right);

  if (a.length * b.length > MAX_CELLS) {
    const result: DiffSegment[] = [];
    if (left) result.push({ text: left, kind: 'removed' });
    if (right) result.push({ text: right, kind: 'added' });
    return result;
  }

  const rows = a.length + 1;
  const cols = b.length + 1;
  const table = new Uint32Array(rows * cols);

  for (let i = a.length - 1; i >= 0; i--) {
    for (let j = b.length - 1; j >= 0; j--) {
      table[i * cols + j] =
        a[i] === b[j]
          ? table[(i + 1) * cols + j + 1] + 1
          : Math.max(table[(i + 1) * cols + j], table[i * cols + j + 1]);
    }
  }

  const segments: DiffSegment[] = [];
  let i = 0;
  let j = 0;

  while (i < a.length && j < b.length) {
    if (a[i] === b[j]) {
      push(segments, a[i], 'same');
      i++;
      j++;
    } else if (table[(i + 1) * cols + j] >= table[i * cols + j + 1]) {
      push(segments, a[i], 'removed');
      i++;
    } else {
      push(segments, b[j], 'added');
      j++;
    }
  }

  for (; i < a.length; i++) push(segments, a[i], 'removed');
  for (; j < b.length; j++) push(segments, b[j], 'added');

  return segments;
}

export function leftSide(segments: DiffSegment[]): DiffSegment[] {
  return segments.filter((segment) => segment.kind !== 'added');
}

export function rightSide(segments: DiffSegment[]): DiffSegment[] {
  return segments.filter((segment) => segment.kind !== 'removed');
}
