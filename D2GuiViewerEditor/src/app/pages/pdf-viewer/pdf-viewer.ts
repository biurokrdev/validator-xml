import {
  Component,
  OnInit,
  OnDestroy,
  inject,
  signal,
  ElementRef,
  ViewChild,
  HostListener,
  NgZone,
  ChangeDetectionStrategy
} from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { getDocument, GlobalWorkerOptions, TextLayer } from 'pdfjs-dist';
import type { PDFDocumentProxy } from 'pdfjs-dist';
import { DocumentStorageService } from '../../services/document-storage.service';
import { DocumentClassificationBadgeComponent } from '../../components/document-classification-badge/document-classification-badge';

GlobalWorkerOptions.workerSrc = '/pdf.worker.min.mjs';

@Component({
  selector: 'd2-pdf-viewer',
  standalone: true,
  imports: [RouterLink, DocumentClassificationBadgeComponent],
  templateUrl: './pdf-viewer.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './pdf-viewer.scss',
})
export class PdfViewerComponent implements OnInit, OnDestroy {
  private route = inject(ActivatedRoute);
  private storageService = inject(DocumentStorageService);
  private ngZone = inject(NgZone);

  isLoading = signal(true);
  isRendering = signal(false);
  isNotFound = signal(false);
  errorMessage = signal<string | null>(null);
  totalPages = signal(0);
  currentPage = signal(1);
  scale = signal(1.5);
  documentName = signal('Dokument PDF');
  classification = signal<string | null>(null);

  showSearchBar = signal(false);
  searchQuery = signal('');
  searchResults = signal(0);
  currentMatchIndex = signal(0);

  private pdfDoc: PDFDocumentProxy | null = null;
  private intersectionObserver: IntersectionObserver | null = null;
  private textNodes: Text[] = [];
  private matchNodes: Text[] = [];
  private highlightOverlays: HTMLElement[] = [];

  @ViewChild('viewerContainer') viewerContainer!: ElementRef<HTMLDivElement>;
  @ViewChild('searchInput') searchInput?: ElementRef<HTMLInputElement>;

