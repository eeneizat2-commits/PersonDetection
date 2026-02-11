using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PersonDetection.API.Controllers;
using PersonDetection.API.Hubs;
using PersonDetection.API.Middleware;
using PersonDetection.Application.Commands;
using PersonDetection.Application.Common;
using PersonDetection.Application.Configuration;
using PersonDetection.Application.DTOs;
using PersonDetection.Application.Interfaces;
using PersonDetection.Application.IService;
using PersonDetection.Application.Queries;
using PersonDetection.Application.Services;
using PersonDetection.Domain.Repositories;
using PersonDetection.Domain.Services;
using PersonDetection.Infrastructure.Context;
using PersonDetection.Infrastructure.Detection;
using PersonDetection.Infrastructure.Identity;
using PersonDetection.Infrastructure.Persistence;
using PersonDetection.Infrastructure.ReId;
using PersonDetection.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// ============================================
// CONFIGURATION
// ============================================
builder.Services.Configure<DetectionSettings>(
    builder.Configuration.GetSection(DetectionSettings.SectionName));
builder.Services.Configure<IdentitySettings>(
    builder.Configuration.GetSection(IdentitySettings.SectionName));
builder.Services.Configure<StreamingSettings>(
    builder.Configuration.GetSection(StreamingSettings.SectionName));
builder.Services.Configure<SignalRSettings>(
    builder.Configuration.GetSection(SignalRSettings.SectionName));
builder.Services.Configure<PersistenceSettings>(
    builder.Configuration.GetSection(PersistenceSettings.SectionName));
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 500 * 1024 * 1024; // 500 MB
});

var detectionSettings = builder.Configuration
    .GetSection(DetectionSettings.SectionName)
    .Get<DetectionSettings>() ?? new DetectionSettings();
var identitySettings = builder.Configuration
    .GetSection(IdentitySettings.SectionName)
    .Get<IdentitySettings>() ?? new IdentitySettings();
var signalRSettings = builder.Configuration
    .GetSection(SignalRSettings.SectionName)
    .Get<SignalRSettings>() ?? new SignalRSettings();
var persistenceSettings = builder.Configuration
    .GetSection(PersistenceSettings.SectionName)
    .Get<PersistenceSettings>() ?? new PersistenceSettings();

// ============================================
// BASIC SERVICES
// ============================================
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Person Detection API", Version = "v1" });
});

// SignalR
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
    options.MaximumReceiveMessageSize = 102400;
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
})
.AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
               "http://localhost:4200",   // Angular dev server
               "https://localhost:4200",
               "http://localhost:4000",   // Angular SSR
               "https://localhost:4000")
             .AllowAnyMethod()
             .AllowAnyHeader()
             .AllowCredentials();  // Required for SignalR
    });
});
// ============================================
// DATABASE
// ============================================
builder.Services.AddDbContext<DetectionContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOptions =>
        {
            sqlOptions.EnableRetryOnFailure(3, TimeSpan.FromSeconds(10), null);
            sqlOptions.CommandTimeout(30);
        }),
    ServiceLifetime.Scoped);

// ============================================
// REPOSITORIES & UNIT OF WORK
// ============================================
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<ICameraConfigRepository, CameraConfigRepository>();
// ============================================
// DISPATCHERS
// ============================================
builder.Services.AddScoped<ICommandDispatcher, CommandDispatcher>();
builder.Services.AddScoped<IQueryDispatcher, QueryDispatcher>();
builder.Services.AddScoped<ISignalRNotificationService, SignalRNotificationService>();
// ============================================
// COMMAND HANDLERS
// ============================================
builder.Services.AddScoped<ICommandHandler<StartCameraCommand, CameraSessionDto>, StartCameraHandler>();
builder.Services.AddScoped<ICommandHandler<StopCameraCommand, Unit>, StopCameraHandler>();
builder.Services.AddScoped<ICommandHandler<ProcessFrameCommand, DetectionResultDto>>(sp =>
    new ProcessFrameHandler<YoloDetectionConfig, OSNetConfig>(
        sp.GetRequiredService<IDetectionEngine<YoloDetectionConfig>>(),
        sp.GetRequiredService<IReIdentificationEngine<OSNetConfig>>(),
        sp.GetRequiredService<IPersonIdentityMatcher>(),
        sp.GetRequiredService<IUnitOfWork>(),
        sp.GetRequiredService<IOptions<PersistenceSettings>>(),  // Add this
        sp.GetRequiredService<ILogger<ProcessFrameHandler<YoloDetectionConfig, OSNetConfig>>>()
    ));

