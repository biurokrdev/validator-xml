import {
  Component,
  ViewChild,
  ElementRef,
  inject,
  signal,
  computed,
  HostListener,
  OnInit,
  OnDestroy
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { switchMap, map, filter, distinctUntilChanged } from 'rxjs/operators';
import { from, Observable, Subscription, timer } from 'rxjs';
import { MsalService } from '@azure/msal-angular';
import { AccountInfo } from '@azure/msal-browser';
import { environment } from '../../../environments/environment';
import { WysiwygEditorComponent } from '../wysiwyg-editor/wysiwyg-editor';
import { EditorToolbarComponent } from '../editor-toolbar/editor-toolbar';
import { BarcodeDialogComponent } from '../barcode-dialog/barcode-dialog';
import { RulerComponent, RulerColumnSegment } from '../ruler/ruler';
import { DocumentService, OpenDocumentError } from '../../services/document.service';
import { documentDefectMessage } from '../../core/errors/document-error.util';
import { 
  DocumentContent, 
  DocumentMetadata, 
  EditorState,
  EditorCommand,
  DocumentTemplate,
  PageMargins,
  PageSettings,
  PageSize,
  MARGIN_PRESETS,
  DocumentStyle,
  HeaderFooterContent,
  SectionHeaderFooter,
  DigitalSignatureInfo,
  SignDocumentRequest,
  Footnote,
  Endnote
} from '../../models/document.model';
import { BuildInfoService } from '../../core/services/build-info.service';
import { DocumentNavigationService } from '../../core/services/document-navigation.service';
import { LastHttpErrorService } from '../../core/services/last-http-error.service';
import { NotificationService } from '../../core/services/notification.service';
import { FontProviderService } from '../../services/font-provider.service';
import { CSS_PX_PER_CM } from '../../core/utils/units.util';
import { DocumentStorageService, DeliveryStatus } from '../../services/document-storage.service';
import { DocumentClassificationBadgeComponent } from '../document-classification-badge/document-classification-badge';
import { TablePropertiesPanelComponent } from '../table-properties-panel/table-properties-panel';
import { HeaderFooterPanelComponent } from '../header-footer-panel/header-footer-panel';
import { ImagePropertiesPanelComponent, ImageSelectionState } from '../image-properties-panel/image-properties-panel';
import {
  TableBorderLineStyle,
  TableBorderScope,
  DEFAULT_TABLE_BORDER
} from '../../models/table-style.model';
import {
  applyBorderToCells,
  classifyBorderTarget,
  restoreDefaultTableBorders as restoreDefaultBordersUtil
} from '../../core/utils/table-style.util';
import { resolveTableContext } from '../../core/utils/table-context.util';
import { syncTableColgroup } from '../../core/utils/table-grid.util';
import { isValidReturnUrl } from '../../core/utils/return-url.util';
import {
  applyWordLineSpacing,
  applyExactLineSpacing,
  readWordLineMultiple,
} from '../../core/utils/word-line-spacing.util';

const READ_ONLY_PROTECTED_MESSAGE =
  'Ten dokument jest oznaczony jako tylko do odczytu i nie może być edytowany w DOC2 Editor.';

@Component({
  selector: 'd2-document-editor',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    WysiwygEditorComponent,
    EditorToolbarComponent,
    BarcodeDialogComponent,
    RulerComponent,
    DocumentClassificationBadgeComponent,
    TablePropertiesPanelComponent,
    HeaderFooterPanelComponent,
    ImagePropertiesPanelComponent
  ],
  templateUrl: './document-editor.html',
  styleUrl: './document-editor.scss'
})
export class DocumentEditorComponent implements OnInit, OnDestroy {
  @ViewChild(WysiwygEditorComponent) editor!: WysiwygEditorComponent;
  @ViewChild(EditorToolbarComponent) toolbar!: EditorToolbarComponent;
  @ViewChild('verticalRulerBar') verticalRulerBar?: ElementRef<HTMLDivElement>;
  @ViewChild('horizontalRulerInner') horizontalRulerInner?: ElementRef<HTMLDivElement>;
  @ViewChild('editorScrollContainer') editorScrollContainer?: ElementRef<HTMLDivElement>;

  private documentService = inject(DocumentService);
  private documentStorageService = inject(DocumentStorageService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private documentNavigation = inject(DocumentNavigationService);
  readonly buildInfo = inject(BuildInfoService);
  private readonly lastHttpError = inject(LastHttpErrorService);
  private readonly notification = inject(NotificationService);
  private readonly msal = inject(MsalService);
  private readonly fontProvider = inject(FontProviderService);

  readonly currentUserName = signal<string>('');
  readonly firstName = signal<string>('');
  readonly initials = signal<string>('');
  readonly avatarUrl = signal<string | null>(null);
  private avatarObjectUrl: string | null = null;

  documentContent = signal<string>('<p></p>');
  documentMasterId = signal<string | null>(null);
  documentVersionId = signal<string | null>(null);
  readOnly = signal<boolean>(false);

  lockedByOther = signal<boolean>(false);

  documentEditProtected = signal<boolean>(false);

  editingDisabled = computed(() => this.readOnly() || this.lockedByOther() || this.documentEditProtected());

  autoSaveEnabled = signal<boolean>(environment.autoSave?.enabled ?? true);
  autoSaveStatus = signal<'idle' | 'saving' | 'saved' | 'error'>('idle');
  lastAutoSaveAt = signal<Date | null>(null);
  private autoSaveSub?: Subscription;
  private isAutoSaving = false;

  documentClassification = signal<string | null>(null);

  isFinishing = signal<boolean>(false);
  deliveryStatus = signal<DeliveryStatus | null>(null);
  deliveryId = signal<string | null>(null);
  showSendingModal = signal<boolean>(false);
  showSendErrorModal = signal<boolean>(false);
  private sendCancelled = signal<boolean>(false);
  private sendDetachedFromUi = signal<boolean>(false);
  workFinished = signal<boolean>(false);
  tabCloseBlocked = signal<boolean>(false);
  private tabCloseHintTimer?: ReturnType<typeof setTimeout>;
  returnUrl = signal<string | null>(null);
  readonly canFinish = computed(() => isValidReturnUrl(this.returnUrl()));

  userDownload = signal<boolean>(false);
  readonly canUserDownload = computed(() => this.userDownload());

  loadedFromDisk = signal<boolean>(false);
  private diskOriginalFile: File | null = null;

  readonly canDownloadOriginal = computed(() => this.loadedFromDisk() || this.userDownload());

  showSaveState = signal<boolean>(true);
  private finishSendSub?: Subscription;
  documentMetadata = signal<DocumentMetadata>({
    title: 'Nowy dokument',
    created: new Date().toISOString(),
    modified: new Date().toISOString()
  });
  documentStyles = signal<DocumentStyle[]>([]);
  originalFileName = signal<string>('');
  
  headerContent = signal<HeaderFooterContent>({ html: '', height: 1.25 });
  footerContent = signal<HeaderFooterContent>({ html: '', height: 1.25 });
  
  editorState = signal<EditorState | null>(null);
  
  isLoading = signal(false);
  showMenu = signal(false);
  showEditMenu = signal(false);
  showFormatMenu = signal(false);
  showInsertMenu = signal(false);
  activeSubmenu = signal<string | null>(null);
  showTemplates = signal(false);
  showFindReplace = signal(false);
  showBarcodeDialog = signal(false);
  errorMessage = signal<string | null>(null);
  successMessage = signal<string | null>(null);
  infoMessage = signal<string | null>(null);
  documentNotFound = signal(false);

  showContextMenu = signal(false);
  contextMenuX = signal(0);
  contextMenuY = signal(0);
  contextSubmenu = signal<string | null>(null);
  contextMenuTargetCell = signal<HTMLElement | null>(null);
  contextMenuTargetImage = signal<HTMLImageElement | null>(null);
  contextMenuExtrasVisible = signal(false);

  showMiniToolbar = signal(false);
  miniToolbarX = signal(0);
  miniToolbarY = signal(0);

  readonly commonFonts = this.fontProvider.displayNames;

  readonly miniToolbarFontFamily = computed(() =>
    this.fontProvider.normalize(this.editorState()?.currentStyle?.fontFamily),
  );

  readonly miniToolbarFontOptions = computed<readonly string[]>(() => {
    const current = this.miniToolbarFontFamily();
    const fonts = this.commonFonts();
    if (current && !fonts.some((f) => f.toLowerCase() === current.toLowerCase())) {
      return [current, ...fonts];
    }
    return fonts;
  });

  showToolsMenu = signal(false);

  showParagraphDialog = signal(false);
  paragraphDialogTab = signal<'indents' | 'breaks'>('indents');
  paragraphData = {
    alignment: 'left' as string,
    outlineLevel: 'body' as string,
    indentLeft: 0,
    indentRight: 0,
    specialIndent: 'none' as string,
    specialIndentBy: 1.27,
    mirrorIndents: false,
    spaceBefore: 0,
    spaceAfter: 8,
    lineSpacingType: 'multiple' as string,
    lineSpacingValue: 1.08,
    dontAddSpaceBetweenSameStyle: false,
    widowOrphanControl: true,
    keepWithNext: false,
    keepLinesTogether: false,
    pageBreakBefore: false
  };

  private _paragraphDefaults: typeof DocumentEditorComponent.prototype.paragraphData = {
    alignment: 'left',
    outlineLevel: 'body',
    indentLeft: 0,
    indentRight: 0,
    specialIndent: 'none',
    specialIndentBy: 1.27,
    mirrorIndents: false,
    spaceBefore: 0,
    spaceAfter: 8,
    lineSpacingType: 'multiple',
    lineSpacingValue: 1.08,
    dontAddSpaceBetweenSameStyle: false,
    widowOrphanControl: true,
    keepWithNext: false,
    keepLinesTogether: false,
    pageBreakBefore: false
  };

  showInsertTableDialog = signal(false);
  tableDialogData = {
    columns: 5,
    rows: 2,
    autoFitBehavior: 'fixed' as string,
    fixedWidth: 0,
    rememberDimensions: false
  };
  private savedTableDimensions: { columns: number; rows: number } | null = null;
  
  templates = signal<DocumentTemplate[]>([]);

  zoomLevel = signal(100);
  zoomLevels = [50, 75, 100, 125, 150, 200];

  isInTable = signal(false);
  activeTableCell = signal<HTMLTableCellElement | null>(null);
  activeTable = signal<HTMLTableElement | null>(null);

  showTablePanel = signal(false);
  private tablePanelManuallyClosed = false;

  tableBorderColor = signal(DEFAULT_TABLE_BORDER.color);
  tableBorderWidth = signal(DEFAULT_TABLE_BORDER.width);
  tableBorderStyle = signal<TableBorderLineStyle>(DEFAULT_TABLE_BORDER.style);
  lastBorderScope = signal<TableBorderScope | null>(null);

  borderTargetInfo = computed(() => {
    const table = this.activeTable();
    if (!table) return null;
    return classifyBorderTarget(table, this.resolveAutoTargetCells(table));
  });

  selectedCells = signal<Set<HTMLTableCellElement>>(new Set());
  private cellSelectionStartCell: HTMLTableCellElement | null = null;
  private isCellSelecting = false;

  showShadingDropdown = signal(false);

  currentPage = signal(1);
  totalPages = signal(1);
  
  showPageIndicator = signal(false);
  private pageIndicatorTimeout?: ReturnType<typeof setTimeout>;

  showPageSetup = signal(false);
  showMarginGuides = signal(false);
  showRuler = signal(true);

  currentBlockIndent = signal<{ start: number; end: number }>({ start: 0, end: 0 });

  currentColumnRuler = signal<{ segments: RulerColumnSegment[]; activeIndex: number } | null>(null);

  rulerGuide = signal<{ active: boolean; axis: 'horizontal' | 'vertical'; offsetPx: number }>({
    active: false,
    axis: 'horizontal',
    offsetPx: 0
  });

  editingSection = signal<'header' | 'footer' | 'body'>('body');

  showHeaderFooterPanel = computed(() =>
    this.editingSection() !== 'body'
    && !this.showFindReplace()
    && !this.showTablePanel()
    && !this.showImagePanel()
  );

  selectedImage = signal<ImageSelectionState | null>(null);
  imageLockAspect = signal<boolean>(true);

  showImagePanel = computed(() =>
    this.selectedImage() !== null
    && !this.showFindReplace()
    && !this.showTablePanel()
  );

  onImageSelectionChange(state: ImageSelectionState | null): void {
    this.selectedImage.set(state);
  }

  sectionGeometry = signal<{ section: 'header' | 'footer'; topCm: number; bottomCm: number } | null>(null);

  verticalRulerMargins = computed<PageMargins>(() => {
    const m = this.pageSettings().margins;
    const pageH = this.pageSettings().orientation === 'portrait' ? 29.7 : 21;
    const section = this.editingSection();
    const geo = this.sectionGeometry();
    if ((section === 'header' || section === 'footer') && geo && geo.section === section) {
      return { ...m, top: Math.max(0, geo.topCm), bottom: Math.max(0, pageH - geo.bottomCm) };
    }
    return m;
  });

  pageList = computed(() => Array.from({ length: this.totalPages() }, (_, i) => i));

  vRulerSegmentHeightPx = computed(() =>
    (this.pageSettings().orientation === 'portrait' ? 1122 : 794) * (this.zoomLevel() / 100)
  );

  vRulerGapPx = computed(() => 8 * (this.zoomLevel() / 100));

  vRulerSegments = signal<{ top: number; height: number }[]>([]);

  vRulerSegmentsView = computed(() => {
    const scale = this.zoomLevel() / 100;
    const toCm = (px: number) => px / (DocumentEditorComponent.CM_TO_PX * scale);
    const measured = this.vRulerSegments();
    if (measured.length === this.totalPages() && measured.length > 0) {
      return measured.map(s => ({ ...s, axisCm: toCm(s.height) }));
    }
    const h = this.vRulerSegmentHeightPx();
    const g = this.vRulerGapPx();
    const pad = 20;
    const fallbackAxis = this.pageSettings().orientation === 'portrait' ? 29.7 : 21;
    return this.pageList().map((_, i) => ({ top: pad + i * (h + g), height: h, axisCm: fallbackAxis }));
  });

  vRulerInnerHeight = computed(() => {
    const segs = this.vRulerSegmentsView();
    return segs.length ? segs[segs.length - 1].top + segs[segs.length - 1].height : 0;
  });

  private vRulerResizeObserver?: ResizeObserver;

  private measureVRuler(): void {
    requestAnimationFrame(() => {
      const next = this.editor?.getPageLayout() ?? [];
      const cur = this.vRulerSegments();
      const same = cur.length === next.length
        && cur.every((s, i) => Math.abs(s.height - next[i].height) < 0.5 && Math.abs(s.top - next[i].top) < 0.5);
      if (!same) this.vRulerSegments.set(next);
    });
  }

  private ensureVRulerObserver(): void {
    if (this.vRulerResizeObserver) return;
    const wrapper = this.editorScrollContainer?.nativeElement?.querySelector('.editor-wrapper') as HTMLElement | null;
    if (!wrapper) return;
    this.vRulerResizeObserver = new ResizeObserver(() => this.measureVRuler());
    this.vRulerResizeObserver.observe(wrapper);
  }

  private static readonly CM_TO_PX = CSS_PX_PER_CM;

  showViewMenu = signal(false);
  showHelpMenu = signal(false);
  pageSettings = signal<PageSettings>({
    margins: { top: 2.5, bottom: 2.5, left: 2.5, right: 2.5 },
    orientation: 'portrait',
    paperSize: 'a4'
  });
  documentPageSize = signal<PageSize | undefined>(undefined);
  sectionHeadersFooters = signal<SectionHeaderFooter[] | null>(null);
  footnotes = signal<Footnote[] | null>(null);
  endnotes = signal<Endnote[] | null>(null);
  footnoteNumberFormat = signal<string | null>(null);
  endnoteNumberFormat = signal<string | null>(null);
  marginPresets = MARGIN_PRESETS;

  showHeaderFooterDialog = signal(false);
  headerFooterDialogData = signal<{
    headerMargin: number;
    footerMargin: number;
    differentFirstPage: boolean;
    differentOddEven: boolean;
  }>({
    headerMargin: 1.27,
    footerMargin: 1.27,
    differentFirstPage: false,
    differentOddEven: false
  });

  showPropertiesDialog = signal(false);
  propertiesData = signal<DocumentMetadata>({});

  showSignatureDialog = signal(false);
  signatureDialogTab = signal<'list' | 'sign'>('list');
  signatureData = {
    signerName: '' as string,
    signerTitle: '' as string,
    signerEmail: '' as string,
    reason: '' as string,
    certificateBase64: '' as string,
    certificatePassword: '' as string,
    certificateFileName: '' as string
  };

  documentSignatures = signal<DigitalSignatureInfo[]>([]);

  showLeaveDialog = signal(false);

  protected readonly Math = Math;

  constructor() {
    this.loadTemplates();
  }

  ngOnInit(): void {
    this.route.queryParams.pipe(
      map(params => ({
        masterId: params['masterId'] as string | undefined,
        versionId: params['versionId'] as string | undefined
      })),
      filter(p => !!p.masterId),
      distinctUntilChanged((a, b) => a.masterId === b.masterId && a.versionId === b.versionId)
    ).subscribe(({ masterId, versionId }) => {
      this.documentVersionId.set(versionId ?? null);
      this.readOnly.set(!versionId);
      this.loadFromStorage(masterId!, versionId ?? null);
    });

    const account = this.msal.instance.getActiveAccount() ?? this.msal.instance.getAllAccounts()[0] ?? null;
    const fullName = account?.name?.trim() || account?.username || '';
    this.currentUserName.set(fullName);

    const parenthesized = fullName.match(/\(([^)]+)\)/)?.[1]?.trim();
    const givenName = (account?.idTokenClaims as Record<string, unknown> | undefined)?.['given_name'];
    const first = parenthesized
      ? parenthesized
      : typeof givenName === 'string' && givenName.trim()
        ? givenName.trim()
        : this.deriveFirstName(fullName);
    this.firstName.set(first);
    this.initials.set(this.deriveInitials(fullName || first));

    if (account) {
      void this.loadAvatar(account);
    }

    this.startAutoSave();
  }

