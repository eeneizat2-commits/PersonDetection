# Person Detection Project
A system for detecting and tracking people in videos and streams using OpenCV and YOLO/ONNX models, with Angular frontend and C# Core API backend.

#back-end structure 
```PersonDetection/
│
├── API/                                      # Presentation Layer
│   ├── Controllers/
│   │   ├── CameraConfigController.cs
│   │   ├── CameraController.cs
│   │   ├── DetectionController.cs
│   │   └── VideoController.cs
│   ├── Hubs/
│   │   └── DetectionHub.cs                   # SignalR real-time communication
│   └── Middleware/
│       └── ExceptionHandlingMiddleware.cs
│
├── Application/                              # Use Cases & Orchestration
│   ├── Commands/
│   │   ├── ProcessFrameCommand.cs
│   │   ├── ProcessVideoCommand.cs
│   │   ├── StartCameraCommand.cs
│   │   └── StopCameraHandler.cs
│   ├── Common/
│   │   └── ICommand.cs
│   ├── Configuration/
│   │   ├── DetectionSettings.cs
│   │   └── PersistenceSettings.cs
│   ├── DTOs/
│   │   ├── CameraDtos.cs
│   │   ├── DetectionDtos.cs
│   │   └── VideoDtos.cs
│   ├── Interfaces/
│   │   ├── IDetectionEngine.cs
│   │   └── IVideoProcessingService.cs
│   ├── IService/
│   │   └── ISignalRNotificationService.cs
│   ├── Queries/
│   │   ├── GetActiveCamerasQuery.cs
│   │   ├── GetCameraStatsQuery.cs
│   │   └── GetVideoStatusQuery.cs
│   └── Services/
│       ├── CommandDispatcher.cs
│       └── SignalRNotificationService.cs
│
├── Domain/                                   # Core Business Logic 
│   ├── Common/
│   │   └── Entity.cs
│   ├── Entities/
│   │   ├── Camera.cs
│   │   ├── CameraSession.cs
│   │   ├── DetectionResult.cs
│   │   ├── PersonDetection.cs
│   │   ├── PersonSighting.cs
│   │   ├── UniquePerson.cs
│   │   ├── VideoJob.cs
│   │   └── VideoPersonTimeline.cs
│   ├── Events/
│   │   └── DomainEvents.cs
│   ├── Repositories/
│   │   ├── ICameraConfigRepository.cs
│   │   └── IDetectionRepository.cs
│   ├── Services/
│   │   └── IPersonIdentityMatcher.cs
│   ├── Specifications/
│   │   └── DetectionSpecifications.cs
│   └── ValueObjects/
│       ├── BoundingBox.cs
│       └── FeatureVector.cs
│
├── Infrastructure/                           # External Concerns & Implementations
│   ├── Context/
│   │   └── DetectionContext.cs               # EF Core DbContext
│   ├── Detection/
│   │   ├── GenericOnnxDetectionEngine.cs
│   │   └── YoloV11DetectionEngine.cs         # YOLO v11 implementation
│   ├── Identity/
│   │   └── PersonIdentityService.cs
│   ├── Persistence/
│   │   ├── CameraConfigRepository.cs
│   │   └── DetectionRepository.cs
│   ├── ReId/
│   │   ├── GenericReIdEngine.cs
│   │   └── OSNetReIdEngine.cs                # Person Re-Identification
│   ├── Services/
│   │   ├── DatabaseCleanupService.cs
│   │   ├── IdentityCleanupService.cs
│   │   └── VideoProcessingService.cs
│   └── Streaming/
│       ├── CameraStreamProcessor.cs
│       └── StreamProcessorFactory.cs
│
├── Migrations/                               # EF Core Migrations
│
├── Models/                                   # ONNX ML Models
│   ├── osnet_x1_0.onnx                       # OSNet Re-ID model
│   ├── yolo11s.onnx                          # YOLO v11 small
│   ├── yolov8l.onnx                          # YOLO v8 large
│   ├── yolov8n.onnx                          # YOLO v8 nano
│   └── yolov8s.onnx                          # YOLO v8 small
│
├── uploads/                                  # Uploaded video files
│
├── appsettings.json                          # Configuration
├── PersonDetection.http                      # HTTP request tests
├── Program.cs                                # Application entry point
└── WeatherForecast.cs                        # (Default template)
```
```
#front-end structure (angular ts)
person-detection-ui/
│
├── public/                                   # Static public assets
│
├── src/
│   ├── app/
│   │   ├── core/                             # Core module (singleton services, models)
│   │   │   └── models/
│   │   │       ├── detection.models.ts
│   │   │       └── video.models.ts
│   │   │
│   │   ├── features/                         # Feature modules (lazy-loaded components)
│   │   │   ├── add-camera-dialog/
│   │   │   │   └── add-camera-dialog.component.ts
│   │   │   │
│   │   │   ├── camera-controls/
│   │   │   │   ├── camera-controls.component.html
│   │   │   │   ├── camera-controls.component.scss
│   │   │   │   └── camera-controls.component.ts
│   │   │   │
│   │   │   ├── camera-view/
│   │   │   │   ├── camera-view.component.html
│   │   │   │   ├── camera-view.component.scss
│   │   │   │   └── camera-view.component.ts
│   │   │   │
│   │   │   ├── dashboard/
│   │   │   │   ├── dashboard.component.html
│   │   │   │   ├── dashboard.component.scss
│   │   │   │   └── dashboard.component.ts
│   │   │   │
│   │   │   ├── video-detail-dialog/
│   │   │   │   ├── video-detail-dialog.component.html
│   │   │   │   ├── video-detail-dialog.component.scss
│   │   │   │   └── video-detail-dialog.component.ts
│   │   │   │
│   │   │   ├── video-jobs/
│   │   │   │   ├── video-jobs.component.html
│   │   │   │   ├── video-jobs.component.scss
│   │   │   │   └── video-jobs.component.ts
│   │   │   │
│   │   │   ├── video-player-dialog/
│   │   │   │   ├── video-player-dialog.component.html
│   │   │   │   ├── video-player-dialog.component.scss
│   │   │   │   └── video-player-dialog.component.ts
│   │   │   │
│   │   │   └── video-upload-dialog/
│   │   │       ├── video-upload-dialog.component.html
│   │   │       ├── video-upload-dialog.component.scss
│   │   │       └── video-upload-dialog.component.ts
│   │   │
│   │   ├── services/                         # Application services
│   │   │   ├── camera.service.ts
│   │   │   ├── camera-config.service.ts
│   │   │   ├── detection.service.ts
│   │   │   ├── signalr.service.ts            # SignalR client service
│   │   │   └── video.service.ts
│   │   │
│   │   ├── shared/                           # Shared components, pipes, directives
│   │   │
│   │   ├── app.config.server.ts              # Server-side config (SSR)
│   │   ├── app.config.ts                     # Client-side config
│   │   ├── app.html                          # Root component template
│   │   ├── app.routes.server.ts              # Server routes (SSR)
│   │   ├── app.routes.ts                     # Client routes
│   │   ├── app.scss                          # Root component styles
│   │   ├── app.spec.ts                       # Root component tests
│   │   └── app.ts                            # Root component
│   │
│   ├── assets/                               # Static assets
│   │   └── no-signal.svg
│   │
│   ├── environments/                         # Environment configs
│   │   ├── environment.development.ts
│   │   └── environment.ts
│   │
│   ├── proxy.conf.json                       # Dev proxy configuration
│   ├── index.html                            # Main HTML file
│   ├── main.server.ts                        # Server bootstrap (SSR)
│   ├── main.ts                               # Client bootstrap
│   ├── server.ts                             # Express server (SSR)
│   └── styles.scss                           # Global styles
│
├── uploads/                                  # Local uploads folder
│
├── .editorconfig                             # Editor configuration
├── .gitignore                                # Git ignore rules
├── angular.json                              # Angular CLI configuration
├── package.json                              # NPM dependencies
├── README.md                                 # Project documentation
└── tsconfig.json                             # TypeScript configuration
```
```
┌─────────────────────────────────────────────────────────────────────┐
│                        PRESENTATION LAYER                           │
│  ┌─────────────────────┐          ┌─────────────────────────────┐  │
│  │   Angular Frontend  │◄──────►  │    ASP.NET Core API         │  │
│  │  (person-detection- │ SignalR  │  (Controllers, Hubs)        │  │
│  │        ui)          │   HTTP   │                             │  │
│  └─────────────────────┘          └─────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
┌─────────────────────────────────────────────────────────────────────┐
│                        APPLICATION LAYER                            │
│           Commands, Queries, DTOs, Services, Interfaces             │
└─────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
┌─────────────────────────────────────────────────────────────────────┐
│                          DOMAIN LAYER                               │
│         Entities, Value Objects, Events, Repositories (I)           │
└─────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
┌─────────────────────────────────────────────────────────────────────┐
│                      INFRASTRUCTURE LAYER                           │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐ ┌────────────┐ │
│  │  Detection   │ │     ReId     │ │  Streaming   │ │Persistence │ │
│  │(YOLO, ONNX)  │ │   (OSNet)    │ │  (OpenCV)    │ │ (EF Core)  │ │
│  └──────────────┘ └──────────────┘ └──────────────┘ └────────────┘ │
└─────────────────────────────────────────────────────────────────────┘
```
