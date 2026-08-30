using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSinglePartMultiLotCertificationFilename : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_plant_certification_settings_multi_lot_filename_not_blank",
                table: "plant_certification_settings");

            migrationBuilder.RenameColumn(
                name: "multi_lot_filename_template",
                table: "plant_certification_settings",
                newName: "multi_part_filename_template");

            migrationBuilder.AddColumn<string>(
                name: "single_part_multi_lot_filename_template",
                table: "plant_certification_settings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_plant_certification_settings_single_part_multi_lot_filename~",
                table: "plant_certification_settings",
                sql: "btrim(single_part_multi_lot_filename_template) <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_plant_certification_settings_multi_part_filename_not_blank",
                table: "plant_certification_settings",
                sql: "btrim(multi_part_filename_template) <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_plant_certification_settings_single_part_multi_lot_filename~",
                table: "plant_certification_settings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_plant_certification_settings_multi_part_filename_not_blank",
                table: "plant_certification_settings");

            migrationBuilder.DropColumn(
                name: "single_part_multi_lot_filename_template",
                table: "plant_certification_settings");

            migrationBuilder.RenameColumn(
                name: "multi_part_filename_template",
                table: "plant_certification_settings",
                newName: "multi_lot_filename_template");

            migrationBuilder.AddCheckConstraint(
                name: "CK_plant_certification_settings_multi_lot_filename_not_blank",
                table: "plant_certification_settings",
                sql: "btrim(multi_lot_filename_template) <> ''");
        }
    }
}
