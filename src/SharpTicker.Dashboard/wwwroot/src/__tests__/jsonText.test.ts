/**
 * The JSON text of a request body in the dashboard: checking it, locating an error by line and column, and formatting
 * it without changing the data (numbers as typed, key order, string contents).
 */
import { describe, expect, it } from 'vitest'
import { checkJson, describeJsonError, indentJson } from '@/utilities/jsonText'

describe('checkJson', () => {
  it.each([undefined, null, '', '   ', '\n\t'])('treats %j as an empty, valid request', (text) => {
    expect(checkJson(text)).toEqual({ valid: true, formatted: '' })
  })

  it('pretty-prints valid JSON with two spaces', () => {
    const result = checkJson('{"OrderId":"A-1","Items":[1,2],"Note":null}')

    expect(result).toEqual({
      valid: true,
      formatted: '{\n  "OrderId": "A-1",\n  "Items": [\n    1,\n    2\n  ],\n  "Note": null\n}',
    })
  })

  it('is idempotent: formatting formatted text changes nothing', () => {
    const first = checkJson('{"a":{"b":[1,{"c":true}]}}')
    if (!first.valid) throw new Error('expected valid')

    expect(checkJson(first.formatted)).toEqual(first)
  })

  it.each(['42', '"text"', 'true', 'null', '[]'])('accepts the top-level value %s', (text) => {
    expect(checkJson(text).valid).toBe(true)
  })

  it.each([
    ['a trailing comma', '{"a": 1,}'],
    ['single quotes', "{'a': 1}"],
    ['an unquoted key', '{a: 1}'],
    ['a missing closing brace', '{"a": 1'],
    ['a comment', '{"a": 1 /* x */}'],
    ['plain words', 'not json'],
    ['a lone comma', ','],
  ])('rejects %s', (_, text) => {
    const result = checkJson(text)

    expect(result.valid).toBe(false)
    if (!result.valid) expect(result.message.startsWith('Invalid JSON: ')).toBe(true)
  })

  it('never throws, whatever the input', () => {
    for (const text of ['\u0000', '{"a":', '[[[[', '\\', '{"a": 1}}', '﻿{"a":1}']) {
      expect(() => checkJson(text)).not.toThrow()
    }
  })
})

describe('describeJsonError', () => {
  it('keeps the line and column when the engine reports them', () => {
    const message = 'Expected property name or } in JSON at position 1 (line 1 column 2)'

    expect(describeJsonError(new Error(message), '{a}')).toBe(`Invalid JSON: ${message}`)
  })

  it('works out line and column from a bare position', () => {
    const text = '{\n  "a": 1,\n  "b": x\n}'
    const position = text.indexOf('x')

    expect(describeJsonError(new Error(`Unexpected token x in JSON at position ${position}`), text)).toBe(
      `Invalid JSON: Unexpected token x in JSON at position ${position} (line 3, column 8)`,
    )
  })

  it('passes a message without a position through', () => {
    expect(describeJsonError(new Error('Unexpected end of JSON input'), '{')).toBe(
      'Invalid JSON: Unexpected end of JSON input',
    )
  })

  it('accepts a thrown value that is not an Error', () => {
    expect(describeJsonError('boom', '{')).toBe('Invalid JSON: boom')
  })
})

describe('indentJson (formatting must not change the data)', () => {
  it('keeps numbers exactly as typed', () => {
    const text = '{"price":10.50,"big":12345678901234567890,"exp":1e3,"neg":-0.0,"int":7}'

    expect(indentJson(text)).toBe(
      '{\n  "price": 10.50,\n  "big": 12345678901234567890,\n  "exp": 1e3,\n  "neg": -0.0,\n  "int": 7\n}',
    )
  })

  it('keeps the order of keys, including keys that look like numbers', () => {
    expect(indentJson('{"2":"b","1":"a","x":0}')).toBe('{\n  "2": "b",\n  "1": "a",\n  "x": 0\n}')
  })

  it('keeps string contents and escapes untouched, including braces, commas and colons inside strings', () => {
    const text = String.raw`{"a":"x, y: {z} [w] \" \\ é  two  spaces","b":"tab\there"}`

    expect(checkJson(text).valid).toBe(true) // the sample is real JSON, escapes included
    expect(indentJson(text)).toBe(
      '{\n' +
        String.raw`  "a": "x, y: {z} [w] \" \\ é  two  spaces",` +
        '\n' +
        String.raw`  "b": "tab\there"` +
        '\n}',
    )
  })

  it('keeps empty objects and arrays on one line, whatever whitespace they hold', () => {
    expect(indentJson('{"a":{ },"b":[\n],"c":[]}')).toBe('{\n  "a": {},\n  "b": [],\n  "c": []\n}')
  })

  it('indents nesting one level per container', () => {
    expect(indentJson('{"a":[{"b":[1,2]},3]}')).toBe(
      '{\n  "a": [\n    {\n      "b": [\n        1,\n        2\n      ]\n    },\n    3\n  ]\n}',
    )
  })

  it('re-indents already formatted and badly spaced input to the same result', () => {
    const tidy = '{\n  "a": [\n    1,\n    2\n  ],\n  "b": true\n}'

    expect(indentJson(tidy)).toBe(tidy)
    expect(indentJson('  {  "a" :[1 ,2 ]  ,\r\n"b":   true}  ')).toBe(tidy)
  })

  it('handles top-level values that are not containers', () => {
    expect(indentJson('  42 ')).toBe('42')
    expect(indentJson(' "a b" ')).toBe('"a b"')
    expect(indentJson('null')).toBe('null')
  })

  it('produces JSON that parses to the same value as the input', () => {
    const samples = ['{"a":[1,{"b":null}],"c":"x"}', '[1,[2,[3,[]]],{}]', '{"k":"\\\\"}', '{"":""}']

    for (const text of samples) {
      expect(JSON.parse(indentJson(text))).toEqual(JSON.parse(text))
    }
  })

  it('is applied by checkJson', () => {
    expect(checkJson('{"price":10.50}')).toEqual({ valid: true, formatted: '{\n  "price": 10.50\n}' })
  })
})
