using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonDetection.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Cameras",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Type = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastConnectedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cameras", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CameraSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CameraId = table.Column<int>(type: "int", nullable: false),
                    Url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StoppedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CameraSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UniquePersons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GlobalPersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FirstSeenCameraId = table.Column<int>(type: "int", nullable: false),
                    LastSeenCameraId = table.Column<int>(type: "int", nullable: false),
                    TotalSightings = table.Column<int>(type: "int", nullable: false),
                    FeatureVector = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ThumbnailData = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Label = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UniquePersons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VideoJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    OriginalFilePath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    VideoDataBase64 = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    TotalFrames = table.Column<int>(type: "int", nullable: false),
                    ProcessedFrames = table.Column<int>(type: "int", nullable: false),
                    TotalDetections = table.Column<int>(type: "int", nullable: false),
                    UniquePersonCount = table.Column<int>(type: "int", nullable: false),
                    VideoDurationSeconds = table.Column<double>(type: "float", nullable: false),
                    VideoFps = table.Column<double>(type: "float", nullable: false),
                    ProcessingTimeSeconds = table.Column<double>(type: "float", nullable: false),
                    FrameSkip = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DetectionResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CameraId = table.Column<int>(type: "int", nullable: false),
                    VideoJobId = table.Column<int>(type: "int", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TotalDetections = table.Column<int>(type: "int", nullable: false),
                    ValidDetections = table.Column<int>(type: "int", nullable: false),
                    UniquePersonCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DetectionResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DetectionResults_VideoJobs_VideoJobId",
                        column: x => x.VideoJobId,
                        principalTable: "VideoJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "VideoPersonTimelines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VideoJobId = table.Column<int>(type: "int", nullable: false),
                    GlobalPersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UniquePersonId = table.Column<int>(type: "int", nullable: true),
                    FirstAppearanceSeconds = table.Column<double>(type: "float", nullable: false),
                    LastAppearanceSeconds = table.Column<double>(type: "float", nullable: false),
                    TotalAppearances = table.Column<int>(type: "int", nullable: false),
                    AverageConfidence = table.Column<float>(type: "real", nullable: false),
                    ThumbnailBase64 = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FeatureVector = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoPersonTimelines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VideoPersonTimelines_UniquePersons_UniquePersonId",
                        column: x => x.UniquePersonId,
                        principalTable: "UniquePersons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_VideoPersonTimelines_VideoJobs_VideoJobId",
                        column: x => x.VideoJobId,
                        principalTable: "VideoJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DetectedPersons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GlobalPersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Confidence = table.Column<float>(type: "real", nullable: false),
                    BoundingBox_X = table.Column<int>(type: "int", nullable: false),
                    BoundingBox_Y = table.Column<int>(type: "int", nullable: false),
                    BoundingBox_Width = table.Column<int>(type: "int", nullable: false),
                    BoundingBox_Height = table.Column<int>(type: "int", nullable: false),
                    FeatureVector = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    TrackId = table.Column<int>(type: "int", nullable: true),
                    DetectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DetectionResultId = table.Column<int>(type: "int", nullable: false),
                    VideoJobId = table.Column<int>(type: "int", nullable: true),
                    FrameNumber = table.Column<int>(type: "int", nullable: true),
                    TimestampSeconds = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DetectedPersons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DetectedPersons_DetectionResults_DetectionResultId",
                        column: x => x.DetectionResultId,
                        principalTable: "DetectionResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DetectedPersons_VideoJobs_VideoJobId",
                        column: x => x.VideoJobId,
                        principalTable: "VideoJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PersonSightings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UniquePersonId = table.Column<int>(type: "int", nullable: false),
                    CameraId = table.Column<int>(type: "int", nullable: false),
                    DetectionResultId = table.Column<int>(type: "int", nullable: true),
                    SeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Confidence = table.Column<float>(type: "real", nullable: false),
                    BoundingBox_X = table.Column<int>(type: "int", nullable: false),
                    BoundingBox_Y = table.Column<int>(type: "int", nullable: false),
                    BoundingBox_Width = table.Column<int>(type: "int", nullable: false),
                    BoundingBox_Height = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonSightings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PersonSightings_DetectionResults_DetectionResultId",
                        column: x => x.DetectionResultId,
                        principalTable: "DetectionResults",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PersonSightings_UniquePersons_UniquePersonId",
                        column: x => x.UniquePersonId,
                        principalTable: "UniquePersons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Cameras",
                columns: new[] { "Id", "CreatedAt", "Description", "DisplayOrder", "IsEnabled", "LastConnectedAt", "Name", "Type", "Url" },
                values: new object[] { 1, new DateTime(2025, 12, 22, 0, 0, 0, 0, DateTimeKind.Utc), "Built-in webcam", 0, true, null, "Webcam", 0, "0" });

            migrationBuilder.CreateIndex(
                name: "IX_Cameras_IsEnabled",
                table: "Cameras",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_CameraSessions_CameraId_IsActive",
                table: "CameraSessions",
                columns: new[] { "CameraId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_DetectedPersons_DetectedAt",
                table: "DetectedPersons",
                column: "DetectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DetectedPersons_DetectionResultId",
                table: "DetectedPersons",
                column: "DetectionResultId");

            migrationBuilder.CreateIndex(
                name: "IX_DetectedPersons_GlobalPersonId",
                table: "DetectedPersons",
                column: "GlobalPersonId");

            migrationBuilder.CreateIndex(
                name: "IX_DetectedPersons_VideoJobId",
                table: "DetectedPersons",
                column: "VideoJobId");

            migrationBuilder.CreateIndex(
                name: "IX_DetectionResults_CameraId",
                table: "DetectionResults",
                column: "CameraId");

            migrationBuilder.CreateIndex(
                name: "IX_DetectionResults_Timestamp",
                table: "DetectionResults",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_DetectionResults_VideoJobId",
                table: "DetectionResults",
                column: "VideoJobId");

            migrationBuilder.CreateIndex(
                name: "IX_PersonSightings_CameraId",
                table: "PersonSightings",
                column: "CameraId");

            migrationBuilder.CreateIndex(
                name: "IX_PersonSightings_DetectionResultId",
                table: "PersonSightings",
                column: "DetectionResultId");

            migrationBuilder.CreateIndex(
                name: "IX_PersonSightings_SeenAt",
                table: "PersonSightings",
                column: "SeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_PersonSightings_UniquePersonId",
                table: "PersonSightings",
                column: "UniquePersonId");

            migrationBuilder.CreateIndex(
                name: "IX_UniquePersons_GlobalPersonId",
                table: "UniquePersons",
                column: "GlobalPersonId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UniquePersons_LastSeenAt",
                table: "UniquePersons",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_VideoJobs_CreatedAt",
                table: "VideoJobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_VideoJobs_JobId",
                table: "VideoJobs",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VideoJobs_State",
                table: "VideoJobs",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_VideoPersonTimelines_GlobalPersonId",
                table: "VideoPersonTimelines",
                column: "GlobalPersonId");

            migrationBuilder.CreateIndex(
                name: "IX_VideoPersonTimelines_UniquePersonId",
                table: "VideoPersonTimelines",
                column: "UniquePersonId");

            migrationBuilder.CreateIndex(
                name: "IX_VideoPersonTimelines_VideoJobId",
                table: "VideoPersonTimelines",
                column: "VideoJobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Cameras");

            migrationBuilder.DropTable(
                name: "CameraSessions");

            migrationBuilder.DropTable(
                name: "DetectedPersons");

            migrationBuilder.DropTable(
                name: "PersonSightings");

            migrationBuilder.DropTable(
                name: "VideoPersonTimelines");

            migrationBuilder.DropTable(
                name: "DetectionResults");

            migrationBuilder.DropTable(
                name: "UniquePersons");

            migrationBuilder.DropTable(
                name: "VideoJobs");
        }
    }
}