  ngOnDestroy(): void {
    this.stopAutoSave();
    this.finishSendSub?.unsubscribe();
    this.vRulerResizeObserver?.disconnect();
    clearTimeout(this.tabCloseHintTimer);
    if (this.avatarObjectUrl) {
      URL.revokeObjectURL(this.avatarObjectUrl);
      this.avatarObjectUrl = null;
    }
  }

  private async loadAvatar(account: AccountInfo): Promise<void> {
    try {
      const result = await this.msal.instance.acquireTokenSilent({ scopes: ['User.Read'], account });
      const response = await fetch('https://graph.microsoft.com/v1.0/me/photo/$value', {
        headers: { Authorization: `Bearer ${result.accessToken}` },
      });
      if (!response.ok) return;
      const blob = await response.blob();
      this.avatarObjectUrl = URL.createObjectURL(blob);
      this.avatarUrl.set(this.avatarObjectUrl);
    } catch {
    }
  }

  private deriveFirstName(fullName: string): string {
    if (!fullName) return '';
    if (fullName.includes(',')) {
      return fullName.split(',')[1]?.trim().split(/\s+/)[0] ?? '';
    }
    if (fullName.includes('@')) {
      return fullName.split('@')[0];
    }
    return fullName.split(/\s+/)[0];
  }

  private deriveInitials(name: string): string {
    const parts = name.replace(',', ' ').split(/\s+/).filter(Boolean);
    if (parts.length === 0) return '?';
    if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
    return (parts[0][0] + parts[1][0]).toUpperCase();
  }

  private startAutoSave(): void {
    this.stopAutoSave();
    const intervalMs = (environment.autoSave?.intervalSeconds ?? 30) * 1000;
    this.autoSaveSub = timer(intervalMs, intervalMs).subscribe(() => {
      if (!this.autoSaveEnabled()) return;
      if (this.isAutoSaving) return;
      if (this.editingDisabled()) return;
      if (!this.documentVersionId() || !this.documentMasterId()) return;
      if (!this.editorState()?.isModified) return;
      this.performAutoSave();
    });
  }

  private stopAutoSave(): void {
    this.autoSaveSub?.unsubscribe();
    this.autoSaveSub = undefined;
  }

  toggleAutoSave(): void {
    const next = !this.autoSaveEnabled();
    this.autoSaveEnabled.set(next);
    this.autoSaveStatus.set('idle');
  }

  private buildSaveRequest() {
    const html = this.editor?.getContent() || this.documentContent();
    const fileName = this.originalFileName() || `${this.documentMetadata().title || 'dokument'}.docx`;
    return {
      html,
      originalFileName: fileName,
      metadata: this.documentMetadata(),
      header: this.headerContent(),
      footer: this.footerContent(),
      margins: this.pageSettings().margins,
      pageSize: this.documentPageSize(),
      sectionHeadersFooters: this.sectionHeadersFooters() ?? undefined,
      footnotes: this.footnotes() ?? undefined,
      endnotes: this.endnotes() ?? undefined,
      masterId: this.documentMasterId() ?? undefined,
      footnoteNumberFormat: this.footnoteNumberFormat() ?? 'decimal',
      endnoteNumberFormat: this.endnoteNumberFormat() ?? 'lowerRoman'
    };
  }

  private persistDocument(): Observable<unknown> {
    const masterId = this.documentMasterId()!;
    const versionId = this.documentVersionId();
    return this.documentService.saveDocument(this.buildSaveRequest()).pipe(
      switchMap(blob => from(this.blobToBase64(blob))),
      switchMap(base64 => versionId
        ? this.documentStorageService.updateDocumentVersion(masterId, versionId, { content: base64 })
        : this.documentStorageService.saveDocumentVersion(masterId, { content: base64 })
      )
    );
  }

  private performAutoSave(): void {
    this.isAutoSaving = true;
    this.autoSaveStatus.set('saving');

    this.persistDocument().subscribe({
      next: () => {
        this.editor?.markAsSaved();
        this.lastAutoSaveAt.set(new Date());
        this.autoSaveStatus.set('saved');
        this.isAutoSaving = false;
      },
      error: () => {
        this.autoSaveStatus.set('error');
        this.isAutoSaving = false;
      }
    });
  }

  private logOpenDiagnostics(): void {
    const block = this.formatDiagnostics(this.collectDiagnosticRows());
    console.info(`[open] ${new Date().toISOString()} — otwarto dokument\n${block}`);
  }

  private static readonly DOCX_MIME = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document';
  private static readonly DOC_MIME = 'application/msword';
  private static readonly PDF_MIME = 'application/pdf';

  private loadFromStorage(masterId: string, versionId: string | null): void {
    this.isLoading.set(true);
    this.errorMessage.set(null);
    this.documentMasterId.set(masterId);
    this.returnUrl.set(null);
    this.userDownload.set(false);
    this.showSaveState.set(true);
    this.loadedFromDisk.set(false);
    this.diskOriginalFile = null;

    this.documentStorageService.getDocumentMetadata(masterId).pipe(
      switchMap(meta => {
        const mime = (meta.mimeType || '').toLowerCase();

        this.documentClassification.set(meta.classification ?? null);
        this.returnUrl.set(meta.returnUrl ?? null);
        this.userDownload.set(meta.userDownload === true);
        this.showSaveState.set(meta.showSaveState !== false);

        if (mime === DocumentEditorComponent.PDF_MIME) {
          this.router.navigate(['/viewer'], { queryParams: { masterId } });
          return from(Promise.reject({ handled: true } as const));
        }

        const bytes$ = versionId
          ? this.documentStorageService.downloadVersion(masterId, versionId)
          : this.documentStorageService.downloadBaseVersion(masterId);

        const ext = mime === DocumentEditorComponent.DOC_MIME ? '.doc' : '.docx';
        const fileName = `dokument${ext}`;

        return bytes$.pipe(
          map(blob => ({
            file: new File([blob], fileName, { type: mime || DocumentEditorComponent.DOCX_MIME }),
            fileName
          }))
        );
      })
    ).subscribe({
      next: ({ file, fileName }) => {
        this._convertAndLoad(file, fileName);
      },
      error: (err) => {
        if (err?.handled) {
          return;
        }
        if (err.status === 404) {
          this.documentNotFound.set(true);
        } else {
          this.showError(err.message || 'Nie udało się otworzyć dokumentu z bazy danych');
        }
        this.isLoading.set(false);
      }
    });
  }

  updateTitle(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.documentMetadata.update(m => ({ ...m, title: input.value }));
  }

  private loadTemplates(): void {
    this.documentService.getTemplates().subscribe({
      next: (templates) => this.templates.set(templates),
      error: (err) => console.error('Błąd ładowania szablonów:', err)
    });
  }

  goToDashboard(): void {
    const hasDocument = !!this.documentMasterId();
    const hasChanges = !!this.editorState()?.isModified;

    if (hasDocument || hasChanges) {
      this.showLeaveDialog.set(true);
    } else {
      this.router.navigate(['/']);
    }
  }

  confirmLeave(): void {
    this.showLeaveDialog.set(false);
    this.router.navigate(['/']);
  }

  cancelLeave(): void {
    this.showLeaveDialog.set(false);
  }

  newDocument(): void {
    if (this.editorState()?.isModified) {
      if (!confirm('Masz niezapisane zmiany. Czy na pewno chcesz utworzyć nowy dokument?')) {
        return;
      }
    }

    this.showMenu.set(false);
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.documentService.newDocument().pipe(
      switchMap(content =>
        this.documentService.saveDocument({ html: content.html, metadata: content.metadata }).pipe(
          switchMap(blob =>
            from(this.blobToBase64(blob)).pipe(
              switchMap(base64 =>
                this.documentStorageService.uploadDocument({
                  name: 'Nowy dokument.docx',
                  mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
                  content: base64
                }).pipe(
                  switchMap(result =>
                    this.documentStorageService.saveDocumentVersion(result.masterId, { content: base64 }).pipe(
                      map(saved => ({ masterId: result.masterId, versionId: saved.versionId }))
                    )
                  )
                )
              )
            )
          )
        )
      )
    ).subscribe({
      next: ({ masterId, versionId }) => {
        this.router.navigate(['/editor'], { queryParams: { masterId, versionId } });
      },
      error: () => {
        this.showError('Nie udało się utworzyć nowego dokumentu');
        this.isLoading.set(false);
      }
    });
  }

  openDocument(): void {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.docx,.doc,.pdf';

    input.onchange = (e) => {
      const file = (e.target as HTMLInputElement).files?.[0];
      if (!file) return;

      this.showMenu.set(false);

      if (file.name.toLowerCase().endsWith('.pdf')) {
        this.openPdfInViewer(file);
        return;
      }

      this.loadDocument(file);
    };

    input.click();
  }

  private async openPdfInViewer(file: File): Promise<void> {
    this.isLoading.set(true);
    this.errorMessage.set(null);
    try {
      const base64 = await this.documentStorageService.fileToBase64(file);
      this.documentStorageService.uploadDocument({
        name: file.name,
        mimeType: 'application/pdf',
        content: base64,
      }).subscribe({
        next: (result) => {
          this.documentNavigation.navigateToDocument(result.masterId, 'application/pdf');
        },
        error: (err) => {
          this.isLoading.set(false);
          this.showError(documentDefectMessage(err)
            ?? 'Błąd podczas wczytywania pliku PDF. Spróbuj ponownie.');
        },
      });
    } catch {
      this.isLoading.set(false);
      this.showError('Błąd podczas odczytu pliku PDF.');
    }
  }

