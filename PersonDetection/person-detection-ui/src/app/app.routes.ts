// src/app/app.routes.ts
import { Routes } from '@angular/router';

export const routes: Routes = [
    { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
    {
        path: 'dashboard',
        loadComponent: () => import('./features/dashboard/dashboard.component')
            .then(m => m.DashboardComponent)
    },
    {
        path: 'camera/:id',
        loadComponent: () => import('./features/camera-view/camera-view.component')
            .then(m => m.CameraViewComponent)
    },
    {
        path: 'videos',
        loadComponent: () => import('./features/video-jobs/video-jobs.component')
            .then(m => m.VideoJobsComponent)
    }
];