// src/app/core/services/detection.service.ts
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ActiveCameras, CameraStats, GlobalStats } from '../core/models/detection.models';

@Injectable({
    providedIn: 'root'
})
export class DetectionService {
    private http = inject(HttpClient);
    private baseUrl = `${environment.apiUrl}/Detection`;

    getActiveCameras(): Observable<ActiveCameras> {
        return this.http.get<ActiveCameras>(`${this.baseUrl}/active`);
    }

    getCameraDetections(cameraId: number): Observable<CameraStats> {
        return this.http.get<CameraStats>(`${this.baseUrl}/camera/${cameraId}`);
    }

    getGlobalStats(): Observable<GlobalStats> {
        return this.http.get<GlobalStats>(`${this.baseUrl}/stats`);  // ✅ Just /stats
    }

    resetIdentities(): Observable<{ message: string; timestamp: Date }> {
        return this.http.post<{ message: string; timestamp: Date }>(
            `${this.baseUrl}/reset-identities`,  // ✅ Just /reset-identities
            {}
        );
    }
}