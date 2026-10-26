// Turns message text into DOM for the chat list. Everything the agent sends is untrusted: markdown and
// HTML go through DOMPurify before they reach the live document (the old page parsed raw output into a
// live element first, which lets an <img onerror> run), and the result is a DocumentFragment that is
// appended as-is. Showdown and highlight.js stay the page-level globals the template already loads.

import DOMPurify from 'dompurify';
import { h, render as mount } from 'preact';
import { copyToClipboard } from './clipboard';
import { CheckIcon, CopyIcon } from './icons';

interface ShowdownConverter {
  makeHtml(text: string): string;
}
interface ShowdownStatic {
  Converter: new (options: Record<string, unknown>) => ShowdownConverter;
}
interface Hljs {
  highlightElement(el: Element): void;
  highlight(code: string, options: { language: string; ignoreIllegals?: boolean }): { value: string };
}

declare global {
  interface Window {
    showdown?: ShowdownStatic;
    hljs?: Hljs;
  }
}

// ---- sanitizing ----

DOMPurify.addHook('afterSanitizeAttributes', (node) => {
  // links open in a new window (the host sends them to the system browser): never let them reach back
  if (node.tagName === 'A' && node.getAttribute('target')) node.setAttribute('rel', 'noopener noreferrer');
  // markdown task lists render disabled checkboxes; any other form control would be agent-drawn UI
  if (node.tagName === 'INPUT' && node.getAttribute('type') !== 'checkbox') node.remove();
});

function sanitize(html: string): DocumentFragment {
  return DOMPurify.sanitize(html, {
    RETURN_DOM_FRAGMENT: true,
    ADD_ATTR: ['target'],
    FORBID_TAGS: ['style', 'form', 'button', 'textarea', 'select', 'option'],
  });
}

export function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// ---- markdown ----

let converter: ShowdownConverter | null | undefined;

function getConverter(): ShowdownConverter | null {
  if (converter === undefined) {
    converter = window.showdown
      ? new window.showdown.Converter({
          tables: true,
          tasklists: true,
          strikethrough: true,
          ghCodeBlocks: true,
          simplifiedAutoLink: true,
          literalMidWordUnderscores: true,
          openLinksInNewWindow: true,
        })
      : null;
  }
  return converter;
}

// File extensions (and common aliases) to highlight.js language names.
const EXT_TO_LANG: Record<string, string> = {
  cs: 'csharp', ts: 'typescript', tsx: 'typescript', js: 'javascript', jsx: 'javascript', py: 'python',
  json: 'json', xml: 'xml', html: 'html', css: 'css', sh: 'bash', ps1: 'powershell', sql: 'sql',
  yaml: 'yaml', yml: 'yaml', md: 'markdown', tf: 'hcl', go: 'go', java: 'java', cpp: 'cpp', c: 'c', h: 'cpp',
  rs: 'rust', rb: 'ruby', php: 'php', swift: 'swift', kt: 'kotlin', razor: 'xml', csproj: 'xml', props: 'xml',
  targets: 'xml',
};

/** Language of a fenced code block from its class: `language-x`, or Cursor's `start:end:path` citation. */
export function resolveLanguage(className: string): string | null {
  for (const cls of className.split(/\s+/)) {
    const lang = cls.startsWith('language-') ? cls.slice(9) : cls;
    // Cursor citation format: startLine:endLine:filepath (e.g. "6:9:C:\foo\Bar.cs")
    const citation = lang.match(/^\d+:\d+:.+\.([a-zA-Z0-9]+)$/);
    if (citation) return EXT_TO_LANG[citation[1]!.toLowerCase()] ?? null;
    // Plain language name (possibly mixed case, e.g. "CSharp")
    if (cls.startsWith('language-') && !lang.includes(':')) return lang.toLowerCase();
  }
  return null;
}

function highlightCodeBlocks(root: ParentNode): void {
  root.querySelectorAll('pre code').forEach((block) => {
    const lang = resolveLanguage(block.getAttribute('class') ?? '');
    if (!lang) {
      // No declared language: hljs falls back to auto-detect, which happily "recognizes" plain
      // prose (a commit message, a file list) as some language and colorizes words at random.
      // Its own confidence score does not separate the two cases (prose can outscore real code),
      // so leave the block as plain monospace text instead of guessing.
      block.removeAttribute('class');
      return;
    }
    block.setAttribute('class', 'language-' + lang);
    try {
      window.hljs?.highlightElement(block);
    } catch {
      // unknown language: leave the block unhighlighted
    }
  });
}

export function renderMarkdown(text: string): DocumentFragment {
  if (!text) return document.createDocumentFragment();
  const html = getConverter()?.makeHtml(text) ?? `<p>${escapeHtml(text)}</p>`;
  const fragment = sanitize(html);
  highlightCodeBlocks(fragment);
  return fragment;
}

/**
 * Gives each fenced code block of freshly rendered (already sanitized) markdown its own copy button, shown while the
 * block is hovered. The block is wrapped so the button stays put when a wide block scrolls sideways. Called after
 * sanitizing: the sanitizer forbids buttons, so the agent can never put one of these in its text. Call it once per
 * render, on a finished reply: a streaming reply is rebuilt every frame, which would replace a button mid-click.
 */
