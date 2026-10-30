import { afterEach, describe, expect, it, vi } from 'vitest'
import { copyText } from '@/utilities/clipboard'

// The test environment has no DOM, so document and window are replaced by small fakes.
const stubDocument = (execCommand: () => boolean) => {
  const appended: unknown[] = []
  const area = { value: '', style: {} as Record<string, string>, setAttribute: vi.fn(), select: vi.fn() }
  vi.stubGlobal('document', {
    createElement: vi.fn().mockReturnValue(area),
    body: {
      appendChild: vi.fn((el: unknown) => appended.push(el)),
      removeChild: vi.fn((el: unknown) => appended.splice(appended.indexOf(el), 1)),
    },
    execCommand: vi.fn(execCommand),
  })
  return { area, appended }
}

describe('copyText', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
  })

  it('uses the Clipboard API in a secure context', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    vi.stubGlobal('navigator', { clipboard: { writeText } })
    vi.stubGlobal('window', { isSecureContext: true })

    expect(await copyText('{"a": 1}')).toBe(true)
    expect(writeText).toHaveBeenCalledWith('{"a": 1}')
  })

  it('falls back to a hidden textarea when the Clipboard API is missing', async () => {
    vi.stubGlobal('navigator', {})
    vi.stubGlobal('window', { isSecureContext: false })
    const { area, appended } = stubDocument(() => true)

    expect(await copyText('{"a": 1}')).toBe(true)
    expect(area.value).toBe('{"a": 1}')
    expect(area.select).toHaveBeenCalled()
    expect((document as unknown as { execCommand: unknown }).execCommand).toHaveBeenCalledWith('copy')
    // the temporary textarea is removed again
    expect(appended).toHaveLength(0)
  })

  it('falls back when the Clipboard API rejects', async () => {
    const writeText = vi.fn().mockRejectedValue(new Error('denied'))
    vi.stubGlobal('navigator', { clipboard: { writeText } })
    vi.stubGlobal('window', { isSecureContext: true })
    const { area } = stubDocument(() => true)

    expect(await copyText('x')).toBe(true)
    expect(area.value).toBe('x')
  })

  it('reports failure, and still removes the textarea, when copying throws', async () => {
    vi.stubGlobal('navigator', {})
    vi.stubGlobal('window', { isSecureContext: false })
    const { appended } = stubDocument(() => {
      throw new Error('unsupported')
    })

    expect(await copyText('x')).toBe(false)
    expect(appended).toHaveLength(0)
  })
})
