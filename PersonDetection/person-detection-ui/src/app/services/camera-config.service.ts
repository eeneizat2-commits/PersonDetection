// src/app/core/services/camera-config.service.ts
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, BehaviorSubject, catchError, of, tap } from 'rxjs';
import { environment } from '../../environments/environment';
import { CameraDto, CreateCameraRequest, CameraType } from '../core/models/detection.models';

@Injectable({ providedIn: 'root' })
export class CameraConfigService {
    private http = inject(HttpClient);
    private baseUrl = `${environment.apiUrl}/cameras`;

    private camerasSubject = new BehaviorSubject<CameraDto[]>([]);
    cameras$ = this.camerasSubject.asObservable();

    private loadingSubject = new BehaviorSubject<boolean>(false);
    loading$ = this.loadingSubject.asObservable();

    loadCameras(): Observable<CameraDto[]> {
        this.loadingSubject.next(true);
        return this.http.get<CameraDto[]>(this.baseUrl).pipe(
            tap(cameras => {
                this.camerasSubject.next(cameras);
                this.loadingSubject.next(false);
            }),
            catchError(err => {
                console.error('Failed to load cameras:', err);
                this.loadingSubject.next(false);
                return of([]);
            })
        );
    }

    createCamera(request: CreateCameraRequest): Observable<CameraDto> {
        return this.http.post<CameraDto>(this.baseUrl, request).pipe(
            tap(() => this.loadCameras().subscribe())
        );
    }

    updateCamera(id: number, request: Partial<CameraDto>): Observable<CameraDto> {
        return this.http.put<CameraDto>(`${this.baseUrl}/${id}`, request).pipe(
            tap(() => this.loadCameras().subscribe())
        );
    }

    deleteCamera(id: number): Observable<void> {
        return this.http.delete<void>(`${this.baseUrl}/${id}`).pipe(
            tap(() => this.loadCameras().subscribe())
        );
    }

    getCameraTypeLabel(type: CameraType): string {
        const labels: Record<CameraType, string> = {
            [CameraType.Webcam]: 'Webcam',
            [CameraType.IP]: 'IP Camera',
            [CameraType.RTSP]: 'RTSP',
            [CameraType.HTTP]: 'HTTP',
            [CameraType.File]: 'File'
        };
        return labels[type] ?? 'Unknown';
    }

    getCameraTypeIcon(type: CameraType): string {
        const icons: Record<CameraType, string> = {
            [CameraType.Webcam]: 'laptop',
            [CameraType.IP]: 'videocam',
            [CameraType.RTSP]: 'stream',
            [CameraType.HTTP]: 'http',
            [CameraType.File]: 'movie'
        };
        return icons[type] ?? 'videocam';
    }
}