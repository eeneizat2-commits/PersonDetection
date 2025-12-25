// src/app/features/camera-controls/camera-controls.component.ts
import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { CameraService } from '../../services/camera.service';
import { CameraConfig, CreateCameraRequest } from '../../core/models/detection.models';
import { CameraConfigService } from '../../services/camera-config.service';


@Component({
    selector: 'app-camera-controls',
    standalone: true,
    imports: [
        CommonModule,
        FormsModule,
        MatCardModule,
        MatButtonModule,
        MatInputModule,
        MatFormFieldModule,
        MatIconModule,
        MatSnackBarModule
    ],
    templateUrl: './camera-controls.component.html',
    styleUrl: './camera-controls.component.scss'
})
export class CameraControlsComponent {
    private cameraService = inject(CameraService);
    private snackBar = inject(MatSnackBar);
    private cameraConfigService = inject(CameraConfigService);
    newCamera: Partial<CreateCameraRequest> = {
        name: '',
        url: '',
        description: '',
        type: undefined
    };

    addCamera(): void {
        if (!this.newCamera.name || !this.newCamera.url) {
            this.snackBar.open('Please fill in all fields', 'OK', { duration: 3000 });
            return;
        }

        const cameras = this.cameraService.activeCamerasValue || [];
        const newId = cameras.length ? Math.max(...cameras.map(c => c.id)) + 1 : 0;


        this.cameraConfigService.createCamera({
            name: this.newCamera.name,
            url: this.newCamera.url,
            description: this.newCamera.description,
            type: this.newCamera.type
        });

        this.snackBar.open('Camera added successfully', 'OK', { duration: 3000 });
        this.newCamera = { name: '', url: '' };
    }
}