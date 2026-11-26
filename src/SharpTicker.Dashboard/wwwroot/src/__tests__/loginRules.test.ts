/**
 * The sign-in form accepts every credential the server can accept. WithApiKey and WithBasicAuth take any length, so a
 * dashboard configured with a short API key or user name must still be reachable; only an empty field is refused.
 */
import { describe, expect, it } from 'vitest'
import { canSubmitLogin, loginRules } from '@/utilities/loginRules'

const passes = (rules: ((v: string) => true | string)[], value: string) => rules.every((rule) => rule(value) === true)

describe('sign-in rules', () => {
  it.each(['k', 'short1', 'a-key-of-any-length-1234567890'])('accept the API key %j', (key) => {
    expect(passes(loginRules.apiKey, key)).toBe(true)
    expect(canSubmitLogin('apikey', { apiKey: key })).toBe(true)
  })

  it.each(['a', 'ab', 'admin'])('accept the user name %j', (name) => {
    expect(passes(loginRules.username, name)).toBe(true)
    expect(canSubmitLogin('basic', { username: name, password: 'p' })).toBe(true)
  })

  it('refuse empty fields, with a message', () => {
    expect(loginRules.apiKey[0]('')).toBe('API key is required')
    expect(loginRules.username[0]('')).toBe('Username is required')
    expect(loginRules.password[0]('')).toBe('Password is required')
    expect(canSubmitLogin('apikey', { apiKey: '' })).toBe(false)
    expect(canSubmitLogin('basic', { username: 'admin', password: '' })).toBe(false)
    expect(canSubmitLogin('basic', { username: '', password: 'p' })).toBe(false)
  })

  it('let host mode through and nothing else unknown', () => {
    expect(canSubmitLogin('host', {})).toBe(true)
    expect(canSubmitLogin('none', { apiKey: 'k' })).toBe(false)
  })
})
