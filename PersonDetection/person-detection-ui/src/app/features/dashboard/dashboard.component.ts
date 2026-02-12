// src/app/features/dashboard/dashboard.component.ts
import { Component, OnInit, OnDestroy, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatChipsModule } from '@angular/material/chips';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatBadgeModule } from '@angular/material/badge';
import { Subject, takeUntil, timer } from 'rxjs';  // ✅ timer instead of interval

import { CameraService } from '../../services/camera.service';
import { CameraConfigService } from '../../services/camera-config.service';
import { SignalRService } from '../../services/signalr.service';
import { DetectionService } from '../../services/detection.service';
import { CameraDto, CameraType, DetectionUpdate, GlobalStats } from '../../core/models/detection.models';
import { AddCameraDialogComponent } from '../add-camera-dialog/add-camera-dialog.component';
import { VideoUploadDialogComponent } from '../video-upload-dialog/video-upload-dialog.component';
import { StatsDialogComponent } from '../stats-dialog/stats-dialog.component';

@Component({
    selector: 'app-dashboard',
    standalone: true,
    imports: [
        CommonModule,
        MatCardModule,
        MatButtonModule,
        MatIconModule,
        MatChipsModule,
        MatProgressSpinnerModule,
        MatSnackBarModule,
        MatDialogModule,
        MatMenuModule,
        MatTooltipModule,
        MatProgressBarModule,
        MatBadgeModule
    ],
    templateUrl: './dashboard.component.html',
    styleUrl: './dashboard.component.scss'
})
export class DashboardComponent implements OnInit, OnDestroy {
    private cameraService = inject(CameraService);
    private cameraConfigService = inject(CameraConfigService);
    private signalRService = inject(SignalRService);
    private detectionService = inject(DetectionService);
    private snackBar = inject(MatSnackBar);
    private dialog = inject(MatDialog);
    private router = inject(Router);
    private destroy$ = new Subject<void>();

    // Signals for reactive state
    cameras = signal<CameraDto[]>([]);
    detectionData = signal<Record<number, DetectionUpdate>>({});
    globalStats = signal<GlobalStats | null>(null);
    loadingCameras = signal<Set<number>>(new Set());
    isLoading = signal(true);

    // Computed values
    activeCameras = computed(() => this.cameras().filter(c => c.isActive));

    totalPersons = computed(() => {
        const data = this.detectionData();
        return Object.values(data).reduce((sum, d) => sum + (d.count || 0), 0);
    });

    totalUnique = computed(() => {
        const data = this.detectionData();
        const values = Object.values(data);

        if (values.length > 0) {
            const maxFromSignalR = Math.max(...values.map(d => d.todayUniqueCount || 0));
            if (maxFromSignalR > 0) {
                return maxFromSignalR;
            }
        }

        const stats = this.globalStats();
        return stats?.todayUniqueCount || 0;
    });

    ngOnInit(): void {
        this.loadCameras();
        this.loadGlobalStats();
        this.subscribeToDetections();
    }

    ngOnDestroy(): void {
        this.cameras().filter(c => c.isActive).forEach(camera => {
            this.signalRService.unsubscribeFromCamera(camera.id);
        });

        this.destroy$.next();
        this.destroy$.complete();
    }

    openStatsDialog(): void {
        this.dialog.open(StatsDialogComponent, {
            width: '800px',
            maxHeight: '90vh'
        });
    }

    private loadCameras(): void {
        this.isLoading.set(true);
        this.cameraConfigService.loadCameras()
            .pipe(takeUntil(this.destroy$))
            .subscribe({
                next: (cameras) => {
                    this.cameras.set(cameras);
                    this.isLoading.set(false);
                    this.subscribeToActiveCameras(cameras);
                },
                error: (err) => {
                    console.error('Failed to load cameras:', err);
                    this.isLoading.set(false);
                }
            });
    }

    private loadGlobalStats(): void {
        this.detectionService.getGlobalStats()
            .pipe(takeUntil(this.destroy$))
            .subscribe({
                next: (stats) => {
                    this.globalStats.set(stats);
                    console.log('Global stats loaded:', stats);
                },
                error: (err) => {
                    console.error('Failed to load global stats:', err);
                }
            });
    }

    // ✅ FIXED: Use timer instead of interval
    private startStatsPolling(): void {
        timer(10000, 10000)  // Start after 10s, repeat every 10s
            .pipe(takeUntil(this.destroy$))
            .subscribe(() => {
                this.loadGlobalStats();
            });
    }

    // ... rest of the methods stay the same
    private async subscribeToActiveCameras(cameras: CameraDto[]): Promise<void> {
        const activeCameras = cameras.filter(c => c.isActive);

        for (const camera of activeCameras) {
            try {
                await this.signalRService.subscribeToCamera(camera.id);
                console.log(`Dashboard subscribed to camera ${camera.id}`);
            } catch (error) {
                console.error(`Failed to subscribe to camera ${camera.id}:`, error);
            }
        }
    }

