// Path data copied from the previous WPF toolbar (Material icons, 24x24 viewBox).

export function Svg({ d, size = 14, viewBox = '0 0 24 24', stroke }: { d: string; size?: number; viewBox?: string; stroke?: boolean }) {
  return (
    <svg class="icon" width={size} height={size} viewBox={viewBox} aria-hidden="true">
      {stroke ? <path d={d} fill="none" stroke="currentColor" stroke-width="1.5" /> : <path d={d} fill="currentColor" />}
    </svg>
  );
}

export const ChevronIcon = () => <Svg d="M0,0 L4,4 L8,0" size={8} viewBox="0 0 8 4" stroke />;
export const AttachIcon = () => <Svg d="M16.5,6V17.5A4,4 0 0,1 12.5,21.5A4,4 0 0,1 8.5,17.5V5A2.5,2.5 0 0,1 11,2.5A2.5,2.5 0 0,1 13.5,5V15.5A1,1 0 0,1 12.5,16.5A1,1 0 0,1 11.5,15.5V6H10V15.5A2.5,2.5 0 0,0 12.5,18A2.5,2.5 0 0,0 15,15.5V5A4,4 0 0,0 11,1A4,4 0 0,0 7,5V17.5A5.5,5.5 0 0,0 12.5,23A5.5,5.5 0 0,0 18,17.5V6H16.5Z" />;
export const ShieldIcon = () => <Svg d="M12,1L3,5V11C3,16.55 6.84,21.74 12,23C17.16,21.74 21,16.55 21,11V5L12,1M10,17L6,13L7.41,11.59L10,14.17L16.59,7.58L18,9L10,17Z" size={13} />;
export const CloseIcon = () => <Svg d="M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z" size={13} />;
export const KeepIcon = () => <Svg d="M21,7L9,19L3.5,13.5L4.91,12.09L9,16.17L19.59,5.59L21,7Z" size={13} />;
export const FolderIcon = () => <Svg d="M10,4H4C2.9,4 2,4.9 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.9 21.1,6 20,6H12L10,4Z" size={12} />;
/** An open folder: the in-box "folders this chat's session uses" button, distinct from the closed folder of the shared-folders chip. */
export const FolderOpenIcon = () => <Svg d="M19,20H4C2.89,20 2,19.1 2,18V6C2,4.89 2.89,4 4,4H10L12,6H19A2,2 0 0,1 21,8H21L4,8V18L6.14,10H23.21L20.93,18.5C20.7,19.37 19.92,20 19,20Z" size={13} />;
export const FileIcon = ({ size = 10 }: { size?: number }) => <Svg d="M13,9H18.5L13,3.5V9M6,2H14L20,8V20A2,2 0 0,1 18,22H6C4.89,22 4,21.1 4,20V4C4,2.89 4.89,2 6,2M15,18V16H6V18H15M18,14V12H6V14H18Z" size={size} />;

export const StopIcon = () => (
  <svg class="icon" width="14" height="14" viewBox="0 0 14 14" aria-hidden="true">
    <rect x="2" y="2" width="10" height="10" rx="2" ry="2" fill="currentColor" />
  </svg>
);

export const SendIcon = () => <Svg d="M2.01,21L23,12L2.01,3L2,10L17,12L2,14L2.01,21Z" />;

export const ImageGlyph = () => <span aria-hidden="true">{'\u{1F5BC}'}</span>;
export const FileGlyph = () => <span aria-hidden="true">{'\u{1F4C4}'}</span>;
export const FolderGlyph = () => <span aria-hidden="true">{'\u{1F4C1}'}</span>;

