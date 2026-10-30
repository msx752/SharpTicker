import { describe, expect, it, vi } from 'vitest'
import { GroupMembership, type GroupConnection } from '@/hub/base/groupMembership'

function fakeConnection(connected = true) {
  const state = { connected }
  const invoke = vi.fn<GroupConnection['invoke']>().mockResolvedValue(undefined)
  const connection: GroupConnection = { isConnected: () => state.connected, invoke }
  return { connection, invoke, state }
}

const quietLogger = () => ({ error: vi.fn() })

describe('GroupMembership', () => {
  it('joins a group on the server when the connection is up', async () => {
    const { connection, invoke } = fakeConnection()
    const groups = new GroupMembership(connection, quietLogger())

    await groups.join('cron-1')

    expect(invoke).toHaveBeenCalledWith('JoinGroup', 'cron-1')
    expect(groups.joined).toEqual(['cron-1'])
  })

  it('resolves join only once the server call has finished, so callers can load data after it', async () => {
    const { connection, invoke } = fakeConnection()
    let release!: () => void
    invoke.mockReturnValueOnce(new Promise<void>((resolve) => (release = resolve)))
    const groups = new GroupMembership(connection, quietLogger())

    let resolved = false
    const joining = groups.join('cron-1').then(() => (resolved = true))
    await Promise.resolve()
    expect(resolved).toBe(false)

    release()
    await joining
    expect(resolved).toBe(true)
  })

  it('remembers a group asked for while the connection is down and does not fail', async () => {
    const { connection, invoke } = fakeConnection(false)
    const groups = new GroupMembership(connection, quietLogger())

    await expect(groups.join('cron-1')).resolves.toBeUndefined()

    expect(invoke).not.toHaveBeenCalled()
    expect(groups.joined).toEqual(['cron-1'])
  })

  it('joins every remembered group again after a reconnect', async () => {
    const { connection, invoke } = fakeConnection()
    const groups = new GroupMembership(connection, quietLogger())
    await groups.join('a')
    await groups.join('b')
    invoke.mockClear()

    await groups.rejoinAll()

    expect(invoke.mock.calls).toEqual([
      ['JoinGroup', 'a'],
      ['JoinGroup', 'b'],
    ])
  })

  it('joins a group asked for while the connection was down once it is back', async () => {
    const { connection, invoke, state } = fakeConnection(false)
    const groups = new GroupMembership(connection, quietLogger())
    await groups.join('cron-1')

    state.connected = true
    await groups.rejoinAll()

    expect(invoke).toHaveBeenCalledExactlyOnceWith('JoinGroup', 'cron-1')
  })

  it('does not keep or rejoin a group that was left', async () => {
    const { connection, invoke } = fakeConnection()
    const groups = new GroupMembership(connection, quietLogger())
    await groups.join('a')
    await groups.join('b')

    await groups.leave('a')
    invoke.mockClear()
    await groups.rejoinAll()

    expect(invoke).toHaveBeenCalledExactlyOnceWith('JoinGroup', 'b')
    expect(groups.joined).toEqual(['b'])
  })

  it('forgets a group left while the connection is down without telling the server', async () => {
    const { connection, invoke, state } = fakeConnection()
    const groups = new GroupMembership(connection, quietLogger())
    await groups.join('a')
    invoke.mockClear()

    state.connected = false
    await groups.leave('a')

    expect(invoke).not.toHaveBeenCalled()
    expect(groups.joined).toEqual([])
  })

  it('keeps the group when the server call fails, so the next reconnect tries again', async () => {
    const { connection, invoke } = fakeConnection()
    const logger = quietLogger()
    invoke.mockRejectedValueOnce(new Error('boom'))
    const groups = new GroupMembership(connection, logger)

    await expect(groups.join('a')).resolves.toBeUndefined()

    expect(logger.error).toHaveBeenCalledOnce()
    expect(groups.joined).toEqual(['a'])
  })

  it('keeps rejoining the other groups when one of them fails', async () => {
    const { connection, invoke } = fakeConnection()
    const logger = quietLogger()
    const groups = new GroupMembership(connection, logger)
    await groups.join('a')
    await groups.join('b')
    await groups.join('c')
    invoke.mockClear()
    invoke.mockRejectedValueOnce(new Error('boom')) // the first one, 'a'

    await groups.rejoinAll()

    expect(invoke.mock.calls.map((call) => call[1])).toEqual(['a', 'b', 'c'])
    expect(logger.error).toHaveBeenCalledOnce()
  })

  it('counts a group once however often it is joined', async () => {
    const { connection, invoke } = fakeConnection()
    const groups = new GroupMembership(connection, quietLogger())
    await groups.join('a')
    await groups.join('a')
    invoke.mockClear()

    await groups.rejoinAll()

    expect(invoke).toHaveBeenCalledOnce()
  })
})
