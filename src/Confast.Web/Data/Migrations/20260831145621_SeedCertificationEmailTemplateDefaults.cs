using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeedCertificationEmailTemplateDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO certification_email_templates (template_type, subject_template, html_body_template)
                VALUES
                    (0, 'Certification package for {CustomerName} - {PartNumber}, Lot {LotNumber}', '<p>Attached is the certification package for <strong>{PartNumber}</strong>, lot <strong>{LotNumber}</strong>.</p><p>Ship date: {ShipDate}.</p>'),
                    (1, 'Certification package for {CustomerName} - {PartNumber}', '<p>Attached is the certification package for <strong>{PartNumber}</strong>.</p><p>Lots: {LotNumbers}</p><p>Ship date: {ShipDate}.</p>'),
                    (2, 'Certification package for {CustomerName}', '<p>Attached is the certification package for the following parts and lots.</p>{PartLotSummary}<p>Ship date: {ShipDate}.</p>')
                ON CONFLICT (template_type) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
