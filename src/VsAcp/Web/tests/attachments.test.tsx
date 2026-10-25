import { afterEach, describe, expect, test, vi } from 'vitest';
import { render } from 'preact';
import { Attachments } from '../src/composer/Attachments';
import { composer } from '../src/state';
import { emptyComposerState } from '../src/bridge/protocol';

function mountAttachments(): HTMLDivElement {
  const container = document.createElement('div');
  document.body.appendChild(container);
  render(<Attachments />, container);
  return container;
}

afterEach(() => {
  composer.value = emptyComposerState;
  document.body.innerHTML = '';
  delete (window as { chrome?: unknown }).chrome;
});

describe('Attachments', () => {
  test('renders nothing with no attachments', () => {
    const container = mountAttachments();
    expect(container.querySelector('.attachments')).toBeNull();
  });

  test('a folder chip renders with its own icon, distinct from a file chip', () => {
    composer.value = {
      ...emptyComposerState,
      files: [{ id: 'f1', fileName: 'a.cs', filePath: 'C:\\a.cs' }],
      attachedFolders: [{ id: 'd1', folderName: 'assets', folderPath: 'C:\\assets' }],
    };
    const container = mountAttachments();

    const chips = Array.from(container.querySelectorAll('.attach-chip'));
    expect(chips).toHaveLength(2);

    const labels = chips.map((c) => c.querySelector('.attach-label')?.textContent);
    expect(labels).toEqual(['a.cs', 'assets']);

    const icons = chips.map((c) => c.querySelector('[aria-hidden="true"]')?.textContent);
    expect(icons[0]).not.toEqual(icons[1]);

    const folderChip = chips[1]!;
    expect(folderChip.getAttribute('title')).toBe('C:\\assets');
  });

  test('removing a folder chip posts composer.remove with kind folder', () => {
    const postMessage = vi.fn();
    (window as { chrome?: unknown }).chrome = {
      webview: { postMessage, postMessageWithAdditionalObjects: vi.fn(), addEventListener: vi.fn() },
    };

    composer.value = {
      ...emptyComposerState,
      attachedFolders: [{ id: 'd1', folderName: 'assets', folderPath: 'C:\\assets' }],
    };
    const container = mountAttachments();
    const removeBtn = container.querySelector('.attach-remove') as HTMLButtonElement;
    removeBtn.click();

    expect(postMessage).toHaveBeenCalledTimes(1);
    expect(JSON.parse(postMessage.mock.calls[0]![0])).toEqual({ type: 'composer.remove', kind: 'folder', id: 'd1' });
  });
});
