import { readFileSync } from 'node:fs';
import { beforeAll, describe, expect, test } from 'vitest';
import {
  linkifyFilePaths,
  parseToolTitle,
  renderCode,
  renderHtml,
  renderMarkdown,
  resolveLanguage,
  toolIconKind,
} from '../src/messages/render';

// The page gets Showdown and highlight.js as globals from the template; load the same files here.
beforeAll(() => {
  for (const name of ['showdown.min.js', 'highlight.min.js']) {
    const source = readFileSync(new URL('../../Assets/' + name, import.meta.url), 'utf8');
    // a classic <script> turns top-level `var hljs` into window.hljs; inside a Function it stays local
    new Function(source + '\n;this.hljs = typeof hljs === "undefined" ? this.hljs : hljs;').call(window);
  }
});

const html = (fragment: DocumentFragment): string => {
  const div = document.createElement('div');
  div.appendChild(fragment);
  return div.innerHTML;
};

describe('parseToolTitle', () => {
  test('known tool names split off their detail', () => {
    expect(parseToolTitle('Read File')).toEqual({ name: 'Read File', detail: '' });
    expect(parseToolTitle('Read src/a.cs')).toEqual({ name: 'Read', detail: 'src/a.cs' });
    expect(parseToolTitle('Terminal dotnet test')).toEqual({ name: 'Terminal', detail: 'dotnet test' });
  });

  test('unknown titles split on a short first word, otherwise stay whole', () => {
    expect(parseToolTitle('Fetch https://example.com')).toEqual({ name: 'Fetch', detail: 'https://example.com' });
    expect(parseToolTitle('averyveryverylongtoolnamewithoutspaces')).toEqual({
      name: 'averyveryverylongtoolnamewithoutspaces',
      detail: '',
    });
    expect(parseToolTitle('')).toEqual({ name: 'Tool', detail: '' });
    expect(parseToolTitle(null)).toEqual({ name: 'Tool', detail: '' });
  });

  test('icon kind by tool name', () => {
    expect(toolIconKind('Read File')).toBe('file');
    expect(toolIconKind('Edit')).toBe('edit');
    expect(toolIconKind('Grep')).toBe('search');
    expect(toolIconKind('Terminal')).toBe('terminal');
    expect(toolIconKind('Plan')).toBe('plan');
    expect(toolIconKind('Something')).toBe('info');
  });
});

describe('resolveLanguage', () => {
  test('language classes, case-insensitive', () => {
    expect(resolveLanguage('language-csharp')).toBe('csharp');
    expect(resolveLanguage('language-CSharp')).toBe('csharp');
  });

  test("Cursor's start:end:path citations map by file extension", () => {
    expect(resolveLanguage('language-6:9:C:\\foo\\Bar.cs')).toBe('csharp');
    expect(resolveLanguage('language-1:2:src/app.tsx')).toBe('typescript');
    expect(resolveLanguage('language-1:2:notes.unknownext')).toBeNull();
  });

  test('nothing usable', () => {
    expect(resolveLanguage('')).toBeNull();
    expect(resolveLanguage('hljs')).toBeNull();
  });
});

