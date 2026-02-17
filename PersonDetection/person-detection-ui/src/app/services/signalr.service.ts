import { Injectable, PLATFORM_ID, inject } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { BehaviorSubject, Subject } from 'rxjs';
import * as signalR from '@microsoft/signalr';
import { environment } from '../../environments/environment';
import { DetectionUpdate, StreamStatusUpdate } from '../core/models/detection.models';
import { VideoProgressUpdate, VideoCompleteUpdate, NewVideoJobUpdate } from '../core/models/video.models';

export enum SignalRConnectionState {
  Disconnected = 'disconnected',
  Connecting = 'connecting',
  Connected = 'connected',
  Reconnecting = 'reconnecting'
}

@Injectable({
  providedIn: 'root'
})
export class SignalRService {
  private platformId = inject(PLATFORM_ID);
  private hubConnection: signalR.HubConnection | null = null;
  private reconnectAttempt = 0;

  private connectionStateSubject = new BehaviorSubject<SignalRConnectionState>(
    SignalRConnectionState.Disconnected
  );
  connectionState$ = this.connectionStateSubject.asObservable();

  private connectionStatusSubject = new BehaviorSubject<boolean>(false);
  connectionStatus$ = this.connectionStatusSubject.asObservable();

  private detectionUpdateSubject = new Subject<DetectionUpdate>();
  detectionUpdate$ = this.detectionUpdateSubject.asObservable();

  private streamStatusSubject = new Subject<StreamStatusUpdate>();
  streamStatus$ = this.streamStatusSubject.asObservable();

  private cameraStatusMap = new Map<number, BehaviorSubject<StreamStatusUpdate | null>>();

  private videoProgressSubject = new Subject<VideoProgressUpdate>();
  videoProgress$ = this.videoProgressSubject.asObservable();

  private videoCompleteSubject = new Subject<VideoCompleteUpdate>();
  videoComplete$ = this.videoCompleteSubject.asObservable();

  private newVideoJobSubject = new Subject<NewVideoJobUpdate>();
  newVideoJob$ = this.newVideoJobSubject.asObservable();

  private subscribedCameras = new Set<number>();
  private subscribedVideoJobs = new Set<string>();
  private isSubscribedToStreamStatus = false;

  private get isBrowser(): boolean {
    return isPlatformBrowser(this.platformId);
  }

  getCameraStatus$(cameraId: number) {
    if (!this.cameraStatusMap.has(cameraId)) {
      this.cameraStatusMap.set(cameraId, new BehaviorSubject<StreamStatusUpdate | null>(null));
    }
    return this.cameraStatusMap.get(cameraId)!.asObservable();
  }

  async startConnection(): Promise<void> {
    if (!this.isBrowser) {
      console.log('SignalR: Skipping connection on server');
      return;
    }

    if (this.hubConnection?.state === signalR.HubConnectionState.Connected) {
      return;
    }

    this.connectionStateSubject.next(SignalRConnectionState.Connecting);

    const { reconnectIntervalsMs, fallbackReconnectDelayMs } = environment.signalR;

    this.hubConnection = new signalR.HubConnectionBuilder()
      .withUrl(environment.signalRUrl, {
        skipNegotiation: true,
        transport: signalR.HttpTransportType.WebSockets
      })
      .withAutomaticReconnect(reconnectIntervalsMs)
      .configureLogging(signalR.LogLevel.Information)
      .build();

    this.hubConnection.on('DetectionUpdate', (data: DetectionUpdate) => {
      console.log('Detection update received:', data);
      this.detectionUpdateSubject.next(data);
    });

    this.hubConnection.on('StreamStatusUpdate', (data: StreamStatusUpdate) => {
      console.log('Stream status update:', data);
      this.streamStatusSubject.next(data);

      if (!this.cameraStatusMap.has(data.cameraId)) {
        this.cameraStatusMap.set(data.cameraId, new BehaviorSubject<StreamStatusUpdate | null>(null));
      }
      this.cameraStatusMap.get(data.cameraId)!.next(data);
    });

    this.hubConnection.on('VideoProcessingProgress', (data: VideoProgressUpdate) => {
      console.log('Video progress update:', data);
      this.videoProgressSubject.next(data);
    });

    this.hubConnection.on('VideoProcessingComplete', (data: VideoCompleteUpdate) => {
      console.log('Video processing complete:', data);
      this.videoCompleteSubject.next(data);
    });

    this.hubConnection.on('NewVideoJobCreated', (data: NewVideoJobUpdate) => {
      console.log('New video job created:', data);
      this.newVideoJobSubject.next(data);
    });

    this.hubConnection.onreconnecting((error) => {
      this.reconnectAttempt++;
      console.log(`SignalR reconnecting (attempt ${this.reconnectAttempt})...`, error);
      this.connectionStateSubject.next(SignalRConnectionState.Reconnecting);
      this.connectionStatusSubject.next(false);
    });

    this.hubConnection.onreconnected((connectionId) => {
      this.reconnectAttempt = 0;
      console.log('SignalR reconnected:', connectionId);
      this.connectionStateSubject.next(SignalRConnectionState.Connected);
      this.connectionStatusSubject.next(true);
      this.resubscribeAll();
    });

    this.hubConnection.onclose((error) => {
      console.log('SignalR connection closed:', error);
      this.connectionStateSubject.next(SignalRConnectionState.Disconnected);
      this.connectionStatusSubject.next(false);
      setTimeout(() => this.startConnection(), fallbackReconnectDelayMs);
    });

    try {
      await this.hubConnection.start();
      this.reconnectAttempt = 0;
      console.log('SignalR connected successfully');
      this.connectionStateSubject.next(SignalRConnectionState.Connected);
      this.connectionStatusSubject.next(true);
    } catch (error) {
      console.error('SignalR connection error:', error);
      this.connectionStateSubject.next(SignalRConnectionState.Disconnected);
      this.connectionStatusSubject.next(false);
      setTimeout(() => this.startConnection(), fallbackReconnectDelayMs);
    }
  }

