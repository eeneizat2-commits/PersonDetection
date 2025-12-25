// src/app/features/video-jobs/video-jobs.component.ts

import { Component, OnInit, OnDestroy, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatChipsModule } from '@angular/material/chips';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatTabsModule } from '@angular/material/tabs';
import { Subject, takeUntil } from 'rxjs';

import { VideoService } from '../../services/video.service';
import { SignalRService } from '../../services/signalr.service';
import {
  VideoProcessingStatus,
  VideoProcessingState,
  VideoHistoryItem,
  VideoHistoryResponse
} from '../../core/models/video.models';
import { VideoUploadDialogComponent } from '../video-upload-dialog/video-upload-dialog.component';
import { VideoDetailDialogComponent } from '../video-detail-dialog/video-detail-dialog.component';
import { VideoPlayerDialogComponent } from '../video-player-dialog/video-player-dialog.component';

@Component({
  selector: 'app-video-jobs',
  standalone: true,
  imports: [
    CommonModule,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatProgressBarModule,
    MatProgressSpinnerModule,
    MatChipsModule,
    MatMenuModule,
    MatTooltipModule,
    MatSnackBarModule,
    MatDialogModule,
    MatPaginatorModule,
    MatTabsModule
  ],
  templateUrl: './video-jobs.component.html',
  styleUrls: ['./video-jobs.component.scss']
})
export class VideoJobsComponent implements OnInit, OnDestroy {
  private videoService = inject(VideoService);
  private signalRService = inject(SignalRService);
  private snackBar = inject(MatSnackBar);
  private dialog = inject(MatDialog);
  private router = inject(Router);
  private destroy$ = new Subject<void>();

  // Active jobs (in-memory)
  jobs = signal<VideoProcessingStatus[]>([]);
  isLoading = signal(true);

  // Video history (from database)
  videoHistory = signal<VideoHistoryItem[]>([]);
  historyLoading = signal(false);
  historyTotal = signal(0);
  historyPage = signal(1);
  historyPageSize = signal(10);

  VideoProcessingState = VideoProcessingState;
  Math = Math;

  // Computed values
  processingCount = computed(() =>
    this.jobs().filter(j => j.state === VideoProcessingState.Processing || j.state === VideoProcessingState.Queued).length
  );
  completedCount = computed(() =>
    this.jobs().filter(j => j.state === VideoProcessingState.Completed).length
  );
  totalUniquePersons = computed(() =>
    this.jobs().reduce((sum, j) => sum + (j.uniquePersonsDetected || 0), 0)
  );
  totalHistoryPersons = computed(() =>
    this.videoHistory().reduce((sum, v) => sum + (v.uniquePersonCount || 0), 0)
  );

  ngOnInit(): void {
    this.loadJobs();
    this.loadVideoHistory();
    this.subscribeToUpdates();
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  private loadJobs(): void {
    this.isLoading.set(true);
    this.videoService.getAllJobs()
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (jobs) => {
          this.jobs.set(jobs);
          this.isLoading.set(false);
        },
        error: (err) => {
          console.error('Failed to load jobs:', err);
          this.isLoading.set(false);
          this.showError('Failed to load video jobs');
        }
      });
  }

  loadVideoHistory(): void {
    this.historyLoading.set(true);
    this.videoService.getVideoHistory(this.historyPage(), this.historyPageSize())
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (response: VideoHistoryResponse) => {
          this.videoHistory.set(response.items);
          this.historyTotal.set(response.total);
          this.historyLoading.set(false);
        },
        error: (err) => {
          console.error('Failed to load video history:', err);
          this.historyLoading.set(false);
          this.showError('Failed to load video history');
        }
      });
  }

  onHistoryPageChange(event: PageEvent): void {
    this.historyPage.set(event.pageIndex + 1);
    this.historyPageSize.set(event.pageSize);
    this.loadVideoHistory();
  }

  private subscribeToUpdates(): void {
    this.signalRService.videoProgress$
      .pipe(takeUntil(this.destroy$))
      .subscribe(update => {
        this.jobs.update(jobs => jobs.map(job =>
          job.jobId === update.jobId
            ? {
              ...job,
              processedFrames: update.processedFrames,
              progressPercent: update.progress,
              totalPersonsDetected: update.totalPersonsDetected,
              uniquePersonsDetected: update.uniquePersons
            }
            : job
        ));
      });

    this.signalRService.videoComplete$
      .pipe(takeUntil(this.destroy$))
      .subscribe(update => {
        this.showSuccess(`Video "${update.fileName}" processing completed!`);
        this.loadJobs();
        this.loadVideoHistory(); // Refresh history too
      });
  }

  openUploadDialog(): void {
    const dialogRef = this.dialog.open(VideoUploadDialogComponent, {
      width: '550px',
      panelClass: 'modern-dialog'
    });

    dialogRef.afterClosed().subscribe(result => {
      if (result?.action === 'view' && result.jobId) {
        this.loadJobs();
      } else if (result) {
        this.loadJobs();
      }
    });
  }

  viewDetails(job: VideoProcessingStatus): void {
    this.dialog.open(VideoDetailDialogComponent, {
      width: '900px',
      maxHeight: '90vh',
      data: { jobId: job.jobId },
      panelClass: 'modern-dialog'
    });
  }

  viewHistoryDetails(video: VideoHistoryItem): void {
    this.dialog.open(VideoDetailDialogComponent, {
      width: '900px',
      maxHeight: '90vh',
      data: { jobId: video.jobId, fromHistory: true },
      panelClass: 'modern-dialog'
    });
  }

  playVideo(video: VideoHistoryItem): void {
    this.dialog.open(VideoPlayerDialogComponent, {
      width: '900px',
      maxHeight: '90vh',
      data: {
        jobId: video.jobId,
        fileName: video.fileName,
        videoUrl: this.videoService.getVideoStreamUrl(video.jobId)
      },
      panelClass: 'video-player-dialog'
    });
  }

  cancelJob(job: VideoProcessingStatus): void {
    if (confirm(`Cancel processing for "${job.fileName}"?`)) {
      this.videoService.cancelJob(job.jobId).subscribe({
        next: () => {
          this.showSuccess('Job cancelled');
          this.loadJobs();
        },
        error: () => this.showError('Failed to cancel job')
      });
    }
  }

  deleteJob(job: VideoProcessingStatus): void {
    if (confirm(`Delete job "${job.fileName}"? This cannot be undone.`)) {
      this.videoService.deleteJob(job.jobId).subscribe({
        next: () => {
          this.showSuccess('Job deleted');
          this.jobs.update(jobs => jobs.filter(j => j.jobId !== job.jobId));
          this.loadVideoHistory();
        },
        error: () => this.showError('Failed to delete job')
      });
    }
  }

  getStateLabel(state: VideoProcessingState): string {
    return this.videoService.getStateLabel(state);
  }

  getStateIcon(state: VideoProcessingState | string): string {
    const stateMap: Record<string, string> = {
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
    return stateMap[state.toString()] || 'help';
  }

  getStateClass(state: VideoProcessingState | string): string {
    const classMap: Record<string, string> = {
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
    return classMap[state.toString()] || '';
  }

  formatDuration(seconds: number): string {
    return this.videoService.formatDuration(seconds);
  }

  formatProcessingTime(seconds: number): string {
    return this.videoService.formatProcessingTime(seconds);
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
}