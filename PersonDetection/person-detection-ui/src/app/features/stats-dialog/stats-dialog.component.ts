// features/stats-dialog/stats-dialog.component.ts
import { Component, OnInit, OnDestroy, ChangeDetectorRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTableModule } from '@angular/material/table';
import { MatSelectModule } from '@angular/material/select';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatChipsModule } from '@angular/material/chips';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { StatsService, HistoricalStats } from '../../services/stats.service';
import { CameraConfigService } from '../../services/camera-config.service';
import { Subject, Subscription, takeUntil } from 'rxjs';
import { CameraDto } from '../../core/models/detection.models';
import { MatCard, MatCardContent, MatCardHeader, MatCardModule } from "@angular/material/card";
import { MatDivider } from "@angular/material/divider";

interface CameraOption {
  id: number;
  name: string;
}

@Component({
  selector: 'app-stats-dialog',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatButtonModule,
    MatIconModule,
    MatTableModule,
    MatSelectModule,
    MatInputModule,
    MatFormFieldModule,
    MatProgressSpinnerModule,
    MatChipsModule,
    MatCheckboxModule,
    MatCard,
    MatCardContent,
    MatCardHeader,
    MatCardModule,
    MatDivider
],
  templateUrl: './stats-dialog.component.html',
  styleUrls: ['./stats-dialog.component.scss']
})
export class StatsDialogComponent implements OnInit, OnDestroy {
  // ─── Data ───
  stats: HistoricalStats | null = null;
  availableCameras: CameraOption[] = [];
  selectedCameraIds: number[] = [];

  // ─── UI State ───
  loading = false;
  error: string | null = null;
  selectedPeriod: string = '3';
  customStartDateTime: string = '';
  customEndDateTime: string = '';

  displayedColumns: string[] = [
    'date', 'dayName', 'uniquePersons', 'totalDetections', 'peakHour'
  ];

  private destroy$ = new Subject<void>();
  private currentRequest?: Subscription;

  constructor(
    private statsService: StatsService,
    private cameraConfigService: CameraConfigService,  // ✅ Changed
    private cdr: ChangeDetectorRef
  ) {
    this.setDefaultDateTimeRange();
  }

  ngOnInit(): void {
    this.loadAvailableCameras();
  }

  ngOnDestroy(): void {
    this.cancelCurrentRequest();
    this.destroy$.next();
    this.destroy$.complete();
  }

  // ─── Camera Management ───

