// src/app/shared/pipes/active-cameras.pipe.ts
import { Pipe, PipeTransform } from '@angular/core';
import { CameraConfig } from '../../core/models/detection.models';

@Pipe({
    name: 'activeCameras',
    standalone: true
})
export class ActiveCamerasPipe implements PipeTransform {
    transform(cameras: CameraConfig[] | null): number {
        if (!cameras) return 0;
        return cameras.filter(c => c.isActive).length;
    }
}