describe('renderMarkdown', () => {
  test('renders common markdown', () => {
    const out = html(renderMarkdown('# Title\n\nsome **bold** text\n\n- a\n- b'));
    expect(out).toContain('<h1');
    expect(out).toContain('<strong>bold</strong>');
    expect(out).toContain('<li>a</li>');
  });

  test('highlights fenced code and resolves the language class', () => {
    const out = html(renderMarkdown('```csharp\nvar x = 1;\n```'));
    expect(out).toContain('language-csharp');
    expect(out).toContain('hljs');
  });

  test('an unlabeled fenced block is left as plain text, not auto-detected and colorized', () => {
    const out = html(renderMarkdown('```\nPull in react-image-crop and let users adjust a crop before sending.\n```'));
    expect(out).not.toContain('hljs');
  });

  test('links open in a new window without access to the opener', () => {
    const out = html(renderMarkdown('[docs](https://example.com)'));
    expect(out).toContain('target="_blank"');
    expect(out).toContain('rel="noopener noreferrer"');
  });

  test('task lists keep their disabled checkboxes', () => {
    expect(html(renderMarkdown('- [x] done\n- [ ] todo'))).toContain('type="checkbox"');
  });

  describe('agent output is untrusted: scripting and agent-drawn UI never reach the page', () => {
    const cases: Record<string, [string, string[]]> = {
      'script tag': ['hello <script>alert(1)</script>', ['<script', 'alert(1)']],
      'inline event handler': ['<img src="x" onerror="alert(1)">', ['onerror', 'alert(1)']],
      'javascript: link': ['<a href="javascript:alert(1)">x</a>', ['javascript:']],
      'markdown javascript: link': ['[x](javascript:alert(1))', ['javascript:']],
      iframe: ['<iframe src="https://evil.example"></iframe>', ['<iframe']],
      'style tag': ['<style>body{display:none}</style>text', ['<style']],
      'form controls': ['<form action="https://evil.example"><input type="text"><button>Approve</button></form>', ['<form', 'type="text"', '<button']],
      'svg script': ['<svg onload="alert(1)"></svg>', ['onload']],
    };

    for (const [name, [input, forbidden]] of Object.entries(cases)) {
      test(name, () => {
        const out = html(renderMarkdown(input));
        for (const bad of forbidden) expect(out).not.toContain(bad);
      });
    }

    test('the same holds for host-supplied HTML bodies', () => {
      const out = html(renderHtml('<b>ok</b><img src=x onerror=alert(1)><script>alert(1)</script>'));
      expect(out).toContain('<b>ok</b>');
      expect(out).not.toContain('onerror');
      expect(out).not.toContain('<script');
    });

    test('nothing in the fragment is live before it is attached (no handler can fire early)', () => {
      const fragment = renderMarkdown('<img src="x" onerror="window.__pwned = true">');
      expect(fragment.querySelector('[onerror]')).toBeNull();
      expect((window as unknown as { __pwned?: boolean }).__pwned).toBeUndefined();
    });
  });
});

describe('renderCode', () => {
  test('escapes plain text', () => {
    expect(html(renderCode('a < b && <script>', null))).toContain('a &lt; b &amp;&amp; &lt;script&gt;');
  });

  test('highlights when the language is known and falls back when it is not', () => {
    expect(html(renderCode('var x = 1;', 'csharp'))).toContain('hljs');
    expect(html(renderCode('<b>x</b>', 'no-such-language'))).toContain('&lt;b&gt;');
  });
});

describe('linkifyFilePaths', () => {
  const run = (markup: string): HTMLElement => {
    const div = document.createElement('div');
    div.innerHTML = markup;
    linkifyFilePaths(div);
    return div;
  };

  test('a code span that is exactly one path becomes a link', () => {
    const a = run('<p><code>src/VsAcp.Core/Program.cs</code></p>').querySelector('a.file-link') as HTMLElement;
    expect(a.dataset.path).toBe('src/VsAcp.Core/Program.cs');
  });

  test('a path with a line number keeps it', () => {
    const a = run('<p><code>C:\\src\\a.cs:42</code></p>').querySelector('a.file-link') as HTMLElement;
    expect(a.dataset.path).toBe('C:\\src\\a.cs:42');
  });

  test('paths in plain text are linked', () => {
    const div = run('<p>Edit src/main/app.ts and then run it</p>');
    const a = div.querySelector('a.file-link') as HTMLElement;
    expect(a.dataset.path).toBe('src/main/app.ts');
    expect(div.textContent).toBe('Edit src/main/app.ts and then run it');
  });

  test('relative markdown links become file links; web links are left alone', () => {
    const div = run('<p><a href="docs/guide.md">guide</a> <a href="https://example.com">web</a> <a href="mailto:a@b.c">mail</a></p>');
    const links = Array.from(div.querySelectorAll('a'));
    expect(links[0]!.classList.contains('file-link')).toBe(true);
    expect(links[0]!.getAttribute('href')).toBe('#');
    expect(links[1]!.classList.contains('file-link')).toBe(false);
    expect(links[2]!.classList.contains('file-link')).toBe(false);
  });

  test('code blocks, existing links and look-alikes are not linked', () => {
    expect(run('<pre><code>src/a/b.cs</code></pre>').querySelector('a')).toBeNull();
    expect(run('<p>see RTR.MobileApp for details</p>').querySelector('a')).toBeNull();
    expect(run('<p><code>two src/a.cs and src/b.cs</code></p>').querySelector('a')).toBeNull();
  });
});
