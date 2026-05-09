using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class MakeBookIdNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChatMessages_Books_BookId",
                table: "ChatMessages");

            migrationBuilder.AlterColumn<int>(
                name: "BookId",
                table: "ChatMessages",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddForeignKey(
                name: "FK_ChatMessages_Books_BookId",
                table: "ChatMessages",
                column: "BookId",
                principalTable: "Books",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChatMessages_Books_BookId",
                table: "ChatMessages");

            migrationBuilder.AlterColumn<int>(
                name: "BookId",
                table: "ChatMessages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ChatMessages_Books_BookId",
                table: "ChatMessages",
                column: "BookId",
                principalTable: "Books",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
