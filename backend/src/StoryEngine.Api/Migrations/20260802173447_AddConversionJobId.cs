using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddConversionJobId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConversionJobId",
                table: "Books",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConversionJobId",
                table: "Books");
        }
    }
}
