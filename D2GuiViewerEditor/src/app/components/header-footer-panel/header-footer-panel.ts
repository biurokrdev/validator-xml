import { Component, EventEmitter, Input, Output, ChangeDetectionStrategy } from '@angular/core';

@Component({
  selector: 'd2-header-footer-panel',
  standalone: true,
  templateUrl: './header-footer-panel.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './header-footer-panel.scss',
})
export class HeaderFooterPanelComponent {
  @Input() section: 'header' | 'footer' | 'body' = 'header';
  @Input() differentFirstPage = false;

  @Output() close = new EventEmitter<void>();
  @Output() toggleDifferentFirstPage = new EventEmitter<void>();
  @Output() openFormatDialog = new EventEmitter<void>();
  @Output() insertImage = new EventEmitter<void>();
  @Output() insertPageNumbers = new EventEmitter<void>();
  @Output() removeSection = new EventEmitter<void>();

  protected sectionLabel(): string {
    return this.section === 'footer' ? 'Stopka' : 'Nagłówek';
  }

  protected formatLabel(): string {
    return this.section === 'footer' ? 'Format stopki' : 'Format nagłówka';
  }

  protected removeLabel(): string {
    return this.section === 'footer' ? 'Usuń stopkę' : 'Usuń nagłówek';
  }
}
