/** What group membership needs from a SignalR connection. */
export interface GroupConnection {
  isConnected(): boolean
  invoke(method: string, ...args: unknown[]): Promise<unknown>
}

export interface GroupLogger {
  error(message: string, ...details: unknown[]): void
}

/**
 * The SignalR groups a page asked to be in.
 *
 * Group membership belongs to one connection: after a reconnect the server has forgotten it. So the
 * wanted groups are remembered here and joined again when the connection is back, and asking to join
 * while the connection is down is not an error, it is joined later.
 */
export class GroupMembership {
  private readonly groups = new Set<string>()

  constructor(
    private readonly connection: GroupConnection,
    private readonly logger: GroupLogger = console,
  ) {}

  get joined(): readonly string[] {
    return [...this.groups]
  }

  /** Resolves once the server has added the connection to the group, so callers can load data after it. */
  async join(group: string): Promise<void> {
    this.groups.add(group)
    if (!this.connection.isConnected()) return
    try {
      await this.connection.invoke('JoinGroup', group)
    } catch (error) {
      // Kept in the set, so the next reconnect tries again.
      this.logger.error(`SignalR: could not join group ${group}`, error)
    }
  }

  async leave(group: string): Promise<void> {
    this.groups.delete(group)
    if (!this.connection.isConnected()) return
    try {
      await this.connection.invoke('LeaveGroup', group)
    } catch (error) {
      this.logger.error(`SignalR: could not leave group ${group}`, error)
    }
  }

  /** Joins every wanted group again. One group failing does not stop the others. */
  async rejoinAll(): Promise<void> {
    for (const group of [...this.groups]) {
      try {
        await this.connection.invoke('JoinGroup', group)
      } catch (error) {
        this.logger.error(`SignalR: could not rejoin group ${group}`, error)
      }
    }
  }
}