export function addCodeCopyButtons(root: ParentNode): void {
  for (const pre of Array.from(root.querySelectorAll('pre'))) {
    const code = pre.querySelector('code');
    if (!code) continue;

    const wrap = document.createElement('div');
    wrap.className = 'code-block';
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'code-copy-btn';
    button.title = 'Copy code';
    button.setAttribute('aria-label', 'Copy code');
    mount(h(CopyIcon, null), button);
    let reset: ReturnType<typeof setTimeout> | undefined;
    button.addEventListener('click', (e) => {
      e.stopPropagation();
      // the converter ends every block's code with a newline of its own
      copyToClipboard((code.textContent ?? '').replace(/\r?\n$/, ''));
      button.classList.add('copied');
      mount(h(CheckIcon, null), button);
      clearTimeout(reset);
      reset = setTimeout(() => {
        button.classList.remove('copied');
        mount(h(CopyIcon, null), button);
      }, 1500);
    });

    pre.replaceWith(wrap);
    wrap.append(pre, button);
  }
}

/** Host-supplied HTML (a tool body in Html mode): sanitized like markdown output. */
export function renderHtml(html: string): DocumentFragment {
  return sanitize(html);
}

/** A highlighted `<pre><code>` for plain text; the language is optional. */
export function renderCode(text: string, language: string | null): DocumentFragment {
  const fragment = document.createDocumentFragment();
  const pre = document.createElement('pre');
  pre.className = 'plain-body';
  const code = document.createElement('code');
  if (language && window.hljs) {
    try {
      // highlight.js escapes its input, so its output is safe to assign
      code.innerHTML = window.hljs.highlight(text, { language, ignoreIllegals: true }).value;
      code.classList.add('hljs');
    } catch {
      code.textContent = text;
    }
  } else {
    code.textContent = text;
  }
  pre.appendChild(code);
  fragment.appendChild(pre);
  return fragment;
}

// ---- tool step titles and icons ----

const KNOWN_TOOLS = ['Read File', 'Write File', 'Edit File', 'Read', 'Find', 'Terminal', 'Search', 'Grep', 'Glob', 'Shell'];

/** Splits "Read DemoService\Program.cs" into the tool name and its detail (path, pattern, ...). */
export function parseToolTitle(raw: string | null | undefined): { name: string; detail: string } {
  if (!raw) return { name: 'Tool', detail: '' };
  for (const known of KNOWN_TOOLS) {
    if (raw.startsWith(known)) return { name: known, detail: raw.substring(known.length).trim() };
  }
  const space = raw.indexOf(' ');
  if (space > 0 && space < 20) return { name: raw.substring(0, space), detail: raw.substring(space + 1) };
  return { name: raw, detail: '' };
}

export type ToolIconKind = 'file' | 'edit' | 'search' | 'terminal' | 'plan' | 'info';

export function toolIconKind(toolName: string | null | undefined): ToolIconKind {
  const n = (toolName ?? '').toLowerCase();
  if (n.includes('read') || n.includes('file')) return 'file';
  if (n.includes('write') || n.includes('edit')) return 'edit';
  if (['find', 'search', 'glob', 'grep'].some((k) => n.includes(k))) return 'search';
  if (['terminal', 'bash', 'shell', 'command'].some((k) => n.includes(k))) return 'terminal';
  if (n.includes('plan')) return 'plan';
  return 'info';
}

// ---- file path links ----

// File path regex: the extension must start lowercase to avoid false positives like RTR.MobileApp.
const FILE_PATH = /(?:[A-Za-z]:[\\/]|\.{0,2}\/|(?=[\w@]))(?:[\w\-.@]+[\\/])+[\w\-.]+\.[a-z][a-zA-Z0-9]{0,9}(?::(\d+)(?:-\d+)?)?/g;

function fileLink(path: string, text: string): HTMLAnchorElement {
  const a = document.createElement('a');
  a.href = '#';
  a.className = 'file-link';
  a.dataset.path = path;
  a.textContent = text;
  a.title = 'Open in editor';
  return a;
}

/**
 * Turns file paths in rendered content into clickable links: a `code` span that is exactly one path,
 * relative links (markdown links that are not web/mail links), and paths in plain text. Code blocks
 * and existing links are left alone.
 */
export function linkifyFilePaths(container: HTMLElement): void {
  for (const code of Array.from(container.querySelectorAll('code'))) {
    if (code.closest('pre') || code.querySelector('a')) continue;
    const text = code.textContent ?? '';
    const matches = text.match(FILE_PATH);
    if (matches && matches.length === 1 && text.trim() === matches[0]) {
      code.textContent = '';
      code.appendChild(fileLink(matches[0]!, text));
    }
  }

  for (const link of Array.from(container.querySelectorAll('a[href]'))) {
    if (link.classList.contains('file-link')) continue;
    const href = link.getAttribute('href');
    if (!href || /^(https?|mailto|ftp|javascript):/i.test(href)) continue;
    link.classList.add('file-link');
    (link as HTMLElement).dataset.path = href;
    link.setAttribute('title', 'Open in editor');
    link.setAttribute('href', '#');
  }

  // paths in plain text outside code / links
  const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
  const textNodes: Text[] = [];
  while (walker.nextNode()) textNodes.push(walker.currentNode as Text);
  for (const node of textNodes) {
    if (node.parentElement?.closest('pre, code, a')) continue;
    const text = node.nodeValue ?? '';
    FILE_PATH.lastIndex = 0;
    if (!FILE_PATH.test(text)) continue;
    FILE_PATH.lastIndex = 0;
    const fragment = document.createDocumentFragment();
    let last = 0;
    let m: RegExpExecArray | null;
    while ((m = FILE_PATH.exec(text)) !== null) {
      if (m.index > last) fragment.appendChild(document.createTextNode(text.slice(last, m.index)));
      fragment.appendChild(fileLink(m[0], m[0]));
      last = FILE_PATH.lastIndex;
    }
    if (last < text.length) fragment.appendChild(document.createTextNode(text.slice(last)));
    node.parentNode?.replaceChild(fragment, node);
  }
}
