// src/app/core/services/detection.service.ts
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ActiveCameras, CameraStats } from '../core/models/detection.models';

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
}