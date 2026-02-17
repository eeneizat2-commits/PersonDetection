export const environment = {
  production: false,
  apiUrl: 'https://localhost:44375/api',
  signalRUrl: 'https://localhost:44375/detectionHub',

  signalR: {
    reconnectIntervalsMs: [0, 2000, 5000, 10000, 30000],
    maxReconnectAttempts: 10,
    fallbackReconnectDelayMs: 5000,
    keepAliveIntervalMs: 15000
  },

  stream: {
    healthCheckIntervalMs: 5000,
    staleFrameThresholdMs: 10000,
    showReconnectingOverlay: true,
    autoRefreshOnReconnect: true,      // ADD
    autoRefreshStaleStream: true        // ADD
  }
};