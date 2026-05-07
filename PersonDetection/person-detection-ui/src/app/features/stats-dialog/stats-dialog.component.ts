// features/stats-dialog/stats-dialog.component.ts
import { Component, OnInit, OnDestroy, Inject, ChangeDetectorRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTableModule } from '@angular/material/table';
import { MatSelectModule } from '@angular/material/select';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatChipsModule } from '@angular/material/chips';
import { StatsService, HistoricalStats } from '../../services/stats.service';
import { Subject, Subscription, takeUntil } from 'rxjs';

@Component({
  selector: 'app-stats-dialog',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatTableModule,
    MatSelectModule,
    MatInputModule,
    MatFormFieldModule,
    MatProgressSpinnerModule,
    MatChipsModule
  ],
  templateUrl: './stats-dialog.component.html',
  styleUrls: ['./stats-dialog.component.scss']
})
export class StatsDialogComponent implements OnInit, OnDestroy {
  stats: HistoricalStats | null = null;
  loading = false;
  error: string | null = null;

  selectedPeriod: string = '3';
  filterCameraId: number | null = null;
  cameras: any[] = [];
  customStartDateTime: string = '';
  customEndDateTime: string = '';

  displayedColumns: string[] = [
    'date', 'dayName', 'uniquePersons', 'totalDetections', 'peakHour'
  ];

  private destroy$ = new Subject<void>();
  private currentRequest?: Subscription;

  constructor(
    private statsService: StatsService,
    private cdr: ChangeDetectorRef,
    public dialogRef: MatDialogRef<StatsDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: { cameras?: any[]; cameraId?: number }
  ) {
    this.setDefaultDateTimeRange();
    this.cameras = data?.cameras || [];
  }

  ngOnInit(): void {
    // ❌ REMOVED: this.loadStats();
    // ✅ NEW: Don't load stats automatically
    this.cdr.markForCheck();
  }

  ngOnDestroy(): void {
    this.cancelCurrentRequest();
    this.destroy$.next();
    this.destroy$.complete();
  }

  private cancelCurrentRequest(): void {
    if (this.currentRequest && !this.currentRequest.closed) {
      this.currentRequest.unsubscribe();
    }
  }

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

  loadStats(): void {
    // ✅ Validation before loading
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

    if (this.selectedPeriod === 'custom' && this.customStartDateTime && this.customEndDateTime) {
      const startDate = this.parseDateTimeLocal(this.customStartDateTime);
      const endDate = this.parseDateTimeLocal(this.customEndDateTime);

      if (startDate && endDate) {
        observable = this.statsService.getHistoricalStatsWithDateTime(
          startDate,
          endDate,
          this.filterCameraId || undefined
        );
      } else {
        this.error = 'Invalid date range';
        this.loading = false;
        return;
      }
    } else {
      const days = parseInt(this.selectedPeriod, 10);
      observable = this.statsService.getHistoricalStats(
        days, undefined, undefined, this.filterCameraId || undefined
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
          this.error = 'Failed to load statistics';
          this.loading = false;
          this.cdr.markForCheck();
          console.error(err);
        }
      });
  }

  onPeriodChange(): void {
    // ✅ NEW: Don't load automatically, just update custom range UI
    if (this.selectedPeriod === 'custom') {
      this.cancelCurrentRequest();
      this.loading = false;
      this.setDefaultDateTimeRange();
    }
  }

  onCameraFilterChange(): void {
    // ✅ NEW: Just update, don't load
    this.cdr.markForCheck();
  }

  openPicker(input: HTMLInputElement): void {
    if (typeof input.showPicker === 'function') {
      input.showPicker();
    } else {
      input.focus();
      input.click();
    }
  }

  applyCustomRange(): void {
    // ✅ This will be called by the custom range Apply button
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

  close(): void {
    this.dialogRef.close();
  }
}
