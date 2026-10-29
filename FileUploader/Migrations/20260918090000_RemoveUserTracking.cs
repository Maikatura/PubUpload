using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileUploader.Migrations;
    /// <inheritdoc />
    public partial class RemoveUserTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UploadedFiles_IpAddress",
                table: "UploadedFiles");

            migrationBuilder.DropColumn(
                name: "IpAddress",
                table: "UploadedFiles");

            migrationBuilder.DropColumn(
                name: "UserAgent",
                table: "UploadedFiles");

            migrationBuilder.DropColumn(
                name: "IsBanned",
                table: "UploadedFiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IpAddress",
                table: "UploadedFiles",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UserAgent",
                table: "UploadedFiles",
                type: "character varying(150)",
                maxLength: 150,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsBanned",
                table: "UploadedFiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_UploadedFiles_IpAddress",
                table: "UploadedFiles",
                column: "IpAddress");
        }
    }