  private blobToBase64(blob: Blob): Promise<string> {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.readAsDataURL(blob);
      reader.onload = () => resolve((reader.result as string).split(',')[1]);
      reader.onerror = reject;
    });
  }

  private loadDocument(file: File, password?: string): void {
    this.diskOriginalFile = file;
    this.loadedFromDisk.set(true);
    this._convertAndLoad(file, file.name, password, true);
  }

  private _convertAndLoad(file: File, fileName: string, password?: string, announce = false): void {
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.documentService.openDocument(file, password).subscribe({
      next: (content) => {
        this._applyLoadedContent(content, fileName);
        this.logOpenDiagnostics();
        if (content.isReadOnlyProtected === true) {
          this.showInfo(READ_ONLY_PROTECTED_MESSAGE);
        } else if (announce) {
          this.showSuccess(`Otwarto dokument: ${fileName}`);
        }
        this.isLoading.set(false);
      },
      error: (err) => {
        this.isLoading.set(false);
        const code = err instanceof OpenDocumentError ? err.code : undefined;

        if (code === 'PASSWORD_REQUIRED' || code === 'WRONG_PASSWORD') {
          this.openPasswordDialog(pwd => this._convertAndLoad(file, fileName, pwd, announce), code === 'WRONG_PASSWORD');
          return;
        }

        const defect = documentDefectMessage(err);
        if (defect) {
          this.showError(defect);
          return;
        }

        if (err?.status === 404) {
          this.documentNotFound.set(true);
        } else {
          this.showError(err.message || 'Nie udało się otworzyć dokumentu');
        }
      }
    });
  }

  private _applyLoadedContent(content: DocumentContent, fileName: string): void {
    this.documentEditProtected.set(content.isReadOnlyProtected === true);
    this.documentContent.set(content.html);
    this.documentMetadata.set(content.metadata);
    this.documentStyles.set(content.styles || []);
    this.originalFileName.set(fileName);
    this.headerContent.set({
      html: content.header?.html || '',
      height: content.header?.height || 1.25,
      differentFirstPage: content.header?.differentFirstPage ?? false,
      firstPageHtml: content.header?.firstPageHtml ?? '',
      differentOddEven: content.header?.differentOddEven ?? false,
      oddHtml: content.header?.oddHtml ?? '',
      evenHtml: content.header?.evenHtml ?? ''
    });
    this.footerContent.set({
      html: content.footer?.html || '',
      height: content.footer?.height || 1.25,
      differentFirstPage: content.footer?.differentFirstPage ?? false,
      firstPageHtml: content.footer?.firstPageHtml ?? '',
      differentOddEven: content.footer?.differentOddEven ?? false,
      oddHtml: content.footer?.oddHtml ?? '',
      evenHtml: content.footer?.evenHtml ?? ''
    });
    if (content.margins) {
      this.pageSettings.update(s => ({ ...s, margins: content.margins! }));
    }
    if (content.pageSize) {
      this.documentPageSize.set(content.pageSize);
      this.pageSettings.update(s => ({ ...s, orientation: content.pageSize!.orientation }));
    }
    this.sectionHeadersFooters.set(content.sectionHeadersFooters ?? null);
    this.footnotes.set(content.footnotes ?? null);
    this.endnotes.set(content.endnotes ?? null);
    this.footnoteNumberFormat.set(content.footnoteNumberFormat ?? null);
    this.endnoteNumberFormat.set(content.endnoteNumberFormat ?? null);
    if (this.editor) {
      this.editor.setContent(content.html);
    }
    this.documentSignatures.set(content.metadata.signatures || []);
  }

  showPasswordDialog = signal(false);
  passwordDialogValue = '';
  passwordDialogError = signal<string | null>(null);
  private _passwordRetry: ((password: string) => void) | null = null;

  private openPasswordDialog(retry: (password: string) => void, wrong: boolean): void {
    this._passwordRetry = retry;
    this.passwordDialogValue = '';
    this.passwordDialogError.set(wrong ? 'Nieprawidłowe hasło. Spróbuj ponownie.' : null);
    this.showPasswordDialog.set(true);
    setTimeout(() => (document.querySelector('.password-dialog input') as HTMLInputElement | null)?.focus(), 50);
  }

  confirmPasswordDialog(): void {
    const pwd = this.passwordDialogValue;
    if (!pwd) {
      this.passwordDialogError.set('Wpisz hasło.');
      return;
    }
    const retry = this._passwordRetry;
    this.showPasswordDialog.set(false);
    this.passwordDialogValue = '';
    this._passwordRetry = null;
    retry?.(pwd);
  }

  cancelPasswordDialog(): void {
    this.showPasswordDialog.set(false);
    this.passwordDialogValue = '';
    this._passwordRetry = null;
    this.passwordDialogError.set(null);

    if (this.returnUrl()) {
      this.tryCloseBrowserTab();
    } else {
      this.router.navigate(['/']);
    }
  }

  saveDocument(): void {
    if (this.editingDisabled()) {
      this.showError(this.documentEditProtected()
        ? 'Dokument jest chroniony przed edycją — zapis jest zablokowany.'
        : 'Tryb podglądu — dokument jest tylko do odczytu. Użyj „Pobierz dokument", aby zapisać kopię lokalnie.');
      this.showMenu.set(false);
      return;
    }

    const masterId = this.documentMasterId();
    if (!masterId) {
      this.showError('Dokument nie jest powiązany z bazą — użyj „Pobierz dokument".');
      this.showMenu.set(false);
      return;
    }

    this.showMenu.set(false);
    this.isLoading.set(true);
    this.autoSaveStatus.set('saving');

    this.persistDocument().subscribe({
      next: () => {
        this.editor?.markAsSaved();
        this.lastAutoSaveAt.set(new Date());
        this.autoSaveStatus.set('saved');
        this.showSuccess('Dokument został zapisany');
        this.isLoading.set(false);
      },
      error: () => {
        this.autoSaveStatus.set('error');
        this.showError('Nie udało się zapisać dokumentu');
        this.isLoading.set(false);
      }
    });
  }

  downloadDocument(): void {
    const masterId = this.documentMasterId();
    if (!masterId || !this.canUserDownload()) {
      this.showError('Pobieranie pliku na komputer nie jest dostępne dla tego dokumentu.');
      return;
    }

    const request = this.buildSaveRequest();
    const fileName = request.originalFileName;

    try {
      const html = request.html;
      const tmp = document.createElement('div');
      tmp.innerHTML = html;
      const counts = {
        chars: html.length,
        h1: tmp.querySelectorAll('h1').length,
        h2: tmp.querySelectorAll('h2').length,
        h3: tmp.querySelectorAll('h3').length,
        p: tmp.querySelectorAll('p').length,
        b_strong: tmp.querySelectorAll('b,strong').length,
        i_em: tmp.querySelectorAll('i,em').length,
        u: tmp.querySelectorAll('u').length,
        img: tmp.querySelectorAll('img').length,
        table: tmp.querySelectorAll('table').length,
        tr: tmp.querySelectorAll('tr').length,
        td: tmp.querySelectorAll('td').length,
        pageBreaks: tmp.querySelectorAll('div.page-break').length,
        inlineStyles: tmp.querySelectorAll('[style]').length,
        fontSizeAttrs: Array.from(tmp.querySelectorAll('[style*="font-size"]')).slice(0, 5).map(e => (e as HTMLElement).style.fontSize),
        fontFamilyAttrs: Array.from(tmp.querySelectorAll('[style*="font-family"]')).slice(0, 5).map(e => (e as HTMLElement).style.fontFamily),
      };
      console.group('[downloadDocument] DIAGNOSTYKA HTML wysyłanego do API');
      console.log('fileName:', fileName);
      console.log('counts:', counts);
      console.log('first 2000 chars:', html.substring(0, 2000));
      console.log('header:', this.headerContent());
      console.log('footer:', this.footerContent());
      console.log('margins:', this.pageSettings().margins);
      (window as unknown as { __lastSaveHtml?: string }).__lastSaveHtml = html;
      console.log('Pełny HTML dostępny w window.__lastSaveHtml');
      console.groupEnd();
    } catch (e) {
      console.warn('[downloadDocument] diagnostyka failed', e);
    }

    this.isLoading.set(true);
    this.showMenu.set(false);
    this.documentStorageService.downloadEditedDocument(masterId, request).subscribe({
      next: (blob) => {
        this.saveBlobToDisk(blob, fileName.endsWith('.docx') ? fileName : `${fileName}.docx`);
        this.showSuccess('Pobrano dokument');
        this.isLoading.set(false);
      },
      error: (err) => {
        this.isLoading.set(false);
        if (err?.status === 403) {
          this.userDownload.set(false);
          this.showError('Pobieranie pliku na komputer nie jest dostępne dla tego dokumentu.');
          return;
        }
        this.showError('Nie udało się pobrać dokumentu.');
      }
    });
  }

  downloadOriginalDocument(): void {
    this.showMenu.set(false);

    if (!this.canDownloadOriginal()) {
      this.showError('Pobieranie oryginału nie jest dostępne dla tego dokumentu.');
      return;
    }

    if (this.loadedFromDisk() && this.diskOriginalFile) {
      this.saveBlobToDisk(this.diskOriginalFile, this.diskOriginalFile.name);
      this.showSuccess('Pobrano oryginał dokumentu');
      return;
    }

    const masterId = this.documentMasterId();
    if (!masterId) {
      this.showError('Dokument nie jest powiązany z bazą — brak oryginału do pobrania.');
      return;
    }

    this.isLoading.set(true);
    this.documentStorageService.downloadBaseVersion(masterId).subscribe({
      next: (blob) => {
        const fileName = this.originalFileName() || `${this.documentMetadata().title || 'dokument'}.docx`;
        this.saveBlobToDisk(blob, fileName);
        this.showSuccess('Pobrano oryginał dokumentu');
        this.isLoading.set(false);
      },
      error: () => {
        this.isLoading.set(false);
        this.showError('Nie udało się pobrać oryginału dokumentu.');
      }
    });
  }

  private saveBlobToDisk(blob: Blob, fileName: string): void {
    const objectUrl = window.URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = objectUrl;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    window.URL.revokeObjectURL(objectUrl);
  }

  openTemplate(templateId: string): void {
    this.isLoading.set(true);
    this.showTemplates.set(false);
    
    this.documentService.getTemplate(templateId).subscribe({
      next: (content) => {
        this.documentContent.set(content.html);
        this.documentMetadata.set(content.metadata);
        this.originalFileName.set('');
        
        if (this.editor) {
          this.editor.setContent(content.html);
        }
        
        this.isLoading.set(false);
      },
      error: (err) => {
        this.showError('Nie udało się załadować szablonu');
        this.isLoading.set(false);
      }
    });
  }

  onCommand(event: { command: EditorCommand; value?: string }): void {
    this.editor?.executeCommand(event.command, event.value);
  }

  onFontSizeChange(size: number): void {
    this.editor?.setFontSize(size);
  }

  onFontFamilyChange(family: string): void {
    this.editor?.setFontFamily(family);
  }

  onTextColorChange(color: string): void {
    this.editor?.setTextColor(color);
  }

  onBackgroundColorChange(color: string): void {
    this.editor?.setBackgroundColor(color);
  }

  onInsertLink(event: { url: string; text?: string }): void {
    this.editor?.insertLink(event.url, event.text);
  }

  onInsertImage(): void {
    this.editor?.saveSelection();

    const input = document.createElement('input');
    input.type = 'file';
    input.accept = 'image/*';
    
    input.onchange = (e) => {
      const file = (e.target as HTMLInputElement).files?.[0];
      if (file) {
        this.uploadAndInsertImage(file);
      }
    };
    
    input.click();
  }

  private uploadAndInsertImage(file: File): void {
    const reader = new FileReader();
    reader.onload = (e) => {
      const base64 = e.target?.result as string;
      if (base64 && this.editor) {
        this.editor.focus();
        this.editor.restoreSelection();
        this.editor.insertImage(base64, file.name);
      }
    };
    reader.readAsDataURL(file);
  }

  onInsertTable(config: string): void {
    if (this.editor) {
      this.editor.insertTable(config);
      this.applyTableAutoFit(this.tableDialogData.autoFitBehavior, this.tableDialogData.fixedWidth);
    }
  }

  onInsertFootnote(): void {
    this.editor?.addFootnoteAtCursor();
  }

  onInsertEndnote(): void {
    this.editor?.addEndnoteAtCursor();
  }

  onStyleChange(style: DocumentStyle): void {
    if (this.editor) {
      this.editor.applyDocumentStyle(style);
    }
  }

  private copiedFormat: any = null;

  onCopyFormat(): void {
    if (this.editor) {
      this.copiedFormat = this.editor.getCurrentFormatting();
    }
  }

  onPasteFormat(): void {
    if (this.editor && this.copiedFormat) {
      this.editor.applyFormatting(this.copiedFormat);
    }
  }

  private lastSearchText = '';

  onSearchInDocument(event: { text: string; direction: 'next' | 'previous' }): void {
    if (!this.editor) return;

    let result: { count: number; currentIndex: number };

    if (event.text !== this.lastSearchText) {
      this.lastSearchText = event.text;
      result = this.editor.searchText(event.text, event.direction);
    } else {
      result = event.direction === 'next' ? this.editor.findNext() : this.editor.findPrevious();
    }

    if (this.toolbar) {
      this.toolbar.updateSearchResults(result.count, result.currentIndex);
    }
  }

  onReplaceInDocument(event: { searchText: string; replaceText: string; all: boolean }): void {
    if (!this.editor) return;

    let result: { count: number; currentIndex: number };

    if (event.all) {
      result = this.editor.replaceAllMatches(event.replaceText);
    } else {
      result = this.editor.replaceCurrentMatch(event.replaceText);
    }

    if (this.toolbar) {
      this.toolbar.updateSearchResults(result.count, result.currentIndex);
    }
  }

  onClearSearch(): void {
    if (this.editor) {
      this.editor.clearSearchHighlights();
    }
    this.lastSearchText = '';
  }

  onContentChange(html: string): void {
    this.documentContent.set(html);
    this.documentMetadata.update(m => ({
      ...m,
      modified: new Date().toISOString()
    }));
  }

  onHeaderChange(header: HeaderFooterContent): void {
    this.headerContent.set(header);
    this.documentMetadata.update(m => ({
      ...m,
      modified: new Date().toISOString()
    }));
  }

  onFooterChange(footer: HeaderFooterContent): void {
    this.footerContent.set(footer);
    this.documentMetadata.update(m => ({
      ...m,
      modified: new Date().toISOString()
    }));
  }

  onStateChange(state: EditorState): void {
    this.editorState.set(state);
    this.detectTableContext();
  }

  private detectTableContext(): void {
    const selection = window.getSelection();
    const editorEl = this.editor?.editorContent?.nativeElement;
    const ctx = resolveTableContext(selection?.anchorNode, editorEl);

    if (ctx.placement === 'outside-editor') return;

    this.isInTable.set(ctx.placement === 'in-table');
    this.activeTableCell.set(ctx.cell);
    this.activeTable.set(ctx.table);
    if (ctx.placement === 'outside-table') {
      this.clearCellSelection();
    }
    this.syncTablePanel();
  }

  onEditorSelectionChange(): void {
    this.detectTableContext();
  }

  private syncTablePanel(): void {
    if (!this.isInTable() || this.editingDisabled()) {
      this.showTablePanel.set(false);
      this.tablePanelManuallyClosed = false;
      return;
    }
    if (this.tablePanelManuallyClosed) {
      return;
    }
    if (this.showFindReplace()) {
      this.closeFindReplace();
    }
    this.showTablePanel.set(true);
  }

  closeTablePanel(): void {
    this.showTablePanel.set(false);
    this.tablePanelManuallyClosed = true;
  }

  @HostListener('document:keydown.escape', ['$event'])
  onEscapeKeydown(event: KeyboardEvent): void {
    if (event.defaultPrevented) return;
    if (this.isAnyDialogOpen() || this.showContextMenu()) return;

    if (this.editingSection() !== 'body') {
      event.preventDefault();
      this.editor?.stopEditingHeaderFooter();
      return;
    }

    if (!this.showFindReplace() && !this.showTablePanel()) return;

    event.preventDefault();
    this.closeActiveSidePanel();
  }

  closeActiveSidePanel(): void {
    const focusInPanel = !!(document.activeElement as HTMLElement | null)
      ?.closest('.search-panel, d2-table-properties-panel');

    if (this.showFindReplace()) {
      this.closeFindReplace();
    } else if (this.showTablePanel()) {
      this.closeTablePanel();
    }

    if (focusInPanel) {
      this.editor?.focus();
    }
  }

  private isAnyDialogOpen(): boolean {
    return this.showTemplates()
      || this.showPageSetup()
      || this.showHeaderFooterDialog()
      || this.showBarcodeDialog()
      || this.showParagraphDialog()
      || this.showInsertTableDialog()
      || this.showPropertiesDialog()
      || this.showSignatureDialog()
      || this.showLeaveDialog();
  }

  setZoom(level: number): void {
    this.zoomLevel.set(level);
    this.measureVRuler();
  }

  onRulerWheel(e: WheelEvent): void {
    const container = this.editorScrollContainer?.nativeElement;
    if (!container) return;
    e.preventDefault();
    container.scrollTop += e.deltaY;
    container.scrollLeft += e.deltaX;
  }

  onEditorScroll(event: Event): void {
    const container = event.target as HTMLElement;
    const scrollTop = container.scrollTop;
    const scale = this.zoomLevel() / 100;

    if (this.verticalRulerBar?.nativeElement) {
      this.verticalRulerBar.nativeElement.scrollTop = scrollTop;
    }

    if (this.horizontalRulerInner?.nativeElement) {
      this.horizontalRulerInner.nativeElement.style.transform = `translateX(${-container.scrollLeft}px)`;
    }
    
    const PAGE_HEIGHT = 1122;
    const PAGE_GAP = 40;
    const PADDING_TOP = 20;
    
    const scaledPageHeight = PAGE_HEIGHT * scale;
    const scaledGap = PAGE_GAP * scale;
    
    const viewportCenter = scrollTop + (container.clientHeight / 2) - (PADDING_TOP * scale);
    
    const currentPageNum = Math.floor(viewportCenter / (scaledPageHeight + scaledGap)) + 1;
    const maxPages = this.totalPages();
    
    this.currentPage.set(Math.min(Math.max(1, currentPageNum), maxPages));
    
    if (maxPages > 1) {
      this.showPageIndicator.set(true);
      
      if (this.pageIndicatorTimeout) {
        clearTimeout(this.pageIndicatorTimeout);
      }
      this.pageIndicatorTimeout = setTimeout(() => {
        this.showPageIndicator.set(false);
      }, 1500);
    }
  }

  onPagesChange(pageCount: number): void {
    this.totalPages.set(pageCount);
    if (this.currentPage() > pageCount) {
      this.currentPage.set(pageCount);
    }
    this.ensureVRulerObserver();
    this.measureVRuler();
  }

  printDocument(): void {
    window.print();
    this.showMenu.set(false);
  }

  private showSuccess(message: string): void {
    this.errorMessage.set(null);
    this.infoMessage.set(null);
    this.successMessage.set(message);
    setTimeout(() => this.successMessage.set(null), 3000);
  }

  private showInfo(message: string): void {
    this.errorMessage.set(null);
    this.successMessage.set(null);
    this.infoMessage.set(message);
    setTimeout(() => this.infoMessage.set(null), 5000);
  }

  private showError(message: string): void {
    this.successMessage.set(null);
    this.infoMessage.set(null);
    this.errorMessage.set(message);
    setTimeout(() => this.errorMessage.set(null), 5000);
  }

  toggleMenu(): void {
    const wasOpen = this.showMenu();
    this.closeAllMenus();
    this.showMenu.set(!wasOpen);
  }

  toggleEditMenu(): void {
    const wasOpen = this.showEditMenu();
    this.closeAllMenus();
    this.showEditMenu.set(!wasOpen);
  }

  toggleFormatMenu(): void {
    const wasOpen = this.showFormatMenu();
    this.closeAllMenus();
    this.showFormatMenu.set(!wasOpen);
  }

  toggleInsertMenu(): void {
    const wasOpen = this.showInsertMenu();
    this.closeAllMenus();
    this.showInsertMenu.set(!wasOpen);
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    const isMenuArea = target.closest('.menu-bar') || 
                       target.closest('.dropdown-menu');
    const isShadingArea = target.closest('.shading-dropdown') || target.closest('.table-toolbar-btn-shading');
    if (!isMenuArea && !isShadingArea) {
      this.closeAllMenus();
    }
    if (!isShadingArea) {
      this.showShadingDropdown.set(false);
    }
  }


  @HostListener('mousedown', ['$event'])
  onCellMouseDown(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    const editorEl = this.editor?.editorContent?.nativeElement;
    if (!editorEl) return;

    const cell = target.closest('td, th') as HTMLTableCellElement | null;
    if (cell && editorEl.contains(cell)) {
      this.cellSelectionStartCell = cell;
      this.isCellSelecting = false;
      if (!event.shiftKey) {
        this.clearCellSelection();
      }
    } else if (!target.closest('.table-toolbar') && !target.closest('.context-menu') && !target.closest('.shading-dropdown') && !target.closest('d2-table-properties-panel')) {
      this.cellSelectionStartCell = null;
      this.clearCellSelection();
    }
  }

  @HostListener('document:mousemove', ['$event'])
  onCellMouseMove(event: MouseEvent): void {
    if (!this.cellSelectionStartCell || !(event.buttons & 1)) return;

    const target = event.target as HTMLElement;
    const cell = target.closest('td, th') as HTMLTableCellElement | null;
    const editorEl = this.editor?.editorContent?.nativeElement;

    if (cell && editorEl && editorEl.contains(cell) && cell !== this.cellSelectionStartCell) {
      const startTable = this.cellSelectionStartCell.closest('table');
      const endTable = cell.closest('table');
      if (startTable && startTable === endTable) {
        this.isCellSelecting = true;
        event.preventDefault();
        window.getSelection()?.removeAllRanges();
        this.selectCellRange(this.cellSelectionStartCell, cell);
      }
    }
  }

  @HostListener('document:mouseup', ['$event'])
  onCellMouseUp(event: MouseEvent): void {
    if (this.isCellSelecting) {
      this.isCellSelecting = false;
      window.getSelection()?.removeAllRanges();
    }
    this.cellSelectionStartCell = null;
  }

  private selectCellRange(start: HTMLTableCellElement, end: HTMLTableCellElement): void {
    const table = start.closest('table') as HTMLTableElement;
    if (!table) return;

    const startPos = this.getCellPosition(start);
    const endPos = this.getCellPosition(end);
    if (!startPos || !endPos) return;

    const minRow = Math.min(startPos.rowIndex, endPos.rowIndex);
    const maxRow = Math.max(startPos.rowIndex, endPos.rowIndex);
    const minCol = Math.min(startPos.colIndex, endPos.colIndex);
    const maxCol = Math.max(startPos.colIndex, endPos.colIndex);

    const newSelection = new Set<HTMLTableCellElement>();
    for (let r = minRow; r <= maxRow; r++) {
      const row = table.rows[r];
      if (!row) continue;
      for (let c = minCol; c <= maxCol; c++) {
        if (c < row.cells.length) {
          newSelection.add(row.cells[c]);
        }
      }
    }
    this.applyCellSelection(newSelection);
  }

  private applyCellSelection(cells: Set<HTMLTableCellElement>): void {
    const prev = this.selectedCells();
    prev.forEach(c => c.classList.remove('table-cell-selected'));
    cells.forEach(c => c.classList.add('table-cell-selected'));
    this.selectedCells.set(cells);
  }

  clearCellSelection(): void {
    const prev = this.selectedCells();
    prev.forEach(c => c.classList.remove('table-cell-selected'));
    this.selectedCells.set(new Set());
  }

  @HostListener('contextmenu', ['$event'])
  onContextMenu(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    const isEditorArea = target.closest('.editor-main') || 
                         target.closest('d2-wysiwyg-editor') ||
                         target.closest('.paper-container');
    if (isEditorArea) {
      event.preventDefault();
      this.closeAllMenus();
      this.contextSubmenu.set(null);

      const cellTarget = target.closest('td, th') as HTMLElement | null;
      this.contextMenuTargetCell.set(cellTarget);

      const imgTarget = (target.tagName === 'IMG' ? target : target.closest('img')) as HTMLImageElement | null;
      this.contextMenuTargetImage.set(imgTarget);

      const margin = 8;
      const menuWidth = 260;
      const menuHeight = 420;
      const maxX = Math.max(margin, window.innerWidth - menuWidth - margin);
      const maxY = Math.max(margin, window.innerHeight - menuHeight - margin);
      const x = Math.min(Math.max(margin, event.clientX), maxX);
      const y = Math.min(Math.max(margin, event.clientY), maxY);

      this.contextMenuX.set(x);
      this.contextMenuY.set(y);
      this.showContextMenu.set(true);
    }
  }

  closeAllMenus(): void {
    this.showMenu.set(false);
    this.showEditMenu.set(false);
    this.showFormatMenu.set(false);
    this.showInsertMenu.set(false);
    this.showToolsMenu.set(false);
    this.showViewMenu.set(false);
    this.showHelpMenu.set(false);
    this.activeSubmenu.set(null);
    this.showTemplates.set(false);
    this.showContextMenu.set(false);
    this.contextSubmenu.set(null);
    this.showShadingDropdown.set(false);
  }

  finishDocument(): void {
    this.showMenu.set(false);

    if (this.readOnly()) {
      this.showInfo('Tryb podglądu — dokument jest tylko do odczytu.');
      return;
    }

    const masterId = this.documentMasterId();
    const versionId = this.documentVersionId();
    if (!masterId || !versionId) {
      this.showError('Dokument nie jest powiązany z edytowalną wersją — nie można zakończyć i wysłać.');
      return;
    }

    if (this.isFinishing()) {
      return;
    }

    this.isFinishing.set(true);
    this.sendCancelled.set(false);
    this.sendDetachedFromUi.set(false);
    this.deliveryStatus.set('Sending');
    this.showSendErrorModal.set(false);
    this.showSendingModal.set(true);

    this.finishSendSub?.unsubscribe();
    this.finishSendSub = this.documentService.saveDocument(this.buildSaveRequest()).pipe(
      switchMap(blob => from(this.blobToBase64(blob))),
      switchMap(base64 => this.documentStorageService.finishAndSend(masterId, versionId, { content: base64 }))
    ).subscribe({
      next: result => {
        if (this.sendDetachedFromUi()) return;

        this.deliveryId.set(result.deliveryId);
        this.deliveryStatus.set(result.status);

        if (result.delivered) {
          this.showSendingModal.set(false);
          this.enterFinishedAndCloseTab();
          return;
        }

        if (result.status === 'Sending') {
          return;
        }

        this.showSendingModal.set(false);
        this.showSendErrorModal.set(true);
      },
      error: () => {
        if (this.sendCancelled() || this.sendDetachedFromUi()) return;

        this.showSendingModal.set(false);
        this.deliveryStatus.set('RetryScheduled');
        this.showSendErrorModal.set(true);
      }
    });
  }

  cancelSend(): void {
    this.sendCancelled.set(true);
    this.finishSendSub?.unsubscribe();
    this.showSendingModal.set(false);

    const masterId = this.documentMasterId();
    if (!masterId) {
      this.afterSendCancelled();
      return;
    }

    this.documentStorageService.abortSend(masterId).subscribe({
      next: () => this.afterSendCancelled(),
      error: () => this.afterSendCancelled()
    });
  }

  private afterSendCancelled(): void {
    this.isFinishing.set(false);
    this.sendCancelled.set(false);
    this.deliveryStatus.set('Cancelled');
    this.showSuccess('Wysyłka anulowana. Możesz dalej edytować dokument.');
  }

  closeSendingModal(): void {
    this.sendDetachedFromUi.set(true);
    this.showSendingModal.set(false);
    this.enterFinishedAndCloseTab();
  }

  abortSend(): void {
    const masterId = this.documentMasterId();
    if (!masterId) return;

    this.documentStorageService.abortSend(masterId).subscribe({
      next: () => {
        this.showSendErrorModal.set(false);
        this.isFinishing.set(false);
        this.showSuccess('Wysyłka przerwana. Możesz dalej edytować dokument.');
      },
      error: () => {
        this.showError('Nie udało się przerwać wysyłki.');
      }
    });
  }

  continueSendInBackground(): void {
    const masterId = this.documentMasterId();
    if (!masterId) return;

    this.documentStorageService.continueDelivery(masterId).subscribe({
      next: result => {
        this.deliveryStatus.set(result.deliveryStatus);
        this.showSendErrorModal.set(false);
        this.enterFinishedAndCloseTab();
      },
      error: () => {
        this.showError('Nie udało się przekazać wysyłki do realizacji w tle.');
      }
    });
  }

  private enterFinishedAndCloseTab(): void {
    this.isFinishing.set(false);
    this.stopAutoSave();
    this.autoSaveEnabled.set(false);
    this.workFinished.set(true);

    this.tryCloseBrowserTab();
  }

  private tryCloseBrowserTab(): void {
    try {
      window.close();
    } catch {
    }
  }

  closeFinishedTab(): void {
    this.tryCloseBrowserTab();
    clearTimeout(this.tabCloseHintTimer);
    this.tabCloseHintTimer = setTimeout(() => this.tabCloseBlocked.set(true), 300);
  }

  openReportEmail(): void {
    this.closeAllMenus();

    const subject = encodeURIComponent('[Doc2 Editor] Zgłoszenie');
    const body = encodeURIComponent(
      `Dzień dobry,\n\n` +
      `proszę o opis problemu poniżej:\n\n` +
      `\n\n\n` +
      this.buildDiagnosticsBlock()
    );

    const to = environment.supportEmail ?? '';
    window.open(`mailto:${to}?subject=${subject}&body=${body}`, '_self');
  }

  copyDiagnostics(): void {
    this.closeAllMenus();
    const text = this.formatDiagnostics(this.collectDiagnosticRows());
    navigator.clipboard?.writeText(text)
      .then(() => this.notification.success('Skopiowano informacje diagnostyczne do schowka'))
      .catch(() => this.notification.error('Nie udało się skopiować informacji diagnostycznych'));
  }

  private collectDiagnosticRows(): Array<[string, string]> {
    const masterId = this.documentMasterId() ?? '—';
    const versionId = this.documentVersionId() ?? '—';

    const rows: Array<[string, string]> = [
      ['Data zgłoszenia',  new Date().toLocaleString('pl-PL')],
      ['Master ID',        masterId],
      ['Version ID',       versionId],
      ['Wersja aplikacji', this.buildInfo.buildNumber?.() ?? '—'],
      ['Data buildu',      this.buildInfo.buildDate?.() ?? '—'],
      ['Źródło wersji',    this.buildInfo.isApiData?.() ? 'API (health-check)' : 'front (fallback)'],
      ['Środowisko',       this.buildInfo.environment?.() ?? '—'],
      ['Połączenie',       navigator.onLine ? 'online' : 'offline'],
      ['URL',              window.location.href],
      ['Przeglądarka',     navigator.userAgent],
      ['Platforma',        (navigator as unknown as { platform?: string }).platform ?? '—'],
      ['Język',            navigator.language],
      ['Viewport',         `${window.innerWidth}×${window.innerHeight}`],
      ['Ekran',            `${window.screen.width}×${window.screen.height}`],
    ];

    const err = this.lastHttpError.lastError();
    if (err) {
      rows.push(['Ostatni błąd',  `HTTP ${err.status} ${err.method} ${err.url}`]);
      rows.push(['— czas',        err.at]);
      rows.push(['— szczegóły',   err.detail]);
    }

    return rows;
  }

  private formatDiagnostics(rows: Array<[string, string]>): string {
    const labelWidth = Math.max(...rows.map(([k]) => k.length));
    return rows.map(([k, v]) => `  ${k.padEnd(labelWidth)} : ${v}`).join('\n');
  }

  private buildDiagnosticsBlock(): string {
    const separator = '────────────────────────────────────────────';
    return (
      `${separator}\n` +
      `  INFORMACJE DIAGNOSTYCZNE — proszę nie usuwać\n` +
      `${separator}\n` +
      `${this.formatDiagnostics(this.collectDiagnosticRows())}\n` +
      `${separator}\n`
    );
  }

  setActiveSubmenu(submenu: string | null): void {
    this.activeSubmenu.set(submenu);
  }


  undo(): void {
    this.editor?.executeCommand('undo');
    this.closeAllMenus();
  }

  redo(): void {
    this.editor?.executeCommand('redo');
    this.closeAllMenus();
  }

  cut(): void {
    document.execCommand('cut');
    this.closeAllMenus();
  }

  copy(): void {
    document.execCommand('copy');
    this.closeAllMenus();
  }

  paste(): void {
    const target = this.editor?.captureSelectionBookmark() ?? null;
    this.closeAllMenus();
    navigator.clipboard.readText()
      .then(text => this.editor?.pastePlainTextAt(target, text))
      .catch(() => { document.execCommand('paste'); });
  }

  pasteWithoutFormatting(): void {
    const target = this.editor?.captureSelectionBookmark() ?? null;
    this.closeAllMenus();
    navigator.clipboard.readText()
      .then(text => this.editor?.pastePlainTextAt(target, text))
      .catch(() => {  });
  }

  selectAll(): void {
    this.editor?.executeCommand('selectAll');
    this.closeAllMenus();
  }

  deleteSelection(): void {
    document.execCommand('delete');
    this.closeAllMenus();
  }

  @HostListener('document:keydown', ['$event'])
  onGlobalKeydown(e: KeyboardEvent): void {
    if (!(e.ctrlKey || e.metaKey) || e.altKey) return;
    const key = e.key.toLowerCase();
    if (key === 'f') {
      e.preventDefault();
      this.openFindReplace();
      return;
    }
    if (key === 'h' && !this.editingDisabled()) {
      e.preventDefault();
      this.openFindReplace();
      return;
    }
    if (key === 'a') {
      const tag = (e.target as HTMLElement)?.tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
      e.preventDefault();
      this.selectAll();
      return;
    }

    const target = e.target as HTMLElement | null;
    const tag = target?.tagName;
    const inContentEditable = !!target && (target.isContentEditable
      || !!target.closest?.('[contenteditable=""], [contenteditable="true"], [contenteditable="plaintext-only"]'));
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || inContentEditable) {
      return;
    }

    switch (key) {
      case 'z':
        if (this.editingDisabled()) return;
        e.preventDefault();
        if (e.shiftKey) {
          this.redo();
        } else {
          this.undo();
        }
        break;
      case 'y':
        if (this.editingDisabled()) return;
        e.preventDefault();
        this.redo();
        break;
      case 'x':
        if (this.editingDisabled()) return;
        e.preventDefault();
        this.cut();
        break;
      case 'c':
        e.preventDefault();
        this.copy();
        break;
      case 'v':
        if (this.editingDisabled()) return;
        e.preventDefault();
        this.paste();
        break;
    }
  }

  openFindReplace(): void {
    this.showFindReplace.set(true);
    this.showTablePanel.set(false);
    this.closeAllMenus();
  }


  toggleBold(): void {
    this.editor?.executeCommand('bold');
    this.closeAllMenus();
  }

  toggleItalic(): void {
    this.editor?.executeCommand('italic');
    this.closeAllMenus();
  }

  toggleUnderline(): void {
    this.editor?.executeCommand('underline');
    this.closeAllMenus();
  }

  toggleStrikethrough(): void {
    this.editor?.executeCommand('strikethrough');
    this.closeAllMenus();
  }

  toggleSuperscript(): void {
    this.editor?.executeCommand('superscript');
    this.closeAllMenus();
  }

  toggleSubscript(): void {
    this.editor?.executeCommand('subscript');
    this.closeAllMenus();
  }

  increaseFontSize(): void {
    const currentSize = this.editorState()?.currentStyle?.fontSize || 11;
    this.editor?.setFontSize(currentSize + 1);
    this.closeAllMenus();
  }

  decreaseFontSize(): void {
    const currentSize = this.editorState()?.currentStyle?.fontSize || 11;
    if (currentSize > 1) {
      this.editor?.setFontSize(currentSize - 1);
    }
    this.closeAllMenus();
  }

  toUpperCase(): void {
    const selection = window.getSelection();
    if (selection && selection.toString()) {
      const text = selection.toString().toUpperCase();
      document.execCommand('insertText', false, text);
    }
    this.closeAllMenus();
  }

  toLowerCase(): void {
    const selection = window.getSelection();
    if (selection && selection.toString()) {
      const text = selection.toString().toLowerCase();
      document.execCommand('insertText', false, text);
    }
    this.closeAllMenus();
  }

  toTitleCase(): void {
    const selection = window.getSelection();
    if (selection && selection.toString()) {
      const text = selection.toString().replace(/\b\w/g, l => l.toUpperCase());
      document.execCommand('insertText', false, text);
    }
    this.closeAllMenus();
  }

  alignLeft(): void {
    this.editor?.executeCommand('justifyLeft');
    this.closeAllMenus();
  }

  alignCenter(): void {
    this.editor?.executeCommand('justifyCenter');
    this.closeAllMenus();
  }

  alignRight(): void {
    this.editor?.executeCommand('justifyRight');
    this.closeAllMenus();
  }

  alignJustify(): void {
    this.editor?.executeCommand('justifyFull');
    this.closeAllMenus();
  }

  increaseIndent(): void {
    this.editor?.executeCommand('indent');
    this.closeAllMenus();
  }

  decreaseIndent(): void {
    this.editor?.executeCommand('outdent');
    this.closeAllMenus();
  }

  setLineSpacingSingle(): void {
    this.setLineSpacing(1);
  }

  setLineSpacing115(): void {
    this.setLineSpacing(1.15);
  }

  setLineSpacing15(): void {
    this.setLineSpacing(1.5);
  }

  setLineSpacingDouble(): void {
    this.setLineSpacing(2);
  }

  private setLineSpacing(value: number): void {
    const selection = window.getSelection();
    if (selection && selection.rangeCount > 0) {
      const range = selection.getRangeAt(0);
      let block = range.startContainer as Node;
      if (block.nodeType === Node.TEXT_NODE) {
        block = block.parentNode!;
      }
      while (block && !['P', 'DIV', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'LI'].includes((block as HTMLElement).tagName)) {
        block = block.parentNode!;
      }
      if (block) {
        applyWordLineSpacing(block as HTMLElement, value);
        this.notifyEditorChange();
      }
    }
    this.closeAllMenus();
  }

  addSpaceBefore(): void {
    this.setBlockSpacing('marginTop', '12pt');
  }

  removeSpaceBefore(): void {
    this.setBlockSpacing('marginTop', '0');
  }

  addSpaceAfter(): void {
    this.setBlockSpacing('paddingBottom', '12pt');
  }

  removeSpaceAfter(): void {
    this.setBlockSpacing('paddingBottom', '0');
  }

  private setBlockSpacing(property: 'marginTop' | 'paddingBottom', value: string): void {
    const selection = window.getSelection();
    if (selection && selection.rangeCount > 0) {
      const range = selection.getRangeAt(0);
      let block = range.startContainer as Node;
      if (block.nodeType === Node.TEXT_NODE) {
        block = block.parentNode!;
      }
      while (block && !['P', 'DIV', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'LI'].includes((block as HTMLElement).tagName)) {
        block = block.parentNode!;
      }
      if (block) {
        (block as HTMLElement).style[property] = value;
        if (property === 'paddingBottom') {
          (block as HTMLElement).style.marginBottom = '';
        }
      }
    }
    this.closeAllMenus();
  }

  insertBulletList(): void {
    this.editor?.executeCommand('insertUnorderedList');
    this.closeAllMenus();
  }

  insertNumberedList(): void {
    this.editor?.executeCommand('insertOrderedList');
    this.closeAllMenus();
  }

  clearFormatting(): void {
    this.editor?.executeCommand('removeFormat');
    this.closeAllMenus();
  }

  openBarcodeDialog(): void {
    this.editor?.saveSelection();
    this.showBarcodeDialog.set(true);
    this.closeAllMenus();
  }

  onInsertBarcode(event: { base64Image: string; content: string; showValueBelow: boolean }): void {
    if (this.editor) {
      this.editor.focus();
      this.editor.restoreSelection();
      if (event.showValueBelow) {
        this.editor.insertBarcodeWithValue(event.base64Image, event.content);
      } else {
        this.editor.insertImage(event.base64Image, 'barcode');
      }
    }
    this.showBarcodeDialog.set(false);
  }

  closeBarcodeDialog(): void {
    this.showBarcodeDialog.set(false);
  }

  insertHorizontalLine(): void {
    this.editor?.insertHorizontalRule();
    this.closeAllMenus();
  }

  insertPageBreak(): void {
    this.editor?.insertPageBreak();
    this.closeAllMenus();
  }

  setColumns(count: number): void {
    this.editor?.setBaseColumns(count);
    this.closeAllMenus();
  }

  currentColumnCount(): number {
    return this.editor?.getBaseColumnCount() ?? 1;
  }

  insertColumnBreak(): void {
    this.editor?.insertColumnBreak();
    this.closeAllMenus();
  }

  editHeader(): void {
    this.editor?.startEditingHeader();
  }

  editFooter(): void {
    this.editor?.startEditingFooter();
  }

  findText = signal('');
  replaceText = signal('');
  findResultCount = signal(0);
  findCurrentIndex = signal(-1);
  searchResults = signal<{ before: string; match: string; after: string }[]>([]);

  private refreshSearchResults(result: { count: number; currentIndex: number }): void {
    this.findResultCount.set(result.count);
    this.findCurrentIndex.set(result.currentIndex);
    this.searchResults.set(this.editor?.getSearchSnippets() ?? []);
  }

  onFindInput(value: string): void {
    this.findText.set(value);
    if (!value || !this.editor) {
      this.editor?.clearSearchHighlights();
      this.lastSearchText = '';
      this.findResultCount.set(0);
      this.findCurrentIndex.set(-1);
      this.searchResults.set([]);
      return;
    }
    this.lastSearchText = value;
    this.refreshSearchResults(this.editor.searchText(value, 'next'));
  }

  findNext(): void {
    const text = this.findText();
    if (!text || !this.editor) return;
    const result = text !== this.lastSearchText
      ? (this.lastSearchText = text, this.editor.searchText(text, 'next'))
      : this.editor.findNext();
    this.refreshSearchResults(result);
  }

  findPrev(): void {
    const text = this.findText();
    if (!text || !this.editor) return;
    const result = text !== this.lastSearchText
      ? (this.lastSearchText = text, this.editor.searchText(text, 'previous'))
      : this.editor.findPrevious();
    this.refreshSearchResults(result);
  }

  goToResult(index: number): void {
    const result = this.editor?.goToMatch(index);
    if (result) this.findCurrentIndex.set(result.currentIndex);
  }

  closeFindReplace(): void {
    this.showFindReplace.set(false);
    this.editor?.clearSearchHighlights();
    this.lastSearchText = '';
    this.findResultCount.set(0);
    this.findCurrentIndex.set(-1);
    this.searchResults.set([]);
    this.syncTablePanel();
  }

  replaceOne(): void {
    if (this.editingDisabled() || !this.editor || !this.findText()) return;
    if (this.findText() !== this.lastSearchText) {
      this.findNext();
      return;
    }
    const result = this.editor.replaceCurrentMatch(this.replaceText());
    this.findResultCount.set(result.count);
    this.findCurrentIndex.set(result.currentIndex);
  }

  replaceAll(): void {
    if (this.editingDisabled() || !this.editor || !this.findText()) return;
    if (this.findText() !== this.lastSearchText) {
      this.lastSearchText = this.findText();
      this.editor.searchText(this.findText(), 'next');
    }
    const result = this.editor.replaceAllMatches(this.replaceText());
    this.findResultCount.set(result.count);
    this.findCurrentIndex.set(result.currentIndex);
    this.showSuccess(`Zamieniono wszystkie wystąpienia "${this.findText()}"`);
  }

  closeMenuOnOutsideClick(event: MouseEvent): void {
    this.showMenu.set(false);
    this.showTemplates.set(false);
  }

  openPageSetup(): void {
    this.showPageSetup.set(true);
    this.showMenu.set(false);
  }

  applyMarginPreset(preset: { name: string; margins: PageMargins }): void {
    this.pageSettings.update(s => ({
      ...s,
      margins: { ...preset.margins }
    }));
  }

  updateMargin(side: keyof PageMargins, value: number): void {
    this.pageSettings.update(s => ({
      ...s,
      margins: { ...s.margins, [side]: value }
    }));
  }

  getMarginStyles(): { [key: string]: string } {
    const m = this.pageSettings().margins;
    const cmToPx = CSS_PX_PER_CM;
    return {
      'padding-top': `${m.top * cmToPx}px`,
      'padding-bottom': `${m.bottom * cmToPx}px`,
      'padding-left': `${m.left * cmToPx}px`,
      'padding-right': `${m.right * cmToPx}px`
    };
  }

  setOrientation(orientation: 'portrait' | 'landscape'): void {
    this.pageSettings.update(s => ({ ...s, orientation }));
  }

  isPresetActive(preset: { name: string; margins: PageMargins }): boolean {
    const current = this.pageSettings().margins;
    return current.top === preset.margins.top &&
           current.bottom === preset.margins.bottom &&
           current.left === preset.margins.left &&
           current.right === preset.margins.right;
  }

  getPresetPreviewStyle(preset: { name: string; margins: PageMargins }): { [key: string]: string } {
    const m = preset.margins;
    const scale = 2;
    return {
      'padding': `${m.top * scale}px ${m.right * scale}px ${m.bottom * scale}px ${m.left * scale}px`
    };
  }

  getPreviewStyle(): { [key: string]: string } {
    const settings = this.pageSettings();
    const isLandscape = settings.orientation === 'landscape';
    
    return {
      'width': isLandscape ? '140px' : '100px',
      'height': isLandscape ? '100px' : '140px'
    };
  }

  getContentPreviewStyle(): { [key: string]: string } {
    const m = this.pageSettings().margins;
    const scale = 4;
    return {
      'padding-top': `${m.top * scale}px`,
      'padding-bottom': `${m.bottom * scale}px`,
      'padding-left': `${m.left * scale}px`,
      'padding-right': `${m.right * scale}px`
    };
  }

  applyPageSettings(): void {
    this.showPageSetup.set(false);
    this.showSuccess('Zastosowano ustawienia strony');
  }


  onOpenHeaderFooterSettings(data: {
    headerMargin: number;
    footerMargin: number;
    differentFirstPage: boolean;
    differentOddEven: boolean;
  }): void {
    this.headerFooterDialogData.set(data);
    this.showHeaderFooterDialog.set(true);
  }

  closeHeaderFooterDialog(): void {
    this.showHeaderFooterDialog.set(false);
  }

  updateHeaderFooterDialogData(field: string, value: number | boolean): void {
    this.headerFooterDialogData.update(data => ({
      ...data,
      [field]: value
    }));
  }

  applyHeaderFooterSettings(): void {
    const data = this.headerFooterDialogData();
    this.editor?.applyHeaderFooterSettings(data);
    this.closeHeaderFooterDialog();
  }


  onEditorMouseUp(event: MouseEvent): void {
    if (this.showContextMenu()) return;

    setTimeout(() => {
      const selection = window.getSelection();
      if (!selection || selection.isCollapsed || selection.rangeCount === 0) {
        this.showMiniToolbar.set(false);
        return;
      }
      const range = selection.getRangeAt(0);
      const rect = range.getBoundingClientRect();
      if (rect.width === 0) {
        this.showMiniToolbar.set(false);
        return;
      }

      const toolbarWidth = 560;
      const toolbarHeight = 76;
      const margin = 8;

      let x = rect.left + rect.width / 2 - toolbarWidth / 2;
      let y = rect.top - toolbarHeight - margin;

      x = Math.max(margin, Math.min(x, window.innerWidth - toolbarWidth - margin));
      if (y < margin) {
        y = rect.bottom + margin;
      }

      this.miniToolbarX.set(x);
      this.miniToolbarY.set(y);
      this.showMiniToolbar.set(true);
    }, 10);
  }

  onEditorMouseDown(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    if (!target.closest('.mini-toolbar')) {
      this.showMiniToolbar.set(false);
    }
  }

  onMiniToolbarMouseDown(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    if (target.tagName === 'INPUT' || target.tagName === 'SELECT') {
      this.editor?.saveSelection();
    } else {
      event.preventDefault();
    }
  }

  alignmentActive(align: 'left' | 'center' | 'right' | 'justify'): boolean {
    return (this.editorState()?.currentFormatting?.alignment ?? 'left') === align;
  }

  miniToolbarCommand(command: string): void {
    this.editor?.executeCommand(command as any);
    setTimeout(() => {
      const selection = window.getSelection();
      if (!selection || selection.isCollapsed) {
        this.showMiniToolbar.set(false);
      }
    }, 50);
  }

  miniToolbarSetFontFamily(family: string): void {
    this.editor?.setFontFamily(family);
  }

  miniToolbarSetFontSize(event: Event): void {
    const val = parseInt((event.target as HTMLInputElement).value, 10);
    if (!isNaN(val) && val > 0) {
      this.editor?.setFontSize(val);
    }
  }

  miniToolbarIncreaseFontSize(): void {
    const current = this.editorState()?.currentStyle?.fontSize ?? 11;
    this.editor?.setFontSize(current + 1);
  }

  miniToolbarDecreaseFontSize(): void {
    const current = this.editorState()?.currentStyle?.fontSize ?? 11;
    if (current > 1) this.editor?.setFontSize(current - 1);
  }

  miniToolbarSetTextColor(color: string): void {
    this.editor?.setTextColor(color);
  }

  miniToolbarSetHighlightColor(color: string): void {
    this.editor?.setBackgroundColor(color);
  }

  miniToolbarCut(): void {
    document.execCommand('cut');
  }

  miniToolbarCopy(): void {
    document.execCommand('copy');
  }

  miniToolbarPaste(): void {
    const target = this.editor?.captureSelectionBookmark() ?? null;
    navigator.clipboard.readText()
      .then(text => this.editor?.pastePlainTextAt(target, text))
      .catch(() => document.execCommand('paste'));
  }

  miniToolbarIncreaseIndent(): void {
    this.editor?.executeCommand('indent');
  }

  miniToolbarDecreaseIndent(): void {
    this.editor?.executeCommand('outdent');
  }


  closeContextMenu(): void {
    this.showContextMenu.set(false);
    this.contextSubmenu.set(null);
  }

  contextMenuToggleBold(): void {
    this.editor?.executeCommand('bold');
    this.closeContextMenu();
  }

  contextMenuToggleItalic(): void {
    this.editor?.executeCommand('italic');
    this.closeContextMenu();
  }

  contextMenuToggleUnderline(): void {
    this.editor?.executeCommand('underline');
    this.closeContextMenu();
  }

  contextMenuAlignLeft(): void {
    this.editor?.executeCommand('justifyLeft');
    this.closeContextMenu();
  }

  contextMenuAlignCenter(): void {
    this.editor?.executeCommand('justifyCenter');
    this.closeContextMenu();
  }

  contextMenuAlignRight(): void {
    this.editor?.executeCommand('justifyRight');
    this.closeContextMenu();
  }

  contextMenuAlignJustify(): void {
    this.editor?.executeCommand('justifyFull');
    this.closeContextMenu();
  }

  contextMenuSetLineSpacing(value: number): void {
    this.setLineSpacing(value);
    this.closeContextMenu();
  }

  contextMenuIncreaseIndent(): void {
    this.editor?.executeCommand('indent');
    this.closeContextMenu();
  }

  contextMenuDecreaseIndent(): void {
    this.editor?.executeCommand('outdent');
    this.closeContextMenu();
  }

  setCellColor(color: string): void {
    const customSelected = this.selectedCells();
    if (customSelected.size > 0) {
      customSelected.forEach(c => (c as HTMLElement).style.backgroundColor = color);
    } else {
      const cell = this.contextMenuTargetCell() || this.activeTableCell();
      if (cell) {
        (cell as HTMLElement).style.backgroundColor = color;
      }
    }
    this.closeContextMenu();
    this.showShadingDropdown.set(false);
    this.notifyEditorChange();
  }

  clearCellColor(): void {
    const customSelected = this.selectedCells();
    if (customSelected.size > 0) {
      customSelected.forEach(c => (c as HTMLElement).style.backgroundColor = '');
    } else {
      const cell = this.contextMenuTargetCell() || this.activeTableCell();
      if (cell) {
        (cell as HTMLElement).style.backgroundColor = '';
      }
    }
    this.closeContextMenu();
    this.showShadingDropdown.set(false);
    this.notifyEditorChange();
  }


  private getContextCell(): HTMLElement | null {
    return this.contextMenuTargetCell() || this.activeTableCell();
  }

  private createCellLike(reference: HTMLTableCellElement | undefined): HTMLTableCellElement {
    const td = document.createElement('td');
    const style = reference?.getAttribute('style');
    if (style) td.setAttribute('style', style);
    td.innerHTML = '<br>';
    return td;
  }

  contextMenuInsertRowAbove(): void {
    const cell = this.getContextCell();
    if (!cell) { this.closeContextMenu(); return; }
    const row = cell.closest('tr');
    if (!row) { this.closeContextMenu(); return; }
    const table = row.closest('table')!;
    const colspan = row.querySelectorAll('td, th').length;
    const newRow = row.cloneNode(false) as HTMLTableRowElement;
    for (let i = 0; i < colspan; i++) {
      newRow.appendChild(this.createCellLike(row.cells[i]));
    }
    table.querySelector('tbody')?.insertBefore(newRow, row) || row.parentNode?.insertBefore(newRow, row);
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  contextMenuInsertRowBelow(): void {
    const cell = this.getContextCell();
    if (!cell) { this.closeContextMenu(); return; }
    const row = cell.closest('tr');
    if (!row) { this.closeContextMenu(); return; }
    const table = row.closest('table')!;
    const colspan = row.querySelectorAll('td, th').length;
    const newRow = row.cloneNode(false) as HTMLTableRowElement;
    for (let i = 0; i < colspan; i++) {
      newRow.appendChild(this.createCellLike(row.cells[i]));
    }
    const nextSibling = row.nextSibling;
    nextSibling ? row.parentNode?.insertBefore(newRow, nextSibling) : row.parentNode?.appendChild(newRow);
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  contextMenuInsertColLeft(): void {
    const cell = this.getContextCell();
    if (!cell) { this.closeContextMenu(); return; }
    const table = cell.closest('table');
    if (!table) { this.closeContextMenu(); return; }
    const colIndex = (cell as HTMLTableCellElement).cellIndex;
    table.querySelectorAll('tr').forEach(row => {
      const ref = row.cells[colIndex];
      const newTd = this.createCellLike(ref);
      if (ref) row.insertBefore(newTd, ref);
      else row.appendChild(newTd);
    });
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  contextMenuInsertColRight(): void {
    const cell = this.getContextCell();
    if (!cell) { this.closeContextMenu(); return; }
    const table = cell.closest('table');
    if (!table) { this.closeContextMenu(); return; }
    const colIndex = (cell as HTMLTableCellElement).cellIndex;
    table.querySelectorAll('tr').forEach(row => {
      const ref = row.cells[colIndex];
      const newTd = this.createCellLike(ref);
      if (ref?.nextSibling) row.insertBefore(newTd, ref.nextSibling);
      else row.appendChild(newTd);
    });
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  contextMenuDeleteRow(): void {
    const cell = this.getContextCell();
    const row = cell?.closest('tr');
    row?.parentNode?.removeChild(row);
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  contextMenuDeleteCol(): void {
    const cell = this.getContextCell();
    if (!cell) { this.closeContextMenu(); return; }
    const table = cell.closest('table');
    const colIndex = (cell as HTMLTableCellElement).cellIndex;
    table?.querySelectorAll('tr').forEach(row => {
      const td = row.cells[colIndex];
      if (td) row.removeChild(td);
    });
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  contextMenuDeleteTable(): void {
    const cell = this.getContextCell();
    const table = cell?.closest('table');
    table?.parentNode?.removeChild(table);
    this.notifyEditorChange();
    this.closeContextMenu();
  }


  contextMenuAlignImageLeft(): void {
    const img = this.contextMenuTargetImage();
    if (img) {
      img.style.display = 'block';
      img.style.marginLeft = '0';
      img.style.marginRight = 'auto';
      img.style.float = '';
    }
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  contextMenuAlignImageCenter(): void {
    const img = this.contextMenuTargetImage();
    if (img) {
      img.style.display = 'block';
      img.style.marginLeft = 'auto';
      img.style.marginRight = 'auto';
      img.style.float = '';
    }
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  contextMenuAlignImageRight(): void {
    const img = this.contextMenuTargetImage();
    if (img) {
      img.style.display = 'block';
      img.style.marginLeft = 'auto';
      img.style.marginRight = '0';
      img.style.float = '';
    }
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  private getSelectedCells(selection: Selection, editor: HTMLElement): HTMLElement[] {
    const customSelected = this.selectedCells();
    if (customSelected.size > 0) {
      return Array.from(customSelected);
    }

    const cell = this.activeTableCell();
    return cell ? [cell] : [];
  }


  toggleToolsMenu(): void {
    const wasOpen = this.showToolsMenu();
    this.closeAllMenus();
    this.showToolsMenu.set(!wasOpen);
  }


  toggleViewMenu(): void {
    const wasOpen = this.showViewMenu();
    this.closeAllMenus();
    this.showViewMenu.set(!wasOpen);
  }

  toggleHelpMenu(): void {
    const wasOpen = this.showHelpMenu();
    this.closeAllMenus();
    this.showHelpMenu.set(!wasOpen);
  }

  toggleRuler(): void {
    this.showRuler.set(!this.showRuler());
    this.closeAllMenus();
  }

  toggleMarginGuides(): void {
    this.showMarginGuides.set(!this.showMarginGuides());
    this.closeAllMenus();
  }

  onRulerMarginsChange(margins: PageMargins): void {
    this.pageSettings.update(s => ({
      ...s,
      margins: { ...margins }
    }));
  }

  onRulerDragGuide(e: { active: boolean; axis: 'horizontal' | 'vertical'; offsetPx: number }): void {
    this.rulerGuide.set({ ...e });
  }

  onEditingSectionChange(section: 'header' | 'footer' | 'body'): void {
    this.editingSection.set(section);
    if (section === 'body') {
      this.sectionGeometry.set(null);
    }
  }

  onSectionGeometryChange(geo: { section: 'header' | 'footer'; topCm: number; bottomCm: number }): void {
    this.sectionGeometry.set(geo);
  }

  onVerticalRulerMarginsChange(margins: PageMargins): void {
    const section = this.editingSection();
    if (section === 'body') {
      this.onRulerMarginsChange(margins);
      return;
    }
    const pageH = this.pageSettings().orientation === 'portrait' ? 29.7 : 21;
    const geo = this.sectionGeometry();
    if (section === 'header') {
      const topCm = geo ? Math.max(0, geo.topCm) : 0;
      const newBottomCm = pageH - margins.bottom;
      const newHeight = Math.round((newBottomCm - topCm) * 100) / 100;
      this.editor?.setHeaderHeight(newHeight);
    } else if (section === 'footer') {
      const bottomCm = geo ? geo.bottomCm : pageH - margins.bottom;
      const newTopCm = margins.top;
      const newHeight = Math.round((bottomCm - newTopCm) * 100) / 100;
      this.editor?.setFooterHeight(newHeight);
    }
  }

  onRulerBlockIndentChange(indent: { start?: number; end?: number }): void {
    const blocks = this.getSelectedBlocks();
    if (blocks.length === 0) return;

    for (const block of blocks) {
      if (indent.start !== undefined) {
        const cm = Math.max(-this.pageSettings().margins.left + 0.1, indent.start);
        if (cm === 0) {
          block.style.removeProperty('margin-left');
        } else {
          block.style.marginLeft = `${cm.toFixed(2)}cm`;
        }
      }
      if (indent.end !== undefined) {
        const cm = Math.max(-this.pageSettings().margins.right + 0.1, indent.end);
        if (cm === 0) {
          block.style.removeProperty('margin-right');
        } else {
          block.style.marginRight = `${cm.toFixed(2)}cm`;
        }
      }
    }

    this.currentBlockIndent.update(prev => ({
      start: indent.start !== undefined ? indent.start : prev.start,
      end: indent.end !== undefined ? indent.end : prev.end
    }));

    this.editor?.triggerContentChange();
  }

  @HostListener('document:selectionchange')
  onDocumentSelectionChange(): void {
    this.updateCurrentBlockIndent();
  }

  private getSelectedBlocks(): HTMLElement[] {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) return [];

    const range = selection.getRangeAt(0);
    const blocks: HTMLElement[] = [];
    const seen = new Set<HTMLElement>();
    const blockTags = new Set(['P', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'UL', 'OL', 'TABLE', 'FIGURE', 'BLOCKQUOTE', 'LI', 'IMG']);

    const findBlock = (node: Node | null): HTMLElement | null => {
      let n = node;
      while (n && n !== document) {
        if (n.nodeType === Node.ELEMENT_NODE) {
          const el = n as HTMLElement;
          if (blockTags.has(el.tagName)) return el;
        }
        n = n.parentNode;
      }
      return null;
    };

    if (range.collapsed) {
      const b = findBlock(range.startContainer);
      if (b) blocks.push(b);
    } else {
      const walker = document.createTreeWalker(
        range.commonAncestorContainer,
        NodeFilter.SHOW_ELEMENT,
        {
          acceptNode: (n: Node) => {
            const el = n as HTMLElement;
            if (!blockTags.has(el.tagName)) return NodeFilter.FILTER_SKIP;
            return range.intersectsNode(el) ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_SKIP;
          }
        }
      );
      const startBlock = findBlock(range.startContainer);
      if (startBlock) blocks.push(startBlock);
      let node = walker.nextNode();
      while (node) {
        const el = node as HTMLElement;
        blocks.push(el);
        node = walker.nextNode();
      }
      const endBlock = findBlock(range.endContainer);
      if (endBlock) blocks.push(endBlock);
    }

    const result: HTMLElement[] = [];
    for (const b of blocks) {
      if (seen.has(b)) continue;
      seen.add(b);
      result.push(b);
    }
    const filtered = result.filter(el => {
      if (el.tagName === 'LI') {
        const parent = el.parentElement;
        if (parent && (parent.tagName === 'UL' || parent.tagName === 'OL') && seen.has(parent)) {
          return false;
        }
      }
      if (el.tagName === 'IMG') {
        let p = el.parentElement;
        while (p && !['P', 'DIV', 'FIGURE'].includes(p.tagName)) p = p.parentElement;
        if (p) {
          if (!seen.has(p)) {
            seen.add(p);
            result.push(p);
          }
          return false;
        }
      }
      return true;
    });

    return filtered;
  }

  private updateCurrentBlockIndent(): void {
    const blocks = this.getSelectedBlocks();
    if (blocks.length === 0) {
      this.currentBlockIndent.set({ start: 0, end: 0 });
      this.setColumnRuler(null);
      return;
    }
    const block = blocks[0];
    const style = window.getComputedStyle(block);
    const mlPx = parseFloat(style.marginLeft) || 0;
    const mrPx = parseFloat(style.marginRight) || 0;
    const start = Math.round((mlPx / DocumentEditorComponent.CM_TO_PX) * 100) / 100;
    const end = Math.round((mrPx / DocumentEditorComponent.CM_TO_PX) * 100) / 100;
    const prev = this.currentBlockIndent();
    if (prev.start !== start || prev.end !== end) {
      this.currentBlockIndent.set({ start, end });
    }
    this.updateColumnRulerContext(block);
  }

  private updateColumnRulerContext(block: HTMLElement): void {
    const band = block.closest<HTMLElement>('.docx-col-band');
    const editorContent = block.closest<HTMLElement>('.editor-content');
    const container = band ?? editorContent;
    if (!container || !editorContent) {
      this.setColumnRuler(null);
      return;
    }
    const cs = window.getComputedStyle(container);
    const count = parseInt(cs.columnCount, 10);
    if (!count || count <= 1) {
      this.setColumnRuler(null);
      return;
    }

    const gapPx = parseFloat(cs.columnGap) || 0;
    const padLeftPx = parseFloat(cs.paddingLeft) || 0;
    const padRightPx = parseFloat(cs.paddingRight) || 0;
    const contentWidthPx = container.clientWidth - padLeftPx - padRightPx;
    const colWidthPx = Math.max(1, (contentWidthPx - gapPx * (count - 1)) / count);

    const pageRect = editorContent.getBoundingClientRect();
    const scale = editorContent.offsetWidth > 0 ? pageRect.width / editorContent.offsetWidth : 1;
    const containerRect = container.getBoundingClientRect();
    const contentLeftViewportPx = containerRect.left + padLeftPx * scale;
    const baseStartPx = (contentLeftViewportPx - pageRect.left) / scale;

    const cmPerPx = 1 / DocumentEditorComponent.CM_TO_PX;
    const round2 = (v: number) => Math.round(v * 100) / 100;
    const segments: RulerColumnSegment[] = Array.from({ length: count }, (_, i) => ({
      startCm: round2((baseStartPx + i * (colWidthPx + gapPx)) * cmPerPx),
      widthCm: round2(colWidthPx * cmPerPx)
    }));

    const focusX = this.getSelectionFocusX() ?? block.getBoundingClientRect().left;
    const relPx = (focusX - contentLeftViewportPx) / scale;
    const activeIndex = Math.max(0, Math.min(count - 1, Math.floor(relPx / (colWidthPx + gapPx))));

    this.setColumnRuler({ segments, activeIndex });
  }

  private getSelectionFocusX(): number | null {
    const sel = window.getSelection();
    if (!sel || !sel.focusNode) return null;
    try {
      const r = document.createRange();
      r.setStart(sel.focusNode, sel.focusOffset);
      r.collapse(true);
      const rect = r.getClientRects()[0];
      if (rect) return rect.left;
      const el = sel.focusNode.nodeType === Node.ELEMENT_NODE
        ? sel.focusNode as HTMLElement
        : sel.focusNode.parentElement;
      return el ? el.getBoundingClientRect().left : null;
    } catch {
      return null;
    }
  }

  private setColumnRuler(next: { segments: RulerColumnSegment[]; activeIndex: number } | null): void {
    const prev = this.currentColumnRuler();
    if (prev === null && next === null) return;
    if (
      prev && next &&
      prev.activeIndex === next.activeIndex &&
      prev.segments.length === next.segments.length &&
      prev.segments.every((s, i) => s.startCm === next.segments[i].startCm && s.widthCm === next.segments[i].widthCm)
    ) {
      return;
    }
    this.currentColumnRuler.set(next);
  }


  openParagraphDialog(): void {
    this.closeAllMenus();
    this.readCurrentParagraphSettings();
    this.paragraphDialogTab.set('indents');
    this.showParagraphDialog.set(true);
  }

  closeParagraphDialog(): void {
    this.showParagraphDialog.set(false);
  }

  private readCurrentParagraphSettings(): void {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) {
      this.paragraphData = { ...this._paragraphDefaults };
      return;
    }

    const range = selection.getRangeAt(0);
    let block = range.startContainer as Node;
    if (block.nodeType === Node.TEXT_NODE) {
      block = block.parentNode!;
    }
    while (block && !['P', 'DIV', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'LI'].includes((block as HTMLElement).tagName)) {
      block = block.parentNode!;
    }

    if (block) {
      const el = block as HTMLElement;
      const style = window.getComputedStyle(el);

      const textAlign = style.textAlign;
      if (textAlign === 'center') this.paragraphData.alignment = 'center';
      else if (textAlign === 'right' || textAlign === 'end') this.paragraphData.alignment = 'right';
      else if (textAlign === 'justify') this.paragraphData.alignment = 'justify';
      else this.paragraphData.alignment = 'left';

      const pxToCm = (px: number) => Math.round((px / CSS_PX_PER_CM) * 10) / 10;
      this.paragraphData.indentLeft = pxToCm(parseFloat(style.marginLeft) || 0);
      this.paragraphData.indentRight = pxToCm(parseFloat(style.marginRight) || 0);

      const textIndent = parseFloat(style.textIndent) || 0;
      if (textIndent > 0) {
        this.paragraphData.specialIndent = 'firstLine';
        this.paragraphData.specialIndentBy = pxToCm(textIndent);
      } else if (textIndent < 0) {
        this.paragraphData.specialIndent = 'hanging';
        this.paragraphData.specialIndentBy = pxToCm(Math.abs(textIndent));
      } else {
        this.paragraphData.specialIndent = 'none';
      }

      const pxToPt = (px: number) => Math.round((px * 72) / 96);
      this.paragraphData.spaceBefore = pxToPt(parseFloat(style.marginTop) || 0);
      this.paragraphData.spaceAfter = pxToPt(
        (parseFloat(style.marginBottom) || 0) + (parseFloat(style.paddingBottom) || 0));

      const inlineLineHeight = el.style.lineHeight;
      const atLeastMax = /^max\(\s*([\d.]+)pt/.exec(inlineLineHeight);
      if (atLeastMax) {
        this.paragraphData.lineSpacingType = 'atLeast';
        this.paragraphData.lineSpacingValue = parseFloat(atLeastMax[1]) || 12;
      } else if (inlineLineHeight.endsWith('pt')) {
        this.paragraphData.lineSpacingType =
          el.style.getPropertyValue('--w-line-rule').trim() === 'atLeast' ? 'atLeast' : 'exactly';
        this.paragraphData.lineSpacingValue = parseFloat(inlineLineHeight) || 12;
      } else {
        const multiple = readWordLineMultiple(el);
        if (multiple === null || multiple === 1) {
          this.paragraphData.lineSpacingType = 'single';
          this.paragraphData.lineSpacingValue = 1;
        } else {
          this.paragraphData.lineSpacingType = 'multiple';
          this.paragraphData.lineSpacingValue = multiple;
        }
      }

      this.paragraphData.pageBreakBefore =
        /always|page/i.test(el.style.pageBreakBefore || el.style.breakBefore || '');
    }
  }

  applyParagraphSettings(): void {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) {
      this.closeParagraphDialog();
      return;
    }

    const range = selection.getRangeAt(0);
    let block = range.startContainer as Node;
    if (block.nodeType === Node.TEXT_NODE) {
      block = block.parentNode!;
    }
    while (block && !['P', 'DIV', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'LI'].includes((block as HTMLElement).tagName)) {
      block = block.parentNode!;
    }

    if (block) {
      const el = block as HTMLElement;
      const cmToPx = (cm: number) => cm * CSS_PX_PER_CM;

      el.style.textAlign = this.paragraphData.alignment;

      el.style.marginLeft = cmToPx(this.paragraphData.indentLeft) + 'px';
      el.style.marginRight = cmToPx(this.paragraphData.indentRight) + 'px';
      el.style.removeProperty('padding-left');
      el.style.removeProperty('padding-right');

      if (this.paragraphData.specialIndent === 'firstLine') {
        el.style.textIndent = cmToPx(this.paragraphData.specialIndentBy) + 'px';
      } else if (this.paragraphData.specialIndent === 'hanging') {
        el.style.textIndent = '-' + cmToPx(this.paragraphData.specialIndentBy) + 'px';
      } else {
        el.style.textIndent = '0';
      }

      const ptToPx = (pt: number) => (pt * 96) / 72;
      el.style.marginTop = ptToPx(this.paragraphData.spaceBefore) + 'px';
      el.style.paddingBottom = ptToPx(this.paragraphData.spaceAfter) + 'px';
      el.style.marginBottom = '';

      switch (this.paragraphData.lineSpacingType) {
        case 'single':
          applyWordLineSpacing(el, 1);
          break;
        case '1.5':
          applyWordLineSpacing(el, 1.5);
          break;
        case 'double':
          applyWordLineSpacing(el, 2);
          break;
        case 'multiple':
          applyWordLineSpacing(el, this.paragraphData.lineSpacingValue);
          break;
        case 'atLeast':
          applyExactLineSpacing(el, this.paragraphData.lineSpacingValue, true);
          break;
        case 'exactly':
          applyExactLineSpacing(el, this.paragraphData.lineSpacingValue, false);
          break;
      }

      if (this.paragraphData.pageBreakBefore) {
        el.style.pageBreakBefore = 'always';
      } else if (/always|page/i.test(el.style.pageBreakBefore || el.style.breakBefore || '')) {
        el.style.pageBreakBefore = 'auto';
      }

      this.notifyEditorChange();
    }

    this.closeParagraphDialog();
  }

  setParagraphAsDefault(): void {
    this._paragraphDefaults = { ...this.paragraphData };
    this.applyParagraphSettings();
  }

  getLineSpacingUnit(): string {
    switch (this.paragraphData.lineSpacingType) {
      case 'atLeast':
      case 'exactly':
        return 'pkt';
      case 'multiple':
        return '';
      default:
        return '';
    }
  }

  getPreviewLineHeight(): string {
    switch (this.paragraphData.lineSpacingType) {
      case 'single': return '1';
      case '1.5': return '1.5';
      case 'double': return '2';
      case 'multiple': return this.paragraphData.lineSpacingValue.toString();
      default: return '1.15';
    }
  }


  openInsertTableDialog(): void {
    this.closeAllMenus();
    if (this.savedTableDimensions) {
      this.tableDialogData.columns = this.savedTableDimensions.columns;
      this.tableDialogData.rows = this.savedTableDimensions.rows;
    } else {
      this.tableDialogData.columns = 5;
      this.tableDialogData.rows = 2;
    }
    this.tableDialogData.autoFitBehavior = 'fixed';
    this.tableDialogData.fixedWidth = 0;
    this.showInsertTableDialog.set(true);
  }

  closeInsertTableDialog(): void {
    this.showInsertTableDialog.set(false);
  }

  private static readonly TABLE_MIN_COLS = 1;
  private static readonly TABLE_MAX_COLS = 63;
  private static readonly TABLE_MIN_ROWS = 1;
  private static readonly TABLE_MAX_ROWS = 500;

  isTableColumnsValid(): boolean {
    const v = this.tableDialogData.columns;
    return Number.isFinite(v) && Number.isInteger(v)
      && v >= DocumentEditorComponent.TABLE_MIN_COLS && v <= DocumentEditorComponent.TABLE_MAX_COLS;
  }

  isTableRowsValid(): boolean {
    const v = this.tableDialogData.rows;
    return Number.isFinite(v) && Number.isInteger(v)
      && v >= DocumentEditorComponent.TABLE_MIN_ROWS && v <= DocumentEditorComponent.TABLE_MAX_ROWS;
  }

  insertTableValidationError(): string | null {
    if (!this.isTableColumnsValid()) {
      return `Liczba kolumn musi być liczbą całkowitą z zakresu ${DocumentEditorComponent.TABLE_MIN_COLS}–${DocumentEditorComponent.TABLE_MAX_COLS}.`;
    }
    if (!this.isTableRowsValid()) {
      return `Liczba wierszy musi być liczbą całkowitą z zakresu ${DocumentEditorComponent.TABLE_MIN_ROWS}–${DocumentEditorComponent.TABLE_MAX_ROWS}.`;
    }
    return null;
  }

  clampTableColumns(): void {
    const v = this.tableDialogData.columns;
    if (!Number.isFinite(v)) {
      this.tableDialogData.columns = DocumentEditorComponent.TABLE_MIN_COLS;
      return;
    }
    this.tableDialogData.columns = Math.max(
      DocumentEditorComponent.TABLE_MIN_COLS,
      Math.min(DocumentEditorComponent.TABLE_MAX_COLS, Math.floor(v))
    );
  }

  clampTableRows(): void {
    const v = this.tableDialogData.rows;
    if (!Number.isFinite(v)) {
      this.tableDialogData.rows = DocumentEditorComponent.TABLE_MIN_ROWS;
      return;
    }
    this.tableDialogData.rows = Math.max(
      DocumentEditorComponent.TABLE_MIN_ROWS,
      Math.min(DocumentEditorComponent.TABLE_MAX_ROWS, Math.floor(v))
    );
  }

  onFixedWidthChange(value: string): void {
    if (value.toLowerCase() === 'auto' || value === '') {
      this.tableDialogData.fixedWidth = 0;
    } else {
      const num = parseFloat(value);
      if (!isNaN(num)) {
        this.tableDialogData.fixedWidth = num;
      }
    }
  }

  applyInsertTable(): void {
    if (this.insertTableValidationError()) {
      return;
    }
    const cols = Math.max(1, Math.min(63, this.tableDialogData.columns));
    const rows = Math.max(1, Math.min(500, this.tableDialogData.rows));

    if (this.tableDialogData.rememberDimensions) {
      this.savedTableDimensions = { columns: cols, rows: rows };
    }

    const config = `${cols}x${rows}`;

    if (this.editor) {
      this.editor.insertTable(config);
      this.applyTableAutoFit(this.tableDialogData.autoFitBehavior, this.tableDialogData.fixedWidth);
    }

    this.closeInsertTableDialog();
  }


  private notifyEditorChange(): void {
    const el = this.editor?.editorContent?.nativeElement;
    if (el) {
      el.dispatchEvent(new Event('input', { bubbles: true }));
    }
  }

  private getCellPosition(cell: HTMLTableCellElement): { rowIndex: number; colIndex: number } | null {
    const row = cell.parentElement as HTMLTableRowElement;
    if (!row) return null;
    const table = row.closest('table');
    if (!table) return null;
    const rows = Array.from(table.rows);
    const rowIndex = rows.indexOf(row);
    const colIndex = Array.from(row.cells).indexOf(cell);
    return { rowIndex, colIndex };
  }

  tableInsertRowAbove(): void {
    const cell = this.activeTableCell();
    const table = this.activeTable();
    if (!cell || !table) return;
    const pos = this.getCellPosition(cell);
    if (!pos) return;
    const colCount = table.rows[pos.rowIndex]?.cells.length || 1;
    const newRow = table.insertRow(pos.rowIndex);
    for (let i = 0; i < colCount; i++) {
      const td = newRow.insertCell();
      td.innerHTML = '<br>';
      td.style.cssText = 'border:1px solid #ccc;padding:8px;min-width:30px;';
    }
    this.notifyEditorChange();
  }

  tableInsertRowBelow(): void {
    const cell = this.activeTableCell();
    const table = this.activeTable();
    if (!cell || !table) return;
    const pos = this.getCellPosition(cell);
    if (!pos) return;
    const colCount = table.rows[pos.rowIndex]?.cells.length || 1;
    const insertAt = pos.rowIndex + 1;
    const newRow = table.insertRow(insertAt < table.rows.length ? insertAt : -1);
    for (let i = 0; i < colCount; i++) {
      const td = newRow.insertCell();
      td.innerHTML = '<br>';
      td.style.cssText = 'border:1px solid #ccc;padding:8px;min-width:30px;';
    }
    this.notifyEditorChange();
  }

  tableInsertColLeft(): void {
    const cell = this.activeTableCell();
    const table = this.activeTable();
    if (!cell || !table) return;
    const pos = this.getCellPosition(cell);
    if (!pos) return;
    Array.from(table.rows).forEach(row => {
      const td = row.insertCell(Math.min(pos.colIndex, row.cells.length));
      td.innerHTML = '<br>';
      td.style.cssText = 'border:1px solid #ccc;padding:8px;min-width:30px;';
    });
    syncTableColgroup(table);
    this.notifyEditorChange();
  }

  tableInsertColRight(): void {
    const cell = this.activeTableCell();
    const table = this.activeTable();
    if (!cell || !table) return;
    const pos = this.getCellPosition(cell);
    if (!pos) return;
    const insertAt = pos.colIndex + 1;
    Array.from(table.rows).forEach(row => {
      const td = row.insertCell(Math.min(insertAt, row.cells.length));
      td.innerHTML = '<br>';
      td.style.cssText = 'border:1px solid #ccc;padding:8px;min-width:30px;';
    });
    syncTableColgroup(table);
    this.notifyEditorChange();
  }

  tableDeleteRow(): void {
    const cell = this.activeTableCell();
    const table = this.activeTable();
    if (!cell || !table) return;
    const pos = this.getCellPosition(cell);
    if (!pos) return;
    if (table.rows.length <= 1) {
      this.tableDeleteTable();
      return;
    }
    table.deleteRow(pos.rowIndex);
    this.notifyEditorChange();
  }

  tableDeleteCol(): void {
    const cell = this.activeTableCell();
    const table = this.activeTable();
    if (!cell || !table) return;
    const pos = this.getCellPosition(cell);
    if (!pos) return;
    if (table.rows[0]?.cells.length <= 1) {
      this.tableDeleteTable();
      return;
    }
    Array.from(table.rows).forEach(row => {
      if (pos.colIndex < row.cells.length) {
        row.deleteCell(pos.colIndex);
      }
    });
    syncTableColgroup(table);
    this.notifyEditorChange();
  }

  tableDeleteTable(): void {
    const table = this.activeTable();
    if (!table) return;
    table.parentNode?.removeChild(table);
    this.isInTable.set(false);
    this.activeTableCell.set(null);
    this.activeTable.set(null);
    this.showTablePanel.set(false);
    this.tablePanelManuallyClosed = false;
    this.notifyEditorChange();
  }


  setTableBorderStyle(style: TableBorderLineStyle): void {
    this.tableBorderStyle.set(style);
  }

  setTableBorderColor(color: string): void {
    this.tableBorderColor.set(color);
  }

  setTableBorderWidth(width: number): void {
    if (width >= 1 && width <= 12) this.tableBorderWidth.set(width);
  }

  resetTableBorderSettings(): void {
    this.tableBorderColor.set(DEFAULT_TABLE_BORDER.color);
    this.tableBorderWidth.set(DEFAULT_TABLE_BORDER.width);
    this.tableBorderStyle.set(DEFAULT_TABLE_BORDER.style);
  }

  applyTableBorderScope(scope: TableBorderScope): void {
    const table = this.activeTable();
    if (!table) return;
    const cells = this.resolveAutoTargetCells(table);
    if (cells.length === 0) return;
    applyBorderToCells(cells, scope, this.currentBorderSettings());
    this.lastBorderScope.set(scope);
    this.notifyEditorChange();
  }

  clearTableBorders(): void {
    this.applyTableBorderScope('none');
  }

  restoreDefaultTableBorders(): void {
    const table = this.activeTable();
    if (!table) return;
    restoreDefaultBordersUtil(table);
    this.resetTableBorderSettings();
    this.lastBorderScope.set('all');
    this.notifyEditorChange();
  }

  private resolveAutoTargetCells(table: HTMLTableElement): HTMLTableCellElement[] {
    const selected = Array.from(this.selectedCells());
    if (selected.length > 0) return selected;
    const cell = this.activeTableCell();
    if (cell) return [cell];
    return Array.from(table.querySelectorAll('td, th')) as HTMLTableCellElement[];
  }

  private currentBorderSettings() {
    return {
      color: this.tableBorderColor(),
      width: this.tableBorderWidth(),
      style: this.tableBorderStyle()
    };
  }

  tableMergeCells(): void {
    const customSelected = this.selectedCells();
    const cells = customSelected.size > 0
      ? Array.from(customSelected)
      : (() => {
          const selection = window.getSelection();
          const editorEl = this.editor?.editorContent?.nativeElement;
          return (selection && editorEl) ? this.getSelectedCells(selection, editorEl) : [];
        })();
    if (cells.length < 2) return;

    const firstCell = cells[0] as HTMLTableCellElement;
    let mergedContent = '';
    let minRow = Infinity, maxRow = -1, minCol = Infinity, maxCol = -1;

    cells.forEach((c) => {
      const td = c as HTMLTableCellElement;
      const row = td.parentElement as HTMLTableRowElement;
      const table = row.closest('table')!;
      const ri = Array.from(table.rows).indexOf(row);
      const ci = Array.from(row.cells).indexOf(td);
      minRow = Math.min(minRow, ri);
      maxRow = Math.max(maxRow, ri);
      minCol = Math.min(minCol, ci);
      maxCol = Math.max(maxCol, ci + (td.colSpan || 1) - 1);
    });

    cells.forEach(c => {
      const txt = c.innerHTML.trim();
      if (txt && txt !== '&nbsp;' && txt !== '<br>') {
        mergedContent += (mergedContent ? ' ' : '') + txt;
      }
    });

    const colSpan = maxCol - minCol + 1;
    const rowSpan = maxRow - minRow + 1;
    firstCell.colSpan = colSpan;
    firstCell.rowSpan = rowSpan;
    firstCell.innerHTML = mergedContent || '<br>';

    const table = this.activeTable();
    if (!table) return;
    for (let r = minRow; r <= maxRow; r++) {
      const row = table.rows[r];
      if (!row) continue;
      for (let c = row.cells.length - 1; c >= 0; c--) {
        const cell = row.cells[c];
        if (cell !== firstCell && cells.includes(cell)) {
          row.removeChild(cell);
        }
      }
    }
    this.clearCellSelection();
    this.notifyEditorChange();
  }

  tableSplitCell(): void {
    const cell = this.activeTableCell();
    if (!cell) return;
    const table = this.activeTable();
    if (!table) return;

    const cs = cell.colSpan || 1;
    const rs = cell.rowSpan || 1;

    if (cs <= 1 && rs <= 1) {
      const pos = this.getCellPosition(cell);
      if (!pos) return;
      cell.colSpan = 1;
      Array.from(table.rows).forEach((row, ri) => {
        if (ri === pos.rowIndex) {
          const newTd = row.insertCell(pos.colIndex + 1);
          newTd.innerHTML = '<br>';
          newTd.style.cssText = 'border:1px solid #ccc;padding:8px;min-width:30px;';
        } else {
          const newTd = row.insertCell(Math.min(pos.colIndex + 1, row.cells.length));
          newTd.innerHTML = '<br>';
          newTd.style.cssText = 'border:1px solid #ccc;padding:8px;min-width:30px;';
        }
      });
    } else {
      const pos = this.getCellPosition(cell);
      if (!pos) return;
      cell.colSpan = 1;
      cell.rowSpan = 1;
      const row = cell.parentElement as HTMLTableRowElement;
      for (let c = 1; c < cs; c++) {
        const newTd = row.insertCell(Array.from(row.cells).indexOf(cell) + 1);
        newTd.innerHTML = '<br>';
        newTd.style.cssText = 'border:1px solid #ccc;padding:8px;min-width:30px;';
      }
      for (let r = 1; r < rs; r++) {
        const targetRow = table.rows[pos.rowIndex + r];
        if (!targetRow) continue;
        for (let c = 0; c < cs; c++) {
          const insertIdx = Math.min(pos.colIndex, targetRow.cells.length);
          const newTd = targetRow.insertCell(insertIdx);
          newTd.innerHTML = '<br>';
          newTd.style.cssText = 'border:1px solid #ccc;padding:8px;min-width:30px;';
        }
      }
    }
    this.notifyEditorChange();
  }

  tableSplitTable(): void {
    const cell = this.activeTableCell();
    const table = this.activeTable();
    if (!cell || !table) return;
    const pos = this.getCellPosition(cell);
    if (!pos || pos.rowIndex === 0) return;

    const newTable = document.createElement('table');
    newTable.style.cssText = table.style.cssText;
    const rowsToMove = Array.from(table.rows).slice(pos.rowIndex);
    rowsToMove.forEach(row => newTable.appendChild(row));

    const separator = document.createElement('p');
    separator.innerHTML = '&nbsp;';
    table.parentNode?.insertBefore(separator, table.nextSibling);
    separator.parentNode?.insertBefore(newTable, separator.nextSibling);
    this.notifyEditorChange();
  }

  tableAutoFitContents(): void {
    const table = this.activeTable();
    if (!table) return;
    table.style.width = 'auto';
    table.style.tableLayout = 'auto';
    table.querySelectorAll('td, th').forEach(c => {
      (c as HTMLElement).style.width = '';
    });
    this.notifyEditorChange();
  }

  tableAutoFitWindow(): void {
    const table = this.activeTable();
    if (!table) return;
    table.style.width = '100%';
    table.style.tableLayout = 'auto';
    table.querySelectorAll('td, th').forEach(c => {
      (c as HTMLElement).style.width = '';
    });
    this.notifyEditorChange();
  }

  tableFixedWidth(): void {
    const table = this.activeTable();
    if (!table) return;
    table.style.width = '100%';
    table.style.tableLayout = 'fixed';
    this.notifyEditorChange();
  }

  tableDistributeRows(): void {
    const table = this.activeTable();
    if (!table) return;
    Array.from(table.rows).forEach(row => {
      row.style.height = '';
      Array.from(row.cells).forEach(cell => {
        cell.style.height = '';
      });
    });
    this.notifyEditorChange();
  }

  tableDistributeCols(): void {
    const table = this.activeTable();
    if (!table) return;
    const colCount = table.rows[0]?.cells.length || 1;
    const w = Math.floor(100 / colCount);
    Array.from(table.rows).forEach(row => {
      Array.from(row.cells).forEach(cell => {
        cell.style.width = w + '%';
      });
    });
    this.notifyEditorChange();
  }

  showTableGridLines = signal(true);
  tableToggleGridLines(): void {
    this.showTableGridLines.update(v => !v);
    const table = this.activeTable();
    if (!table) return;
    if (this.showTableGridLines()) {
      table.querySelectorAll('td, th').forEach(c => {
        (c as HTMLElement).style.borderColor = '#ccc';
      });
    } else {
      table.querySelectorAll('td, th').forEach(c => {
        (c as HTMLElement).style.borderColor = 'transparent';
      });
    }
  }

  shadingDropdownX = signal(0);
  shadingDropdownY = signal(0);

  toggleShadingDropdown(event: MouseEvent): void {
    const btn = (event.target as HTMLElement).closest('.table-toolbar-btn-shading') as HTMLElement;
    if (btn) {
      const rect = btn.getBoundingClientRect();
      this.shadingDropdownX.set(rect.left);
      this.shadingDropdownY.set(rect.bottom + 4);
    }
    this.showShadingDropdown.update(v => !v);
  }

  shadingColors = [
    '#FFFFFF', '#F2F2F2', '#D9D9D9', '#BFBFBF', '#A6A6A6', '#808080', '#595959', '#404040', '#262626', '#000000',
    '#FFF2CC', '#FFE599', '#FFD966', '#FFC000', '#BF9000', '#806000', '#FCE4D6', '#F8CBAD', '#F4B084', '#ED7D31',
    '#C55A11', '#833C0B', '#D6E4F0', '#B4C6E7', '#8DB4E2', '#4472C4', '#2F5597', '#1F3864', '#E2EFDA', '#C6EFCE',
    '#A9D18E', '#70AD47', '#548235', '#375623', '#F8D7DA', '#F5C6CB', '#E8A0A0', '#FF0000', '#C00000', '#800000',
    '#E6D5F5', '#D0B8E8', '#B490D0', '#7030A0', '#5B259A', '#3B1770'
  ];

  private applyTableAutoFit(behavior: string, fixedWidth: number): void {
    setTimeout(() => {
      const editorEl = this.editor?.editorContent?.nativeElement;
      const tables = editorEl?.querySelectorAll('table');
      if (tables && tables.length > 0) {
        const lastTable = tables[tables.length - 1] as HTMLTableElement;
        switch (behavior) {
          case 'fixed':
            if (fixedWidth > 0) {
              lastTable.style.width = '';
              lastTable.style.tableLayout = 'fixed';
              const widthPx = fixedWidth * CSS_PX_PER_CM;
              lastTable.querySelectorAll('td, th').forEach((cell) => {
                (cell as HTMLElement).style.width = widthPx + 'px';
              });
            } else {
              lastTable.style.width = '100%';
              lastTable.style.tableLayout = 'fixed';
            }
            break;
          case 'contents':
            lastTable.style.width = 'auto';
            lastTable.style.tableLayout = 'auto';
            break;
          case 'window':
            lastTable.style.width = '100%';
            lastTable.style.tableLayout = 'auto';
            break;
        }
      }
    }, 50);
  }


  openPropertiesDialog(): void {
    this.propertiesData.set({ ...this.documentMetadata() });
    this.showPropertiesDialog.set(true);
    this.closeAllMenus();
  }

  saveProperties(): void {
    const props = this.propertiesData();
    this.documentMetadata.update(m => ({
      ...m,
      title: props.title,
      author: props.author,
      subject: props.subject,
      keywords: props.keywords,
      description: props.description,
      category: props.category,
      company: props.company,
      manager: props.manager,
      contentStatus: props.contentStatus,
      lastModifiedBy: props.lastModifiedBy,
      revision: props.revision,
      version: props.version,
      modified: new Date().toISOString()
    }));
    this.showPropertiesDialog.set(false);
    this.showSuccess('Właściwości dokumentu zostały zaktualizowane');
  }

  closePropertiesDialog(): void {
    this.showPropertiesDialog.set(false);
  }

  updateProperty(key: string, value: string): void {
    this.propertiesData.update(p => ({ ...p, [key]: value }));
  }


  openSignatureDialog(): void {
    this.signatureDialogTab.set(
      this.documentSignatures().length > 0 ? 'list' : 'sign'
    );
    this.signatureData.signerName = '';
    this.signatureData.signerTitle = '';
    this.signatureData.signerEmail = '';
    this.signatureData.reason = '';
    this.signatureData.certificateBase64 = '';
    this.signatureData.certificatePassword = '';
    this.signatureData.certificateFileName = '';
    this.showSignatureDialog.set(true);
    this.closeAllMenus();
  }

  closeSignatureDialog(): void {
    this.showSignatureDialog.set(false);
  }

  onCertificateFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    this.signatureData.certificateFileName = file.name;

    const reader = new FileReader();
    reader.onload = () => {
      const arrayBuffer = reader.result as ArrayBuffer;
      const bytes = new Uint8Array(arrayBuffer);
      let binary = '';
      bytes.forEach(b => binary += String.fromCharCode(b));
      this.signatureData.certificateBase64 = btoa(binary);
    };
    reader.readAsArrayBuffer(file);
  }

  signDocument(): void {
    if (!this.signatureData.certificateBase64) {
      this.showError('Wybierz plik certyfikatu (.pfx/.p12)');
      return;
    }
    if (!this.signatureData.signerName.trim()) {
      this.showError('Podaj imię i nazwisko podpisującego');
      return;
    }
    if (!this.signatureData.certificatePassword) {
      this.showError('Podaj hasło do certyfikatu');
      return;
    }

    const html = this.editor?.getContent() || this.documentContent();
    const fileName = this.originalFileName() || `${this.documentMetadata().title || 'dokument'}.docx`;

    this.isLoading.set(true);

    const request: SignDocumentRequest = {
      html,
      originalFileName: fileName,
      metadata: this.documentMetadata(),
      header: this.headerContent(),
      footer: this.footerContent(),
      certificateBase64: this.signatureData.certificateBase64,
      certificatePassword: this.signatureData.certificatePassword,
      signerName: this.signatureData.signerName,
      signerTitle: this.signatureData.signerTitle || undefined,
      signerEmail: this.signatureData.signerEmail || undefined,
      signatureReason: this.signatureData.reason || undefined
    };

    this.documentService.signDocument(request).subscribe({
      next: (blob) => {
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = fileName.endsWith('.docx') ? fileName : `${fileName}.docx`;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        window.URL.revokeObjectURL(url);

        this.showSignatureDialog.set(false);
        this.showSuccess('Dokument został podpisany i pobrany');
        this.isLoading.set(false);
      },
      error: (err) => {
        this.showError(err.message || 'Nie udało się podpisać dokumentu');
        this.isLoading.set(false);
      }
    });
  }

  insertSignatureLine(): void {
    const name = this.signatureData.signerName || '________________________';
    const title = this.signatureData.signerTitle || '';
    const date = new Date().toLocaleDateString('pl-PL');

    const html = `
      <div style="margin: 24px 0; padding: 16px; border: 1px solid #999; width: 300px; font-family: Calibri, sans-serif;">
        <div style="border-bottom: 1px solid #333; padding-bottom: 40px; margin-bottom: 8px; font-size: 10px; color: #999;">
          ✕ Podpis
        </div>
        <div style="font-size: 12px; font-weight: bold;">${name}</div>
        ${title ? `<div style="font-size: 11px; color: #555;">${title}</div>` : ''}
        <div style="font-size: 10px; color: #888; margin-top: 4px;">Data: ${date}</div>
      </div>
    `;

    this.editor?.insertHtml(html);
    this.showSignatureDialog.set(false);
    this.notifyEditorChange();
  }
}
