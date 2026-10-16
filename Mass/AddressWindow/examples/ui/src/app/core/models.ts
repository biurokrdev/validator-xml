// Kontrakt api/letter-inspections (upload, from-recipient). Enumy przychodzą jako nazwy.

export type EnvelopeType = 'SingleWindow' | 'DoubleWindow';
export type InspectionOutcome = 'Valid' | 'Invalid' | 'Inconclusive';
export type FindingSeverity = 'Info' | 'Warning' | 'Error' | 'Inconclusive';
export type RecipientMatch = 'NotChecked' | 'Matched' | 'Mismatch';
export type RegisteredMailKind = 'Domestic' | 'International';
export type DocumentFormat = 'Pdf' | 'Docx';
export type DocumentSource = 'Upload' | 'ExternalMock' | 'FormMock';

export const ENVELOPE_TYPES: readonly EnvelopeType[] = ['SingleWindow', 'DoubleWindow'];

export const ENVELOPE_LABEL: Record<EnvelopeType, string> = {
  SingleWindow: 'Jedno okienko: adres odbiorcy',
  DoubleWindow: 'Dwa okienka: adres odbiorcy i etykieta R',
};

export const OUTCOME_LABEL: Record<InspectionOutcome, string> = {
  Valid: 'poprawny',
  Invalid: 'niepoprawny',
  Inconclusive: 'nierozstrzygnięty',
};

export const MAIL_KIND_LABEL: Record<RegisteredMailKind, string> = {
  Domestic: 'krajowy',
  International: 'zagraniczny',
};

// Limit zgodny z LetterDocument.MaxSizeBytes po stronie API.
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

export interface RegisteredLabel {
  bounds: AreaMm;
  widthPx: number;
  heightPx: number;
  number: string | null;
  humanReadableNumber: string | null;
  kind: RegisteredMailKind | null;
  numberIsValid: boolean;
  symbology: string | null;
  barcodeModulePx: number | null;
}

export interface Overflow {
  leftMm: number;
  topMm: number;
  rightMm: number;
  bottomMm: number;
  description: string;
}

export interface ElementResult {
  status: InspectionOutcome;
  windowName: string;
  windowArea: AreaMm;
  found: boolean;
  address: Address | null;
  label: RegisteredLabel | null;
  overflow: Overflow | null;
  recipientMatch: RecipientMatch;
  reasons: Finding[];
}

export interface LetterInspection {
  status: InspectionOutcome;
  summary: string;
  envelope: EnvelopeType;
  document: { fileName: string; sourceFormat: DocumentFormat; convertedToPdf: boolean; source: DocumentSource };
  profile: { name: string; confirmed: boolean; note: string };
  page: { number: number; count: number; widthMm: number; heightMm: number } | null;
  address: ElementResult | null;
  label: ElementResult | null;
  documentReasons: Finding[];
  preview: { dataUri: string; widthPx: number; heightPx: number; renderedFromPdf: boolean } | null;
}

export interface LetterOrder {
  envelope: EnvelopeType;
  // Dowolny identyfikator źródła przekazywany aplikacji zewnętrznej; opcjonalny.
  source: string | null;
  recipientLines: string[];
  registeredNumber: string | null;
}

export interface FormCheck {
  envelope: EnvelopeType;
  formId: string;
}

export interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
  errors?: Record<string, string[]>;
}
