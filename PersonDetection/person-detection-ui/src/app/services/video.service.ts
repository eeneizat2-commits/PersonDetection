// src/app/services/video.service.ts

import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpEvent, HttpEventType, HttpRequest } from '@angular/common/http';
import { Observable, BehaviorSubject, Subject, map, tap, catchError } from 'rxjs';
import { environment } from '../../environments/environment';
import {
    VideoUploadResult,
    VideoProcessingStatus,
    VideoProcessingSummary,
    VideoProgressUpdate,
    VideoCompleteUpdate,
    VideoProcessingState,
    VideoHistoryResponse,
    VideoDetailsResponse
} from '../core/models/video.models';

export interface UploadProgress {
    progress: number;
    loaded: number;
    total: number;
    state: 'uploading' | 'processing' | 'done' | 'error';
}

@Injectable({ providedIn: 'root' })
export class VideoService {
    private http = inject(HttpClient);
    private baseUrl = `${environment.apiUrl}/Video`;

    // Track all jobs
    private jobsSubject = new BehaviorSubject<Map<string, VideoProcessingStatus>>(new Map());
    jobs$ = this.jobsSubject.asObservable();

    // Upload progress tracking
    private uploadProgressSubject = new BehaviorSubject<Map<string, UploadProgress>>(new Map());
    uploadProgress$ = this.uploadProgressSubject.asObservable();

    // Real-time updates from SignalR
    private progressUpdateSubject = new Subject<VideoProgressUpdate>();
    progressUpdate$ = this.progressUpdateSubject.asObservable();

    private completeUpdateSubject = new Subject<VideoCompleteUpdate>();
    completeUpdate$ = this.completeUpdateSubject.asObservable();

    /**
     * Upload a video file with progress tracking
     */
    uploadVideo(
        file: File,
        frameSkip: number = 5,
        extractFeatures: boolean = true
    ): Observable<VideoUploadResult | UploadProgress> {
        const formData = new FormData();
        formData.append('file', file, file.name);

        const uploadId = crypto.randomUUID();

        const req = new HttpRequest(
            'POST',
            `${this.baseUrl}/upload?frameSkip=${frameSkip}&extractFeatures=${extractFeatures}`,
            formData,
            {
                reportProgress: true
            }
        );

        return this.http.request<VideoUploadResult>(req).pipe(
            map((event: HttpEvent<VideoUploadResult>) => {
                switch (event.type) {
                    case HttpEventType.UploadProgress:
                        const progress: UploadProgress = {
                            progress: event.total ? Math.round((100 * event.loaded) / event.total) : 0,
                            loaded: event.loaded,
                            total: event.total || 0,
                            state: 'uploading'
                        };
                        this.updateUploadProgress(uploadId, progress);
                        return progress;

                    case HttpEventType.Response:
                        const result = event.body as VideoUploadResult;
                        this.updateUploadProgress(uploadId, {
                            progress: 100,
                            loaded: file.size,
                            total: file.size,
                            state: 'processing'
                        });

                        if (result?.jobId) {
                            this.startStatusPolling(result.jobId);
                        }

                        return result;

                    default:
                        return {
                            progress: 0,
                            loaded: 0,
                            total: file.size,
                            state: 'uploading' as const
                        };
                }
            }),
            catchError(error => {
                this.updateUploadProgress(uploadId, {
                    progress: 0,
                    loaded: 0,
                    total: 0,
                    state: 'error'
                });
                throw error;
            })
        );
    }

    /**
     * Get status of a video processing job
     */
    getJobStatus(jobId: string): Observable<VideoProcessingStatus> {
        return this.http.get<VideoProcessingStatus>(`${this.baseUrl}/${jobId}/status`).pipe(
            tap(status => this.updateJobStatus(jobId, status))
        );
    }

    /**
     * Get detailed summary after processing completes
     */
    getJobSummary(jobId: string): Observable<VideoProcessingSummary> {
        return this.http.get<VideoProcessingSummary>(`${this.baseUrl}/${jobId}/summary`);
    }

    /**
     * Get all video processing jobs (in-memory)
     */
    getAllJobs(): Observable<VideoProcessingStatus[]> {
        return this.http.get<VideoProcessingStatus[]>(`${this.baseUrl}/jobs`).pipe(
            tap(jobs => {
                const jobMap = new Map<string, VideoProcessingStatus>();
                jobs.forEach(job => jobMap.set(job.jobId, job));
                this.jobsSubject.next(jobMap);
            })
        );
    }

    // ========== NEW: Video History API Methods ==========

    /**
     * Get video history from database with pagination
     */
    getVideoHistory(page: number = 1, pageSize: number = 10): Observable<VideoHistoryResponse> {
        return this.http.get<VideoHistoryResponse>(`${this.baseUrl}/history`, {
            params: {
                page: page.toString(),
                pageSize: pageSize.toString()
            }
        });
    }

    /**
     * Get video details from database
     */
    getVideoDetails(jobId: string): Observable<VideoDetailsResponse> {
        return this.http.get<VideoDetailsResponse>(`${this.baseUrl}/${jobId}/details`);
    }

    /**
     * Get video stream URL
     */
    getVideoStreamUrl(jobId: string): string {
        return `${this.baseUrl}/${jobId}/video`;
    }

