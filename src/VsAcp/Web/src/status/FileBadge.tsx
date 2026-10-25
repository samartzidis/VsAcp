import { FileIcon } from '../composer/icons';

interface Badge {
  label: string;
  color: string;
}

// Small coloured language tags (mid-tone colours that read on both dark and light backgrounds).
const BADGES: Record<string, Badge> = {
  cs: { label: 'C#', color: '#3da54f' },
  csx: { label: 'C#', color: '#3da54f' },
  ts: { label: 'TS', color: '#3b82d6' },
  tsx: { label: 'TSX', color: '#2fa8c8' },
  js: { label: 'JS', color: '#d1a300' },
  mjs: { label: 'JS', color: '#d1a300' },
  cjs: { label: 'JS', color: '#d1a300' },
  jsx: { label: 'JSX', color: '#2fa8c8' },
  json: { label: '{}', color: '#c4a600' },
  css: { label: 'CSS', color: '#9a5fe0' },
  scss: { label: 'CSS', color: '#d6559a' },
  html: { label: '<>', color: '#e5603a' },
  md: { label: 'MD', color: '#4c94c0' },
  xml: { label: '</>', color: '#c8821a' },
  xaml: { label: '</>', color: '#c8821a' },
  csproj: { label: '</>', color: '#c8821a' },
  slnx: { label: '</>', color: '#c8821a' },
  ps1: { label: 'PS', color: '#3d78c4' },
  py: { label: 'PY', color: '#3d78b4' },
  yml: { label: 'YML', color: '#c4584d' },
  yaml: { label: 'YML', color: '#c4584d' },
  sql: { label: 'SQL', color: '#d07a2b' },
  tf: { label: 'TF', color: '#7b61d9' },
};

/** Small file-type tag in front of a file name: a coloured language label, or a plain file glyph. */
export function FileBadge({ fileName }: { fileName: string }) {
  const dot = fileName.lastIndexOf('.');
  const badge = dot >= 0 ? BADGES[fileName.slice(dot + 1).toLowerCase()] : undefined;
  if (!badge) {
    return (
      <span class="file-badge generic" aria-hidden="true">
        <FileIcon size={12} />
      </span>
    );
  }
  return (
    <span class="file-badge" style={{ color: badge.color }} aria-hidden="true">
      {badge.label}
    </span>
  );
}
