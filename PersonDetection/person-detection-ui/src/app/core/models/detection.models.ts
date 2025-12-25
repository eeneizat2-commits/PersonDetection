// src/app/core/models/detection.models.ts
export interface BoundingBox {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface PersonDetection {
  confidence: number;
  boundingBox: BoundingBox;
}

export interface DetectionUpdate {
  cameraId: number;
  count: number;
  uniqueCount: number;  // Add this
  timestamp: Date;
  fps: number;
  persons: {
    id: string;
    confidence: number;
    isNew: boolean;
  }[];
}

export interface CameraSession {
  cameraId: number;
  url: string;
  isActive: boolean;
  startedAt: Date;
}

export interface StartCameraRequest {
  cameraId: number;
  url: string;
}

export interface CameraStats {
  cameraId: number;
  currentCount: number;
  totalDetectionsToday: number;
  recentDetections: DetectionResult[];
}

export interface DetectionResult {
  id: number;
  cameraId: number;
  timestamp: Date;
  totalDetections: number;
  validDetections: number;
  persons: PersonDetection[];
}

export interface ActiveCameras {
  totalActiveCameras: number;
  totalPersonsDetected: number;
  countByCamera: { [key: number]: number };
}

export interface CameraConfig {
  id: number;
  name: string;
  url: string;
  isActive: boolean;
}

export enum CameraType {
  Webcam = 0,
  IP = 1,
  RTSP = 2,
  HTTP = 3,
  File = 4
}

export interface CameraDto {
  id: number;
  name: string;
  url: string;
  description: string | null;
  type: CameraType;
  isEnabled: boolean;
  createdAt: string;
  lastConnectedAt: string | null;
  displayOrder: number;
  isActive: boolean;
}

export interface CreateCameraRequest {
  name: string;
  url: string;
  description?: string;
  type?: CameraType;
}