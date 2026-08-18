
export interface HeaderFooterContent {
  html: string;
  height: number;
  differentFirstPage?: boolean;
  firstPageHtml?: string;
  differentOddEven?: boolean;
  oddHtml?: string;
  evenHtml?: string;
}

export interface PageSize {
  widthCm: number;
  heightCm: number;
  orientation: 'portrait' | 'landscape';
}

export interface SectionColumn {
  widthTwips: number;
  spaceTwips: number;
}

export interface ColumnLayout {
  count: number;
  equalWidth: boolean;
  spaceTwips: number;
  separator: boolean;
  columns?: SectionColumn[];
}

export interface SectionHeaderFooter {
  sectionIndex: number;
  header?: HeaderFooterContent;
  footer?: HeaderFooterContent;
}

export interface Footnote {
  id: string;
  html: string;
}

export interface Endnote {
  id: string;
  html: string;
}

export interface DocumentContent {
  html: string;
  metadata: DocumentMetadata;
  images: DocumentImage[];
  styles: DocumentStyle[];
  header?: HeaderFooterContent;
  footer?: HeaderFooterContent;
  margins?: PageMargins;
  pageSize?: PageSize;
  columns?: ColumnLayout;
  sectionHeadersFooters?: SectionHeaderFooter[];
  footnotes?: Footnote[];
  endnotes?: Endnote[];
  footnoteNumberFormat?: string;
  endnoteNumberFormat?: string;
  isReadOnlyProtected?: boolean;
}

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

export interface DocumentStyle {
  id: string;
  name: string;
  type: string;
  basedOn?: string;
  nextStyle?: string;
  
  fontFamily?: string;
  fontSize?: number;
  color?: string;
  isBold?: boolean;
  isItalic?: boolean;
  isUnderline?: boolean;
  
  alignment?: string;
  spaceBefore?: number;
  spaceAfter?: number;
  lineSpacing?: number;
  leftIndent?: number;
  rightIndent?: number;
  firstLineIndent?: number;
  
  outlineLevel?: number;
}

export interface PageMargins {
  top: number;
  bottom: number;
  left: number;
  right: number;
}

export interface PageSettings {
  margins: PageMargins;
  orientation: 'portrait' | 'landscape';
  paperSize: 'a4' | 'letter' | 'legal';
}

export const MARGIN_PRESETS: { name: string; margins: PageMargins }[] = [
  { name: 'Normalne', margins: { top: 2.5, bottom: 2.5, left: 2.5, right: 2.5 } },
  { name: 'Wąskie', margins: { top: 1.27, bottom: 1.27, left: 1.27, right: 1.27 } },
  { name: 'Średnie', margins: { top: 2.54, bottom: 2.54, left: 1.91, right: 1.91 } },
  { name: 'Szerokie', margins: { top: 2.54, bottom: 2.54, left: 5.08, right: 5.08 } },
  { name: 'Lustrzane', margins: { top: 2.54, bottom: 2.54, left: 3.18, right: 2.54 } },
];

export interface DocumentImage {
  id: string;
  contentType: string;
  base64Data: string;
}

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
  masterId?: string;
  footnoteNumberFormat?: string;
  endnoteNumberFormat?: string;
}

export interface DocumentTemplate {
  id: string;
  name: string;
  description: string;
}

export interface ImageUploadResponse {
  base64: string;
  fileName: string;
  size: number;
}

export interface TextFormatting {
  bold: boolean;
  italic: boolean;
  underline: boolean;
  strikethrough: boolean;
  subscript: boolean;
  superscript: boolean;
  alignment?: 'left' | 'center' | 'right' | 'justify';
  bulletList?: boolean;
  numberedList?: boolean;
}

export interface ParagraphStyle {
  fontFamily: string;
  fontSize: number;
  textColor: string;
  backgroundColor: string;
  alignment: 'left' | 'center' | 'right' | 'justify';
  lineHeight: number;
  blockFormat?: string;
}

export interface EditorState {
  isModified: boolean;
  canUndo: boolean;
  canRedo: boolean;
  wordCount: number;
  fontSize?: number;
  fontFamily?: string;
  fontMixed?: boolean;
  formattingMarks?: boolean;
  currentFormatting: TextFormatting;
  currentStyle: Partial<ParagraphStyle>;
}

export type ExportFormat = 'docx' | 'pdf' | 'html' | 'txt';

export type HeadingLevel = 1 | 2 | 3 | 4 | 5 | 6;

export type ListType = 'bullet' | 'numbered';

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
  | 'toggleCheckboxBullet'
  | 'heading1' | 'heading2' | 'heading3' | 'heading4' | 'heading5' | 'heading6'
  | 'paragraph';
