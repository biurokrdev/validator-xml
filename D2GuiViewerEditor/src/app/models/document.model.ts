/**
 * Modele dla edytora dokumentów Word
 */

/** Nagłówek/Stopka dokumentu */
export interface HeaderFooterContent {
  html: string;
  height: number; // wysokość w cm
  differentFirstPage?: boolean; // inna treść na pierwszej stronie
  firstPageHtml?: string;
  differentOddEven?: boolean; // różne parzyste i nieparzyste
  oddHtml?: string;
  evenHtml?: string;
}

/** Rozmiar i orientacja strony (w cm) */
export interface PageSize {
  widthCm: number;
  heightCm: number;
  orientation: 'portrait' | 'landscape';
}

/** Pojedyncza kolumna nierównego układu (twipy — jednostka DOCX). */
export interface SectionColumn {
  widthTwips: number;
  spaceTwips: number;
}

/**
 * Układ kolumn sekcji (odwzorowuje w:cols). Domyślnie 1 kolumna. `columns` wypełnione
 * tylko dla kolumn nierównych (equalWidth=false z jawnymi w:col); dla równych wystarczą
 * `count` + `spaceTwips` (ADR-0039).
 */
export interface ColumnLayout {
  count: number;
  equalWidth: boolean;
  spaceTwips: number;
  separator: boolean;
  columns?: SectionColumn[];
}

/**
 * Nagłówek/stopka JEDNEJ sekcji dokumentu wielosekcyjnego (indeks 0-based w kolejności
 * dokumentu). Wpisy istnieją tylko dla sekcji ≥ 1 z WŁASNYMI referencjami; sekcja bez
 * wpisu dziedziczy nagłówek/stopkę poprzedniej (jak Word). Sekcja 0 = pola header/footer.
 */
export interface SectionHeaderFooter {
  sectionIndex: number;
  header?: HeaderFooterContent;
  footer?: HeaderFooterContent;
}

/**
 * Przypis dolny. `id` to STABILNA wewnętrzna tożsamość (np. "fn-1"), niezależna od numeru
 * widocznego — numer wynika z kolejności odwołań w treści i jest liczony przy renderowaniu.
 * `html` to jedyne źródło prawdy dla treści; odwołania w treści dokumentu niosą tylko
 * `data-footnote-id`, nie kopię treści.
 */
export interface Footnote {
  id: string;
  html: string;
}

/**
 * Przypis końcowy. Tożsamość/numer jak w {@link Footnote} (`id` np. "en-1", stabilne;
 * numer z kolejności odwołań), ale ODDZIELNY typ — endnotes mają inną semantykę
 * renderowania (koniec dokumentu) i osobną część OOXML (word/endnotes.xml). Odwołania
 * w treści niosą tylko `data-endnote-id`.
 */
export interface Endnote {
  id: string;
  html: string;
}

/** Zawartość dokumentu z konwersji DOCX */
export interface DocumentContent {
  html: string;
  metadata: DocumentMetadata;
  images: DocumentImage[];
  styles: DocumentStyle[];
  header?: HeaderFooterContent;
  footer?: HeaderFooterContent;
  margins?: PageMargins;
  pageSize?: PageSize;
  /** Układ kolumn sekcji bazowej (0). Null/1 kolumna = jednokolumnowy (ADR-0039). */
  columns?: ColumnLayout;
  sectionHeadersFooters?: SectionHeaderFooter[];
  footnotes?: Footnote[];
  endnotes?: Endnote[];
  /**
   * Format numeracji przypisów z dokumentu (w:numFmt): 'decimal' | 'lowerRoman' | 'upperRoman'
   * | 'lowerLetter' | 'upperLetter'. Undefined = dokument nie ustala → domyślna Worda
   * (dolne = cyfry, końcowe = małe rzymskie). Tylko do wyświetlania (round-trip przez settings.xml).
   */
  footnoteNumberFormat?: string;
  endnoteNumberFormat?: string;
  /**
   * Dokument źródłowy jest chroniony przed edycją (settings.xml: wymuszone
   * w:documentProtection lub w:writeProtection). Edytor otwiera go tylko do odczytu.
   */
  isReadOnlyProtected?: boolean;
}

