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
import Swal from 'sweetalert2';

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

  jobs = signal<VideoProcessingStatus[]>([]);
  isLoading = signal(true);

  videoHistory = signal<VideoHistoryItem[]>([]);
  historyLoading = signal(false);
  historyTotal = signal(0);
  historyPage = signal(1);
  historyPageSize = signal(10);

  VideoProcessingState = VideoProcessingState;
  Math = Math;

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

  // In video-jobs.component.ts - Update subscribeToUpdates method

  private subscribeToUpdates(): void {
    // Progress updates
    this.signalRService.videoProgress$
      .pipe(takeUntil(this.destroy$))
      .subscribe(update => {
        this.jobs.update(jobs => {
          const existingJob = jobs.find(j => j.jobId === update.jobId);

          if (existingJob) {
            return jobs.map(job =>
              job.jobId === update.jobId
                ? {
                  ...job,
                  processedFrames: update.processedFrames,
                  progressPercent: update.progress,
                  totalPersonsDetected: update.totalPersonsDetected,
                  uniquePersonsDetected: update.uniquePersons
                }
                : job
            );
          } else {
            // New job via progress - reload
            this.loadJobs();
            return jobs;
          }
        });
      });

    // Completion updates
    this.signalRService.videoComplete$
      .pipe(takeUntil(this.destroy$))
      .subscribe(update => {
        Swal.fire({
          title: 'Processing Complete!',
          text: `"${update.fileName}" has finished processing.`,
          icon: 'success',
          timer: 3000,
          showConfirmButton: false,
          toast: true,
          position: 'top-end'
        });
        this.loadJobs();
        this.loadVideoHistory();
      });

    // 🆕 New video job created
    this.signalRService.newVideoJob$
      .pipe(takeUntil(this.destroy$))
      .subscribe(newJob => {
        Swal.fire({
          title: 'New Job Started',
          text: `"${newJob.fileName}" has been queued for processing.`,
          icon: 'info',
          timer: 2500,
          showConfirmButton: false,
          toast: true,
          position: 'top-end'
        });
        this.loadJobs();
      });
  }

  openUploadDialog(): void {
    const dialogRef = this.dialog.open(VideoUploadDialogComponent, {
      width: '420px',
      maxWidth: '95vw',
      maxHeight: '90vh',
      panelClass: 'upload-dialog-panel',
      disableClose: false
    });

    dialogRef.afterClosed().subscribe(result => {
      this.loadJobs();
      this.loadVideoHistory();

      if (result?.action === 'view' && result.jobId) {
        setTimeout(() => {
          this.loadJobs();
          this.viewDetails({ jobId: result.jobId } as VideoProcessingStatus);
        }, 500);
      }
    });
  }

  viewDetails(job: VideoProcessingStatus): void {
    this.dialog.open(VideoDetailDialogComponent, {
      width: '95vw',
      maxWidth: '1000px',
      maxHeight: '95vh',
      panelClass: 'video-detail-dialog-panel',
      data: { jobId: job.jobId }
    });
  }

  viewHistoryDetails(video: VideoHistoryItem): void {
    this.dialog.open(VideoDetailDialogComponent, {
      width: '95vw',
      maxWidth: '1000px',
      maxHeight: '95vh',
      panelClass: 'video-detail-dialog-panel',
      data: { jobId: video.jobId, fromHistory: true }
    });
  }

  playVideo(video: VideoHistoryItem): void {
    this.dialog.open(VideoPlayerDialogComponent, {
      maxWidth: '95vw',
      maxHeight: '95vh',
      panelClass: 'video-player-dialog-responsive',
      data: {
        jobId: video.jobId,
        fileName: video.fileName,
        videoUrl: this.videoService.getVideoStreamUrl(video.jobId)
      }
    });
  }

  // Cancel Job with SweetAlert
  cancelJob(job: VideoProcessingStatus): void {
    Swal.fire({
      title: 'Cancel Processing?',
      text: `Are you sure you want to cancel "${job.fileName}"?`,
      icon: 'warning',
      showCancelButton: true,
      confirmButtonColor: '#f59e0b',
      cancelButtonColor: '#64748b',
      confirmButtonText: 'Yes, cancel it',
      cancelButtonText: 'No, keep it',
      background: '#ffffff',
      customClass: {
        popup: 'swal-popup',
        title: 'swal-title',
        confirmButton: 'swal-confirm-btn',
        cancelButton: 'swal-cancel-btn'
      }
    }).then((result) => {
      if (result.isConfirmed) {
        this.videoService.cancelJob(job.jobId).subscribe({
          next: () => {
            Swal.fire({
              title: 'Cancelled!',
              text: 'Job has been cancelled.',
              icon: 'success',
              timer: 1500,
              showConfirmButton: false
            });
            this.loadJobs();
          },
          error: () => {
            Swal.fire({
              title: 'Error',
              text: 'Failed to cancel job',
              icon: 'error'
            });
          }
        });
      }
    });
  }

  // Delete Job with SweetAlert
  deleteJob(job: VideoProcessingStatus): void {
    Swal.fire({
      title: 'Delete Job?',
      html: `
        <div style="text-align: left; padding: 10px 0;">
          <p style="margin: 0 0 8px; color: #64748b;">This will permanently delete:</p>
          <ul style="margin: 0; padding-left: 20px; color: #1e293b;">
            <li><strong>${job.fileName}</strong></li>
            <li>All detection data</li>
            <li>Person thumbnails</li>
          </ul>
        </div>
      `,
      icon: 'warning',
      showCancelButton: true,
      confirmButtonColor: '#ef4444',
      cancelButtonColor: '#64748b',
      confirmButtonText: '<i class="fa fa-trash"></i> Yes, delete it',
      cancelButtonText: 'Cancel',
      background: '#ffffff',
      customClass: {
        popup: 'swal-popup',
        title: 'swal-title',
        htmlContainer: 'swal-html',
        confirmButton: 'swal-delete-btn',
        cancelButton: 'swal-cancel-btn'
      }
    }).then((result) => {
      if (result.isConfirmed) {
        this.videoService.deleteJob(job.jobId).subscribe({
          next: () => {
            Swal.fire({
              title: 'Deleted!',
              text: 'Job has been deleted.',
              icon: 'success',
              timer: 1500,
              showConfirmButton: false,
              background: '#ffffff'
            });
            this.jobs.update(jobs => jobs.filter(j => j.jobId !== job.jobId));
            this.loadVideoHistory();
          },
          error: () => {
            Swal.fire({
              title: 'Error',
              text: 'Failed to delete job',
              icon: 'error',
              background: '#ffffff'
            });
          }
        });
      }
    });
  }

  // Delete History Item with SweetAlert
  deleteHistoryItem(video: VideoHistoryItem): void {
    Swal.fire({
      title: 'Delete Video?',
      html: `
        <div style="text-align: left; padding: 10px 0;">
          <p style="margin: 0 0 8px; color: #64748b;">This will permanently delete:</p>
          <ul style="margin: 0; padding-left: 20px; color: #1e293b;">
            <li><strong>${video.fileName}</strong></li>
            <li>Video file</li>
            <li>All detection data</li>
            <li>Person thumbnails</li>
          </ul>
        </div>
      `,
      icon: 'warning',
      showCancelButton: true,
      confirmButtonColor: '#ef4444',
      cancelButtonColor: '#64748b',
      confirmButtonText: 'Yes, delete it',
      cancelButtonText: 'Cancel',
      background: '#ffffff'
    }).then((result) => {
      if (result.isConfirmed) {
        this.videoService.deleteJob(video.jobId).subscribe({
          next: () => {
            Swal.fire({
              title: 'Deleted!',
              text: 'Video has been deleted.',
              icon: 'success',
              timer: 1500,
              showConfirmButton: false
            });
            this.loadVideoHistory();
            this.loadJobs();
          },
          error: () => {
            Swal.fire({
              title: 'Error',
              text: 'Failed to delete video',
              icon: 'error'
            });
          }
        });
      }
    });
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