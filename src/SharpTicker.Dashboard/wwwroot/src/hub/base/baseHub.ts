import * as signalR from "@microsoft/signalr";
import { getBasePath, getBackendUrl } from '@/utilities/pathResolver';

class BaseHub {
    public connection: signalR.HubConnection;

    // SignalR group membership belongs to one connection. After a reconnect the server has forgotten
    // it, so the groups the page asked for are kept here and joined again.
    private readonly joinedGroups = new Set<string>();

    constructor() {
        this.connection = this.createConnection();
        this.rejoinGroupsOnReconnect();
    }

    private rejoinGroupsOnReconnect(): void {
        this.connection.onreconnected(() => {
            void this.rejoinGroups();
        });
    }

    private async rejoinGroups(): Promise<void> {
        for (const groupName of this.joinedGroups) {
            try {
                await this.connection.invoke("JoinGroup", groupName);
            } catch (err) {
                console.error(`🚨 SignalR: could not rejoin group ${groupName}`, err);
            }
        }
    }

    private createConnection(): signalR.HubConnection {

        const basePath = getBasePath();
        const backendUrl = getBackendUrl();

        // Get auth token and type lazily when building the connection
        const getAuthInfo = () => {
            try {
                // Check window config to determine auth mode
                const config = window.SharpTickerConfig;
                
                if (config?.auth?.mode === 'bearer') {
                    const bearerToken = localStorage.getItem('sharpticker_bearer_token');
                    if (bearerToken) {
                        return {
                            type: 'Bearer',
                            token: bearerToken
                        };
                    }
                }
                
                if (config?.auth?.mode === 'basic') {
                    const basicAuth = localStorage.getItem('sharpticker_basic_auth');
                    if (basicAuth) {
                        return {
                            type: 'Basic',
                            token: basicAuth
                        };
                    }
                }
                
                // No auth configured or no token available
                return null;
            } catch (error) {
                // Could not access auth token
                return null;
            }
        };

        // Use backend domain for WebSocket if configured, otherwise use base path
        let hubUrl: string;
        if (backendUrl) {
            hubUrl = `${backendUrl}/ticker-notification-hub`;
        } else {
            // Avoid leading '//' when basePath is '/'
            hubUrl = basePath === '/' 
                ? '/ticker-notification-hub' 
                : `${basePath}/ticker-notification-hub`;
        }

        const authInfo = getAuthInfo();
        
        // WebSockets cannot send custom headers, so we need to use query parameters
        // For other transports (ServerSentEvents, LongPolling), we can use headers
        const useWebSocketsOnly = true; // Set to false if you want to allow fallback transports
        
        if (useWebSocketsOnly) {
            // WebSocket transport - use access_token query parameter for auth
            const connectionOptions: any = {
                transport: signalR.HttpTransportType.WebSockets
            };
            
            let finalHubUrl = hubUrl;
            
            if (authInfo) {
                const authQuery = authInfo.type === 'Basic' ? authInfo.token : `Bearer:${authInfo.token}`;
                finalHubUrl = `${hubUrl}?access_token=${encodeURIComponent(authQuery)}`;
            }
            
            return new signalR.HubConnectionBuilder()
                .withUrl(finalHubUrl, connectionOptions)
                .withAutomaticReconnect({
                    nextRetryDelayInMilliseconds: retryContext => {
                        // Exponential backoff with max 3 retries
                        if (retryContext.previousRetryCount >= 3) {
                            console.log('🛑 SignalR: Max retry attempts reached, stopping reconnection');
                            return null; // Stop retrying
                        }
                        const delay = Math.min(1000 * Math.pow(2, retryContext.previousRetryCount), 30000);
                        console.log(`🔄 SignalR: Retrying connection in ${delay}ms (attempt ${retryContext.previousRetryCount + 1}/3)`);
                        return delay;
                    }
                })
                .configureLogging(signalR.LogLevel.Information)
                .build();
        } else {
            // Allow fallback transports - use headers for auth
            const connectionOptions: any = {};
            
            if (authInfo) {
                connectionOptions.headers = {
                    'Authorization': `${authInfo.type} ${authInfo.token}`
                };
            }
            
            return new signalR.HubConnectionBuilder()
                .withUrl(hubUrl, connectionOptions)
                .withAutomaticReconnect({
                    nextRetryDelayInMilliseconds: retryContext => {
                        // Exponential backoff with max 3 retries
                        if (retryContext.previousRetryCount >= 3) {
                            console.log('🛑 SignalR: Max retry attempts reached, stopping reconnection (fallback transport)');
                            return null; // Stop retrying
                        }
                        const delay = Math.min(1000 * Math.pow(2, retryContext.previousRetryCount), 30000);
                        console.log(`🔄 SignalR: Retrying connection in ${delay}ms (attempt ${retryContext.previousRetryCount + 1}/3)`);
                        return delay;
                    }
                })
                .configureLogging(signalR.LogLevel.Information)
                .build();
        }
    }