  readonly SCALE_STEPS = [0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

  get scalePercent(): number {
    return Math.round(this.scale() * 100);
  }

  get canZoomIn(): boolean {
    return this.scale() < this.SCALE_STEPS[this.SCALE_STEPS.length - 1];
  }

  get canZoomOut(): boolean {
    return this.scale() > this.SCALE_STEPS[0];
  }

  ngOnInit(): void {
    const masterId = this.route.snapshot.queryParamMap.get('masterId');
    if (!masterId) {
      this.errorMessage.set('Brak identyfikatora dokumentu.');
      this.isLoading.set(false);
      return;
    }

    this.storageService.getDocument(masterId).subscribe({
      next: (doc) => {
        this.documentName.set(doc.name);
        this.loadPdf(doc.content);
      },
      error: () => {
        this.errorMessage.set('Nie można pobrać dokumentu z serwera.');
        this.isLoading.set(false);
      },
    });

    this.storageService.getDocumentMetadata(masterId).subscribe({
      next: (meta) => this.classification.set(meta.classification),
      error: () => this.classification.set(null),
    });
  }

  ngOnDestroy(): void {
    this.disconnectObserver();
    this.pdfDoc?.destroy();
  }

  private async loadPdf(base64: string): Promise<void> {
    try {
      const binary = atob(base64);
      const data = new Uint8Array(binary.length);
      for (let i = 0; i < binary.length; i++) {
        data[i] = binary.charCodeAt(i);
      }

      const loadingTask = getDocument({ data });
      this.pdfDoc = await loadingTask.promise;
      this.totalPages.set(this.pdfDoc.numPages);
      this.isLoading.set(false);

      setTimeout(() => this.renderAllPages(), 0);
    } catch (err) {
      console.error('[PdfViewer] Nie udało się załadować podglądu PDF:', err);
      const msg = err instanceof Error ? err.message : String(err);
      const workerProblem = /worker|dynamically imported|failed to (fetch|load)|importScripts|mjs/i.test(msg);
      this.errorMessage.set(
        workerProblem
          ? 'Nie udało się zainicjować podglądu PDF (moduł renderujący). Odśwież stronę; jeśli błąd wróci — zgłoś go.'
          : 'Błąd podczas parsowania pliku PDF.'
      );
      this.isLoading.set(false);
    }
  }

  private async renderAllPages(): Promise<void> {
    if (!this.pdfDoc || !this.viewerContainer) return;
    this.isRendering.set(true);
    this.textNodes = [];
    this.clearHighlights();

    const container = this.viewerContainer.nativeElement;
    container.innerHTML = '';
    this.disconnectObserver();

    this.intersectionObserver = new IntersectionObserver(
      (entries) => {
        const visiblePages = entries
          .filter((e) => e.isIntersecting)
          .map((e) => parseInt((e.target as HTMLElement).dataset['page']!, 10))
          .filter((n) => !isNaN(n));

        if (visiblePages.length > 0) {
          const topmost = Math.min(...visiblePages);
          this.ngZone.run(() => this.currentPage.set(topmost));
        }
      },
      { root: container.closest('.pdf-scroll-area'), threshold: 0.1 }
    );

    try {
      for (let pageNum = 1; pageNum <= this.pdfDoc.numPages; pageNum++) {
        const wrapper = document.createElement('div');
        wrapper.className = 'pdf-page-wrapper';
        wrapper.dataset['page'] = String(pageNum);
        container.appendChild(wrapper);
        this.intersectionObserver.observe(wrapper);
        try {
          await this.renderPage(pageNum, wrapper);
        } catch (err) {
          console.error(`[PdfViewer] Nie udało się wyrenderować strony ${pageNum}/${this.pdfDoc.numPages}:`, err);
          this.showPageError(wrapper, pageNum, err);
        }
      }
    } finally {
      this.isRendering.set(false);
    }
  }

  private showPageError(wrapper: HTMLDivElement, pageNum: number, err: unknown): void {
    wrapper.innerHTML = '';
    wrapper.classList.add('pdf-page-error');
    if (!wrapper.style.height) {
      wrapper.style.minHeight = '200px';
    }
    const box = document.createElement('div');
    box.className = 'pdf-page-error-box';
    const title = document.createElement('p');
    title.className = 'pdf-page-error-title';
    title.textContent = `Nie udało się wyrenderować strony ${pageNum}.`;
    const detail = document.createElement('p');
    detail.className = 'pdf-page-error-detail';
    detail.textContent = err instanceof Error ? `${err.name}: ${err.message}` : String(err);
    box.append(title, detail);
    wrapper.appendChild(box);
  }

  private async renderPage(pageNum: number, wrapper: HTMLDivElement): Promise<void> {
    const page = await this.pdfDoc!.getPage(pageNum);
    const viewport = page.getViewport({ scale: this.scale() });

    wrapper.style.position = 'relative';
    wrapper.style.width    = `${viewport.width}px`;
    wrapper.style.height   = `${viewport.height}px`;
    wrapper.style.overflow = 'hidden';

    const canvas = document.createElement('canvas');
    canvas.height = viewport.height;
    canvas.width  = viewport.width;
    canvas.style.display = 'block';
    wrapper.appendChild(canvas);
    await page.render({ canvas, viewport }).promise;

    const textContainer = document.createElement('div');
    textContainer.className = 'textLayer';
    textContainer.style.zIndex = '2';
    textContainer.style.setProperty('--total-scale-factor', String(viewport.scale));
    textContainer.style.setProperty('--scale-round-x', '1px');
    textContainer.style.setProperty('--scale-round-y', '1px');
    wrapper.appendChild(textContainer);

    const textLayer = new TextLayer({
      textContentSource: await page.getTextContent(),
      container: textContainer,
      viewport,
    });
    await textLayer.render();

    const walker = document.createTreeWalker(textContainer, NodeFilter.SHOW_TEXT);
    let tn: Text | null;
    while ((tn = walker.nextNode() as Text | null)) {
      if (tn.textContent?.trim()) this.textNodes.push(tn);
    }

    page.cleanup();
  }

  toggleSearch(): void {
    const next = !this.showSearchBar();
    this.showSearchBar.set(next);
    if (next) {
      setTimeout(() => this.searchInput?.nativeElement?.focus(), 50);
    } else {
      this.clearHighlights();
      this.searchQuery.set('');
      this.searchResults.set(0);
      this.currentMatchIndex.set(0);
    }
  }

  onSearchInput(e: Event): void {
    this.searchQuery.set((e.target as HTMLInputElement).value);
    this.performSearch();
  }

  performSearch(): void {
    this.clearHighlights();
    const query = this.searchQuery().trim().toLowerCase();
    if (!query) {
      this.searchResults.set(0);
      this.currentMatchIndex.set(0);
      return;
    }
    this.matchNodes = this.textNodes.filter(
      (tn) => (tn.textContent ?? '').toLowerCase().includes(query)
    );
    this.matchNodes.forEach((tn) => this.createOverlay(tn, query, false));
    this.searchResults.set(this.matchNodes.length);
    if (this.matchNodes.length > 0) {
      this.currentMatchIndex.set(1);
      this.activateMatch(0);
    } else {
      this.currentMatchIndex.set(0);
    }
  }

  nextMatch(): void {
    if (!this.matchNodes.length) return;
    const nextIdx = this.currentMatchIndex() % this.matchNodes.length;
    this.currentMatchIndex.set(nextIdx + 1);
    this.activateMatch(nextIdx);
  }

  prevMatch(): void {
    if (!this.matchNodes.length) return;
    const prevIdx =
      (this.currentMatchIndex() - 2 + this.matchNodes.length) % this.matchNodes.length;
    this.currentMatchIndex.set(prevIdx + 1);
    this.activateMatch(prevIdx);
  }

  private activateMatch(index: number): void {
    this.highlightOverlays.forEach((ov, i) => {
      if (i === index) {
        ov.style.background = 'rgba(255, 98, 0, 0.6)';
        ov.style.outline    = '2px solid rgba(255, 98, 0, 0.85)';
      } else {
        ov.style.background = 'rgba(255, 196, 0, 0.45)';
        ov.style.outline    = '';
      }
    });
    this.scrollToOverlay(this.highlightOverlays[index]);
  }

  private createOverlay(textNode: Text, query: string, active: boolean): void {
    const parentEl = textNode.parentElement;
    const wrapper  = parentEl?.closest('.pdf-page-wrapper') as HTMLElement | null;
    if (!wrapper) return;

    const fullText = (textNode.textContent ?? '').toLowerCase();
    const idx = fullText.indexOf(query);
    if (idx === -1) return;

    const range = document.createRange();
    range.setStart(textNode, idx);
    range.setEnd(textNode, Math.min(idx + query.length, textNode.length));

    const rangeRect   = range.getBoundingClientRect();
    const wrapperRect = wrapper.getBoundingClientRect();

    if (rangeRect.width < 1 && rangeRect.height < 1) return;

    const overlay = document.createElement('div');
    overlay.style.cssText = [
      'position:absolute',
      `left:${rangeRect.left - wrapperRect.left}px`,
      `top:${rangeRect.top  - wrapperRect.top}px`,
      `width:${Math.max(rangeRect.width, 3)}px`,
      `height:${Math.max(rangeRect.height, 3)}px`,
      `background:${active ? 'rgba(255,98,0,0.6)' : 'rgba(255,196,0,0.45)'}`,
      'border-radius:2px',
      'pointer-events:none',
      'z-index:3',
      ...(active ? ['outline:2px solid rgba(255,98,0,0.85)'] : []),
    ].join(';');
    wrapper.appendChild(overlay);
    this.highlightOverlays.push(overlay);
  }

  private scrollToOverlay(overlay: HTMLElement | undefined): void {
    if (!overlay) return;
    const scrollArea = this.viewerContainer?.nativeElement
      ?.closest('.pdf-scroll-area') as HTMLElement | null;
    if (!scrollArea) return;
    const ovRect   = overlay.getBoundingClientRect();
    const areaRect = scrollArea.getBoundingClientRect();
    scrollArea.scrollTo({
      top: scrollArea.scrollTop + (ovRect.top - areaRect.top)
           - (scrollArea.clientHeight / 2) + (ovRect.height / 2),
      behavior: 'smooth',
    });
  }

  private clearHighlights(): void {
    this.highlightOverlays.forEach((ov) => ov.remove());
    this.highlightOverlays = [];
    this.matchNodes = [];
  }

  async zoomIn(): Promise<void> {
    if (!this.canZoomIn || this.isRendering()) return;
    const idx = this.SCALE_STEPS.indexOf(this.scale());
    if (idx < this.SCALE_STEPS.length - 1) {
      this.scale.set(this.SCALE_STEPS[idx + 1]);
      await this.renderAllPages();
    }
  }

  async zoomOut(): Promise<void> {
    if (!this.canZoomOut || this.isRendering()) return;
    const idx = this.SCALE_STEPS.indexOf(this.scale());
    if (idx > 0) {
      this.scale.set(this.SCALE_STEPS[idx - 1]);
      await this.renderAllPages();
    }
  }

  @HostListener('window:keydown', ['$event'])
  onKeyDown(e: KeyboardEvent): void {
    if (this.isLoading() || this.errorMessage()) return;

    if ((e.ctrlKey || e.metaKey) && e.key === 'f') {
      e.preventDefault();
      if (!this.showSearchBar()) {
        this.toggleSearch();
      } else {
        this.searchInput?.nativeElement?.focus();
      }
      return;
    }

    if (e.key === 'Escape' && this.showSearchBar()) {
      e.preventDefault();
      this.toggleSearch();
      return;
    }

    if (e.ctrlKey || e.metaKey) {
      if (e.key === '+' || e.key === '=') {
        e.preventDefault();
        this.zoomIn();
      } else if (e.key === '-') {
        e.preventDefault();
        this.zoomOut();
      }
    }
  }

  private disconnectObserver(): void {
    this.intersectionObserver?.disconnect();
    this.intersectionObserver = null;
  }
}
