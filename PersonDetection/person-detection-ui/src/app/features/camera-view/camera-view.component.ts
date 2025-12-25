// src/app/features/camera-view/camera-view.component.ts
import { Component, OnInit, OnDestroy, inject, PLATFORM_ID } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatToolbarModule } from '@angular/material/toolbar';
import { Subject, takeUntil } from 'rxjs';
import { CameraService } from '../../services/camera.service';
import { CameraConfigService } from '../../services/camera-config.service'; // ✅ Add this
import { SignalRService } from '../../services/signalr.service';
import { CameraDto, DetectionUpdate } from '../../core/models/detection.models';

@Component({
    selector: 'app-camera-view',
    standalone: true,
    imports: [
        CommonModule,
        MatCardModule,
        MatButtonModule,
        MatIconModule,
        MatToolbarModule
    ],
    templateUrl: './camera-view.component.html',
    styleUrl: './camera-view.component.scss'
})
export class CameraViewComponent implements OnInit, OnDestroy {
    private route = inject(ActivatedRoute);
    private router = inject(Router);
    private cameraService = inject(CameraService);
    private cameraConfigService = inject(CameraConfigService); // ✅ Add this
    private signalRService = inject(SignalRService);
    private platformId = inject(PLATFORM_ID);
    private destroy$ = new Subject<void>();

    cameraId: number = 0;
    camera: CameraDto | null = null; // ✅ Store camera info
    streamUrl: string = '';
    currentDetection: DetectionUpdate | null = null;
    isFullscreen = false;

    ngOnInit(): void {
        this.route.params.pipe(takeUntil(this.destroy$)).subscribe(params => {
            this.cameraId = +params['id'];
            this.loadCamera();
        });

        this.signalRService.detectionUpdate$
            .pipe(takeUntil(this.destroy$))
            .subscribe(update => {
                if (update.cameraId === this.cameraId) {
                    this.currentDetection = update;
                }
            });

        // ✅ Listen for fullscreen changes
        if (isPlatformBrowser(this.platformId)) {
            document.addEventListener('fullscreenchange', this.onFullscreenChange.bind(this));
        }
    }

    ngOnDestroy(): void {
        this.destroy$.next();
        this.destroy$.complete();
        
        if (isPlatformBrowser(this.platformId)) {
            document.removeEventListener('fullscreenchange', this.onFullscreenChange.bind(this));
        }
    }

    // ✅ FIXED: Load camera from CameraConfigService
    loadCamera(): void {
        this.cameraConfigService.loadCameras()
            .pipe(takeUntil(this.destroy$))
            .subscribe({
                next: (cameras) => {
                    const camera = cameras.find(c => c.id === this.cameraId);
                    
                    if (camera) {
                        this.camera = camera;
                        this.streamUrl = this.cameraService.getStreamUrl(this.cameraId, camera.url);
                        
                        // Subscribe to SignalR updates for this camera
                        if (camera.isActive) {
                            this.signalRService.subscribeToCamera(this.cameraId);
                        }
                    } else {
                        console.error(`Camera ${this.cameraId} not found`);
                        // Optionally redirect back to dashboard
                        // this.router.navigate(['/dashboard']);
                    }
                },
                error: (err) => {
                    console.error('Failed to load camera:', err);
                }
            });
    }

    goBack(): void {
        this.router.navigate(['/dashboard']);
    }

    // ✅ FIXED: Proper fullscreen toggle
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

    // ✅ Handle fullscreen state change
    private onFullscreenChange(): void {
        this.isFullscreen = !!document.fullscreenElement;
    }
}