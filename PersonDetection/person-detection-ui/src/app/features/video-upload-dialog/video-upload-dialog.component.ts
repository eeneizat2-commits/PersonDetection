// src/app/features/video-upload-dialog/video-upload-dialog.component.ts

import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSliderModule } from '@angular/material/slider';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatTooltipModule } from '@angular/material/tooltip';
import { VideoService, UploadProgress } from '../../services/video.service';
import { VideoUploadResult } from '../../core/models/video.models';

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
    MatFormFieldModule,
    MatInputModule,
    MatSliderModule,
    MatCheckboxModule,
    MatTooltipModule
  ],
  templateUrl: './video-upload-dialog.component.html',
  styleUrls: ['./video-upload-dialog.component.scss']
})
export class VideoUploadDialogComponent {
  private dialogRef = inject(MatDialogRef<VideoUploadDialogComponent>);
  private videoService = inject(VideoService);

  selectedFile = signal<File | null>(null);
  isDragging = signal(false);
  isUploading = signal(false);
  uploadProgress = signal<UploadProgress | null>(null);
  uploadResult = signal<VideoUploadResult | null>(null);
  errorMessage = signal<string | null>(null);

  frameSkip = 5;
  extractFeatures = true;

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isDragging.set(true);
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
    const maxSize = 500 * 1024 * 1024; // 500 MB

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
    this.selectedFile.set(null);
    this.uploadResult.set(null);
    this.errorMessage.set(null);
    this.uploadProgress.set(null);
  }

  upload(): void {
    const file = this.selectedFile();
    if (!file) return;

    this.isUploading.set(true);
    this.errorMessage.set(null);
    this.uploadResult.set(null);

    this.videoService.uploadVideo(file, this.frameSkip, this.extractFeatures).subscribe({
      next: (result) => {
        if ('progress' in result) {
          this.uploadProgress.set(result as UploadProgress);
        } else {
          this.uploadResult.set(result as VideoUploadResult);
          this.isUploading.set(false);
        }
      },
      error: (error) => {
        console.error('Upload failed:', error);
        this.errorMessage.set(error?.error?.error || 'Upload failed. Please try again.');
        this.isUploading.set(false);
        this.uploadProgress.set(null);
      }
    });
  }

  viewJob(): void {
    const result = this.uploadResult();
    if (result?.jobId) {
      this.dialogRef.close({ action: 'view', jobId: result.jobId });
    }
  }

  getProgressLabel(): string {
    const state = this.uploadProgress()?.state;
    if (state === 'uploading') return 'Uploading...';
    if (state === 'processing') return 'Processing queued...';
    return 'Upload';
  }

  formatFileSize(bytes: number): string {
    if (bytes === 0) return '0 Bytes';
    const k = 1024;
    const sizes = ['Bytes', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  }
}