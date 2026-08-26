export const PARAGRAPH_SPACING_SUM_CLASS = 'para-spacing-sum';

export function isSumSpacingModel(el: Element): boolean {
  return !!el.closest('.editor-content')?.classList.contains(PARAGRAPH_SPACING_SUM_CLASS);
}

export function setParagraphSpaceBefore(el: HTMLElement, value: string): void {
  el.style.marginTop = value;
  el.style.removeProperty('--w-before-lines');
}

export function setParagraphSpaceAfter(el: HTMLElement, value: string): void {
  if (isSumSpacingModel(el)) {
    el.style.paddingBottom = value;
    el.style.marginBottom = '';
  } else {
    el.style.marginBottom = value;
    el.style.paddingBottom = '';
  }
  el.style.removeProperty('--w-after-lines');
}

export function readParagraphSpaceBeforePx(el: HTMLElement): number {
  const inline = parseFloat(el.style.marginTop);
  if (Number.isFinite(inline)) return inline * inlineLengthToPx(el.style.marginTop, el);
  return parseFloat(getComputedStyle(el).marginTop) || 0;
}

export function readParagraphSpaceAfterPx(el: HTMLElement): number {
  const inlineMargin = el.style.marginBottom;
  const inlinePadding = el.style.paddingBottom;
  if (inlineMargin || inlinePadding) {
    return (
      (parseFloat(inlineMargin) || 0) * inlineLengthToPx(inlineMargin, el) +
      (parseFloat(inlinePadding) || 0) * inlineLengthToPx(inlinePadding, el)
    );
  }
  const cs = getComputedStyle(el);
  return (parseFloat(cs.marginBottom) || 0) + (parseFloat(cs.paddingBottom) || 0);
}

function inlineLengthToPx(value: string, el: HTMLElement): number {
  if (!value) return 1;
  if (value.endsWith('pt')) return 96 / 72;
  if (value.endsWith('em')) return parseFloat(getComputedStyle(el).fontSize) || 16;
  return 1;
}
