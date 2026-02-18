// src/app/services/stats.service.ts
import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface DailyStats {
  date: string;
  dayName: string;
  uniquePersons: number;
  totalDetections: number;
  peakHour: number;
  peakHourCount: number;
}

export interface CameraStats {
  cameraId: number;
  cameraName: string;
  totalDetections: number;
  uniqueToday: number;
}

export interface HistoricalStats {
  startDate: string;
  endDate: string;
  totalDays: number;
  totalUniquePersons: number;
  totalDetections: number;
  dailyStats: DailyStats[];
  cameraBreakdown: CameraStats[];
}

export interface SummaryStats {
  today: {
    uniquePersons: number;
    detections: number;
  };
  thisWeek: {
    uniquePersons: number;
    detections: number;
    dailyAverage: number;
  };
  thisMonth: {
    uniquePersons: number;
    detections: number;
    dailyAverage: number;
  };
}

@Injectable({
  providedIn: 'root'
})
export class StatsService {
  private baseUrl = `${environment.apiUrl}/stats`;

  constructor(private http: HttpClient) {}

  getHistoricalStats(
    lastDays?: number,
    startDate?: Date,
    endDate?: Date,
    cameraId?: number
  ): Observable<HistoricalStats> {
    let params = new HttpParams();

    if (lastDays) {
      params = params.set('lastDays', lastDays.toString());
    }
    
    if (startDate) {
      // ✅ Send as local date string YYYY-MM-DD
      params = params.set('startDate', this.formatLocalDate(startDate));
    }
    
    if (endDate) {
      // ✅ Send as local date string YYYY-MM-DD
      params = params.set('endDate', this.formatLocalDate(endDate));
    }
    
    if (cameraId) {
      params = params.set('cameraId', cameraId.toString());
    }

    return this.http.get<HistoricalStats>(`${this.baseUrl}/historical`, { params });
  }

  getQuickStats(
    period: 'today' | 'yesterday' | 'week' | 'month' | '3days' | '4days', 
    cameraId?: number
  ): Observable<HistoricalStats> {
    let params = new HttpParams();
    if (cameraId) {
      params = params.set('cameraId', cameraId.toString());
    }
    return this.http.get<HistoricalStats>(`${this.baseUrl}/quick/${period}`, { params });
  }

  getSummary(): Observable<SummaryStats> {
    return this.http.get<SummaryStats>(`${this.baseUrl}/summary`);
  }

  /**
   * Format date as local YYYY-MM-DD string (no timezone conversion)
   */
  private formatLocalDate(date: Date): string {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
  }
}