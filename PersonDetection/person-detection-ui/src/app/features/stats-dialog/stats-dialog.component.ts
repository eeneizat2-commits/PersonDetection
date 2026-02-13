// src/app/features/stats-dialog/stats-dialog.component.ts
import { Component, OnInit, Inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTableModule } from '@angular/material/table';
import { MatSelectModule } from '@angular/material/select';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatNativeDateModule } from '@angular/material/core';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatChipsModule } from '@angular/material/chips';
import { StatsService, HistoricalStats, DailyStats } from '../../services/stats.service';

@Component({
  selector: 'app-stats-dialog',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatTabsModule,
    MatTableModule,
    MatSelectModule,
    MatDatepickerModule,
    MatNativeDateModule,
    MatInputModule,
    MatFormFieldModule,
    MatProgressSpinnerModule,
    MatChipsModule
  ],
  templateUrl: './stats-dialog.component.html',
  styleUrls: ['./stats-dialog.component.scss']
})
export class StatsDialogComponent implements OnInit {
  stats: HistoricalStats | null = null;
  loading = false;
  error: string | null = null;

  // Quick select options
  selectedPeriod: string = '3';
  customStartDate: Date | null = null;
  customEndDate: Date | null = null;

  // Display columns
  displayedColumns: string[] = ['date', 'dayName', 'uniquePersons', 'totalDetections', 'peakHour'];

  constructor(
    private statsService: StatsService,
    public dialogRef: MatDialogRef<StatsDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: { cameraId?: number }
  ) {}

  ngOnInit(): void {
    this.loadStats();
  }

  loadStats(): void {
    this.loading = true;
    this.error = null;

    let observable;

    if (this.selectedPeriod === 'custom' && this.customStartDate && this.customEndDate) {
      observable = this.statsService.getHistoricalStats(
        undefined,
        this.customStartDate,
        this.customEndDate,
        this.data?.cameraId
      );
    } else {
      const days = parseInt(this.selectedPeriod, 10);
      observable = this.statsService.getHistoricalStats(days, undefined, undefined, this.data?.cameraId);
    }

    observable.subscribe({
      next: (stats) => {
        this.stats = stats;
        this.loading = false;
      },
      error: (err) => {
        this.error = 'Failed to load statistics';
        this.loading = false;
        console.error(err);
      }
    });
  }

  onPeriodChange(): void {
    if (this.selectedPeriod !== 'custom') {
      this.loadStats();
    }
  }

  applyCustomRange(): void {
    if (this.customStartDate && this.customEndDate) {
      this.loadStats();
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

  close(): void {
    this.dialogRef.close();
  }
}