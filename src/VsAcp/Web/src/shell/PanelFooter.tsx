import { post } from '../bridge/host';
import { FoldersMenu } from '../composer/FoldersMenu';
import { CogIcon, LogIcon } from '../composer/icons';
import { panelFolders } from '../panel';

/**
 * The row under the chats: the ACP Agent settings, the VsAcp log and the shared folders. All belong to the whole
 * solution, not to one chat, so they are here whether or not a chat is open.
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
      <button
        type="button"
        class="ctx-btn logs-btn"
        title="Show the VsAcp log in the Output window"
        aria-label="Show the VsAcp log in the Output window"
        onClick={() => post({ type: 'logs.open' })}
      >
        <LogIcon />
      </button>
      {folders && <FoldersMenu folders={folders} />}
    </div>
  );
}
