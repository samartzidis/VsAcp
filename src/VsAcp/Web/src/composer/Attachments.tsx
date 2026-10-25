import type { ComponentChildren } from 'preact';
import { post } from '../bridge/host';
import type { AttachmentKind } from '../bridge/protocol';
import { composer } from '../state';
import { FileGlyph, FolderGlyph, ImageGlyph } from './icons';

function Chip(props: { kind: AttachmentKind; id: string; icon: ComponentChildren; label: string; title?: string }) {
  return (
    <span class="attach-chip" title={props.title}>
      {props.icon}
      <span class="attach-label">{props.label}</span>
      <button
        type="button"
        class="attach-remove"
        aria-label={`Remove ${props.label}`}
        onClick={() => post({ type: 'composer.remove', kind: props.kind, id: props.id })}
      >
        {'✕'}
      </button>
    </span>
  );
}

/** Images, dropped files/folders and pasted code references waiting to be sent with the next message. */
export function Attachments() {
  const { images, files, attachedFolders, selections } = composer.value;
  if (images.length + files.length + attachedFolders.length + selections.length === 0) return null;
  return (
    <div class="attachments">
      {images.map((i) => (
        <Chip key={i.id} kind="image" id={i.id} icon={<ImageGlyph />} label={i.fileName} />
      ))}
      {files.map((f) => (
        <Chip key={f.id} kind="file" id={f.id} icon={<FileGlyph />} label={f.fileName} title={f.filePath} />
      ))}
      {attachedFolders.map((f) => (
        <Chip key={f.id} kind="folder" id={f.id} icon={<FolderGlyph />} label={f.folderName} title={f.folderPath} />
      ))}
      {selections.map((s) => (
        <Chip key={s.id} kind="selection" id={s.id} icon={<FileGlyph />} label={s.label} title={s.filePath} />
      ))}
    </div>
  );
}
