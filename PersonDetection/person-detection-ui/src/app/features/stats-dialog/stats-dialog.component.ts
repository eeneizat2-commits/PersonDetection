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
import { Subject, takeUntil } from 'rxjs';

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

  // Combined datetime strings for input type="datetime-local"
  customStartDateTime: string = '';
  customEndDateTime: string = '';

  displayedColumns: string[] = ['date', 'dayName', 'uniquePersons', 'totalDetections', 'peakHour'];

  private destroy$ = new Subject<void>();

  constructor(
    private statsService: StatsService,
    private cdr: ChangeDetectorRef,
    public dialogRef: MatDialogRef<StatsDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: { cameraId?: number }
  ) {
    // Set default datetime values
    this.setDefaultDateTimeRange();
  }

  ngOnInit(): void {
    this.loadStats();
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  /**
   * Set default datetime range (today start to end)
   */
  private setDefaultDateTimeRange(): void {
    const now = new Date();
    const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());

    // Start: today at 00:00
    this.customStartDateTime = this.toDateTimeLocalString(today);

    // End: today at 23:59
    const endOfDay = new Date(today);
    endOfDay.setHours(23, 59, 0, 0);
    this.customEndDateTime = this.toDateTimeLocalString(endOfDay);
  }

  /**
   * Convert Date to datetime-local input format (YYYY-MM-DDTHH:mm)
   */
  private toDateTimeLocalString(date: Date): string {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    const hours = String(date.getHours()).padStart(2, '0');
    const minutes = String(date.getMinutes()).padStart(2, '0');
    return `${year}-${month}-${day}T${hours}:${minutes}`;
  }

  /**
   * Parse datetime-local string to Date object
   */
  private parseDateTimeLocal(dateTimeStr: string): Date | null {
    if (!dateTimeStr) return null;
    return new Date(dateTimeStr);
  }

  loadStats(): void {
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
          this.data?.cameraId
        );
      } else {
        this.error = 'Invalid date range';
        this.loading = false;
        return;
      }
    } else {
      const days = parseInt(this.selectedPeriod, 10);
      observable = this.statsService.getHistoricalStats(days, undefined, undefined, this.data?.cameraId);
    }

    observable.pipe(takeUntil(this.destroy$)).subscribe({
      next: (stats) => {
        this.stats = stats;
        this.loading = false;
        this.cdr.markForCheck();
      },
      error: (err) => {
        this.error = 'Failed to load statistics';
        this.loading = false;
        this.cdr.markForCheck();
        console.error(err);
      }
    });
  }

  onPeriodChange(): void {
    if (this.selectedPeriod !== 'custom') {
      this.loadStats();
    } else {
      // Reset to default when switching to custom
      this.setDefaultDateTimeRange();
    }
  }

  /**
 * Open the native datetime picker
 */
  openPicker(input: HTMLInputElement): void {
    // Modern browsers support showPicker()
    if (typeof input.showPicker === 'function') {
      input.showPicker();
    } else {
      // Fallback for older browsers - focus and click
      input.focus();
      input.click();
    }
  }

  applyCustomRange(): void {
    if (this.customStartDateTime && this.customEndDateTime) {
      // Validate that end is after start
      const start = this.parseDateTimeLocal(this.customStartDateTime);
      const end = this.parseDateTimeLocal(this.customEndDateTime);

      if (start && end && end > start) {
        this.loadStats();
      } else {
        this.error = 'End date must be after start date';
      }
    }
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