  private loadAvailableCameras(): void {
    // ✅ Use existing loadCameras() method - map CameraDto to CameraOption
    this.cameraConfigService.loadCameras()
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (cameras: CameraDto[]) => {
          // Map CameraDto to simple CameraOption {id, name}
          this.availableCameras = cameras.map(c => ({
            id: c.id,
            name: c.name
          }));
          this.cdr.markForCheck();
        },
        error: (err) => {
          console.error('Failed to load cameras:', err);
          this.error = 'Failed to load available cameras';
          this.cdr.markForCheck();
        }
      });
  }

  // ✅ Toggle camera selection
  toggleCamera(cameraId: number): void {
    const index = this.selectedCameraIds.indexOf(cameraId);
    if (index > -1) {
      this.selectedCameraIds.splice(index, 1);
    } else {
      this.selectedCameraIds.push(cameraId);
    }
    this.cdr.markForCheck();
  }

  // ✅ Check if camera is selected
  isCameraSelected(cameraId: number): boolean {
    return this.selectedCameraIds.includes(cameraId);
  }

  // ✅ Clear all camera selections
  clearCameraSelection(): void {
    this.selectedCameraIds = [];
    this.cdr.markForCheck();
  }

  // ✅ Get selected camera names for display
  getSelectedCameraNames(): string {
    if (this.selectedCameraIds.length === 0) {
      return 'All Cameras';
    }
    return this.selectedCameraIds
      .map(id => this.availableCameras.find(c => c.id === id)?.name)
      .filter(Boolean)
      .join(', ');
  }

  // ─── Date/Time Management ───
  private setDefaultDateTimeRange(): void {
    const now = new Date();
    const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    this.customStartDateTime = this.toDateTimeLocalString(today);

    const endOfDay = new Date(today);
    endOfDay.setHours(23, 59, 0, 0);
    this.customEndDateTime = this.toDateTimeLocalString(endOfDay);
  }

  private toDateTimeLocalString(date: Date): string {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    const hours = String(date.getHours()).padStart(2, '0');
    const minutes = String(date.getMinutes()).padStart(2, '0');
    return `${year}-${month}-${day}T${hours}:${minutes}`;
  }

  private parseDateTimeLocal(dateTimeStr: string): Date | null {
    if (!dateTimeStr) return null;
    return new Date(dateTimeStr);
  }

  onPeriodChange(): void {
    if (this.selectedPeriod === 'custom') {
      this.cancelCurrentRequest();
      this.loading = false;
      this.setDefaultDateTimeRange();
    }
  }

  // ─── Data Loading ───
  private cancelCurrentRequest(): void {
    if (this.currentRequest && !this.currentRequest.closed) {
      this.currentRequest.unsubscribe();
    }
  }

  loadStats(): void {
    // ✅ Validation
    if (this.selectedPeriod === 'custom') {
      if (!this.customStartDateTime || !this.customEndDateTime) {
        this.error = 'Please select both start and end date/time';
        return;
      }

      const startDate = this.parseDateTimeLocal(this.customStartDateTime);
      const endDate = this.parseDateTimeLocal(this.customEndDateTime);

      if (!startDate || !endDate || endDate <= startDate) {
        this.error = 'End date must be after start date';
        return;
      }
    }

    this.cancelCurrentRequest();
    this.loading = true;
    this.error = null;
    this.cdr.markForCheck();

    let observable;

    // ✅ Pass selected camera IDs (undefined if empty = all cameras)
    const cameraFilter = this.selectedCameraIds.length > 0 ? this.selectedCameraIds : undefined;

    if (this.selectedPeriod === 'custom' && this.customStartDateTime && this.customEndDateTime) {
      const startDate = this.parseDateTimeLocal(this.customStartDateTime);
      const endDate = this.parseDateTimeLocal(this.customEndDateTime);

      if (startDate && endDate) {
        observable = this.statsService.getHistoricalStatsWithDateTime(
          startDate,
          endDate,
          cameraFilter
        );
      } else {
        this.error = 'Invalid date range';
        this.loading = false;
        return;
      }
    } else {
      const days = parseInt(this.selectedPeriod, 10);
      observable = this.statsService.getHistoricalStats(
        days,
        cameraFilter
      );
    }

    this.currentRequest = observable
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (stats) => {
          this.stats = stats;
          this.loading = false;
          this.cdr.markForCheck();
        },
        error: (err) => {
          if (err.name === 'AbortError' || err.status === 0) {
            console.debug('Stats request was cancelled');
            return;
          }
          this.error = 'Failed to load statistics. Please try again.';
          this.loading = false;
          this.cdr.markForCheck();
          console.error(err);
        }
      });
  }

  // ─── UI Utilities ───
  openPicker(input: HTMLInputElement): void {
    if (typeof input.showPicker === 'function') {
      input.showPicker();
    } else {
      input.focus();
      input.click();
    }
  }

  applyCustomRange(): void {
    this.loadStats();
  }

  formatHour(hour: number): string {
    const ampm = hour >= 12 ? 'PM' : 'AM';
    const h = hour % 12 || 12;
    return `${h}:00 ${ampm}`;
  }

  formatDate(dateStr: string): string {
    return new Date(dateStr).toLocaleDateString('en-US', {
      month: 'short',
      day: 'numeric'
    });
  }

  formatDateTime(dateStr: string): string {
    const date = new Date(dateStr);
    return date.toLocaleDateString('en-US', {
      month: 'short',
      day: 'numeric',
      hour: 'numeric',
      minute: '2-digit',
      hour12: true
    });
  }
}