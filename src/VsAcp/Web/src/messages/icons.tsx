// Icons of the message list (the same SVGs the previous page used).
import type { ToolIconKind } from './render';

const outline = { fill: 'none', stroke: 'currentColor', 'stroke-width': 2 } as const;

export function ToolIcon({ kind }: { kind: ToolIconKind }) {
  switch (kind) {
    case 'file':
      return (
        <svg viewBox="0 0 24 24" {...outline} aria-hidden="true">
          <path d="M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8z" />
          <polyline points="14 2 14 8 20 8" />
        </svg>
      );
    case 'edit':
      return (
        <svg viewBox="0 0 24 24" {...outline} aria-hidden="true">
          <path d="M11 4H4a2 2 0 00-2 2v14a2 2 0 002 2h14a2 2 0 002-2v-7" />
          <path d="M18.5 2.5a2.121 2.121 0 013 3L12 15l-4 1 1-4 9.5-9.5z" />
        </svg>
      );
    case 'search':
      return (
        <svg viewBox="0 0 24 24" {...outline} aria-hidden="true">
          <circle cx="11" cy="11" r="8" />
          <line x1="21" y1="21" x2="16.65" y2="16.65" />
        </svg>
      );
    case 'terminal':
      return (
        <svg viewBox="0 0 24 24" {...outline} aria-hidden="true">
          <polyline points="4 17 10 11 4 5" />
          <line x1="12" y1="19" x2="20" y2="19" />
        </svg>
      );
    case 'plan':
      return (
        <svg viewBox="0 0 24 24" {...outline} aria-hidden="true">
          <path d="M9 11l3 3L22 4" />
          <path d="M21 12v7a2 2 0 01-2 2H5a2 2 0 01-2-2V5a2 2 0 012-2h11" />
        </svg>
      );
    default:
      return (
        <svg viewBox="0 0 24 24" {...outline} aria-hidden="true">
          <circle cx="12" cy="12" r="10" />
          <line x1="12" y1="8" x2="12" y2="12" />
          <line x1="12" y1="16" x2="12.01" y2="16" />
        </svg>
      );
  }
}

const line = { fill: 'none', stroke: 'currentColor', 'stroke-linecap': 'round', 'stroke-linejoin': 'round' } as const;

export const CopyIcon = () => (
  <svg width="14" height="14" viewBox="0 0 16 16" {...line} stroke-width="1.4" aria-hidden="true">
    <rect x="5.5" y="5.5" width="8" height="8" rx="1.5" />
    <path d="M3.5 10.5h-1a1.5 1.5 0 01-1.5-1.5v-6a1.5 1.5 0 011.5-1.5h6a1.5 1.5 0 011.5 1.5v1" />
  </svg>
);

export const CheckIcon = () => (
  <svg width="14" height="14" viewBox="0 0 16 16" {...line} stroke-width="2" aria-hidden="true">
    <polyline points="3.5 8.5 6.5 11.5 12.5 4.5" />
  </svg>
);

/** A branch leaving a line: "Fork from here". */
export const ForkIcon = () => (
  <svg width="14" height="14" viewBox="0 0 16 16" {...line} stroke-width="1.5" aria-hidden="true">
    <circle cx="4.5" cy="3" r="1.5" />
    <circle cx="4.5" cy="13" r="1.5" />
    <circle cx="11.5" cy="5" r="1.5" />
    <path d="M4.5 4.5v7" />
    <path d="M11.5 6.5c0 3-7 2.5-7 5" />
  </svg>
);

export const RevertIcon = () => (
  <svg width="14" height="14" viewBox="0 0 16 16" {...line} stroke-width="1.5" aria-hidden="true">
    <path d="M2.5 8a5.5 5.5 0 1 0 1.1-3.3" />
    <polyline points="2.5 2.5 2.5 5.5 5.5 5.5" />
  </svg>
);
