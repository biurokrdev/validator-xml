/**
 * Generowanie poprawnych numerów R na potrzeby testów.
 * Algorytmy są lustrem RegisteredNumberFormatter (C#) i funkcji SQL w Mass/001_*.sql.
 */

const PP_GS1_PREFIX = '5900773';

/** GS1 mod 10: pozycje od prawej, nieparzyste x3, parzyste x1. */
export function gs1CheckDigit(digits: string): number {
  let sum = 0;
  for (let i = 0; i < digits.length; i++) {
    const d = Number(digits[digits.length - 1 - i]);
    sum += d * (i % 2 === 0 ? 3 : 1);
  }
  return (10 - (sum % 10)) % 10;
}

/** UPU S10: wagi 8 6 4 2 3 5 9 7, C = 11 - (S mod 11); 10 -> 0, 11 -> 5. */
export function s10CheckDigit(serial8: string): number {
  const w = [8, 6, 4, 2, 3, 5, 9, 7];
  let sum = 0;
  for (let i = 0; i < 8; i++) sum += Number(serial8[i]) * w[i];
  const c = 11 - (sum % 11);
  return c === 10 ? 0 : c === 11 ? 5 : c;
}

export function domestic(iac: number, kind: 1 | 4, serial: number): string {
  const body = `${iac}${PP_GS1_PREFIX}${kind}${String(serial).padStart(8, '0')}`;
  return `00${body}${gs1CheckDigit(body)}`;
}

export function international(serial: number, svc = 'RR', cc = 'PL'): string {
  const s = String(serial).padStart(8, '0');
  return `${svc}${s}${s10CheckDigit(s)}${cc}`;
}

/** Numer z celowo błędną cyfrą kontrolną (ostatnia cyfra +1 mod 10). */
export function corruptCheckDigit(fullNumber: string): string {
  const idx = fullNumber.length === 20 ? 19 : 10;
  const bad = (Number(fullNumber[idx]) + 1) % 10;
  return fullNumber.slice(0, idx) + bad + fullNumber.slice(idx + 1);
}

/**
 * Unikalna baza numeru seryjnego dla jednego testu. Pula w API jest wspólna między testami
 * i uruchomieniami, więc każdy scenariusz dostaje własny, losowy przedział.
 */
export function uniqueSerialBase(): number {
  // 8 cyfr: zostawiamy zapas na +1000 numerów w ramach testu.
  return 10_000_000 + Math.floor(Math.random() * 89_000_000);
}

export interface TestRange {
  type: 'Domestic' | 'International';
  first: string;
  numbers: string[];
  serialBase: number;
}

export function domesticRange(count: number, kind: 1 | 4 = 1): TestRange {
  const base = uniqueSerialBase();
  const iac = 1 + Math.floor(Math.random() * 9);
  const numbers = Array.from({ length: count }, (_, i) => domestic(iac, kind, base + i));
  return { type: 'Domestic', first: numbers[0], numbers, serialBase: base };
}

export function internationalRange(count: number): TestRange {
  const base = uniqueSerialBase();
  const numbers = Array.from({ length: count }, (_, i) => international(base + i));
  return { type: 'International', first: numbers[0], numbers, serialBase: base };
}
