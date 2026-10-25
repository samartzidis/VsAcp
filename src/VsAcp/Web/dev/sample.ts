import type { ChatMessage } from '../src/bridge/protocol';

/** Builds a message with sensible defaults, so tests and the sample conversation only state what matters. */
export function msg(over: Partial<ChatMessage> & Pick<ChatMessage, 'id' | 'type'>): ChatMessage {
  return {
    content: '',
    toolName: null,
    title: '',
    status: 'Success',
    expanderTitle: '',
    bodyMode: 'Markdown',
    body: null,
    language: null,
    isStreaming: false,
    imageDataUris: null,
    fileNames: null,
    folderNames: null,
    selections: null,
    checkpointId: null,
    ...over,
  };
}

/** A conversation that exercises every message type; loaded by the dev harness with `?sample`. */
export const sampleMessages: ChatMessage[] = [
  msg({
    id: 'u1',
    type: 'User',
    content: 'Fix the null check in src/VsAcp.Core/Program.cs and explain it',
    fileNames: ['Program.cs'],
    selections: [{ label: 'Program.cs (25-30)', tooltip: 'C:\\src\\Program.cs' }],
    checkpointId: 'cp1',
  }),
  msg({ id: 't1', type: 'Thinking', content: 'The null check happens after the dereference, so the guard never runs.', expanderTitle: 'Thought for 2s' }),
  msg({
    id: 's1',
    type: 'ToolStep',
    toolName: 'Read File',
    expanderTitle: 'Read src/VsAcp.Core/Program.cs',
    bodyMode: 'PlainText',
    language: 'csharp',
    body: 'var name = user.Name;\nif (user == null) return;',
  }),
  msg({ id: 's2', type: 'ToolStep', toolName: 'Terminal', expanderTitle: 'Terminal dotnet test', status: 'Error', bodyMode: 'PlainText', body: 'Failed: 1 test' }),
  msg({
    id: 'a1',
    type: 'Assistant',
    content:
      '## Fix\n\nThe guard ran **after** the dereference. Edit `src/VsAcp.Core/Program.cs`:\n\n```csharp\nif (user == null) return;\nvar name = user.Name;\n```\n\n- first\n- second\n\nSee [the docs](https://example.com).',
  }),
];
