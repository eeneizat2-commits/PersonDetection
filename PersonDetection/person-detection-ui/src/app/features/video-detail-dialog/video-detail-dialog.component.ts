// src/app/features/video-detail-dialog/video-detail-dialog.component.ts

import { Component, OnInit, inject, signal } from '@angular/core';
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

import { VideoService } from '../../services/video.service';
import {
  VideoProcessingStatus,
  VideoProcessingSummary,
  VideoProcessingState,
  VideoDetailsResponse,
  VideoPersonTimelineItem
} from '../../core/models/video.models';
import { VideoPlayerDialogComponent } from '../video-player-dialog/video-player-dialog.component';

interface DialogData {
  jobId: string;
  fromHistory?: boolean;
}

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
export class VideoDetailDialogComponent implements OnInit {
  private dialogRef = inject(MatDialogRef<VideoDetailDialogComponent>);
  private data = inject<DialogData>(MAT_DIALOG_DATA);
  private videoService = inject(VideoService);
  private dialog = inject(MatDialog);

  status = signal<VideoProcessingStatus | null>(null);
  summary = signal<VideoProcessingSummary | null>(null);
  details = signal<VideoDetailsResponse | null>(null);
  isLoading = signal(true);

  VideoProcessingState = VideoProcessingState;

  private videoDuration = 0;

  ngOnInit(): void {
    this.loadData();
  }

  private loadData(): void {
    this.isLoading.set(true);

    // If from history, load from database
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
      // Load from in-memory status
      this.videoService.getJobStatus(this.data.jobId).subscribe({
        next: (status) => {
          this.status.set(status);
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

  playVideo(): void {
    const fileName = this.details()?.fileName || this.status()?.fileName || 'Video';
    this.dialog.open(VideoPlayerDialogComponent, {
      width: '900px',
      maxHeight: '90vh',
      data: {
        jobId: this.data.jobId,
        fileName: fileName,
        videoUrl: this.videoService.getVideoStreamUrl(this.data.jobId)
      },
      panelClass: 'video-player-dialog'
    });
  }

  getPersonThumbnailUrl(globalPersonId: string): string {
    return this.videoService.getPersonThumbnailUrl(this.data.jobId, globalPersonId);
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
      '4': 'cancel'
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

  getTimelinePosition(startSeconds: number): number {
    if (this.videoDuration === 0) return 0;
    return (startSeconds / this.videoDuration) * 100;
  }

  getTimelineWidth(startSeconds: number, endSeconds: number): number {
    if (this.videoDuration === 0) return 100;
    const width = ((endSeconds - startSeconds) / this.videoDuration) * 100;
    return Math.max(width, 2); // Minimum 2% width for visibility
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
        averagePersonsPerFrame: summaryData?.averagePersonsPerFrame,
        peakPersonCount: summaryData?.peakPersonCount,
        processingTimeSeconds: detailsData?.processingTimeSeconds || summaryData?.processingTimeSeconds
      },
      personTimelines: detailsData?.personTimelines || summaryData?.personTimelines,
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