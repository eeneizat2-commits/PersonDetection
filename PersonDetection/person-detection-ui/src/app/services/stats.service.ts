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

  /**
   * Get stats by number of days
   */
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
      params = params.set('startDate', this.formatLocalDate(startDate));
    }
    
    if (endDate) {
      params = params.set('endDate', this.formatLocalDate(endDate));
    }
    
    if (cameraId) {
      params = params.set('cameraId', cameraId.toString());
    }

    return this.http.get<HistoricalStats>(`${this.baseUrl}/historical`, { params });
  }

  /**
   * Get stats with full datetime (date + time)
   */
  getHistoricalStatsWithDateTime(
    startDateTime: Date,
    endDateTime: Date,
    cameraId?: number
  ): Observable<HistoricalStats> {
    let params = new HttpParams();

    // Send full datetime in ISO format but adjusted for local timezone
    params = params.set('startDate', this.formatLocalDateTime(startDateTime));
    params = params.set('endDate', this.formatLocalDateTime(endDateTime));
    
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
   * Format date as local YYYY-MM-DD string
   */
  private formatLocalDate(date: Date): string {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
  }

  /**
   * Format datetime as local YYYY-MM-DDTHH:mm:ss string
   */
/**
 * Format datetime as local YYYY-MM-DDTHH:mm:ss string
 * Handles datetime-local input format properly
 */
private formatLocalDateTime(date: Date): string {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  const hours = String(date.getHours()).padStart(2, '0');
  const minutes = String(date.getMinutes()).padStart(2, '0');
  const seconds = String(date.getSeconds()).padStart(2, '0');
  
  // ✅ FIX: Subtract timezone offset to get true local time
  const offset = date.getTimezoneOffset() * 60000;
  const localDate = new Date(date.getTime() - offset);
  
  const localYear = localDate.getUTCFullYear();
  const localMonth = String(localDate.getUTCMonth() + 1).padStart(2, '0');
  const localDay = String(localDate.getUTCDate()).padStart(2, '0');
  const localHours = String(localDate.getUTCHours()).padStart(2, '0');
  const localMinutes = String(localDate.getUTCMinutes()).padStart(2, '0');
  const localSeconds = String(localDate.getUTCSeconds()).padStart(2, '0');
  
  return `${localYear}-${localMonth}-${localDay}T${localHours}:${localMinutes}:${localSeconds}`;
}
}