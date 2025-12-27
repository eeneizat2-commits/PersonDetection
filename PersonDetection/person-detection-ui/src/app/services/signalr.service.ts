// src/app/services/signalr.service.ts

import { Injectable, PLATFORM_ID, inject } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { BehaviorSubject, Subject } from 'rxjs';
import * as signalR from '@microsoft/signalr';
import { environment } from '../../environments/environment';
import { DetectionUpdate } from '../core/models/detection.models';
import { VideoProgressUpdate, VideoCompleteUpdate, NewVideoJobUpdate } from '../core/models/video.models';


@Injectable({
    providedIn: 'root'
})
export class SignalRService {
    private platformId = inject(PLATFORM_ID);
    private hubConnection: signalR.HubConnection | null = null;

    private connectionStatusSubject = new BehaviorSubject<boolean>(false);
    connectionStatus$ = this.connectionStatusSubject.asObservable();

    private detectionUpdateSubject = new Subject<DetectionUpdate>();
    detectionUpdate$ = this.detectionUpdateSubject.asObservable();

    // Video processing updates
    private videoProgressSubject = new Subject<VideoProgressUpdate>();
    videoProgress$ = this.videoProgressSubject.asObservable();

    private videoCompleteSubject = new Subject<VideoCompleteUpdate>();
    videoComplete$ = this.videoCompleteSubject.asObservable();

    // 🆕 New video job created
    private newVideoJobSubject = new Subject<NewVideoJobUpdate>();
    newVideoJob$ = this.newVideoJobSubject.asObservable();

    private subscribedCameras = new Set<number>();
    private subscribedVideoJobs = new Set<string>();

    private get isBrowser(): boolean {
        return isPlatformBrowser(this.platformId);
    }

    async startConnection(): Promise<void> {
        if (!this.isBrowser) {
            console.log('SignalR: Skipping connection on server');
            return;
        }

        if (this.hubConnection?.state === signalR.HubConnectionState.Connected) {
            return;
        }

        this.hubConnection = new signalR.HubConnectionBuilder()
            .withUrl(environment.signalRUrl, {
                skipNegotiation: true,
                transport: signalR.HttpTransportType.WebSockets
            })
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .configureLogging(signalR.LogLevel.Information)
            .build();

        // Camera detection updates
        this.hubConnection.on('DetectionUpdate', (data: DetectionUpdate) => {
            console.log('Detection update received:', data);
            this.detectionUpdateSubject.next(data);
        });

        // Video processing progress
        this.hubConnection.on('VideoProcessingProgress', (data: VideoProgressUpdate) => {
            console.log('Video progress update:', data);
            this.videoProgressSubject.next(data);
        });

        // Video processing complete
        this.hubConnection.on('VideoProcessingComplete', (data: VideoCompleteUpdate) => {
            console.log('Video processing complete:', data);
            this.videoCompleteSubject.next(data);
        });

        // 🆕 New video job created
        this.hubConnection.on('NewVideoJobCreated', (data: NewVideoJobUpdate) => {
            console.log('New video job created:', data);
            this.newVideoJobSubject.next(data);
        });

        this.hubConnection.onreconnecting((error) => {
            console.log('SignalR reconnecting...', error);
            this.connectionStatusSubject.next(false);
        });

        this.hubConnection.onreconnected((connectionId) => {
            console.log('SignalR reconnected:', connectionId);
            this.connectionStatusSubject.next(true);
            this.subscribedCameras.forEach(id => this.subscribeToCamera(id));
            this.subscribedVideoJobs.forEach(id => this.subscribeToVideoJob(id));
        });

        this.hubConnection.onclose((error) => {
            console.log('SignalR connection closed:', error);
            this.connectionStatusSubject.next(false);
        });

        try {
            await this.hubConnection.start();
            console.log('SignalR connected successfully');
            this.connectionStatusSubject.next(true);
        } catch (error) {
            console.error('SignalR connection error:', error);
            this.connectionStatusSubject.next(false);
            setTimeout(() => this.startConnection(), 5000);
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
        }
    }
}