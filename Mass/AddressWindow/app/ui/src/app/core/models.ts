/** Kontrakt api/letter-inspections (Mass.AddressWindow.Api). Enumy przychodzą jako nazwy. */

export type EnvelopeType = 'SingleWindow' | 'DoubleWindow';
export type WindowKind = 'Recipient' | 'Sender';
/** Czego szukamy w oknie: adresu czy nalepki R listu poleconego (grafiki). */
export type WindowContent = 'Address' | 'RegisteredLabel';
export type FindingSeverity = 'Info' | 'Warning' | 'Error';

export const ENVELOPE_TYPES: readonly EnvelopeType[] = ['SingleWindow', 'DoubleWindow'];

export const ENVELOPE_LABEL: Record<EnvelopeType, string> = {
  SingleWindow: 'Jedno okienko',
  DoubleWindow: 'Dwa okienka',
};

export const ENVELOPE_HINT: Record<EnvelopeType, string> = {
  SingleWindow: 'sprawdzany jest tylko adresat',
  DoubleWindow: 'sprawdzane są adres adresata i nalepka R w oknie nadawcy',
};

export const SEVERITY_LABEL: Record<FindingSeverity, string> = {
  Error: 'błąd',
  Warning: 'ostrzeżenie',
  Info: 'informacja',
};

/** Limit zgodny z PdfLetter.MaxSizeBytes po stronie API. */
export const MAX_FILE_SIZE_BYTES = 20 * 1024 * 1024;

export interface Finding {
  code: string;
  severity: FindingSeverity;
  message: string;
}

export interface AreaMm {
  left: number;
  top: number;
  width: number;
  height: number;
}

export interface Address {
  lines: string[];
  textBounds: AreaMm;
  minFontSizePt: number | null;
  maxFontSizePt: number | null;
}

/** Nalepka R wykryta w oknie: położenie i rozmiar grafiki na stronie. */
export interface RegisteredLabel {
  bounds: AreaMm;
  positionEstimated: boolean;
}

export interface Overflow {
  leftMm: number;
  topMm: number;
  rightMm: number;
  bottomMm: number;
  /** Gotowy opis po polsku, np. „z prawej o 9,7 mm”. */
  description: string;
}

export interface WindowInspection {
  kind: WindowKind;
  name: string;
  found: boolean;
  isValid: boolean;
  area: AreaMm;
  clearanceMm: number;
  content: WindowContent;
  /** Wykryty adres; null w oknie nalepki R. */
  address: Address | null;
  /** Wykryta nalepka R; null w oknie adresowym. */
  label: RegisteredLabel | null;
  overflow: Overflow | null;
  findings: Finding[];
}

export interface PageInfo {
  number: number;
  count: number;
  widthMm: number;
  heightMm: number;
}

export interface Preview {
  dataUri: string;
  widthPx: number;
  heightPx: number;
  renderedFromPdf: boolean;
}

export interface LetterInspection {
  fileName: string;
  envelope: EnvelopeType;
  isValid: boolean;
  /** false = pliku nie udało się odczytać (uszkodzony, zaszyfrowany); powód jest w documentFindings. */
  isReadable: boolean;
  summary: string;
  page: PageInfo | null;
  windows: WindowInspection[];
  documentFindings: Finding[];
  preview: Preview | null;
}

export interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
  errors?: Record<string, string[]>;
}
