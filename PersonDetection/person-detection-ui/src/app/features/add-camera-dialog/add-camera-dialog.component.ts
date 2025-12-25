// src/app/features/add-camera-dialog/add-camera-dialog.component.ts
import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule, ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { CameraType } from '../../core/models/detection.models';

@Component({
    selector: 'app-add-camera-dialog',
    standalone: true,
    imports: [
        CommonModule,
        FormsModule,
        ReactiveFormsModule,
        MatDialogModule,
        MatFormFieldModule,
        MatInputModule,
        MatSelectModule,
        MatButtonModule,
        MatIconModule
    ],
    template: `
    <div class="dialog-container">
      <h2 mat-dialog-title>
        <mat-icon>add_circle</mat-icon>
        Add New Camera
      </h2>
      
      <mat-dialog-content>
        <form [formGroup]="form">
          <mat-form-field appearance="outline" class="full-width">
            <mat-label>Camera Name</mat-label>
            <mat-icon matPrefix>label</mat-icon>
            <input matInput formControlName="name" placeholder="e.g., Front Door">
            @if (form.get('name')?.hasError('required')) {
              <mat-error>Name is required</mat-error>
            }
          </mat-form-field>

          <mat-form-field appearance="outline" class="full-width">
            <mat-label>Stream URL</mat-label>
            <mat-icon matPrefix>link</mat-icon>
            <input matInput formControlName="url" placeholder="e.g., http://192.168.1.100:8080/video">
            <mat-hint>Use "0" for webcam, or enter stream URL</mat-hint>
            @if (form.get('url')?.hasError('required')) {
              <mat-error>URL is required</mat-error>
            }
          </mat-form-field>

          <mat-form-field appearance="outline" class="full-width">
            <mat-label>Camera Type</mat-label>
            <mat-icon matPrefix>category</mat-icon>
            <mat-select formControlName="type">
              @for (type of cameraTypes; track type.value) {
                <mat-option [value]="type.value">
                  <mat-icon>{{ type.icon }}</mat-icon>
                  {{ type.label }}
                </mat-option>
              }
            </mat-select>
          </mat-form-field>

          <mat-form-field appearance="outline" class="full-width">
            <mat-label>Description (optional)</mat-label>
            <mat-icon matPrefix>description</mat-icon>
            <textarea matInput formControlName="description" rows="2" 
                      placeholder="Add notes about this camera..."></textarea>
          </mat-form-field>
        </form>

        <div class="url-examples">
          <p><strong>URL Examples:</strong></p>
          <ul>
            <li><code>0</code> — Built-in webcam</li>
            <li><code>http://192.168.1.100:8080/video</code> — IP Webcam app</li>
            <li><code>rtsp://admin:pass&#64;192.168.1.100:554/stream</code> — RTSP</li>
          </ul>
        </div>
      </mat-dialog-content>

      <mat-dialog-actions align="end">
        <button mat-button mat-dialog-close>Cancel</button>
        <button mat-raised-button color="primary" 
                [disabled]="!form.valid" 
                (click)="save()">
          <mat-icon>add</mat-icon>
          Add Camera
        </button>
      </mat-dialog-actions>
    </div>
  `,
    styles: [`
    .dialog-container {
      min-width: 450px;
    }

    h2[mat-dialog-title] {
      display: flex;
      align-items: center;
      gap: 10px;
      margin: 0;
      padding: 20px 24px;
      font-size: 1.3rem;
      
      mat-icon {
        color: #3b82f6;
      }
    }

    mat-dialog-content {
      padding: 0 24px 24px;
    }

    .full-width {
      width: 100%;
      margin-bottom: 8px;
    }

    mat-icon[matPrefix] {
      margin-right: 8px;
      color: #64748b;
    }

    .url-examples {
      background: #f8fafc;
      border-radius: 8px;
      padding: 12px 16px;
      margin-top: 8px;

      p {
        margin: 0 0 8px;
        font-size: 0.85rem;
        font-weight: 500;
        color: #475569;
      }

      ul {
        margin: 0;
        padding-left: 20px;
        font-size: 0.8rem;
        color: #64748b;

        li {
          margin-bottom: 4px;
        }

        code {
          background: #e2e8f0;
          padding: 2px 6px;
          border-radius: 4px;
          font-size: 0.75rem;
        }
      }
    }

    mat-dialog-actions {
      padding: 16px 24px;
      gap: 8px;
    }
  `]
})
export class AddCameraDialogComponent {
    private fb = inject(FormBuilder);
    private dialogRef = inject(MatDialogRef<AddCameraDialogComponent>);

    form = this.fb.group({
        name: ['', Validators.required],
        url: ['', Validators.required],
        type: [CameraType.HTTP],
        description: ['']
    });

    cameraTypes = [
        { value: CameraType.Webcam, label: 'Webcam', icon: 'laptop' },
        { value: CameraType.IP, label: 'IP Camera', icon: 'videocam' },
        { value: CameraType.HTTP, label: 'HTTP Stream', icon: 'http' },
        { value: CameraType.RTSP, label: 'RTSP Stream', icon: 'stream' },
        { value: CameraType.File, label: 'Video File', icon: 'movie' }
    ];

    save(): void {
        if (this.form.valid) {
            this.dialogRef.close(this.form.value);
        }
    }
}