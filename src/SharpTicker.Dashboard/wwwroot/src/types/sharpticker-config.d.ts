declare global {
  interface Window {
    SharpTickerConfig?: {
      basePath: string;
      backendDomain?: string;
      auth: {
        mode: 'none' | 'basic' | 'apikey' | 'host' | 'custom';
        enabled: boolean;
        sessionTimeout: number;
      };
    };
  }
}

export {};