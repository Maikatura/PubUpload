using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileUploader.Migrations
{
    /// <inheritdoc />
    public partial class HashDeleteTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeleteTokenHash",
                table: "UploadedFiles",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // Hash existing tokens in place so delete links that were already handed out keep working.
            // Must match DeleteTokenHasher: uuid::text is the lowercase hyphenated form.
            migrationBuilder.Sql(
                """
                UPDATE "UploadedFiles"
                SET "DeleteTokenHash" = encode(sha256(convert_to("DeleteToken"::text, 'UTF8')), 'hex');
                """);

            migrationBuilder.DropIndex(
                name: "IX_UploadedFiles_DeleteToken",
                table: "UploadedFiles");

            migrationBuilder.DropColumn(
                name: "DeleteToken",
                table: "UploadedFiles");

            migrationBuilder.CreateIndex(
                name: "IX_UploadedFiles_DeleteTokenHash",
                table: "UploadedFiles",
                column: "DeleteTokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Hashes can't be reversed, so rolling back issues new random tokens and
            // invalidates every existing delete link.
            migrationBuilder.DropIndex(
                name: "IX_UploadedFiles_DeleteTokenHash",
                table: "UploadedFiles");

            migrationBuilder.DropColumn(
                name: "DeleteTokenHash",
                table: "UploadedFiles");

            migrationBuilder.Sql(
                """
                ALTER TABLE "UploadedFiles" ADD "DeleteToken" uuid NOT NULL DEFAULT gen_random_uuid();
                ALTER TABLE "UploadedFiles" ALTER COLUMN "DeleteToken" DROP DEFAULT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_UploadedFiles_DeleteToken",
                table: "UploadedFiles",
                column: "DeleteToken");
        }
    }
}
