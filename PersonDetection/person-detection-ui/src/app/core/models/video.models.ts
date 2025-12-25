// src/app/core/models/video.models.ts

export enum VideoProcessingState {
    Queued = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}

export interface VideoUploadResult {
    jobId: string;
    fileName: string;
    status: string;
    uploadedAt: Date;
    message: string;
}

export interface VideoProcessingStatus {
    jobId: string;
    fileName: string;
    state: VideoProcessingState;
    totalFrames: number;
    processedFrames: number;
    progressPercent: number;
    totalPersonsDetected: number;
    uniquePersonsDetected: number;
    startedAt: Date;
    completedAt: Date | null;
    errorMessage: string | null;
    detections: VideoFrameDetection[];
}

export interface VideoFrameDetection {
    frameNumber: number;
    timestampSeconds: number;
    personCount: number;
    persons: VideoPersonDetection[];
}

export interface VideoPersonDetection {
    id: number;
    boundingBox: {
        x: number;
        y: number;
        width: number;
        height: number;
        aspectRatio: number;
    };
    confidence: number;
    globalPersonId: string;
    trackId: number | null;
    detectedAt: Date;
}

export interface VideoProcessingSummary {
    jobId: string;
    fileName: string;
    videoDuration: string;
    totalFramesProcessed: number;
    totalPersonsDetected: number;
    uniquePersonsIdentified: number;
    averagePersonsPerFrame: number;
    peakPersonCount: number;
    processingTimeSeconds: number;
    personTimelines: PersonTimeline[];
}

export interface PersonTimeline {
    globalPersonId: string;
    shortId: string;
    firstAppearanceSeconds: number;
    lastAppearanceSeconds: number;
    totalAppearances: number;
    averageConfidence: number;
    thumbnailUrl?: string;
    hasThumbnail?: boolean;
}

export interface VideoProgressUpdate {
    jobId: string;
    fileName: string;
    progress: number;
    processedFrames: number;
    totalPersonsDetected: number;
    uniquePersons: number;
}

export interface VideoCompleteUpdate {
    jobId: string;
    fileName: string;
    state: string;
    totalPersonsDetected: number;
    uniquePersons: number;
    processingTimeSeconds: number;
}

// ========== NEW: Video History Models ==========

export interface VideoHistoryItem {
    id: number;
    jobId: string;
    fileName: string;
    state: string;
    totalFrames: number;
    processedFrames: number;
    totalDetections: number;
    uniquePersonCount: number;
    videoDurationSeconds: number;
    processingTimeSeconds: number;
    startedAt: Date;
    completedAt: Date | null;
    createdAt: Date;
}

export interface VideoHistoryResponse {
    total: number;
    page: number;
    pageSize: number;
    totalPages: number;
    items: VideoHistoryItem[];
}

export interface VideoDetailsResponse {
    id: number;
    jobId: string;
    fileName: string;
    state: string;
    totalFrames: number;
    processedFrames: number;
    totalDetections: number;
    uniquePersonCount: number;
    videoDurationSeconds: number;
    videoFps: number;
    processingTimeSeconds: number;
    frameSkip: number;
    averagePersonsPerFrame: number | null;  // 👈 ADD THIS
    peakPersonCount: number | null;
    startedAt: Date;
    completedAt: Date | null;
    errorMessage: string | null;
    videoFileExists: boolean;
    personTimelines: VideoPersonTimelineItem[];
}

export interface VideoPersonTimelineItem {
    id: number;
    globalPersonId: string;
    shortId: string;
    firstAppearanceSeconds: number;
    lastAppearanceSeconds: number;
    totalAppearances: number;
    averageConfidence: number;
    hasThumbnail: boolean;
    thumbnailUrl: string | null;
}