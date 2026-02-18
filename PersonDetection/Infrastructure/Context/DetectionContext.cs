// PersonDetection.Infrastructure/Context/DetectionContext.cs
namespace PersonDetection.Infrastructure.Context
{
    using Microsoft.EntityFrameworkCore;
    using PersonDetection.Application.DTOs;
    using PersonDetection.Domain.Entities;

    public class DetectionContext : DbContext
    {
        public DetectionContext(DbContextOptions<DetectionContext> options)
            : base(options)
        {
        }

        public DbSet<Camera> Cameras { get; set; } = null!;
        public DbSet<CameraSession> CameraSessions { get; set; } = null!;
        public DbSet<DetectionResult> DetectionResults { get; set; } = null!;
        public DbSet<DetectedPerson> DetectedPersons { get; set; } = null!;
        public DbSet<UniquePerson> UniquePersons { get; set; } = null!;
        public DbSet<PersonSighting> PersonSightings { get; set; } = null!;

        // 👇 ADD THESE NEW DbSets
        public DbSet<VideoJob> VideoJobs { get; set; } = null!;
        public DbSet<VideoPersonTimeline> VideoPersonTimelines { get; set; } = null!;


        // ═══════════════════════════════════════════════════════════════
        // STORED PROCEDURE RESULT SETS (Keyless)
        // ═══════════════════════════════════════════════════════════════
        public DbSet<SpStatsSummary> SpStatsSummary => Set<SpStatsSummary>();
        public DbSet<SpDailyStats> SpDailyStats => Set<SpDailyStats>();
        public DbSet<SpCameraBreakdown> SpCameraBreakdown => Set<SpCameraBreakdown>();
        public DbSet<SpHourlyStats> SpHourlyStats => Set<SpHourlyStats>();


        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Camera
            modelBuilder.Entity<Camera>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
                builder.Property(x => x.Url).HasMaxLength(500).IsRequired();
                builder.Property(x => x.Description).HasMaxLength(500);
                builder.HasIndex(x => x.IsEnabled);
                builder.Ignore(x => x.DomainEvents);
            });

            // CameraSession
            modelBuilder.Entity<CameraSession>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Url).HasMaxLength(500);
                builder.HasIndex(x => new { x.CameraId, x.IsActive });
                builder.Ignore(x => x.DomainEvents);
            });

            // DetectionResult
            modelBuilder.Entity<DetectionResult>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasIndex(x => x.CameraId);
                builder.HasIndex(x => x.Timestamp);
                builder.HasIndex(x => x.VideoJobId);  // 👈 ADD INDEX
                builder.HasOne(x => x.VideoJob)       // 👈 ADD RELATIONSHIP
                    .WithMany(v => v.DetectionResults)
                    .HasForeignKey(x => x.VideoJobId)
                    .OnDelete(DeleteBehavior.SetNull);
                builder.Ignore(x => x.DomainEvents);
            });

            // DetectedPerson
            modelBuilder.Entity<DetectedPerson>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasIndex(x => x.GlobalPersonId);
                builder.HasIndex(x => x.DetectedAt);
                builder.HasIndex(x => x.VideoJobId);  // 👈 ADD INDEX
                builder.Property(x => x.FeatureVector).HasMaxLength(8000);
                builder.HasOne(x => x.VideoJob)       // 👈 ADD RELATIONSHIP
                    .WithMany(v => v.DetectedPersons)
                    .HasForeignKey(x => x.VideoJobId)
                    .OnDelete(DeleteBehavior.SetNull);
                builder.Ignore(x => x.BoundingBox);
                builder.Ignore(x => x.DomainEvents);
            });

            // UniquePerson
            modelBuilder.Entity<UniquePerson>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasIndex(x => x.GlobalPersonId).IsUnique();
                builder.HasIndex(x => x.LastSeenAt);
                builder.Property(x => x.Label).HasMaxLength(100);
                builder.Ignore(x => x.DomainEvents);
            });

            // PersonSighting
            modelBuilder.Entity<PersonSighting>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasIndex(x => x.UniquePersonId);
                builder.HasIndex(x => x.CameraId);
                builder.HasIndex(x => x.SeenAt);
                builder.Ignore(x => x.DomainEvents);
            });

            // 👇 ADD NEW CONFIGURATIONS

            // VideoJob
            modelBuilder.Entity<VideoJob>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasIndex(x => x.JobId).IsUnique();
                builder.HasIndex(x => x.State);
                builder.HasIndex(x => x.CreatedAt);
                builder.Property(x => x.FileName).HasMaxLength(500).IsRequired();
                builder.Property(x => x.OriginalFilePath).HasMaxLength(1000);
                builder.Property(x => x.StoredFilePath).HasMaxLength(1000);
                builder.Property(x => x.VideoDataBase64).HasColumnType("nvarchar(max)");
                builder.Property(x => x.ErrorMessage).HasMaxLength(2000);
                builder.Ignore(x => x.DomainEvents);
            });

            // VideoPersonTimeline
            modelBuilder.Entity<VideoPersonTimeline>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasIndex(x => x.VideoJobId);
                builder.HasIndex(x => x.GlobalPersonId);
                builder.HasIndex(x => x.UniquePersonId);
                builder.Property(x => x.ThumbnailBase64).HasColumnType("nvarchar(max)");
                builder.Property(x => x.FeatureVector).HasMaxLength(8000);
                builder.HasOne(x => x.VideoJob)
                    .WithMany(v => v.PersonTimelines)
                    .HasForeignKey(x => x.VideoJobId)
                    .OnDelete(DeleteBehavior.Cascade);
                builder.HasOne(x => x.UniquePerson)
                    .WithMany()
                    .HasForeignKey(x => x.UniquePersonId)
                    .OnDelete(DeleteBehavior.SetNull);
                builder.Ignore(x => x.DomainEvents);
            });

            // Seed default webcam
            modelBuilder.Entity<Camera>().HasData(
                new Camera
                {
                    Id = 1,
                    Name = "Webcam",
                    Url = "0",
                    Type = CameraType.Webcam,
                    Description = "Built-in webcam",
                    IsEnabled = true,
                    CreatedAt = new DateTime(2025, 12, 22, 0, 0, 0, DateTimeKind.Utc),
                    DisplayOrder = 0
                }
            );

            // ═══════════════════════════════════════════════════════════
            // KEYLESS ENTITIES FOR STORED PROCEDURES
            // ═══════════════════════════════════════════════════════════
            modelBuilder.Entity<SpStatsSummary>(entity =>
            {
                entity.HasNoKey();
                entity.ToView(null); // Not mapped to any view/table
            });

            modelBuilder.Entity<SpDailyStats>(entity =>
            {
                entity.HasNoKey();
                entity.ToView(null);
            });

            modelBuilder.Entity<SpCameraBreakdown>(entity =>
            {
                entity.HasNoKey();
                entity.ToView(null);
            });

            modelBuilder.Entity<SpHourlyStats>(entity =>
            {
                entity.HasNoKey();
                entity.ToView(null);
            });
        }
    }
}