// ============================================
// QUERY HANDLERS
// ============================================
builder.Services.AddScoped<IQueryHandler<GetActiveCamerasQuery, ActiveCamerasDto>, GetActiveCamerasHandler>();
builder.Services.AddScoped<IQueryHandler<GetCameraStatsQuery, CameraStatsDto>, GetCameraStatsHandler>();
builder.Services.AddScoped<ICommandHandler<ProcessVideoCommand, VideoUploadResultDto>, ProcessVideoHandler>();
builder.Services.AddScoped<IQueryHandler<GetVideoStatusQuery, VideoProcessingStatusDto?>, GetVideoStatusHandler>();
builder.Services.AddScoped<IQueryHandler<GetVideoSummaryQuery, VideoProcessingSummaryDto?>, GetVideoSummaryHandler>();
builder.Services.AddScoped<IQueryHandler<GetAllVideoJobsQuery, List<VideoProcessingStatusDto>>, GetAllVideoJobsHandler>();

// ============================================
// DOMAIN SERVICES
// ============================================
// Add TrackingSettings configuration
builder.Services.Configure<TrackingSettings>(
    builder.Configuration.GetSection(TrackingSettings.SectionName));

// Load settings
var trackingSettings = builder.Configuration
    .GetSection(TrackingSettings.SectionName)
    .Get<TrackingSettings>() ?? new TrackingSettings();

// Register PersonIdentityService with new settings
// In Program.cs, update the PersonIdentityService registration:

builder.Services.AddSingleton<IPersonIdentityMatcher>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<IdentitySettings>>().Value;
    return new PersonIdentityService(
        sp,
        settings,
        sp.GetRequiredService<ILogger<PersonIdentityService>>());
});

// ============================================
// DETECTION & RE-ID ENGINES (Singleton - expensive to create)
// ============================================
builder.Services.AddSingleton<IDetectionEngine<YoloDetectionConfig>>(sp =>
    new YoloV11DetectionEngine(
        detectionSettings.YoloModelPath,
        detectionSettings.UseGpu,
        sp.GetRequiredService<ILogger<YoloV11DetectionEngine>>()));

builder.Services.AddSingleton<IReIdentificationEngine<OSNetConfig>>(sp =>
    new OSNetReIdEngine(
        detectionSettings.ReIdModelPath,
        detectionSettings.UseGpu,
        sp.GetRequiredService<ILogger<OSNetReIdEngine>>()));

// ============================================
// STREAM PROCESSING (Use fully qualified to avoid ambiguity)
// ============================================
builder.Services.AddSingleton<IStreamProcessorFactory, PersonDetection.Infrastructure.Streaming.StreamProcessorFactory>();
// ============================================
// VIDEO PROCESSING
// ============================================

// In Program.cs or your DI configuration
builder.Services.AddSingleton<IVideoProcessingService>(sp =>
    new VideoProcessingService(
        sp.GetRequiredService<IDetectionEngine<YoloDetectionConfig>>(),
        sp.GetService<IReIdentificationEngine<OSNetConfig>>(),   // Optional
        sp.GetRequiredService<IHubContext<DetectionHub>>(),
        sp.GetRequiredService<IServiceScopeFactory>(),           // ✅ FIX
        sp.GetRequiredService<ILogger<VideoProcessingService>>()
    ));

// ============================================
// BACKGROUND SERVICES
// ============================================
builder.Services.AddHostedService<IdentityCleanupService>();
builder.Services.AddHostedService<DatabaseCleanupService>();

// ============================================
// RESPONSE COMPRESSION
// ============================================
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "image/jpeg",
        "application/octet-stream"
    });
});

// ============================================
// HEALTH CHECKS
// ============================================
builder.Services.AddHealthChecks()
    .AddCheck("database", () =>
    {
        try
        {
            using var scope = builder.Services.BuildServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DetectionContext>();
            return db.Database.CanConnect()
                ? HealthCheckResult.Healthy("Database connection OK")
                : HealthCheckResult.Unhealthy("Cannot connect to database");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Database error: {ex.Message}");
        }
    })
    .AddCheck("yolo-model", () =>
    {
        return File.Exists(detectionSettings.YoloModelPath)
            ? HealthCheckResult.Healthy("YOLO model found")
            : HealthCheckResult.Unhealthy("YOLO model not found");
    })
    .AddCheck("reid-model", () =>
    {
        return File.Exists(detectionSettings.ReIdModelPath)
            ? HealthCheckResult.Healthy("ReID model found")
            : HealthCheckResult.Unhealthy("ReID model not found");
    });

// ============================================
// BUILD APP
// ============================================
var app = builder.Build();

// ============================================
// MIDDLEWARE PIPELINE
// ============================================
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Person Detection API V1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseCors();
app.UseHttpsRedirection();
app.UseResponseCompression();

app.MapControllers();
app.MapHub<DetectionHub>("/detectionHub");
app.MapHealthChecks("/health");

// ============================================
// DATABASE MIGRATION (Development only)
// ============================================
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<DetectionContext>();
    db.Database.EnsureCreated();
}

app.Run();