import { post, postWithFiles } from '../bridge/host';
import { composer, editor } from '../state';

/**
 * Drag and drop for the whole page. WebView2 owns the OLE drop target for its window, so the page (not
 * WPF) sees every drop. Files go to the host, which reads their paths from the WebView2 additional
 * objects; a drag that carries no files (Solution Explorer items) is still reported so the host can
 * fall back to the current Solution Explorer selection; plain text is inserted into the input.
 *
 * A drop on the shared-folders chip (or its popover) is a separate target: the host adds the folders among
 * the dropped items as extra folders (`folders.drop`). A read-only chip accepts nothing.
 */
export function installDropHandling(): void {
  let depth = 0;
  const box = () => document.querySelector('.composer-box');
  const chip = () => document.querySelector('.folders-btn');
  const clear = () => {
    depth = 0;
    box()?.classList.remove('drop-target');
    chip()?.classList.remove('drop-target');
  };
  const overFolders = (e: Event) => e.target instanceof Element && e.target.closest('.folders') !== null;
  const foldersEditable = () => composer.value.folders?.canEdit === true;

  // exactly one place is highlighted: the chip when the drag is over it (and it can take drops), else the composer box
  const highlight = (e: Event) => {
    const onFolders = overFolders(e);
    box()?.classList.toggle('drop-target', !onFolders);
    chip()?.classList.toggle('drop-target', onFolders && foldersEditable());
  };

  document.addEventListener('dragenter', (e) => {
    e.preventDefault();
    depth++;
    highlight(e);
  });
  document.addEventListener('dragleave', () => {
    if (--depth <= 0) clear();
  });
  document.addEventListener('dragover', (e) => {
    e.preventDefault(); // allows the drop and stops the browser from navigating to a dropped file
    highlight(e);
    if (e.dataTransfer) e.dataTransfer.dropEffect = overFolders(e) && !foldersEditable() ? 'none' : 'copy';
  });
  document.addEventListener('drop', (e) => {
    e.preventDefault();
    clear();
    const files = Array.from(e.dataTransfer?.files ?? []);
    if (overFolders(e)) {
      if (!foldersEditable()) return;
      if (files.length > 0) postWithFiles({ type: 'folders.drop' }, files);
      else post({ type: 'folders.drop' });
      return;
    }
    const text = e.dataTransfer?.getData('text/plain') ?? '';
    if (files.length === 0 && text.length > 0) {
      editor.current?.insertText(text);
      return;
    }
    if (files.length > 0) postWithFiles({ type: 'composer.drop' }, files);
    else post({ type: 'composer.drop' });
  });
}
