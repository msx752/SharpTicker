/**
 * Checks of the sign-in form. They only refuse what the server cannot accept: an empty field. The server compares
 * the user name, password or API key with the configured ones as they are, so a length rule here would lock out a
 * dashboard whose key or user name is short (WithApiKey and WithBasicAuth accept any length).
 */
export type LoginMode = 'basic' | 'apikey' | 'host' | string

export interface LoginCredentials {
  username?: string
  password?: string
  apiKey?: string
}

type Rule = (v: string) => true | string

export const loginRules: { username: Rule[]; password: Rule[]; apiKey: Rule[] } = {
  username: [(v) => !!v || 'Username is required'],
  password: [(v) => !!v || 'Password is required'],
  apiKey: [(v) => !!v || 'API key is required'],
}

/** Whether the sign-in button can be used for this mode and these credentials. */
export function canSubmitLogin(mode: LoginMode, credentials: LoginCredentials): boolean {
  if (mode === 'basic') return !!credentials.username && !!credentials.password
  if (mode === 'apikey') return !!credentials.apiKey
  if (mode === 'host') return true
  return false
}
