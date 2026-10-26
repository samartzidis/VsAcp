import type { ComponentProps, Ref } from 'preact';
import { useLayoutEffect, useRef } from 'preact/hooks';
import { addCodeCopyButtons, linkifyFilePaths, renderCode, renderHtml, renderMarkdown } from './render';

export type RichKind = 'markdown' | 'html' | 'code' | 'text';

interface Props {
  kind: RichKind;
  text: string;
  /** Highlight language for `code`. */
  language?: string | null;
  class?: string;
  style?: ComponentProps<'div'>['style'];
  /** The rendered element, for copying its text. */
  elRef?: Ref<HTMLDivElement>;
  /** Markdown only: each fenced code block gets a copy button of its own. */
  copyCodeBlocks?: boolean;
}

/**
 * Renders message text into its element. The DOM is built (sanitized) outside Preact and swapped in, so
 * Preact never diffs it; file paths in it become links. Re-runs only when the text or kind changes.
 */
export function RichContent({ kind, text, language = null, class: className, style, elRef, copyCodeBlocks = false }: Props) {
  const own = useRef<HTMLDivElement>(null);
  const ref = (elRef ?? own) as { current: HTMLDivElement | null };

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    switch (kind) {
      case 'markdown':
        el.replaceChildren(renderMarkdown(text));
        if (copyCodeBlocks) addCodeCopyButtons(el);
        break;
      case 'html':
        el.replaceChildren(renderHtml(text));
        break;
      case 'code':
        el.replaceChildren(renderCode(text, language));
        break;
      default:
        el.textContent = text;
    }
    linkifyFilePaths(el);
  }, [kind, text, language, copyCodeBlocks]);

  return <div ref={ref as Ref<HTMLDivElement>} class={className} style={style} />;
}