/** Metadane dokumentu */
export interface DocumentMetadata {
  title?: string;
  author?: string;
  subject?: string;
  keywords?: string;
  description?: string;
  category?: string;
  contentStatus?: string;
  lastModifiedBy?: string;
  revision?: string;
  version?: string;
  created?: string;
  modified?: string;
  pageCount?: number;
  wordCount?: number;
  company?: string;
  manager?: string;
  signatures?: DigitalSignatureInfo[];
}

/** Informacja o podpisie cyfrowym */
export interface DigitalSignatureInfo {
  signerName: string;
  signerEmail?: string;
  signerTitle?: string;
  certificateSubject: string;
  certificateIssuer: string;
  certificateSerialNumber: string;
  signedAt: string;
  certificateValidFrom: string;
  certificateValidTo: string;
  isValid: boolean;
  validationMessage?: string;
  reason?: string;
}

/** Request podpisania dokumentu */
export interface SignDocumentRequest {
  html: string;
  originalFileName?: string;
  metadata?: DocumentMetadata;
  header?: HeaderFooterContent;
  footer?: HeaderFooterContent;
  certificateBase64: string;
  certificatePassword: string;
  signerName: string;
  signerTitle?: string;
  signerEmail?: string;
  signatureReason?: string;
}

/** Styl dokumentu (Nagłówek 1, Normalny, itp.) */
export interface DocumentStyle {
  id: string;
  name: string;
  type: string; // paragraph, character
  basedOn?: string;
  nextStyle?: string;
  
  // Właściwości czcionki
  fontFamily?: string;
  fontSize?: number; // w punktach
  color?: string;
  isBold?: boolean;
  isItalic?: boolean;
  isUnderline?: boolean;
  
  // Właściwości paragrafu
  alignment?: string; // left, center, right, justify
  spaceBefore?: number; // w punktach
  spaceAfter?: number; // w punktach
  lineSpacing?: number; // mnożnik
  leftIndent?: number; // w cm
  rightIndent?: number; // w cm
  firstLineIndent?: number; // w cm
  
  // Poziom outline (dla nagłówków)
  outlineLevel?: number;
}

/** Marginesy dokumentu (w cm) */
export interface PageMargins {
  top: number;
  bottom: number;
  left: number;
  right: number;
}

/** Ustawienia strony */
export interface PageSettings {
  margins: PageMargins;
  orientation: 'portrait' | 'landscape';
  paperSize: 'a4' | 'letter' | 'legal';
}

/** Predefiniowane ustawienia marginesów */
export const MARGIN_PRESETS: { name: string; margins: PageMargins }[] = [
  { name: 'Normalne', margins: { top: 2.5, bottom: 2.5, left: 2.5, right: 2.5 } },
  { name: 'Wąskie', margins: { top: 1.27, bottom: 1.27, left: 1.27, right: 1.27 } },
  { name: 'Średnie', margins: { top: 2.54, bottom: 2.54, left: 1.91, right: 1.91 } },
  { name: 'Szerokie', margins: { top: 2.54, bottom: 2.54, left: 5.08, right: 5.08 } },
  { name: 'Lustrzane', margins: { top: 2.54, bottom: 2.54, left: 3.18, right: 2.54 } },
];

/** Obraz osadzony w dokumencie */
export interface DocumentImage {
  id: string;
  contentType: string;
  base64Data: string;
}

