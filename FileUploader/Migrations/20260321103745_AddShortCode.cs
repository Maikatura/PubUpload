using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileUploader.Migrations;
    /// <inheritdoc />
    public partial class AddShortCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ShortCode",
                table: "UploadedFiles",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            // Generate short codes for existing records
            migrationBuilder.Sql(@"
            UPDATE ""UploadedFiles""
            SET ""ShortCode"" = substr(md5(random()::text || ""Id""::text), 1, 7)
            WHERE ""ShortCode"" IS NULL;
        ");

            // Make it NOT NULL after populating
            migrationBuilder.AlterColumn<string>(
                name: "ShortCode",
                table: "UploadedFiles",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10,
                oldNullable: true);

            // Create unique index
            migrationBuilder.CreateIndex(
                name: "IX_UploadedFiles_ShortCode",
                table: "UploadedFiles",
                column: "ShortCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UploadedFiles_ShortCode",
                table: "UploadedFiles");

            migrationBuilder.DropColumn(
                name: "ShortCode",
                table: "UploadedFiles");
        }
    }

