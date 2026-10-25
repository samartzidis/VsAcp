import { useEffect, useLayoutEffect, useState } from 'preact/hooks';
import { post } from '../bridge/host';
import { copyToClipboard } from './clipboard';
import { lightboxSrc } from './MessageView';

/** Full-size view of an attached image; click or Esc closes it. */
export function Lightbox() {
  const src = lightboxSrc.value;
  // a layout effect: Esc must work the moment the image is on screen, not after the next paint
  useLayoutEffect(() => {
    if (!src) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') lightboxSrc.value = null;
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [src]);

  if (!src) return null;
  return (
    <div class="lightbox" role="dialog" aria-label="Image" onClick={() => (lightboxSrc.value = null)}>
      <img src={src} alt="" />
    </div>
  );
}

/**
 * Right-click menu with Copy, shown only when text is selected (the default browser menu is off, so
 * without this a selection could not be copied with the mouse).
 */
export function ContextMenu() {
  const [at, setAt] = useState<{ x: number; y: number } | null>(null);

  useEffect(() => {
    const onContext = (e: MouseEvent) => {
      if (!window.getSelection()?.toString()) return;
      e.preventDefault();
      // near the cursor, kept inside the viewport
      setAt({ x: Math.min(e.clientX, window.innerWidth - 140), y: Math.min(e.clientY, window.innerHeight - 40) });
    };
    const close = () => setAt(null);
    document.addEventListener('contextmenu', onContext);
    document.addEventListener('click', close);
    return () => {
      document.removeEventListener('contextmenu', onContext);
      document.removeEventListener('click', close);
    };
  }, []);

  if (!at) return null;
  return (
    <div class="ctx-menu" role="menu" style={{ display: 'block', left: at.x + 'px', top: at.y + 'px' }}>
      <div
        class="ctx-menu-item"
        role="menuitem"
        onClick={() => {
          const selected = window.getSelection()?.toString();
          if (selected) copyToClipboard(selected);
          setAt(null);
        }}
      >
        Copy
      </div>
    </div>
  );
}

/** Clicking a file path link in a message asks the host to open it in the editor. */
export function installLinkHandling(): void {
  document.addEventListener('click', (e) => {
    const link = (e.target as Element | null)?.closest('a.file-link') as HTMLElement | null;
    if (!link) return;
    e.preventDefault();
    const path = link.dataset.path;
    if (path) post({ type: 'openFile', path });
  });
}