    // Method to rebuild connection with new auth token
    public rebuildConnection(): void {
        if (this.connection.state === signalR.HubConnectionState.Connected) {
            this.connection.stop();
        }
        this.connection = this.createConnection();
        this.rejoinGroupsOnReconnect();
    }

    // Send a message to the server
    protected async sendMessage(methodName: string): Promise<void> {
        if (this.connection.state === signalR.HubConnectionState.Connected) {
            try {
                await this.connection.invoke(methodName);
            } catch (err) {
                // Error sending message
            }
        } else {
            // Cannot send message: SignalR connection is not active.
        }
    }

    // Start Connection
    async startConnectionAsync(): Promise<void> {
        if (this.connection.state === signalR.HubConnectionState.Connected) {
            return;
        }
        
        if (this.connection.state === signalR.HubConnectionState.Connecting) {
            return;
        }
        
        try {
            console.log('🔗 SignalR: Starting connection...');
            await this.connection.start();
            console.log('✅ SignalR: Connection established successfully');
            await this.rejoinGroups();
        } catch (err: any) {
            console.error('🚨 SignalR Connection Error:', err);
            
            // Check if it's an authentication error
            if (err?.message?.includes('401') || 
                err?.message?.includes('Unauthorized') ||
                err?.message?.includes('Authentication failed')) {
                console.error('🚫 SignalR: Authentication failed - connection will not retry');
                // Don't rethrow authentication errors to prevent infinite retry
                return;
            }
            
            // For other errors, rethrow to allow normal error handling and retry logic
            throw err;
        }
    }

    async stopConnectionAsync(): Promise<void> {
        try {
            await this.connection.stop();
        } catch (err) {
            // Error stopping SignalR connection
        }
    }

    // Resolves once the server has added this connection to the group, so callers can load data
    // afterwards without missing an update. If the connection is down the group is remembered and
    // joined when it comes back.
    async joinGroup(groupName: string): Promise<void> {
        this.joinedGroups.add(groupName);
        if (this.connection.state !== signalR.HubConnectionState.Connected) {
            return;
        }
        try {
            await this.connection.invoke("JoinGroup", groupName);
        } catch (err) {
            console.error(`🚨 SignalR: could not join group ${groupName}`, err);
        }
    }

    async leaveGroup(groupName: string): Promise<void> {
        this.joinedGroups.delete(groupName);
        if (this.connection.state !== signalR.HubConnectionState.Connected) {
            return;
        }
        try {
            await this.connection.invoke("LeaveGroup", groupName);
        } catch (err) {
            console.error(`🚨 SignalR: could not leave group ${groupName}`, err);
        }
    }

    // Subscribe to messages from the server
    onReceiveMessageAsSingle<T>(methodName: string, callback: (response: T) => void): void {
        this.connection.on(methodName, (responseFromHub: any) => {
            if (Array.isArray(responseFromHub)) {
                responseFromHub.forEach((response) => {
                    callback(response);
                });
            } else {
                callback(responseFromHub);
            }
        });
    }
}

export default BaseHub;
