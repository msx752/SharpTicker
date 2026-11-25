/**
 * Request data and examples are text that comes from the application and its users. The dashboard shows them as
 * text: code that hands such text to `v-html`, `innerHTML`, `outerHTML` or `insertAdjacentHTML` lets a value like
 * `<img src onerror=...>` run as script in the operator's browser. None of the components or scripts use these;
 * text goes through `{{ }}`, which Vue escapes.
 */
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const srcDir = fileURLToPath(new URL('..', import.meta.url))
const htmlSink = /\bv-html\s*=|\binner-?html\b|\bouterHTML\b|insertAdjacentHTML/i

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name)
    if (statSync(path).isDirectory()) return name === '__tests__' ? [] : sourceFiles(path)
    return /\.(vue|ts)$/.test(name) && !name.endsWith('.d.ts') ? [path] : []
  })
}

describe('dashboard source', () => {
  it('finds the components and scripts to check', () => {
    const files = sourceFiles(srcDir)
    expect(files.filter((f) => f.endsWith('.vue')).length).toBeGreaterThan(10)
    expect(files.filter((f) => f.endsWith('.ts')).length).toBeGreaterThan(10)
  })

  it('never inserts text as HTML (v-html, innerHTML, outerHTML, insertAdjacentHTML)', () => {
    const offenders = sourceFiles(srcDir).flatMap((file) =>
      readFileSync(file, 'utf8')
        .split('\n')
        .map((line, i) => ({ line, at: `${relative(srcDir, file)}:${i + 1}` }))
        .filter(({ line }) => htmlSink.test(line))
        .map(({ at, line }) => `${at}  ${line.trim()}`),
    )

    expect(offenders).toEqual([])
  })
})
