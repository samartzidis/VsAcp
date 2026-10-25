import type { SlashCommand } from '../bridge/protocol';

/** A `/filter` token that ends at the caret and starts at the beginning of the text or after whitespace. */
const SLASH_TOKEN = /(?:^|(?<=\s))\/(\S*)$/;

export interface SlashToken {
  /** Text typed after the slash (may be empty). */
  filter: string;
  /** Length of the whole token including the slash. */
  length: number;
}

/** `textBeforeCaret` must not contain the command chip's own text. */
export function findSlashToken(textBeforeCaret: string): SlashToken | null {
  const m = SLASH_TOKEN.exec(textBeforeCaret);
  if (!m) return null;
  return { filter: m[1] ?? '', length: m[0].length };
}

/** Commands whose name contains the filter (case-insensitive); an empty filter lists them all. */
export function filterCommands(commands: readonly SlashCommand[], filter: string): SlashCommand[] {
  if (filter.length === 0) return [...commands];
  const f = filter.toLowerCase();
  return commands.filter((c) => c.name.toLowerCase().includes(f));
}

/** Moves a list selection by `delta`, stopping at both ends (no wrap-around). */
export function moveIndex(index: number, delta: number, count: number): number {
  if (count <= 0) return 0;
  return Math.min(count - 1, Math.max(0, index + delta));
}

/** The prompt the agent receives: `/name rest` when a command chip is present, otherwise the plain text. */
export function composePrompt(text: string, commandName: string | null): string {
  const body = text.trim();
  return commandName ? `/${commandName} ${body}`.trimEnd() : body;
}