/** Request zapisu dokumentu */
export interface SaveDocumentRequest {
  html: string;
  originalFileName?: string;
  metadata?: DocumentMetadata;
  header?: HeaderFooterContent;
  footer?: HeaderFooterContent;
  margins?: PageMargins;
  pageSize?: PageSize;
  sectionHeadersFooters?: SectionHeaderFooter[];
  footnotes?: Footnote[];
  endnotes?: Endnote[];
  /** Gdy podane, backend zapisuje przez pass-through oryginalnego pakietu (zachowuje
   *  style tabel/motyw/numerację). Brak → pełna regeneracja pakietu. */
  masterId?: string;
  /** Efektywny format numeracji przypisów POKAZYWANY w edytorze (token w:numFmt) —
   *  writer emituje go jawnie, żeby zapisany plik wyglądał jak ekran. */
  footnoteNumberFormat?: string;
  endnoteNumberFormat?: string;
}

/** Szablon dokumentu */
export interface DocumentTemplate {
  id: string;
  name: string;
  description: string;
}

/** Odpowiedź z wgrywania obrazu */
export interface ImageUploadResponse {
  base64: string;
  fileName: string;
  size: number;
}

/** Konfiguracja formatowania tekstu */
export interface TextFormatting {
  bold: boolean;
  italic: boolean;
  underline: boolean;
  strikethrough: boolean;
  subscript: boolean;
  superscript: boolean;
  /** Wyrównanie akapitu pod karetką — stan podświetlenia przycisków toolbara. */
  alignment?: 'left' | 'center' | 'right' | 'justify';
  /** Karetka wewnątrz listy punktowanej (ul) / numerowanej (ol). */
  bulletList?: boolean;
  numberedList?: boolean;
  /** Karetka w liście pól wyboru (punktor ☐/☑) — wtedy bulletList/numberedList = false. */
  checkboxList?: boolean;
  /**
   * Stan pola, na którym zadziała „Zaznacz / Odznacz pole”: punkt listy kontrolnej pod karetką albo pierwszy
   * formant pola wyboru w bieżącym akapicie. null = pod karetką nie ma żadnego pola (pozycja menu wyszarzona).
   */
  checkboxState?: 'checked' | 'unchecked' | null;
}

/** Styl paragrafu */
export interface ParagraphStyle {
  fontFamily: string;
  fontSize: number;
  textColor: string;
  backgroundColor: string;
  alignment: 'left' | 'center' | 'right' | 'justify';
  lineHeight: number;
  blockFormat?: string;
}

/** Stan edytora */
export interface EditorState {
  isModified: boolean;
  canUndo: boolean;
  canRedo: boolean;
  wordCount: number;
  fontSize?: number;
  fontFamily?: string;
  /** True when the current selection spans more than one font family (item 6). */
  fontMixed?: boolean;
  /** Tryb „Pokaż wszystko" (¶) — widoczność znaczników formatowania. */
  formattingMarks?: boolean;
  currentFormatting: TextFormatting;
  currentStyle: Partial<ParagraphStyle>;
}

/** Opcje eksportu */
export type ExportFormat = 'docx' | 'pdf' | 'html' | 'txt';

/** Poziom nagłówka */
export type HeadingLevel = 1 | 2 | 3 | 4 | 5 | 6;

/** Typ listy */
export type ListType = 'bullet' | 'numbered';

/** Komendy edytora */
export type EditorCommand = 
  | 'bold' | 'italic' | 'underline' | 'strikethrough'
  | 'subscript' | 'superscript'
  | 'alignLeft' | 'alignCenter' | 'alignRight' | 'alignJustify'
  | 'justifyLeft' | 'justifyCenter' | 'justifyRight' | 'justifyFull'
  | 'indent' | 'outdent'
  | 'bulletList' | 'numberedList'
  | 'insertUnorderedList' | 'insertOrderedList'
  | 'insertLink' | 'insertImage' | 'insertTable'
  | 'undo' | 'redo'
  | 'selectAll'
  | 'removeFormat'
  | 'toggleFormattingMarks'
  | 'toggleCheckboxBullet' | 'checkboxList' | 'insertCheckboxControl' | 'applyCheckboxMark'
  | 'heading1' | 'heading2' | 'heading3' | 'heading4' | 'heading5' | 'heading6'
  | 'paragraph';
