// src/app/features/video-upload-dialog/video-upload-dialog.component.ts

import { Component, inject, signal, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { Subject, takeUntil } from 'rxjs';
import { VideoService } from '../../services/video.service';
import { UploadProgress, VideoUploadResult } from '../../core/models/video.models';

@Component({
  selector: 'app-video-upload-dialog',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatProgressBarModule,
    MatTooltipModule,
    MatCheckboxModule
  ],
  templateUrl: './video-upload-dialog.component.html',
  styleUrls: ['./video-upload-dialog.component.scss']
})
export class VideoUploadDialogComponent implements OnDestroy {
  private dialogRef = inject(MatDialogRef<VideoUploadDialogComponent>);
  private videoService = inject(VideoService);
  private destroy$ = new Subject<void>();

  selectedFile = signal<File | null>(null);
  isDragging = signal(false);
  isUploading = signal(false);
  uploadProgress = signal<UploadProgress | null>(null);
  uploadResult = signal<VideoUploadResult | null>(null);
  errorMessage = signal<string | null>(null);

  frameSkip = 5;
  extractFeatures = true;

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  incrementFrameSkip(): void {
    if (this.frameSkip < 30) {
      this.frameSkip++;
    }
  }

  decrementFrameSkip(): void {
    if (this.frameSkip > 1) {
      this.frameSkip--;
    }
  }

  getOrdinalSuffix(n: number): string {
    const s = ['th', 'st', 'nd', 'rd'];
    const v = n % 100;
    return s[(v - 20) % 10] || s[v] || s[0];
  }

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    if (!this.isUploading()) {
      this.isDragging.set(true);
    }
  }

  onDragLeave(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isDragging.set(false);
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isDragging.set(false);

    if (this.isUploading()) return;

    const files = event.dataTransfer?.files;
    if (files && files.length > 0) {
      this.validateAndSetFile(files[0]);
    }
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files && input.files.length > 0) {
      this.validateAndSetFile(input.files[0]);
    }
  }

  private validateAndSetFile(file: File): void {
    const allowedTypes = ['video/mp4', 'video/avi', 'video/quicktime', 'video/x-matroska', 'video/webm', 'video/x-ms-wmv'];
    const allowedExtensions = ['.mp4', '.avi', '.mov', '.mkv', '.webm', '.wmv'];
    const maxSize = 500 * 1024 * 1024;

    const extension = '.' + file.name.split('.').pop()?.toLowerCase();

    if (!allowedTypes.includes(file.type) && !allowedExtensions.includes(extension)) {
      this.errorMessage.set('Invalid file type. Please upload a video file.');
      return;
    }

    if (file.size > maxSize) {
      this.errorMessage.set('File too large. Maximum size is 500 MB.');
      return;
    }

    this.selectedFile.set(file);
    this.errorMessage.set(null);
  }

  clearFile(event: Event): void {
    event.stopPropagation();
    this.resetState();
  }

  private resetState(): void {
    this.selectedFile.set(null);
    this.uploadResult.set(null);
    this.errorMessage.set(null);
    this.uploadProgress.set(null);
    this.isUploading.set(false);
  }

  upload(): void {
    const file = this.selectedFile();
    if (!file || this.isUploading()) return;

    this.isUploading.set(true);
    this.errorMessage.set(null);
    this.uploadResult.set(null);

    // Initialize progress
    this.uploadProgress.set({
      state: 'uploading',
      progress: 0,
      loaded: 0,
      total: file.size
    });

    this.videoService.uploadVideo(file, this.frameSkip, this.extractFeatures)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (result) => {
          // Check if it's a progress update or final result
          if (this.isUploadProgress(result)) {
            // Update progress bar
            this.uploadProgress.set(result);
          } else {
            // Final result - upload complete
            this.uploadProgress.set(null); // Clear progress first
            this.uploadResult.set(result as VideoUploadResult);
            this.isUploading.set(false);
          }
        },
        error: (error) => {
          console.error('Upload failed:', error);
          this.errorMessage.set(error?.error?.error || error?.message || 'Upload failed. Please try again.');
          this.isUploading.set(false);
          this.uploadProgress.set(null);
        }
      });
  }

  // Type guard to check if result is UploadProgress
  private isUploadProgress(result: any): result is UploadProgress {
    return result && 'state' in result && 'progress' in result &&
      (result.state === 'uploading' || result.state === 'processing');
  }
  
  private handleUploadSuccess(result: VideoUploadResult): void {
    // Clear progress first
    this.uploadProgress.set(null);
    // Then set result
    this.uploadResult.set(result);
    // Finally stop uploading state
    this.isUploading.set(false);
  }

  private handleUploadError(error: any): void {
    console.error('Upload failed:', error);
    this.errorMessage.set(error?.error?.error || error?.message || 'Upload failed. Please try again.');
    this.isUploading.set(false);
    this.uploadProgress.set(null);
  }

  viewJob(): void {
    const result = this.uploadResult();
    if (result?.jobId) {
      this.dialogRef.close({ action: 'view', jobId: result.jobId });
    }
  }

  getProgressLabel(): string {
    const progress = this.uploadProgress();
    if (!progress) return 'Preparing...';

    switch (progress.state) {
      case 'uploading':
        return 'Uploading...';
      case 'processing':
        return 'Processing...';
      default:
        return 'Preparing...';
    }
  }

  formatFileSize(bytes: number): string {
    if (!bytes || bytes === 0) return '0 Bytes';
    const k = 1024;
    const sizes = ['Bytes', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  }
}