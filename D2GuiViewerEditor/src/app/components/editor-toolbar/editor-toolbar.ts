import {
  Component,
  EventEmitter,
  Input,
  Output,
  signal,
  computed,
  effect,
  inject,
  HostListener
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { EditorCommand, EditorState, HeadingLevel, DocumentStyle } from '../../models/document.model';
import { FontProviderService } from '../../services/font-provider.service';


const DEFAULT_WORD_STYLES: DocumentStyle[] = [
  {
    id: 'Title',
    name: 'Tytuł',
    type: 'paragraph',
    fontFamily: 'Calibri Light',
    fontSize: 28,
    color: '#000000',
    isBold: false,
    isItalic: false,
    isUnderline: false
  },
  {
    id: 'Subtitle',
    name: 'Podtytuł',
    type: 'paragraph',
    fontFamily: 'Calibri',
    fontSize: 14,
    color: '#5A5A5A',
    isBold: false,
    isItalic: true,
    isUnderline: false
  },
  {
    id: 'Normal',
    name: 'Normalny',
    type: 'paragraph',
    fontFamily: 'Calibri',
    fontSize: 11,
    color: '#000000',
    isBold: false,
    isItalic: false,
    isUnderline: false,
    alignment: 'left',
    spaceAfter: 8,
    lineSpacing: 1.08
  },
  {
    id: 'Heading1',
    name: 'Nagłówek 1',
    type: 'paragraph',
    fontFamily: 'Calibri Light',
    fontSize: 16,
    color: '#2F5496',
    isBold: true,
    isItalic: false,
    isUnderline: false,
    spaceBefore: 12,
    outlineLevel: 1
  },
  {
    id: 'Heading2',
    name: 'Nagłówek 2',
    type: 'paragraph',
    fontFamily: 'Calibri Light',
    fontSize: 13,
    color: '#2F5496',
    isBold: true,
    isItalic: false,
    isUnderline: false,
    spaceBefore: 2,
    outlineLevel: 2
  },
  {
    id: 'Heading3',
    name: 'Nagłówek 3',
    type: 'paragraph',
    fontFamily: 'Calibri Light',
    fontSize: 12,
    color: '#1F3763',
    isBold: true,
    isItalic: false,
    isUnderline: false,
    spaceBefore: 2,
    outlineLevel: 3
  },
  {
    id: 'Heading4',
    name: 'Nagłówek 4',
    type: 'paragraph',
    fontFamily: 'Calibri Light',
    fontSize: 11,
    color: '#2F5496',
    isBold: true,
    isItalic: true,
    isUnderline: false,
    outlineLevel: 4
  },
  {
    id: 'Heading5',
    name: 'Nagłówek 5',
    type: 'paragraph',
    fontFamily: 'Calibri Light',
    fontSize: 11,
    color: '#2F5496',
    isBold: false,
    isItalic: false,
    isUnderline: false,
    outlineLevel: 5
  },
  {
    id: 'Heading6',
    name: 'Nagłówek 6',
    type: 'paragraph',
    fontFamily: 'Calibri Light',
    fontSize: 11,
    color: '#1F3763',
    isBold: false,
    isItalic: true,
    isUnderline: false,
    outlineLevel: 6
  }
];


@Component({
  selector: 'd2-editor-toolbar',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './editor-toolbar.html',
  styleUrl: './editor-toolbar.scss'
})
export class EditorToolbarComponent {
  private readonly fontProvider = inject(FontProviderService);
  private _editorState: EditorState | null = null;

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    
    if ((event.target as HTMLElement).closest('d2-editor-toolbar')) {
      return;
    }
    this.showStyleDropdown.set(false);
  }
  
  @Input() set editorState(state: EditorState | null) {
    this._editorState = state;
    this.updateFromEditorState(state);
  }
  
  get editorState(): EditorState | null {
    return this._editorState;
  }
  
  
  @Input() readOnly = false;

  @Input() set documentStyles(styles: DocumentStyle[] | null) {
    if (styles && styles.length > 0) {
      this._documentStyles.set(styles);
    } else {
      this._documentStyles.set(DEFAULT_WORD_STYLES);
    }
  }
  
  @Output() command = new EventEmitter<{ command: EditorCommand; value?: string }>();
  @Output() fontSizeChange = new EventEmitter<number>();
  @Output() fontFamilyChange = new EventEmitter<string>();
  @Output() textColorChange = new EventEmitter<string>();
  @Output() backgroundColorChange = new EventEmitter<string>();
  @Output() insertLink = new EventEmitter<{ url: string; text?: string }>();
  @Output() insertImage = new EventEmitter<void>();
  @Output() insertTable = new EventEmitter<string>();
  @Output() openTableDialog = new EventEmitter<void>();
  @Output() insertFootnote = new EventEmitter<void>();
  @Output() insertEndnote = new EventEmitter<void>();
  @Output() insertBarcode = new EventEmitter<void>();
  @Output() styleChange = new EventEmitter<DocumentStyle>();
  @Output() copyFormat = new EventEmitter<void>();
  @Output() pasteFormat = new EventEmitter<void>();
  @Output() searchInDocument = new EventEmitter<{ text: string; direction: 'next' | 'previous' }>();
  @Output() replaceInDocument = new EventEmitter<{ searchText: string; replaceText: string; all: boolean }>();
  
  @Output() preserveSelection = new EventEmitter<void>();
  @Output() clearSearch = new EventEmitter<void>();
  
  @Output() openSearch = new EventEmitter<void>();
  
  @Output() openParagraph = new EventEmitter<void>();

  
  private _documentStyles = signal<DocumentStyle[]>(DEFAULT_WORD_STYLES);
  
  
  blockFormats = computed(() => {
    return this._documentStyles().map(style => ({
      value: this.styleIdToCommand(style.id),
      label: style.name,
      style: style
    }));
  });

  
  readonly fontFamilies = this.fontProvider.displayNames;

  
  readonly fontMixed = signal(false);

  
  readonly fontInputValue = computed(() =>
    this.fontMixed() ? '' : this.selectedFontFamily(),
  );

  
  readonly fontPlaceholder = computed(() =>
    this.fontMixed() ? '—' : (this.selectedFontFamily() || 'Czcionka'),
  );

  
  readonly fontDropdownOpen = signal(false);
  private readonly fontFilter = signal('');

  
  readonly filteredFonts = computed(() => {
    const filter = this.fontFilter().trim().toLowerCase();
    const fonts = this.fontFamilies();
    return filter ? fonts.filter(f => f.toLowerCase().includes(filter)) : fonts;
  });

  
  private fontEditing = false;

  
  fontSizes = [8, 9, 10, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72];

  
  selectedFontFamily = signal('Calibri');
  selectedFontSize = signal(11);
  selectedTextColor = signal('#000000');
  selectedBgColor = signal('#ffffff');

  
  formatPainterActive = signal(false);
  private copiedFormat: Partial<EditorState['currentFormatting']> | null = null;

  
  private lastManualFontSizeChange = 0;

  
  private lastManualFontFamilyChange = 0;

  
  showLinkDialog = signal(false);
  showStyleDropdown = signal(false);
  
  readonly moreOpen = signal(false);

  toggleMore(): void {
    this.moreOpen.update((v) => !v);
  }
  showSearchBar = signal(false);
  showReplaceRow = signal(false);
  searchText = '';
  replaceText = '';
  searchResultCount = signal(0);
  currentSearchIndex = signal(0);
  linkUrl = '';
  linkText = '';

  selectedBlockFormat = signal('paragraph');

  
  getSelectedStyleLabel(): string {
    const format = this.blockFormats().find(f => f.value === this.selectedBlockFormat());
    return format?.label || 'Normalny';
  }

  
  toggleStyleDropdown(): void {
    this.showStyleDropdown.update(v => !v);
  }

  
  closeStyleDropdown(): void {
    this.showStyleDropdown.set(false);
  }

  
  selectStyle(format: { value: string; label: string; style: DocumentStyle }): void {
    this.selectedBlockFormat.set(format.value);
    this.styleChange.emit(format.style);
    this.showStyleDropdown.set(false);
  }

  
  getStylePreviewSize(originalSize: number | undefined): number {
    if (!originalSize) return 11;
    
    
    if (originalSize >= 24) return 18;
    if (originalSize >= 16) return 14;
    if (originalSize >= 13) return 12;
    return 11;
  }

  
  private updateFromEditorState(state: EditorState | null): void {
    if (!state?.currentStyle) return;

    
    
    if (state.currentStyle.fontSize && state.currentStyle.fontSize > 0) {
      const sinceManual = Date.now() - this.lastManualFontSizeChange;
      if (sinceManual > 300) {
        this.selectedFontSize.set(state.currentStyle.fontSize);
      }
    }

    
    
    
    
    
    if (!this.fontEditing && Date.now() - this.lastManualFontFamilyChange > 300) {
      this.fontMixed.set(!!state.fontMixed);
      const rawFont = state.currentStyle.fontFamily ?? state.fontFamily;
      if (!state.fontMixed && rawFont) {
        this.selectedFontFamily.set(this.fontProvider.normalize(rawFont));
      }
    }

    
    if (state.currentStyle.textColor) {
      this.selectedTextColor.set(state.currentStyle.textColor);
    }

    
    this.updateBlockFormatFromState(state);
  }

  
  private updateBlockFormatFromState(state: EditorState): void {
    const blockFormat = state.currentStyle?.blockFormat;
    const fontSize = state.currentStyle?.fontSize || 11;
    const isBold = state.currentFormatting?.bold || false;
    const isItalic = state.currentFormatting?.italic || false;

    let format = 'paragraph';

    
    if (blockFormat === 'h1') {
      format = 'heading1';
    } else if (blockFormat === 'h2') {
      format = 'heading2';
    } else if (blockFormat === 'h3') {
      format = 'heading3';
    } else if (blockFormat === 'h4') {
      format = 'heading4';
    } else if (blockFormat === 'h5') {
      format = 'heading5';
    } else if (blockFormat === 'h6') {
      format = 'heading6';
    } else {
      
      
      const tolerance = 2;
      
      
      if (fontSize >= 26) {
        format = 'title';
      }
      
      else if (fontSize >= 15 && fontSize <= 18 && isBold) {
        format = 'heading1';
      }
      
      else if (fontSize >= 13 && fontSize <= 15 && isItalic && !isBold) {
        format = 'subtitle';
      }
      
      else if (fontSize >= 12 && fontSize <= 14 && isBold && !isItalic) {
        format = 'heading2';
      }
      
      else if (fontSize >= 11 && fontSize <= 13 && isBold && !isItalic) {
        format = 'heading3';
      }
      
      else if (fontSize >= 10 && fontSize <= 12 && isBold && isItalic) {
        format = 'heading4';
      }
      
      else if (fontSize >= 18) {
        format = 'title';
      }
      
      else {
        format = 'paragraph';
      }
    }

    this.selectedBlockFormat.set(format);
  }

  
  private styleIdToCommand(styleId: string): string {
    const id = styleId.toLowerCase();
    if (id === 'normal') return 'paragraph';
    if (id === 'title') return 'title';
    if (id === 'subtitle') return 'subtitle';
    if (id.startsWith('heading')) {
      const level = id.replace('heading', '');
      return `heading${level}`;
    }
    return styleId.toLowerCase();
  }

  
  executeCommand(cmd: EditorCommand, value?: string): void {
    this.command.emit({ command: cmd, value });
  }

  
  onBlockFormatSelect(format: string): void {
    this.selectedBlockFormat.set(format);
    
    
    const selectedFormat = this.blockFormats().find(f => f.value === format);
    if (selectedFormat) {
      this.styleChange.emit(selectedFormat.style);
    }
  }

  
  onBlockFormatChange(event: Event): void {
    const select = event.target as HTMLSelectElement;
    this.onBlockFormatSelect(select.value);
  }

  
  onFontFocus(event: FocusEvent): void {
    this.fontEditing = true;
    
    
    
    (event.target as HTMLInputElement).value = '';
    this.fontFilter.set('');
    this.fontDropdownOpen.set(true);
    
    this.preserveSelection.emit();
  }

  
  onFontBlur(event: FocusEvent): void {
    const input = event.target as HTMLInputElement;
    this.fontEditing = false;
    this.fontDropdownOpen.set(false);
    if (!input.value.trim()) {
      input.value = this.fontInputValue();
    }
  }

  
  onFontInput(event: Event): void {
    this.fontFilter.set((event.target as HTMLInputElement).value);
    this.fontDropdownOpen.set(true);
  }

  
  onFontOptionMouseDown(event: MouseEvent, font: string): void {
    event.preventDefault();
    const input = (event.target as HTMLElement)
      .closest('.font-family-group')?.querySelector('input') as HTMLInputElement | null;
    this.fontDropdownOpen.set(false);
    if (input) {
      this.commitFont(font, input);
      input.blur();
    } else {
      
      this.commitFontValue(font);
    }
  }

  onFontKeydown(event: KeyboardEvent): void {
    const input = event.target as HTMLInputElement;
    if (event.key === 'Enter') {
      event.preventDefault();
      
      const matches = this.filteredFonts();
      const value = input.value.trim() && matches.length === 1 ? matches[0] : input.value;
      this.fontDropdownOpen.set(false);
      this.commitFont(value, input);
      input.blur();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      
      this.fontDropdownOpen.set(false);
      input.value = this.fontInputValue();
      input.blur();
    }
  }

  
  onFontCommit(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.fontEditing = false;
    this.commitFont(input.value, input);
  }

  private commitFont(raw: string, input: HTMLInputElement): void {
    this.fontEditing = false;
    const value = raw.trim();
    if (!value) {
      
      input.value = this.fontInputValue();
      return;
    }
    input.value = this.commitFontValue(value);
  }

  
  private commitFontValue(value: string): string {
    const canonical = this.fontProvider.normalize(value);
    if (this.fontMixed() || canonical !== this.selectedFontFamily()) {
      this.selectedFontFamily.set(canonical);
      this.fontMixed.set(false);
      this.lastManualFontFamilyChange = Date.now();
      this.fontFamilyChange.emit(canonical);
    }
    return canonical;
  }

  
  onFontSizeChange(event: Event): void {
    const select = event.target as HTMLSelectElement;
    const size = parseInt(select.value, 10);
    this.selectedFontSize.set(size);
    this.lastManualFontSizeChange = Date.now();
    this.fontSizeChange.emit(size);
  }

  
  increaseFontSize(): void {
    const currentSize = this.selectedFontSize();
    const next = this.fontSizes.find(s => s > currentSize);
    const newSize = next ?? Math.min(currentSize + 2, 400);
    this.selectedFontSize.set(newSize);
    this.lastManualFontSizeChange = Date.now();
    this.fontSizeChange.emit(newSize);
  }

  
  decreaseFontSize(): void {
    const currentSize = this.selectedFontSize();
    const prev = [...this.fontSizes].reverse().find(s => s < currentSize);
    const newSize = prev ?? Math.max(currentSize - 2, 1);
    this.selectedFontSize.set(newSize);
    this.lastManualFontSizeChange = Date.now();
    this.fontSizeChange.emit(newSize);
  }

  
  onFontSizeInputEnter(event: Event): void {
    
    
    
    
    
    
    event.preventDefault();
    const input = event.target as HTMLInputElement;
    setTimeout(() => input.blur(), 0);
  }

  
  onFontSizeInputBlur(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.applyFontSizeFromInput(input);
  }

  
  private applyFontSizeFromInput(input: HTMLInputElement): void {
    const value = parseInt(input.value, 10);
    if (!isNaN(value) && value >= 1 && value <= 400) {
      this.selectedFontSize.set(value);
      this.lastManualFontSizeChange = Date.now();
      this.fontSizeChange.emit(value);
    } else {
      
      input.value = this.selectedFontSize().toString();
    }
  }

  
  toggleFormatPainter(): void {
    if (this.formatPainterActive()) {
      
      this.formatPainterActive.set(false);
    } else {
      
      this.copyFormat.emit();
      this.formatPainterActive.set(true);
    }
  }

  
  applyFormatPainter(): void {
    if (this.formatPainterActive()) {
      this.pasteFormat.emit();
      this.formatPainterActive.set(false);
    }
  }

  
  deactivateFormatPainter(): void {
    this.formatPainterActive.set(false);
  }

  
  onTextColorChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectedTextColor.set(input.value);
    this.textColorChange.emit(input.value);
  }

  
  onBgColorChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectedBgColor.set(input.value);
    this.backgroundColorChange.emit(input.value);
  }

  
  openLinkDialog(): void {
    this.linkUrl = '';
    this.linkText = '';
    this.showLinkDialog.set(true);
  }

  
  closeLinkDialog(): void {
    this.showLinkDialog.set(false);
  }

  
  confirmInsertLink(): void {
    if (this.linkUrl) {
      this.insertLink.emit({ 
        url: this.linkUrl, 
        text: this.linkText || undefined 
      });
    }
    this.closeLinkDialog();
  }

  
  onOpenTableDialog(): void {
    this.openTableDialog.emit();
  }

  
  onInsertImage(): void {
    this.insertImage.emit();
  }

  onInsertFootnote(): void {
    this.insertFootnote.emit();
  }

  onInsertEndnote(): void {
    this.insertEndnote.emit();
  }

  
  onInsertBarcode(): void {
    this.insertBarcode.emit();
  }

  
  isActive(format: keyof EditorState['currentFormatting']): boolean {
    return this.editorState?.currentFormatting?.[format] === true;
  }

  
  isFormattingMarksActive(): boolean {
    return this.editorState?.formattingMarks === true;
  }

  
  isAlignActive(align: 'left' | 'center' | 'right' | 'justify'): boolean {
    return (this.editorState?.currentFormatting?.alignment ?? 'left') === align;
  }

  
  toggleSearchBar(): void {
    const newValue = !this.showSearchBar();
    this.showSearchBar.set(newValue);
    if (!newValue) {
      this.searchText = '';
      this.replaceText = '';
      this.searchResultCount.set(0);
      this.currentSearchIndex.set(0);
      this.showReplaceRow.set(false);
      this.clearSearch.emit();
    }
  }

  
  closeSearchBar(): void {
    this.showSearchBar.set(false);
    this.searchText = '';
    this.replaceText = '';
    this.searchResultCount.set(0);
    this.currentSearchIndex.set(0);
    this.showReplaceRow.set(false);
    this.clearSearch.emit();
  }

  
  toggleReplaceRow(): void {
    this.showReplaceRow.update(v => !v);
  }

  
  onSearchInput(): void {
    if (this.searchText.length > 0) {
      this.searchInDocument.emit({ text: this.searchText, direction: 'next' });
    } else {
      this.searchResultCount.set(0);
      this.currentSearchIndex.set(0);
      this.clearSearch.emit();
    }
  }

  
  findNext(): void {
    if (this.searchText) {
      this.searchInDocument.emit({ text: this.searchText, direction: 'next' });
    }
  }

  
  findPrevious(): void {
    if (this.searchText) {
      this.searchInDocument.emit({ text: this.searchText, direction: 'previous' });
    }
  }

  
  replaceNext(): void {
    if (this.searchText) {
      this.replaceInDocument.emit({ searchText: this.searchText, replaceText: this.replaceText, all: false });
    }
  }

  
  replaceAll(): void {
    if (this.searchText) {
      this.replaceInDocument.emit({ searchText: this.searchText, replaceText: this.replaceText, all: true });
    }
  }

  
  updateSearchResults(count: number, currentIndex: number): void {
    this.searchResultCount.set(count);
    this.currentSearchIndex.set(currentIndex);
  }

  
  onToolbarMouseDown(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    
    if (target.tagName !== 'INPUT' && target.tagName !== 'SELECT') {
      event.preventDefault();
    } else {
      this.preserveSelection.emit();
    }
  }
}
