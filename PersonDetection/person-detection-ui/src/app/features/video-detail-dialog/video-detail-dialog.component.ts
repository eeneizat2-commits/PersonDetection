// src/app/features/video-detail-dialog/video-detail-dialog.component.ts

import { Component, OnInit, OnDestroy, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef, MatDialog } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTabsModule } from '@angular/material/tabs';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { MatChipsModule } from '@angular/material/chips';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDividerModule } from '@angular/material/divider';
import { Subject, takeUntil, interval } from 'rxjs';

import { VideoService } from '../../services/video.service';
import { SignalRService } from '../../services/signalr.service';
import {
  VideoProcessingStatus,
  VideoProcessingSummary,
  VideoProcessingState,
  VideoDetailsResponse,
  PersonTimeline,
  VideoPersonTimelineItem,
  DialogData,
  PersonTimelineDisplay
} from '../../core/models/video.models';
import { VideoPlayerDialogComponent } from '../video-player-dialog/video-player-dialog.component';


@Component({
  selector: 'app-video-detail-dialog',
  standalone: true,
  imports: [
    CommonModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatTabsModule,
    MatProgressSpinnerModule,
    MatProgressBarModule,
    MatTableModule,
    MatChipsModule,
    MatTooltipModule,
    MatDividerModule
  ],
  templateUrl: './video-detail-dialog.component.html',
  styleUrls: ['./video-detail-dialog.component.scss']
})
export class VideoDetailDialogComponent implements OnInit, OnDestroy {
  private dialogRef = inject(MatDialogRef<VideoDetailDialogComponent>);
  private data = inject<DialogData>(MAT_DIALOG_DATA);
  private videoService = inject(VideoService);
  private signalRService = inject(SignalRService);
  private dialog = inject(MatDialog);
  private destroy$ = new Subject<void>();

  status = signal<VideoProcessingStatus | null>(null);
  summary = signal<VideoProcessingSummary | null>(null);
  details = signal<VideoDetailsResponse | null>(null);
  isLoading = signal(true);

  // Real-time progress tracking
  currentProgress = signal(0);
  currentFrames = signal(0);
  totalFrames = signal(0);
  currentDetections = signal(0);
  currentUniquePersons = signal(0);

  VideoProcessingState = VideoProcessingState;

  private videoDuration = 0;

