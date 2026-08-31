using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCertificationEmailTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "certification_email_templates",
                columns: table => new
                {
                    template_type = table.Column<int>(type: "integer", nullable: false),
                    subject_template = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    html_body_template = table.Column<string>(type: "text", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_by_user_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_certification_email_templates", x => x.template_type);
                    table.CheckConstraint("CK_certification_email_templates_body_not_blank", "btrim(html_body_template) <> ''");
                    table.CheckConstraint("CK_certification_email_templates_subject_not_blank", "btrim(subject_template) <> ''");
                    table.ForeignKey(
                        name: "FK_certification_email_templates_updated_by_user_id",
                        column: x => x.updated_by_user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_certification_email_templates_updated_by_user_id",
                table: "certification_email_templates",
                column: "updated_by_user_id");

            migrationBuilder.Sql("""
                INSERT INTO certification_email_templates (template_type, subject_template, html_body_template)
                VALUES
                    (0, 'Certification package for {CustomerName} - {PartNumber}, Lot {LotNumber}', '<p>Attached is the certification package for <strong>{PartNumber}</strong>, lot <strong>{LotNumber}</strong>.</p><p>Ship date: {ShipDate}.</p>'),
                    (1, 'Certification package for {CustomerName} - {PartNumber}', '<p>Attached is the certification package for <strong>{PartNumber}</strong>.</p><p>Lots: {LotNumbers}</p><p>Ship date: {ShipDate}.</p>'),
                    (2, 'Certification package for {CustomerName}', '<p>Attached is the certification package for the following parts and lots.</p>{PartLotSummary}<p>Ship date: {ShipDate}.</p>');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "certification_email_templates");
        }
    }
}