    /**
     * Get person thumbnail URL
     */
    getPersonThumbnailUrl(jobId: string, globalPersonId: string): string {
        return `${this.baseUrl}/${jobId}/person/${globalPersonId}/thumbnail`;
    }

    /**
     * Check if video file exists
     */
    checkVideoExists(jobId: string): Observable<boolean> {
        return this.http.get<VideoDetailsResponse>(`${this.baseUrl}/${jobId}/details`).pipe(
            map(details => details.videoFileExists),
            catchError(() => [false])
        );
    }

    // ========== END NEW ==========

    /**
     * Cancel a processing job
     */
    cancelJob(jobId: string): Observable<any> {
        return this.http.post(`${this.baseUrl}/${jobId}/cancel`, {}).pipe(
            tap(() => {
                const jobs = this.jobsSubject.value;
                const job = jobs.get(jobId);
                if (job) {
                    job.state = VideoProcessingState.Cancelled;
                    this.jobsSubject.next(new Map(jobs));
                }
            })
        );
    }

    /**
     * Delete a completed job
     */
    deleteJob(jobId: string): Observable<any> {
        return this.http.delete(`${this.baseUrl}/${jobId}`).pipe(
            tap(() => {
                const jobs = this.jobsSubject.value;
                jobs.delete(jobId);
                this.jobsSubject.next(new Map(jobs));
            })
        );
    }

    /**
     * Get detections for specific frame range
     */
    getDetections(jobId: string, startFrame: number = 0, endFrame: number = 999999, limit: number = 100): Observable<any> {
        return this.http.get(`${this.baseUrl}/${jobId}/detections`, {
            params: {
                startFrame: startFrame.toString(),
                endFrame: endFrame.toString(),
                limit: limit.toString()
            }
        });
    }

    /**
     * Handle SignalR progress update
     */
    handleProgressUpdate(update: VideoProgressUpdate): void {
        this.progressUpdateSubject.next(update);

        const jobs = this.jobsSubject.value;
        const existingJob = jobs.get(update.jobId);
        if (existingJob) {
            existingJob.processedFrames = update.processedFrames;
            existingJob.progressPercent = update.progress;
            existingJob.totalPersonsDetected = update.totalPersonsDetected;
            existingJob.uniquePersonsDetected = update.uniquePersons;
            this.jobsSubject.next(new Map(jobs));
        }
    }

    /**
     * Handle SignalR complete update
     */
    handleCompleteUpdate(update: VideoCompleteUpdate): void {
        this.completeUpdateSubject.next(update);
        this.getJobStatus(update.jobId).subscribe();
    }

    private startStatusPolling(jobId: string): void {
        const pollInterval = setInterval(() => {
            this.getJobStatus(jobId).subscribe({
                next: (status) => {
                    if (status.state === VideoProcessingState.Completed ||
                        status.state === VideoProcessingState.Failed ||
                        status.state === VideoProcessingState.Cancelled) {
                        clearInterval(pollInterval);
                    }
                },
                error: () => clearInterval(pollInterval)
            });
        }, 2000);

        setTimeout(() => clearInterval(pollInterval), 30 * 60 * 1000);
    }

    private updateJobStatus(jobId: string, status: VideoProcessingStatus): void {
        const jobs = this.jobsSubject.value;
        jobs.set(jobId, status);
        this.jobsSubject.next(new Map(jobs));
    }

    private updateUploadProgress(uploadId: string, progress: UploadProgress): void {
        const progressMap = this.uploadProgressSubject.value;
        progressMap.set(uploadId, progress);
        this.uploadProgressSubject.next(new Map(progressMap));
    }

    /**
     * Get state label for display
     */
    getStateLabel(state: VideoProcessingState | string): string {
        if (typeof state === 'string') {
            return state;
        }
        const labels: Record<VideoProcessingState, string> = {
            [VideoProcessingState.Queued]: 'Queued',
            [VideoProcessingState.Processing]: 'Processing',
            [VideoProcessingState.Completed]: 'Completed',
            [VideoProcessingState.Failed]: 'Failed',
            [VideoProcessingState.Cancelled]: 'Cancelled'
        };
        return labels[state] || 'Unknown';
    }

    /**
     * Get state color for display
     */
    getStateColor(state: VideoProcessingState): string {
        const colors: Record<VideoProcessingState, string> = {
            [VideoProcessingState.Queued]: 'accent',
            [VideoProcessingState.Processing]: 'primary',
            [VideoProcessingState.Completed]: 'primary',
            [VideoProcessingState.Failed]: 'warn',
            [VideoProcessingState.Cancelled]: 'warn'
        };
        return colors[state] || 'primary';
    }

    /**
     * Format duration in seconds to readable string
     */
    formatDuration(seconds: number): string {
        if (!seconds || seconds <= 0) return '0:00';
        const mins = Math.floor(seconds / 60);
        const secs = Math.floor(seconds % 60);
        return `${mins}:${secs.toString().padStart(2, '0')}`;
    }

    /**
     * Format processing time
     */
    formatProcessingTime(seconds: number): string {
        if (!seconds) return '--';
        if (seconds < 60) return `${seconds.toFixed(1)}s`;
        const mins = Math.floor(seconds / 60);
        const secs = Math.floor(seconds % 60);
        return `${mins}m ${secs}s`;
    }
}