  ngOnInit(): void {
    this.loadData();
    this.subscribeToUpdates();
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  private subscribeToUpdates(): void {
    // Subscribe to real-time progress updates
    this.signalRService.videoProgress$
      .pipe(takeUntil(this.destroy$))
      .subscribe(update => {
        if (update.jobId === this.data.jobId) {
          this.currentProgress.set(update.progress);
          this.currentFrames.set(update.processedFrames);
          this.currentDetections.set(update.totalPersonsDetected);
          this.currentUniquePersons.set(update.uniquePersons);

          // Update status object too
          const currentStatus = this.status();
          if (currentStatus) {
            this.status.set({
              ...currentStatus,
              progressPercent: update.progress,
              processedFrames: update.processedFrames,
              totalPersonsDetected: update.totalPersonsDetected,
              uniquePersonsDetected: update.uniquePersons
            });
          }
        }
      });

    // Subscribe to completion updates
    this.signalRService.videoComplete$
      .pipe(takeUntil(this.destroy$))
      .subscribe(update => {
        if (update.jobId === this.data.jobId) {
          // Reload data when completed
          this.loadData();
        }
      });

    // Also poll for updates every 2 seconds as backup
    if (!this.data.fromHistory) {
      interval(2000)
        .pipe(takeUntil(this.destroy$))
        .subscribe(() => {
          const currentStatus = this.status();
          if (currentStatus && currentStatus.state === VideoProcessingState.Processing) {
            this.refreshStatus();
          }
        });
    }
  }

  private refreshStatus(): void {
    this.videoService.getJobStatus(this.data.jobId).subscribe({
      next: (status) => {
        this.status.set(status);
        this.currentProgress.set(status.progressPercent);
        this.currentFrames.set(status.processedFrames);
        this.totalFrames.set(status.totalFrames);
        this.currentDetections.set(status.totalPersonsDetected);
        this.currentUniquePersons.set(status.uniquePersonsDetected);

        if (status.state === VideoProcessingState.Completed) {
          this.loadSummary();
        }
      }
    });
  }

  private loadData(): void {
    this.isLoading.set(true);

    if (this.data.fromHistory) {
      this.videoService.getVideoDetails(this.data.jobId).subscribe({
        next: (details) => {
          this.details.set(details);
          this.videoDuration = details.videoDurationSeconds;
          this.isLoading.set(false);
        },
        error: (err) => {
          console.error('Failed to load details:', err);
          this.isLoading.set(false);
        }
      });
    } else {
      this.videoService.getJobStatus(this.data.jobId).subscribe({
        next: (status) => {
          this.status.set(status);
          this.currentProgress.set(status.progressPercent);
          this.currentFrames.set(status.processedFrames);
          this.totalFrames.set(status.totalFrames);
          this.currentDetections.set(status.totalPersonsDetected);
          this.currentUniquePersons.set(status.uniquePersonsDetected);
          this.isLoading.set(false);

          if (status.state === VideoProcessingState.Completed) {
            this.loadSummary();
          }
        },
        error: (err) => {
          console.error('Failed to load status:', err);
          this.isLoading.set(false);
        }
      });
    }
  }

  private loadSummary(): void {
    this.videoService.getJobSummary(this.data.jobId).subscribe({
      next: (summary) => {
        this.summary.set(summary);
        if (summary.videoDuration) {
          this.videoDuration = this.parseDuration(summary.videoDuration);
        }
      },
      error: (err) => {
        console.error('Failed to load summary:', err);
      }
    });
  }

  private parseDuration(durationStr: string): number {
    const parts = durationStr.split(':').map(Number);
    if (parts.length === 3) {
      return parts[0] * 3600 + parts[1] * 60 + parts[2];
    }
    return 0;
  }

  // ... rest of the methods remain the same
  // (getPersonTimelines, convertVideoPersonTimeline, etc.)

  getPersonTimelines(): PersonTimelineDisplay[] {
    const detailsData = this.details();
    if (detailsData?.personTimelines && detailsData.personTimelines.length > 0) {
      return detailsData.personTimelines.map(p => this.convertVideoPersonTimeline(p));
    }

    const summaryData = this.summary();
    if (summaryData?.personTimelines && summaryData.personTimelines.length > 0) {
      return summaryData.personTimelines.map(p => this.convertPersonTimeline(p));
    }

    const statusData = this.status();
    if (statusData?.detections && statusData.detections.length > 0) {
      return this.extractTimelinesFromDetections(statusData);
    }

    return [];
  }

  private convertVideoPersonTimeline(item: VideoPersonTimelineItem): PersonTimelineDisplay {
    return {
      globalPersonId: item.globalPersonId,
      shortId: item.shortId,
      firstAppearanceSeconds: item.firstAppearanceSeconds,
      lastAppearanceSeconds: item.lastAppearanceSeconds,
      totalAppearances: item.totalAppearances,
      averageConfidence: item.averageConfidence
    };
  }

  private convertPersonTimeline(item: PersonTimeline): PersonTimelineDisplay {
    return {
      globalPersonId: item.globalPersonId,
      shortId: item.shortId,
      firstAppearanceSeconds: item.firstAppearanceSeconds,
      lastAppearanceSeconds: item.lastAppearanceSeconds,
      totalAppearances: item.totalAppearances,
      averageConfidence: item.averageConfidence
    };
  }

  private extractTimelinesFromDetections(status: VideoProcessingStatus): PersonTimelineDisplay[] {
    const personMap = new Map<string, {
      globalPersonId: string;
      shortId: string;
      firstAppearance: number;
      lastAppearance: number;
      appearances: number;
      confidences: number[];
    }>();

    status.detections?.forEach(detection => {
      detection.persons?.forEach(person => {
        const id = person.globalPersonId;
        if (!id) return;

        const existing = personMap.get(id);

        if (existing) {
          existing.lastAppearance = Math.max(existing.lastAppearance, detection.timestampSeconds);
          existing.firstAppearance = Math.min(existing.firstAppearance, detection.timestampSeconds);
          existing.appearances++;
          existing.confidences.push(person.confidence || 0);
        } else {
          personMap.set(id, {
            globalPersonId: id,
            shortId: id.substring(0, 6),
            firstAppearance: detection.timestampSeconds,
            lastAppearance: detection.timestampSeconds,
            appearances: 1,
            confidences: [person.confidence || 0]
          });
        }
      });
    });

    return Array.from(personMap.values()).map(p => ({
      globalPersonId: p.globalPersonId,
      shortId: p.shortId,
      firstAppearanceSeconds: p.firstAppearance,
      lastAppearanceSeconds: p.lastAppearance,
      totalAppearances: p.appearances,
      averageConfidence: p.confidences.length > 0
        ? p.confidences.reduce((a, b) => a + b, 0) / p.confidences.length
        : 0
    }));
  }

  getPersonThumbnailUrl(globalPersonId: string): string {
    return this.videoService.getPersonThumbnailUrl(this.data.jobId, globalPersonId);
  }

  onThumbnailError(event: Event): void {
    const img = event.target as HTMLImageElement;
    img.style.display = 'none';
    const parent = img.parentElement;
    if (parent) {
      const placeholder = parent.querySelector('.avatar-placeholder') as HTMLElement;
      if (placeholder) {
        placeholder.style.display = 'flex';
      }
    }
  }

  onThumbnailLoad(event: Event): void {
    const img = event.target as HTMLImageElement;
    img.style.display = 'block';
    const parent = img.parentElement;
    if (parent) {
      const placeholder = parent.querySelector('.avatar-placeholder') as HTMLElement;
      if (placeholder) {
        placeholder.style.display = 'none';
      }
    }
  }

  playVideo(): void {
    const fileName = this.details()?.fileName || this.status()?.fileName || 'Video';
    this.dialog.open(VideoPlayerDialogComponent, {
      maxWidth: '95vw',
      maxHeight: '95vh',
      panelClass: 'video-player-dialog-responsive',
      data: {
        jobId: this.data.jobId,
        fileName: fileName,
        videoUrl: this.videoService.getVideoStreamUrl(this.data.jobId)
      }
    });
  }

  getStateLabel(state: VideoProcessingState | string | undefined): string {
    if (state === undefined) return 'Unknown';
    if (typeof state === 'string') return state;
    return this.videoService.getStateLabel(state);
  }

  getStateIcon(state: VideoProcessingState | string | undefined): string {
    const icons: Record<string, string> = {
      'Queued': 'schedule',
      'Processing': 'sync',
      'Completed': 'check_circle',
      'Failed': 'error',
      'Cancelled': 'cancel',
      '0': 'schedule',
      '1': 'sync',
      '2': 'check_circle',
      '3': 'error',
      '4': 'cancelled'
    };
    return state !== undefined ? (icons[state.toString()] || 'help') : 'help';
  }

  getStateClass(state: VideoProcessingState | string | undefined): string {
    const classes: Record<string, string> = {
      'Queued': 'queued',
      'Processing': 'processing',
      'Completed': 'completed',
      'Failed': 'failed',
      'Cancelled': 'cancelled',
      '0': 'queued',
      '1': 'processing',
      '2': 'completed',
      '3': 'failed',
      '4': 'cancelled'
    };
    return state !== undefined ? (classes[state.toString()] || '') : '';
  }

  getPersonColor(shortId: string): string {
    const colors = [
      '#3b82f6', '#ef4444', '#22c55e', '#f59e0b', '#8b5cf6',
      '#ec4899', '#06b6d4', '#84cc16', '#f97316', '#6366f1'
    ];
    let hash = 0;
    for (let i = 0; i < shortId.length; i++) {
      hash = shortId.charCodeAt(i) + ((hash << 5) - hash);
    }
    return colors[Math.abs(hash) % colors.length];
  }

  getPersonColorDark(shortId: string): string {
    const colorMap: Record<string, string> = {
      '#3b82f6': '#1d4ed8',
      '#ef4444': '#b91c1c',
      '#22c55e': '#15803d',
      '#f59e0b': '#b45309',
      '#8b5cf6': '#6d28d9',
      '#ec4899': '#be185d',
      '#06b6d4': '#0e7490',
      '#84cc16': '#4d7c0f',
      '#f97316': '#c2410c',
      '#6366f1': '#4338ca',
    };
    const baseColor = this.getPersonColor(shortId);
    return colorMap[baseColor] || '#1e40af';
  }

  getTimelinePosition(startSeconds: number): number {
    if (this.videoDuration === 0) return 0;
    return (startSeconds / this.videoDuration) * 100;
  }

  getTimelineWidth(startSeconds: number, endSeconds: number): number {
    if (this.videoDuration === 0) return 100;
    const width = ((endSeconds - startSeconds) / this.videoDuration) * 100;
    return Math.max(width, 2);
  }

  formatTime(seconds: number): string {
    const mins = Math.floor(seconds / 60);
    const secs = Math.floor(seconds % 60);
    return `${mins}:${secs.toString().padStart(2, '0')}`;
  }

  formatDuration(seconds: number): string {
    return this.videoService.formatDuration(seconds);
  }

  formatProcessingTime(seconds: number | undefined): string {
    if (!seconds) return '--';
    return this.videoService.formatProcessingTime(seconds);
  }

  exportResults(): void {
    const summaryData = this.summary();
    const statusData = this.status();
    const detailsData = this.details();

    const exportData = {
      jobId: detailsData?.jobId || summaryData?.jobId || statusData?.jobId,
      fileName: detailsData?.fileName || summaryData?.fileName || statusData?.fileName,
      videoDuration: detailsData?.videoDurationSeconds || summaryData?.videoDuration,
      processedAt: new Date().toISOString(),
      statistics: {
        totalFramesProcessed: detailsData?.processedFrames || summaryData?.totalFramesProcessed || statusData?.processedFrames,
        totalPersonsDetected: detailsData?.totalDetections || summaryData?.totalPersonsDetected || statusData?.totalPersonsDetected,
        uniquePersonsIdentified: detailsData?.uniquePersonCount || summaryData?.uniquePersonsIdentified || statusData?.uniquePersonsDetected,
        averagePersonsPerFrame: detailsData?.averagePersonsPerFrame || summaryData?.averagePersonsPerFrame,
        peakPersonCount: detailsData?.peakPersonCount || summaryData?.peakPersonCount,
        processingTimeSeconds: detailsData?.processingTimeSeconds || summaryData?.processingTimeSeconds
      },
      personTimelines: this.getPersonTimelines(),
      detections: statusData?.detections
    };

    const blob = new Blob([JSON.stringify(exportData, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `video-analysis-${(exportData.jobId || 'unknown').substring(0, 8)}.json`;
    a.click();
    URL.revokeObjectURL(url);
  }

  close(): void {
    this.dialogRef.close();
  }
}