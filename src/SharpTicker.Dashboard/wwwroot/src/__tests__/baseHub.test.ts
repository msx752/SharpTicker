import { beforeEach, describe, expect, it, vi } from 'vitest'

// A stand-in for the SignalR connection. It records what BaseHub asks of it and lets the test play the
// part of the library: report the connection state and run the reconnect callback.
const fake = vi.hoisted(() => {
  const connection = {
    state: 'Connected' as string,
    invoke: vi.fn().mockResolvedValue(undefined),
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    on: vi.fn(),
    onreconnected: vi.fn(),
  }
  return { connection }
})

vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    withUrl() {
      return this
    }
    withAutomaticReconnect() {
      return this
    }
    configureLogging() {
      return this
    }
    build() {
      return fake.connection
    }
  }
  return {
    HubConnectionBuilder,
    HttpTransportType: { WebSockets: 1 },
    LogLevel: { Information: 2 },
    HubConnectionState: {
      Connected: 'Connected',
      Disconnected: 'Disconnected',
      Connecting: 'Connecting',
      Reconnecting: 'Reconnecting',
    },
  }
})

vi.mock('@/utilities/pathResolver', () => ({
  getBasePath: () => '/',
  getBackendUrl: () => '',
}))

import BaseHub from '@/hub/base/baseHub'

const joinCalls = () =>
  fake.connection.invoke.mock.calls.filter((call) => call[0] === 'JoinGroup').map((call) => call[1])

const runReconnectCallback = () => {
  const callback = fake.connection.onreconnected.mock.calls[0][0] as () => void
  callback()
}

describe('BaseHub group membership', () => {
  beforeEach(() => {
    fake.connection.state = 'Connected'
    fake.connection.invoke.mockClear()
    fake.connection.start.mockReset().mockResolvedValue(undefined)
    fake.connection.onreconnected.mockClear()
  })

  it('asks the library to tell it about reconnects', () => {
    new BaseHub()

    expect(fake.connection.onreconnected).toHaveBeenCalledOnce()
  })

  it('joins the groups again when the connection comes back', async () => {
    const hub = new BaseHub()
    await hub.joinGroup('cron-1')
    await hub.joinGroup('cron-2')
    fake.connection.invoke.mockClear()

    runReconnectCallback()

    await vi.waitFor(() => expect(joinCalls()).toEqual(['cron-1', 'cron-2']))
  })

  it('does not rejoin a group after it was left', async () => {
    const hub = new BaseHub()
    await hub.joinGroup('cron-1')
    await hub.leaveGroup('cron-1')
    fake.connection.invoke.mockClear()

    runReconnectCallback()
    await Promise.resolve()

    expect(joinCalls()).toEqual([])
  })

  it('joins a group asked for before the connection started once it has started', async () => {
    fake.connection.state = 'Disconnected'
    const hub = new BaseHub()
    await hub.joinGroup('cron-1')
    expect(joinCalls()).toEqual([])

    fake.connection.start.mockImplementationOnce(async () => {
      fake.connection.state = 'Connected'
    })
    await hub.startConnectionAsync()

    expect(joinCalls()).toEqual(['cron-1'])
  })

  it('resolves joinGroup only after the server call finished', async () => {
    const hub = new BaseHub()
    let release!: () => void
    fake.connection.invoke.mockReturnValueOnce(new Promise<void>((resolve) => (release = resolve)))

    let joined = false
    const joining = hub.joinGroup('cron-1').then(() => (joined = true))
    await Promise.resolve()
    expect(joined).toBe(false)

    release()
    await joining
    expect(joined).toBe(true)
  })
})
