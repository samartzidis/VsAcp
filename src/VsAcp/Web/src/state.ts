import { signal } from '@preact/signals';
import { emptyComposerState, type Banner, type ComposerState } from './bridge/protocol';
import type { EditorController } from './composer/editorDom';

/** Latest composer snapshot from the host (everything except the text being typed). */
export const composer = signal<ComposerState>(emptyComposerState);

/**
 * The live editor, set by <Composer>. Host messages that change the text (`composer.set`,
 * `composer.insert`, `composer.focus`) act on it directly instead of going through state, because the
 * page owns the text while the user types.
 */
export const editor: { current: EditorController | null } = { current: null };

/** The banner currently asking the user something, if any. A newer banner replaces an older one. */
export const banner = signal<Banner | null>(null);