    private subscribeToDetections(): void {
        this.signalRService.detectionUpdate$
            .pipe(takeUntil(this.destroy$))
            .subscribe((update: DetectionUpdate) => {
                console.log('Dashboard received detection update:', update);
                this.detectionData.update(data => ({
                    ...data,
                    [update.cameraId]: update
                }));
            });
    }

    async startCamera(camera: CameraDto): Promise<void> {
        if (this.isLoadingCamera(camera.id)) return;
        this.setLoadingCamera(camera.id, true);

        try {
            await this.cameraService.startCamera({
                cameraId: camera.id,
                url: camera.url
            }).toPromise();

            await this.signalRService.subscribeToCamera(camera.id);

            this.cameras.update(cameras =>
                cameras.map(c =>
                    c.id === camera.id
                        ? { ...c, isActive: true }
                        : c
                )
            );

            this.loadGlobalStats();
            this.showSuccess(`${camera.name} started`);

        } catch (error: any) {
            console.error('Failed to start camera:', error);
            this.showError(error?.error?.message || `Failed to start ${camera.name}`);
            this.loadCameras();
        } finally {
            this.setLoadingCamera(camera.id, false);
        }
        this.loadGlobalStats();
    }

    async stopCamera(camera: CameraDto): Promise<void> {
        if (this.isLoadingCamera(camera.id)) return;
        this.setLoadingCamera(camera.id, true);

        try {
            await this.cameraService.stopCamera(camera.id).toPromise();
            await this.signalRService.unsubscribeFromCamera(camera.id);

            this.cameras.update(cameras =>
                cameras.map(c =>
                    c.id === camera.id
                        ? { ...c, isActive: false }
                        : c
                )
            );

            this.detectionData.update(data => {
                const newData = { ...data };
                delete newData[camera.id];
                return newData;
            });

            this.showSuccess(`${camera.name} stopped`);

        } catch (error: any) {
            console.error('Failed to stop camera:', error);
            this.showError(error?.error?.message || `Failed to stop ${camera.name}`);
            this.loadCameras();
        } finally {
            this.setLoadingCamera(camera.id, false);
        }
        this.loadGlobalStats();
    }

    openAddDialog(): void {
        const dialogRef = this.dialog.open(AddCameraDialogComponent, {
            width: '500px',
            panelClass: 'modern-dialog'
        });

        dialogRef.afterClosed().subscribe(result => {
            if (result) {
                this.cameraConfigService.createCamera(result).subscribe({
                    next: (newCamera) => {
                        this.showSuccess('Camera added successfully');
                        if (newCamera) {
                            this.cameras.update(cameras => [...cameras, newCamera]);
                        } else {
                            this.loadCameras();
                        }
                    },
                    error: (err) => {
                        console.error('Failed to add camera:', err);
                        this.showError('Failed to add camera');
                    }
                });
            }
        });
    }

    deleteCamera(camera: CameraDto, event: Event): void {
        event.stopPropagation();
        if (camera.isActive) {
            this.showError('Stop the camera before deleting');
            return;
        }

        if (confirm(`Delete "${camera.name}"?`)) {
            this.cameraConfigService.deleteCamera(camera.id).subscribe({
                next: () => {
                    this.showSuccess('Camera deleted');
                    this.cameras.update(cameras =>
                        cameras.filter(c => c.id !== camera.id)
                    );
                },
                error: (err) => {
                    console.error('Failed to delete camera:', err);
                    this.showError('Failed to delete camera');
                }
            });
        }
    }

    openVideoUpload(): void {
        const dialogRef = this.dialog.open(VideoUploadDialogComponent, {
            width: '550px',
            panelClass: 'modern-dialog'
        });

        dialogRef.afterClosed().subscribe(result => {
            if (result?.action === 'view') {
                this.router.navigate(['/videos']);
            }
        });
    }

    viewFullScreen(camera: CameraDto): void {
        this.router.navigate(['/camera', camera.id]);
    }

    getStreamUrl(camera: CameraDto): string {
        return this.cameraService.getStreamUrl(camera.id, camera.url);
    }

    getDetection(cameraId: number): DetectionUpdate | undefined {
        return this.detectionData()[cameraId];
    }

    getCameraTypeLabel(type: CameraType): string {
        return this.cameraConfigService.getCameraTypeLabel(type);
    }

    isLoadingCamera(id: number): boolean {
        return this.loadingCameras().has(id);
    }

    private setLoadingCamera(id: number, loading: boolean): void {
        this.loadingCameras.update(set => {
            const newSet = new Set(set);
            if (loading) newSet.add(id);
            else newSet.delete(id);
            return newSet;
        });
    }

    private showSuccess(message: string): void {
        this.snackBar.open(message, '✓', {
            duration: 3000,
            panelClass: 'success-snackbar'
        });
    }

    private showError(message: string): void {
        this.snackBar.open(message, '✕', {
            duration: 5000,
            panelClass: 'error-snackbar'
        });
    }

    trackByCamera(index: number, camera: CameraDto): number {
        return camera.id;
    }
}