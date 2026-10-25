import { useEffect, useRef } from 'preact/hooks';
import type { SlashCommand } from '../bridge/protocol';

interface Props {
  commands: SlashCommand[];
  selected: number;
  onPick(name: string): void;
}

export function SlashPopup({ commands, selected, onPick }: Props) {
  const listRef = useRef<HTMLUListElement>(null);

  useEffect(() => {
    listRef.current?.children[selected]?.scrollIntoView({ block: 'nearest' });
  }, [selected]);

  return (
    <ul class="slash-popup" role="listbox" aria-label="Slash commands" ref={listRef}>
      {commands.map((c, i) => (
        <li
          key={c.name}
          role="option"
          aria-selected={i === selected}
          class={i === selected ? 'slash-item selected' : 'slash-item'}
          // mousedown (not click) with preventDefault keeps focus and the caret in the editor
          onMouseDown={(e) => {
            e.preventDefault();
            onPick(c.name);
          }}
        >
          <span class="slash-name">
            <span class="slash-sigil">/</span>
            {c.name}
          </span>
          <span class="slash-desc">{c.description}</span>
        </li>
      ))}
    </ul>
  );
}
