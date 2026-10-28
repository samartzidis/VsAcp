import { post } from '../bridge/host';
import { FoldersMenu } from '../composer/FoldersMenu';
import { CogIcon } from '../composer/icons';
import { panelFolders } from '../panel';

/**
 * The row under the chats: the ACP Agent settings and the shared folders. Both belong to the whole solution, not to
 * one chat, so they are here whether or not a chat is open.
 */
export function PanelFooter() {
  const folders = panelFolders.value;
  return (
    <div class="panel-footer">
      <button
        type="button"
        class="ctx-btn settings-btn"
        title="Open the ACP Agent settings"
        aria-label="Open the ACP Agent settings"
        onClick={() => post({ type: 'settings.open' })}
      >
        <CogIcon />
      </button>
      {folders && <FoldersMenu folders={folders} />}
    </div>
  );
}
