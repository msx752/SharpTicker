export type JsonCheck = { valid: true; formatted: string } | { valid: false; message: string }

/**
 * Checks that text is JSON and, when it is, returns it indented.
 * Empty text is valid: a request is optional, and the result is then an empty string.
 */
export function checkJson(text: string | null | undefined): JsonCheck {
  if (text == null || text.trim() === '') return { valid: true, formatted: '' }

  try {
    JSON.parse(text)
    return { valid: true, formatted: indentJson(text) }
  } catch (error) {
    return { valid: false, message: describeJsonError(error, text) }
  }
}

/**
 * Turns a JSON.parse error into one line for the user. Engines word these errors differently, and
 * only some say where the problem is, so the line and column are worked out from the position
 * when the message gives only that.
 */
export function describeJsonError(error: unknown, text: string): string {
  const raw = (error instanceof Error ? error.message : String(error)).trim()
  if (/\(line \d+ column \d+\)/.test(raw)) return `Invalid JSON: ${raw}`

  const position = /position (\d+)/.exec(raw)
  if (!position) return `Invalid JSON: ${raw}`

  const index = Number(position[1])
  const before = text.slice(0, index)
  const line = before.split('\n').length
  const column = index - (before.lastIndexOf('\n') + 1) + 1
  return `Invalid JSON: ${raw} (line ${line}, column ${column})`
}

const isSpace = (c: string | undefined) => c === ' ' || c === '\t' || c === '\n' || c === '\r'
const indent = (depth: number) => '\n' + '  '.repeat(depth)

/**
 * Re-indents JSON text that is already known to be valid. Only the whitespace between tokens changes:
 * numbers, string escapes and the order of keys stay exactly as typed. Going through JSON.parse and
 * JSON.stringify would not do that (10.50 becomes 10.5, 12345678901234567890 loses digits, and keys
 * such as "2" and "1" swap places), and a request must reach the job as written.
 */
export function indentJson(text: string): string {
  let out = ''
  let depth = 0
  let inString = false

  for (let i = 0; i < text.length; i++) {
    const ch = text[i]

    if (inString) {
      out += ch
      if (ch === '\\') out += text[++i]
      else if (ch === '"') inString = false
      continue
    }

    if (ch === '"') {
      inString = true
      out += ch
    } else if (isSpace(ch)) {
      continue
    } else if (ch === '{' || ch === '[') {
      let next = i + 1
      while (isSpace(text[next])) next++
      if (text[next] === (ch === '{' ? '}' : ']')) {
        out += ch + text[next] // an empty object or array stays on one line
        i = next
      } else {
        depth++
        out += ch + indent(depth)
      }
    } else if (ch === '}' || ch === ']') {
      depth--
      out += indent(depth) + ch
    } else if (ch === ',') {
      out += ',' + indent(depth)
    } else if (ch === ':') {
      out += ': '
    } else {
      out += ch
    }
  }

  return out
}