  private async resubscribeAll(): Promise<void> {
    for (const cameraId of this.subscribedCameras) {
      await this.subscribeToCamera(cameraId);
    }
    for (const jobId of this.subscribedVideoJobs) {
      await this.subscribeToVideoJob(jobId);
    }
    if (this.isSubscribedToStreamStatus) {
      await this.subscribeToStreamStatus();
    }
  }

  async subscribeToCamera(cameraId: number): Promise<void> {
    if (!this.isBrowser) return;
    if (this.hubConnection?.state === signalR.HubConnectionState.Connected) {
      try {
        await this.hubConnection.invoke('SubscribeToCamera', cameraId);
        this.subscribedCameras.add(cameraId);
        console.log(`Subscribed to camera ${cameraId}`);
      } catch (error) {
        console.error(`Failed to subscribe to camera ${cameraId}:`, error);
      }
    } else {
      this.subscribedCameras.add(cameraId);
    }
  }

  async unsubscribeFromCamera(cameraId: number): Promise<void> {
    if (!this.isBrowser) return;
    if (this.hubConnection?.state === signalR.HubConnectionState.Connected) {
      try {
        await this.hubConnection.invoke('UnsubscribeFromCamera', cameraId);
        this.subscribedCameras.delete(cameraId);
        console.log(`Unsubscribed from camera ${cameraId}`);
      } catch (error) {
        console.error(`Failed to unsubscribe from camera ${cameraId}:`, error);
      }
    }
  }

  async subscribeToStreamStatus(): Promise<void> {
    if (!this.isBrowser) return;
    if (this.hubConnection?.state === signalR.HubConnectionState.Connected) {
      try {
        await this.hubConnection.invoke('SubscribeToStreamStatus');
        this.isSubscribedToStreamStatus = true;
        console.log('Subscribed to stream status');
      } catch (error) {
        console.error('Failed to subscribe to stream status:', error);
      }
    } else {
      this.isSubscribedToStreamStatus = true;
    }
  }

  async unsubscribeFromStreamStatus(): Promise<void> {
    if (!this.isBrowser) return;
    if (this.hubConnection?.state === signalR.HubConnectionState.Connected) {
      try {
        await this.hubConnection.invoke('UnsubscribeFromStreamStatus');
        this.isSubscribedToStreamStatus = false;
        console.log('Unsubscribed from stream status');
      } catch (error) {
        console.error('Failed to unsubscribe from stream status:', error);
      }
    }
  }

  async subscribeToVideoJob(jobId: string): Promise<void> {
    if (!this.isBrowser) return;
    if (this.hubConnection?.state === signalR.HubConnectionState.Connected) {
      try {
        await this.hubConnection.invoke('SubscribeToVideoJob', jobId);
        this.subscribedVideoJobs.add(jobId);
        console.log(`Subscribed to video job ${jobId}`);
      } catch (error) {
        console.error(`Failed to subscribe to video job ${jobId}:`, error);
      }
    }
  }

  async unsubscribeFromVideoJob(jobId: string): Promise<void> {
    if (!this.isBrowser) return;
    if (this.hubConnection?.state === signalR.HubConnectionState.Connected) {
      try {
        await this.hubConnection.invoke('UnsubscribeFromVideoJob', jobId);
        this.subscribedVideoJobs.delete(jobId);
        console.log(`Unsubscribed from video job ${jobId}`);
      } catch (error) {
        console.error(`Failed to unsubscribe from video job ${jobId}:`, error);
      }
    }
  }

  async getGlobalStatus(): Promise<any> {
    if (!this.isBrowser) return null;
    if (this.hubConnection?.state === signalR.HubConnectionState.Connected) {
      return await this.hubConnection.invoke('GetGlobalStatus');
    }
    return null;
  }

  async stopConnection(): Promise<void> {
    if (this.hubConnection) {
      await this.hubConnection.stop();
      this.subscribedCameras.clear();
      this.subscribedVideoJobs.clear();
      this.isSubscribedToStreamStatus = false;
      this.connectionStateSubject.next(SignalRConnectionState.Disconnected);
      this.connectionStatusSubject.next(false);
    }
  }
}
