import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Input,
  ViewChild,
  computed,
  signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  XmlViewLine,
  collectHiddenLines,
  toFormattedLines,
  toRawLines,
} from '../../../../core/utils/xml-format.util';

type ViewerMode = 'raw' | 'formatted';

@Component({
  selector: 'd2-structure-xml-viewer',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './structure-xml-viewer.html',
  styleUrl: './structure-xml-viewer.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StructureXmlViewerComponent {
  @ViewChild('scroll') private scrollHost?: ElementRef<HTMLElement>;

  readonly title = signal<string | null>(null);
  readonly meta = signal<string | null>(null);
  readonly mode = signal<ViewerMode>('formatted');
  readonly search = signal('');
  readonly copied = signal(false);

  private readonly xml = signal<string | null>(null);
  private readonly highlight = signal<number | null>(null);
  private readonly collapsed = signal<ReadonlySet<number>>(new Set<number>());
  private readonly currentMatch = signal(0);

  @Input() set content(value: { title: string; meta: string; xml: string; highlightLine: number | null } | null) {
    this.title.set(value?.title ?? null);
    this.meta.set(value?.meta ?? null);
    this.xml.set(value?.xml ?? null);
    this.highlight.set(value?.highlightLine ?? null);
    this.collapsed.set(new Set<number>());
    this.search.set('');
    this.currentMatch.set(0);

    if (value?.highlightLine) {
      this.mode.set('raw');
      queueMicrotask(() => this.scrollToLine(value.highlightLine!));
    }
  }

  @Input() loading = false;

  readonly lines = computed<XmlViewLine[]>(() => {
    const xml = this.xml();

    if (!xml) {
      return [];
    }

    return this.mode() === 'raw' ? toRawLines(xml) : toFormattedLines(xml);
  });

  readonly visibleLines = computed(() => {
    const hidden = collectHiddenLines(this.lines(), this.collapsed());

    return this.lines().filter((line) => !hidden.has(line.number));
  });

  readonly matchLineNumbers = computed<number[]>(() => {
    const search = this.search().trim().toLocaleLowerCase();

    if (!search) {
      return [];
    }

    return this.lines()
      .filter((line) => line.text.toLocaleLowerCase().includes(search))
      .map((line) => line.number);
  });

  readonly currentMatchNumber = computed<number | null>(() => {
    const matches = this.matchLineNumbers();

    return matches.length === 0 ? null : matches[Math.min(this.currentMatch(), matches.length - 1)];
  });

  readonly matchPositionLabel = computed(() => {
    const total = this.matchLineNumbers().length;

    if (total === 0) {
      return '0 trafień';
    }

    return `${Math.min(this.currentMatch(), total - 1) + 1} / ${total}`;
  });

  readonly highlightLine = computed(() => (this.mode() === 'raw' ? this.highlight() : null));

  setMode(mode: ViewerMode): void {
    this.mode.set(mode);
    this.collapsed.set(new Set<number>());
    this.currentMatch.set(0);

    if (mode === 'raw' && this.highlight()) {
      queueMicrotask(() => this.scrollToLine(this.highlight()!));
    }
  }

  onSearchChange(value: string): void {
    this.search.set(value);
    this.currentMatch.set(0);
  }

  onSearchKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter') {
      event.preventDefault();
      this.goToMatch(event.shiftKey ? -1 : 1);
    }
  }

  goToMatch(delta: number): void {
    const matches = this.matchLineNumbers();

    if (matches.length === 0) {
      return;
    }

    const current = Math.min(this.currentMatch(), matches.length - 1);
    const next = (current + delta + matches.length) % matches.length;

    this.currentMatch.set(next);
    this.revealLine(matches[next]);
  }

  isCollapsed(line: XmlViewLine): boolean {
    return this.collapsed().has(line.number);
  }

  toggleFold(line: XmlViewLine): void {
    if (!line.foldable) {
      return;
    }

    const collapsed = new Set(this.collapsed());

    if (!collapsed.delete(line.number)) {
      collapsed.add(line.number);
    }

    this.collapsed.set(collapsed);
  }

  collapseAll(): void {
    this.collapsed.set(new Set(this.lines().filter((line) => line.foldable).map((line) => line.number)));
  }

  expandAll(): void {
    this.collapsed.set(new Set<number>());
  }

  matchesSearch(line: XmlViewLine): boolean {
    const search = this.search().trim().toLocaleLowerCase();

    return search.length > 0 && line.text.toLocaleLowerCase().includes(search);
  }

  indent(depth: number): number {
    return Math.min(depth, 24) * 12;
  }

  async copy(): Promise<void> {
    const xml = this.xml();

    if (!xml || !navigator.clipboard?.writeText) {
      return;
    }

    await navigator.clipboard.writeText(xml);
    this.copied.set(true);
    setTimeout(() => this.copied.set(false), 1500);
  }

  private revealLine(lineNumber: number): void {
    const collapsed = new Set(this.collapsed());
    let changed = false;

    for (const line of this.lines()) {
      if (line.foldEnd !== null &&
          collapsed.has(line.number) &&
          line.number < lineNumber &&
          lineNumber <= line.foldEnd) {
        collapsed.delete(line.number);
        changed = true;
      }
    }

    if (changed) {
      this.collapsed.set(collapsed);
    }

    queueMicrotask(() => this.scrollToLine(lineNumber));
  }

  private scrollToLine(lineNumber: number): void {
    this.scrollHost?.nativeElement
      .querySelector<HTMLElement>(`[data-line="${lineNumber}"]`)
      ?.scrollIntoView({ block: 'center' });
  }
}
