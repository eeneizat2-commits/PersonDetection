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
  customStartDateTime: string = '';
  customEndDateTime: string = '';

  displayedColumns: string[] = [
    'date', 'dayName', 'uniquePersons', 'totalDetections', 'peakHour'
  ];

  private destroy$ = new Subject<void>();

  // ✅ FIX: Track the current in-flight request so we can cancel it
  private currentRequest?: Subscription;

  constructor(
    private statsService: StatsService,
    private cdr: ChangeDetectorRef,
    public dialogRef: MatDialogRef<StatsDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: { cameraId?: number }
  ) {
    this.setDefaultDateTimeRange();
  }

  ngOnInit(): void {
    this.loadStats();
  }

  ngOnDestroy(): void {
    // ✅ FIX: Cancel any in-flight request when dialog closes
    this.cancelCurrentRequest();
    this.destroy$.next();
    this.destroy$.complete();
  }

  // ══════════════════════════════════════════════════════════════
  // ✅ FIX: Cancel the previous HTTP request before starting a new one
  // ══════════════════════════════════════════════════════════════
  private cancelCurrentRequest(): void {
    if (this.currentRequest && !this.currentRequest.closed) {
      this.currentRequest.unsubscribe(); // This aborts the HTTP request
      // When Angular's HttpClient subscription is unsubscribed,
      // the browser aborts the XMLHttpRequest, which triggers
      // CancellationToken on the ASP.NET Core server
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
    // ✅ FIX: Cancel any previous request first
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
          this.data?.cameraId
        );
      } else {
        this.error = 'Invalid date range';
        this.loading = false;
        return;
      }
    } else {
      const days = parseInt(this.selectedPeriod, 10);
      observable = this.statsService.getHistoricalStats(
        days, undefined, undefined, this.data?.cameraId
      );
    }

    // ✅ FIX: Store the subscription so we can cancel it later
    this.currentRequest = observable
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (stats) => {
          this.stats = stats;
          this.loading = false;
          this.cdr.markForCheck();
        },
        error: (err) => {
          // ✅ FIX: Don't show error for cancelled requests
          if (err.name === 'AbortError' || err.status === 0) {
            // Request was cancelled — ignore silently
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
    if (this.selectedPeriod !== 'custom') {
      this.loadStats(); // ← Will cancel the previous request automatically now
    } else {
      this.cancelCurrentRequest(); // Cancel any running request
      this.loading = false;
      this.setDefaultDateTimeRange();
    }
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
    if (this.customStartDateTime && this.customEndDateTime) {
      const start = this.parseDateTimeLocal(this.customStartDateTime);
      const end = this.parseDateTimeLocal(this.customEndDateTime);

      if (start && end && end > start) {
        this.loadStats(); // ← Will cancel the previous request automatically now
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
