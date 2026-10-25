import { describe, expect, test } from 'vitest';
import type { ElicitationField } from '../src/bridge/protocol';
import { fieldLabel, groupSteps, initialState, readField, readForm } from '../src/banners/elicitation';

const field = (over: Partial<ElicitationField>): ElicitationField => ({ name: 'f', kind: 'text', required: false, ...over });

describe('groupSteps', () => {
  const q0 = field({ name: 'question_0', kind: 'select', options: [{ value: 'a', title: 'A' }] });
  const q0Other = field({ name: 'question_0_custom', title: 'Other' });
  const q1 = field({ name: 'question_1', kind: 'multiSelect', options: [{ value: 'b', title: 'B' }] });
  const q1Other = field({ name: 'question_1_custom', title: 'Other' });

  test('folds "<question>_custom" into its choice question; the fields stay separate', () => {
    const steps = groupSteps([q0, q0Other, q1, q1Other]);
    expect(steps).toHaveLength(2);
    expect(steps[0]).toEqual({ field: q0, other: q0Other });
    expect(steps[1]).toEqual({ field: q1, other: q1Other });
  });

  test('works when the Other field is listed before its question or after all questions', () => {
    expect(groupSteps([q0Other, q0])).toEqual([{ field: q0, other: q0Other }]);
    expect(groupSteps([q0, q1, q0Other, q1Other])).toHaveLength(2);
  });

  test('a _custom field without a choice question, or not free text, is an ordinary step', () => {
    expect(groupSteps([q0Other])).toHaveLength(1);
    expect(groupSteps([field({ name: 'name' }), field({ name: 'name_custom' })])).toHaveLength(2);
    const notText = field({ name: 'question_0_custom', kind: 'boolean' });
    expect(groupSteps([q0, notText])).toHaveLength(2);
  });
});

describe('labels and initial values', () => {
  test('label prefers the title and marks required fields', () => {
    expect(fieldLabel(field({ name: 'n' }))).toBe('n');
    expect(fieldLabel(field({ name: 'n', title: 'Name', required: true }))).toBe('Name *');
  });

  test('defaults per kind', () => {
    const state = initialState([
      field({ name: 't', defaultText: 'hi' }),
      field({ name: 'n', kind: 'number', defaultNumber: 2.5 }),
      field({ name: 'b', kind: 'boolean', defaultBoolean: true }),
      field({ name: 'b2', kind: 'boolean' }),
      field({ name: 'm', kind: 'multiSelect', defaultValues: ['a'], options: [{ value: 'a', title: 'A' }] }),
    ]);
    expect(state).toEqual({ t: 'hi', n: '2.5', b: true, b2: false, m: ['a'] });
  });

  test('a required select starts on its first option, an optional one on nothing, a default wins', () => {
    const options = [
      { value: 'x', title: 'X' },
      { value: 'y', title: 'Y' },
    ];
    expect(initialState([field({ name: 's', kind: 'select', required: true, options })]).s).toBe('x');
    expect(initialState([field({ name: 's', kind: 'select', options })]).s).toBe('');
    expect(initialState([field({ name: 's', kind: 'select', options, defaultText: 'y' })]).s).toBe('y');
    expect(initialState([field({ name: 's', kind: 'select', options, defaultText: 'nope' })]).s).toBe('');
  });
});

describe('text fields', () => {
  test('required vs optional empty', () => {
    expect(readField(field({ required: true }), '  ')).toEqual({ error: 'this field is required' });
    expect(readField(field({}), '  ')).toEqual({});
  });

  test('value is trimmed; length limits', () => {
    expect(readField(field({}), '  ab  ')).toEqual({ value: 'ab' });
    expect(readField(field({ minLength: 3 }), 'ab')).toEqual({ error: 'at least 3 characters' });
    expect(readField(field({ maxLength: 1 }), 'ab')).toEqual({ error: 'at most 1 characters' });
  });

  test('pattern: mismatch is an error, an invalid pattern is accepted', () => {
    expect(readField(field({ pattern: '^[a-z]+$' }), 'abc')).toEqual({ value: 'abc' });
    expect(readField(field({ pattern: '^[a-z]+$' }), 'ab1')).toEqual({ error: 'does not match the expected format' });
    expect(readField(field({ pattern: '([' }), 'anything')).toEqual({ value: 'anything' });
  });
});

describe('numbers', () => {
  test('integer parsing and bounds', () => {
    const f = field({ kind: 'integer', minimum: 1, maximum: 10 });
    expect(readField(f, '5')).toEqual({ value: 5 });
    expect(readField(f, ' +7 ')).toEqual({ value: 7 });
    expect(readField(f, '5.5')).toEqual({ error: 'enter a whole number' });
    expect(readField(f, 'x')).toEqual({ error: 'enter a whole number' });
    expect(readField(f, '0')).toEqual({ error: 'must be at least 1' });
    expect(readField(f, '11')).toEqual({ error: 'must be at most 10' });
  });

  test('number parsing and bounds', () => {
    const f = field({ kind: 'number', maximum: 1 });
    expect(readField(f, '0.5')).toEqual({ value: 0.5 });
    expect(readField(f, '.5')).toEqual({ value: 0.5 });
    expect(readField(f, '1e-1')).toEqual({ value: 0.1 });
    expect(readField(f, '1.5')).toEqual({ error: 'must be at most 1' });
    expect(readField(f, 'abc')).toEqual({ error: 'enter a number' });
    expect(readField(f, '1,5')).toEqual({ error: 'enter a number' });
  });
});

describe('choices and booleans', () => {
  const options = [
    { value: 'a', title: 'A' },
    { value: 'b', title: 'B' },
    { value: 'c', title: 'C' },
  ];

  test('boolean always yields a value', () => {
    expect(readField(field({ kind: 'boolean' }), true)).toEqual({ value: true });
    expect(readField(field({ kind: 'boolean' }), false)).toEqual({ value: false });
  });

  test('select', () => {
    expect(readField(field({ kind: 'select', options }), 'b')).toEqual({ value: 'b' });
    expect(readField(field({ kind: 'select', options }), '')).toEqual({});
    expect(readField(field({ kind: 'select', options, required: true }), '')).toEqual({ error: 'please choose an option' });
  });

  test('multi-select keeps option order and enforces item counts', () => {
    const f = field({ kind: 'multiSelect', options, minItems: 2, maxItems: 2 });
    expect(readField(f, ['c', 'a'])).toEqual({ value: ['a', 'c'] });
    expect(readField(f, ['a'])).toEqual({ error: 'choose at least 2' });
    expect(readField(f, ['a', 'b', 'c'])).toEqual({ error: 'choose at most 2' });
    expect(readField(field({ kind: 'multiSelect', options }), [])).toEqual({});
    expect(readField(field({ kind: 'multiSelect', options, required: true }), [])).toEqual({ error: 'please choose at least one option' });
  });
});

describe('readForm', () => {
  const fields = [
    field({ name: 'name', title: 'Your name', required: true }),
    field({ name: 'age', kind: 'integer' }),
    field({ name: 'ok', kind: 'boolean' }),
  ];

  test('collects values and omits empty optional fields', () => {
    expect(readForm(fields, { name: 'Ada', age: '', ok: false })).toEqual({ values: { name: 'Ada', ok: false } });
  });

  test('reports the first problem with the field title', () => {
    expect(readForm(fields, { name: '', age: 'x', ok: false })).toEqual({ error: 'Your name: this field is required' });
    expect(readForm(fields, { name: 'Ada', age: 'x', ok: false })).toEqual({ error: 'age: enter a whole number' });
  });
});
