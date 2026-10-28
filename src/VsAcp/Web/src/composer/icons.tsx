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
export const CogIcon = () => <Svg d="M19.14,12.94c0.04-0.3,0.06-0.61,0.06-0.94c0-0.32-0.02-0.64-0.07-0.94l2.03-1.58c0.18-0.14,0.23-0.41,0.12-0.61l-1.92-3.32c-0.12-0.22-0.37-0.29-0.59-0.22l-2.39,0.96c-0.5-0.38-1.03-0.7-1.62-0.94L14.4,2.81c-0.04-0.24-0.24-0.41-0.48-0.41h-3.84c-0.24,0-0.43,0.17-0.47,0.41L9.25,5.35C8.66,5.59,8.12,5.92,7.63,6.29L5.24,5.33c-0.22-0.08-0.47,0-0.59,0.22L2.74,8.87C2.62,9.08,2.66,9.34,2.86,9.48l2.03,1.58C4.84,11.36,4.8,11.69,4.8,12s0.02,0.64,0.07,0.94l-2.03,1.58c-0.18,0.14-0.23,0.41-0.12,0.61l1.92,3.32c0.12,0.22,0.37,0.29,0.59,0.22l2.39-0.96c0.5,0.38,1.03,0.7,1.62,0.94l0.36,2.54c0.05,0.24,0.24,0.41,0.48,0.41h3.84c0.24,0,0.44-0.17,0.47-0.41l0.36-2.54c0.59-0.24,1.13-0.56,1.62-0.94l2.39,0.96c0.22,0.08,0.47,0,0.59-0.22l1.92-3.32c0.12-0.22,0.07-0.47-0.12-0.61L19.14,12.94z M12,15.6c-1.98,0-3.6-1.62-3.6-3.6s1.62-3.6,3.6-3.6s3.6,1.62,3.6,3.6S13.98,15.6,12,15.6z" size={13} />;
export const FileIcon =({ size = 10 }: { size?: number }) => <Svg d="M13,9H18.5L13,3.5V9M6,2H14L20,8V20A2,2 0 0,1 18,22H6C4.89,22 4,21.1 4,20V4C4,2.89 4.89,2 6,2M15,18V16H6V18H15M18,14V12H6V14H18Z" size={size} />;

/** The Claude Code mascot in Claude's orange (brand colour, the same in light and dark). */
export const ClaudeLogo = () => (
  <svg class="icon agent-logo" width="16" height="16" viewBox="0 0 24 24" aria-hidden="true">
    <path
      fill="#D97757"
      fill-rule="evenodd"
      clip-rule="evenodd"
      d="M20.998 10.949H24v3.102h-3v3.028h-1.487V20H18v-2.921h-1.487V20H15v-2.921H9V20H7.488v-2.921H6V20H4.487v-2.921H3V14.05H0V10.95h3V5h17.998v5.949zM6 10.949h1.488V8.102H6v2.847zm10.51 0H18V8.102h-1.49v2.847z"
    />
  </svg>
);

/** The Cursor cube, drawn in the text colour so it shows on light and dark themes alike. */
export const CursorLogo = () => (
  <svg class="icon agent-logo" width="14" height="16" viewBox="0 0 466.73 532.09" aria-hidden="true">
    <path
      fill="currentColor"
      d="M457.43 125.94 244.42 2.96a22.13 22.13 0 0 0-22.12 0L9.3 125.94C3.55 129.26 0 135.4 0 142.05v247.99c0 6.65 3.55 12.79 9.3 16.11l213.01 122.98a22.13 22.13 0 0 0 22.12 0l213.01-122.98c5.75-3.32 9.3-9.46 9.3-16.11V142.05c0-6.65-3.55-12.79-9.3-16.11zm-13.38 26.05L238.42 508.15c-1.39 2.4-5.06 1.42-5.06-1.36V273.58c0-4.66-2.49-8.97-6.53-11.31L24.87 145.67c-2.4-1.39-1.42-5.06 1.36-5.06h411.26c5.84 0 9.49 6.33 6.57 11.39h-.01Z"
    />
  </svg>
);

export const StopIcon = () => (
  <svg class="icon" width="14" height="14" viewBox="0 0 14 14" aria-hidden="true">
    <rect x="2" y="2" width="10" height="10" rx="2" ry="2" fill="currentColor" />
  </svg>
);

export const SendIcon = () => <Svg d="M2.01,21L23,12L2.01,3L2,10L17,12L2,14L2.01,21Z" />;

export const ImageGlyph = () => <span aria-hidden="true">{'\u{1F5BC}'}</span>;
export const FileGlyph = () => <span aria-hidden="true">{'\u{1F4C4}'}</span>;
export const FolderGlyph = () => <span aria-hidden="true">{'\u{1F4C1}'}</span>;

