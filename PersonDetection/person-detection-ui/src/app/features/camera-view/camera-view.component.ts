import { Component, OnInit, OnDestroy, inject, PLATFORM_ID } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Subject, takeUntil, filter } from 'rxjs';
import { CameraService } from '../../services/camera.service';
import { CameraConfigService } from '../../services/camera-config.service';
import { SignalRService } from '../../services/signalr.service';
import {
  CameraDto,
  DetectionUpdate,
  StreamStatusUpdate,
  StreamConnectionState
} from '../../core/models/detection.models';
import { environment } from '../../../environments/environment';

@Component({
  selector: 'app-camera-view',
  standalone: true,
  imports: [
    CommonModule,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatToolbarModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    MatProgressSpinnerModule
  ],
  templateUrl: './camera-view.component.html',
  styleUrl: './camera-view.component.scss'
})
export class CameraViewComponent implements OnInit, OnDestroy {
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private cameraService = inject(CameraService);
  private cameraConfigService = inject(CameraConfigService);
  private signalRService = inject(SignalRService);
  private platformId = inject(PLATFORM_ID);
  private destroy$ = new Subject<void>();

  cameraId: number = 0;
  camera: CameraDto | null = null;
  streamUrl: string = '';
  currentDetection: DetectionUpdate | null = null;
  isFullscreen = false;

  streamStatus: StreamStatusUpdate | null = null;
  StreamConnectionState = StreamConnectionState;

  lastFrameTime: Date | null = null;
  isStreamStale = false;
  private staleCheckInterval: any;

  ngOnInit(): void {
    this.route.params.pipe(takeUntil(this.destroy$)).subscribe(params => {
      this.cameraId = +params['id'];
      this.loadCamera();
    });

    this.signalRService.detectionUpdate$
      .pipe(
        takeUntil(this.destroy$),
        filter(update => update.cameraId === this.cameraId)
      )
      .subscribe(update => {
        this.currentDetection = update;
        this.lastFrameTime = new Date();
        this.isStreamStale = false;
      });

    this.signalRService.getCameraStatus$(this.cameraId)
      .pipe(takeUntil(this.destroy$))
      .subscribe(status => {
        if (status) {
          this.streamStatus = status;
          this.handleStreamStatusChange(status);
        }
      });

    if (isPlatformBrowser(this.platformId)) {
      this.staleCheckInterval = setInterval(() => {
        this.checkStreamHealth();
      }, environment.stream.healthCheckIntervalMs);

      document.addEventListener('fullscreenchange', this.onFullscreenChange.bind(this));
    }
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();

    if (isPlatformBrowser(this.platformId)) {
      if (this.staleCheckInterval) {
        clearInterval(this.staleCheckInterval);
      }
      document.removeEventListener('fullscreenchange', this.onFullscreenChange.bind(this));
    }

    this.signalRService.unsubscribeFromCamera(this.cameraId);
  }

  loadCamera(): void {
    this.cameraConfigService.loadCameras()
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (cameras) => {
          const camera = cameras.find(c => c.id === this.cameraId);

          if (camera) {
            this.camera = camera;
            this.streamUrl = this.cameraService.getStreamUrl(this.cameraId, camera.url);

            if (camera.isActive) {
              this.signalRService.subscribeToCamera(this.cameraId);
            }
          } else {
            console.error(`Camera ${this.cameraId} not found`);
          }
        },
        error: (err) => {
          console.error('Failed to load camera:', err);
        }
      });
  }

  private handleStreamStatusChange(status: StreamStatusUpdate): void {
    const previousState = this.streamStatus?.state;

    console.log(`Camera ${this.cameraId} status:`, status.stateName, status.stateMessage);

    switch (status.state) {
      case StreamConnectionState.Connected:
        this.isStreamStale = false;

        // AUTO-REFRESH: If we were reconnecting and now connected, refresh the stream
        if (previousState === StreamConnectionState.Reconnecting ||
          previousState === StreamConnectionState.Error) {
          console.log(`Camera ${this.cameraId}: Auto-refreshing stream after reconnection`);
          setTimeout(() => this.refreshStream(), 500); // Small delay to ensure backend is ready
        }
        break;

      case StreamConnectionState.Reconnecting:
        console.warn(`Camera ${this.cameraId} is reconnecting (attempt ${status.reconnectAttempt})`);
        break;

      case StreamConnectionState.Error:
      case StreamConnectionState.Stopped:
        this.isStreamStale = true;
        break;
    }
  }

  private checkStreamHealth(): void {
    if (!this.camera?.isActive) return;

    // Check based on last frame time
    if (this.lastFrameTime) {
      const timeSinceLastFrame = Date.now() - this.lastFrameTime.getTime();

      if (timeSinceLastFrame > environment.stream.staleFrameThresholdMs) {
        if (!this.isStreamStale) {
          console.warn(`Camera ${this.cameraId}: Stream appears stale (no updates for ${timeSinceLastFrame}ms)`);
          this.isStreamStale = true;
        }

        // Auto-refresh if stale for too long (e.g., 2x threshold)
        if (timeSinceLastFrame > environment.stream.staleFrameThresholdMs * 2) {
          console.log(`Camera ${this.cameraId}: Auto-refreshing stale stream`);
          this.refreshStream();
          this.lastFrameTime = new Date(); // Reset to prevent continuous refresh
        }
      }
    }
  }

  get isReconnecting(): boolean {
    return this.streamStatus?.state === StreamConnectionState.Reconnecting;
  }

  get isConnected(): boolean {
    return this.streamStatus?.state === StreamConnectionState.Connected;
  }

  get isError(): boolean {
    return this.streamStatus?.state === StreamConnectionState.Error;
  }

  get showOverlay(): boolean {
    if (!environment.stream.showReconnectingOverlay) return false;
    return this.isReconnecting || this.isError || this.isStreamStale;
  }

  get overlayMessage(): string {
    if (this.isError) {
      return this.streamStatus?.stateMessage || 'Connection Error';
    }
    if (this.isReconnecting) {
      const attempt = this.streamStatus?.reconnectAttempt || 0;
      const max = this.streamStatus?.maxReconnectAttempts || 0;
      return `Reconnecting... (${attempt}/${max})`;
    }
    if (this.isStreamStale) {
      return 'Stream appears stale';
    }
    return '';
  }

  get overlayIcon(): string {
    if (this.isError) return 'error';
    if (this.isReconnecting) return 'sync';
    if (this.isStreamStale) return 'warning';
    return 'info';
  }

  refreshStream(): void {
    if (this.camera) {
      const baseUrl = this.cameraService.getStreamUrl(this.cameraId, this.camera.url);
      this.streamUrl = `${baseUrl}&_t=${Date.now()}`;
    }
  }

  goBack(): void {
    this.router.navigate(['/dashboard']);
  }

  toggleFullscreen(): void {
    if (!isPlatformBrowser(this.platformId)) return;

    const elem = document.documentElement;

    if (!document.fullscreenElement) {
      elem.requestFullscreen?.().catch(err => {
        console.error('Error attempting to enable fullscreen:', err);
      });
    } else {
      document.exitFullscreen?.().catch(err => {
        console.error('Error attempting to exit fullscreen:', err);
      });
    }
  }

  private onFullscreenChange(): void {
    this.isFullscreen = !!document.fullscreenElement;
  }
}
