// src/app/core/services/camera.service.ts
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { BehaviorSubject, Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { CameraConfig, StartCameraRequest } from '../core/models/detection.models';

@Injectable({ providedIn: 'root' })
export class CameraService {
    private http = inject(HttpClient);
    private baseUrl = `${environment.apiUrl}/Camera`;


     private activeCamerasSubject = new BehaviorSubject<CameraConfig[]>([]);
    activeCameras$ = this.activeCamerasSubject.asObservable();

    
    get activeCamerasValue(): CameraConfig[] {
        return this.activeCamerasSubject.value;
    }

    startCamera(request: StartCameraRequest): Observable<any> {
        return this.http.post(`${this.baseUrl}/start`, request);
    }

    stopCamera(cameraId: number): Observable<any> {
        return this.http.post(`${this.baseUrl}/stop/${cameraId}`, {});
    }

    getStreamUrl(cameraId: number, url: string): string {
        const baseApiUrl = environment.apiUrl.startsWith('/')
            ? `${window.location.origin}${environment.apiUrl}`
            : environment.apiUrl;
        return `${baseApiUrl}/Camera/${cameraId}/stream?url=${encodeURIComponent(url)}`;
    }
}