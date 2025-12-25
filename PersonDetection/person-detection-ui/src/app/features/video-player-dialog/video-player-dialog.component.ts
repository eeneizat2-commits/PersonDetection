// src/app/features/video-player-dialog/video-player-dialog.component.ts

import { Component, inject, OnInit, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { DomSanitizer, SafeUrl } from '@angular/platform-browser';

export interface VideoPlayerDialogData {
    jobId: string;
    fileName: string;
    videoUrl: string;
}

@Component({
    selector: 'app-video-player-dialog',
    standalone: true,
    imports: [
        CommonModule,
        MatDialogModule,
        MatButtonModule,
        MatIconModule,
        MatProgressSpinnerModule
    ],
    templateUrl: './video-player-dialog.component.html',
    styleUrls: ['./video-player-dialog.component.scss']
})
export class VideoPlayerDialogComponent implements OnInit, OnDestroy {
    private dialogRef = inject(MatDialogRef<VideoPlayerDialogComponent>);
    private data = inject<VideoPlayerDialogData>(MAT_DIALOG_DATA);
    private sanitizer = inject(DomSanitizer);

    videoUrl: SafeUrl | null = null;
    fileName = signal('');
    isLoading = signal(true);
    hasError = signal(false);
    errorMessage = signal('');

    ngOnInit(): void {
        this.fileName.set(this.data.fileName);
        this.videoUrl = this.sanitizer.bypassSecurityTrustUrl(this.data.videoUrl);
    }

    ngOnDestroy(): void {
        // Cleanup if needed
    }

    onVideoLoaded(): void {
        this.isLoading.set(false);
    }

    onVideoError(event: Event): void {
        this.isLoading.set(false);
        this.hasError.set(true);
        this.errorMessage.set('Failed to load video. The file may not exist or is not accessible.');
    }

    close(): void {
        this.dialogRef.close();
    }

    downloadVideo(): void {
        const link = document.createElement('a');
        link.href = this.data.videoUrl;
        link.download = this.data.fileName;
        link.click();
    }
}