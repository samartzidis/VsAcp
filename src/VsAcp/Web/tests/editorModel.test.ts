import { describe, expect, test } from 'vitest';
import {
  PASTE_LIMIT,
  composePrompt,
  filterCommands,
  findSlashToken,
  moveIndex,
  pasteOverflow,
  pasteRefusal,
} from '../src/composer/editorModel';

describe('pasteOverflow', () => {
  test('is zero while the result stays within the limit, even exactly at it', () => {
    expect(pasteOverflow(0, 100)).toBe(0);
    expect(pasteOverflow(10, PASTE_LIMIT - 10)).toBe(0);
  });

  test('counts the characters past the limit, with the text already in the input', () => {
    expect(pasteOverflow(0, PASTE_LIMIT + 19899)).toBe(19899);
    expect(pasteOverflow(100, PASTE_LIMIT)).toBe(100);
  });

  test('the notice names the overflow and the limit', () => {
    expect(pasteRefusal(19899, 50000)).toBe(
      'That paste is 19899 characters over the limit of 50000 · nothing was added, paste less of it',
    );
  });
});

describe('findSlashToken', () => {
  test('matches a lone slash and a partial command at the start', () => {
    expect(findSlashToken('/')).toEqual({ filter: '', length: 1 });
    expect(findSlashToken('/rev')).toEqual({ filter: 'rev', length: 4 });
  });

  test('matches a token after whitespace, including a newline', () => {
    expect(findSlashToken('hello /te')).toEqual({ filter: 'te', length: 3 });
    expect(findSlashToken('line one\n/x')).toEqual({ filter: 'x', length: 2 });
  });

  test('ignores slashes inside words, paths and finished tokens', () => {
    expect(findSlashToken('src/main')).toBeNull();
    expect(findSlashToken('a/b')).toBeNull();
    expect(findSlashToken('http://x')).toBeNull();
    expect(findSlashToken('/review ')).toBeNull();
    expect(findSlashToken('')).toBeNull();
  });
});

describe('filterCommands', () => {
  const commands = [
    { name: 'review', description: '' },
    { name: 'Test', description: '' },
    { name: 'explain', description: '' },
  ];

  test('empty filter lists everything', () => {
    expect(filterCommands(commands, '')).toHaveLength(3);
  });

  test('matches by case-insensitive substring', () => {
    expect(filterCommands(commands, 'ev').map((c) => c.name)).toEqual(['review']);
    expect(filterCommands(commands, 'TES').map((c) => c.name)).toEqual(['Test']);
    expect(filterCommands(commands, 'zzz')).toEqual([]);
  });
});

describe('moveIndex', () => {
  test('stops at both ends instead of wrapping', () => {
    expect(moveIndex(0, -1, 3)).toBe(0);
    expect(moveIndex(2, 1, 3)).toBe(2);
    expect(moveIndex(1, 1, 3)).toBe(2);
    expect(moveIndex(5, 0, 0)).toBe(0);
  });
});

describe('composePrompt', () => {
  test('prepends the command and trims', () => {
    expect(composePrompt('  fix it  ', 'review')).toBe('/review fix it');
    expect(composePrompt('', 'review')).toBe('/review');
    expect(composePrompt(' hi ', null)).toBe('hi');
  });
});
