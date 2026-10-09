using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthorizationFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "identity_roles",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_enabled",
                table: "identity_roles",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "system_key",
                table: "identity_roles",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "system_kind",
                table: "identity_roles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "authorization_change_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_kind = table.Column<int>(type: "integer", nullable: false),
                    actor_user_id = table.Column<string>(type: "text", nullable: true),
                    purpose = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    previous_epoch = table.Column<long>(type: "bigint", nullable: false),
                    resulting_epoch = table.Column<long>(type: "bigint", nullable: false),
                    installation_generation = table.Column<Guid>(type: "uuid", nullable: false),
                    direct_anchor_role_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authorization_change_history", x => x.id);
                    table.CheckConstraint("CK_authorization_history_actor", "(actor_kind = 0 AND actor_user_id IS NOT NULL) OR (actor_kind = 1 AND actor_user_id IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "authorization_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    root_user_id = table.Column<string>(type: "text", nullable: true),
                    baseline_role_id = table.Column<string>(type: "text", nullable: false),
                    root_role_id = table.Column<string>(type: "text", nullable: false),
                    global_epoch = table.Column<long>(type: "bigint", nullable: false),
                    installation_generation = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    catalog_version = table.Column<string>(type: "text", nullable: false),
                    readiness = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authorization_state", x => x.id);
                    table.CheckConstraint("CK_authorization_readiness", "(readiness = 0 AND root_user_id IS NULL) OR (readiness = 1 AND root_user_id IS NOT NULL)");
                    table.CheckConstraint("CK_authorization_singleton", "id = 1 AND global_epoch >= 0");
                    table.ForeignKey(
                        name: "FK_authorization_state_identity_roles_baseline_role_id",
                        column: x => x.baseline_role_id,
                        principalTable: "identity_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_authorization_state_identity_roles_root_role_id",
                        column: x => x.root_role_id,
                        principalTable: "identity_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_authorization_state_identity_users_root_user_id",
                        column: x => x.root_user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "permissions",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    authority = table.Column<int>(type: "integer", nullable: false),
                    allowed_in_baseline = table.Column<bool>(type: "boolean", nullable: false),
                    catalog_version = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permissions", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "role_inheritance",
                columns: table => new
                {
                    child_role_id = table.Column<string>(type: "text", nullable: false),
                    parent_role_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_inheritance", x => new { x.child_role_id, x.parent_role_id });
                    table.CheckConstraint("CK_role_inheritance_not_self", "child_role_id <> parent_role_id");
                    table.ForeignKey(
                        name: "FK_role_inheritance_identity_roles_child_role_id",
                        column: x => x.child_role_id,
                        principalTable: "identity_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_role_inheritance_identity_roles_parent_role_id",
                        column: x => x.parent_role_id,
                        principalTable: "identity_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "authorization_change_details",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    history_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    role_id = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<string>(type: "text", nullable: true),
                    permission_key = table.Column<string>(type: "text", nullable: true),
                    parent_role_id = table.Column<string>(type: "text", nullable: true),
                    was_present = table.Column<bool>(type: "boolean", nullable: true),
                    is_present = table.Column<bool>(type: "boolean", nullable: true),
                    previous_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    resulting_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    was_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authorization_change_details", x => x.id);
                    table.ForeignKey(
                        name: "FK_authorization_change_details_authorization_change_history_h~",
                        column: x => x.history_id,
                        principalTable: "authorization_change_history",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                columns: table => new
                {
                    role_id = table.Column<string>(type: "text", nullable: false),
                    permission_key = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permissions", x => new { x.role_id, x.permission_key });
                    table.ForeignKey(
                        name: "FK_role_permissions_identity_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "identity_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_role_permissions_permissions_permission_key",
                        column: x => x.permission_key,
                        principalTable: "permissions",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                table: "identity_roles",
                keyColumn: "id",
                keyValue: "1b171cb9-9273-42fc-b790-ea934dbb12b9",
                columns: new[] { "description", "is_enabled", "system_key", "system_kind" },
                values: new object[] { null, true, "ReadOnlyBaseline", 1 });

            migrationBuilder.UpdateData(
                table: "identity_roles",
                keyColumn: "id",
                keyValue: "47cd3d4a-0d66-4acf-8556-4017336798d8",
                columns: new[] { "description", "is_enabled", "system_key", "system_kind" },
                values: new object[] { null, true, null, 0 });

            migrationBuilder.UpdateData(
                table: "identity_roles",
                keyColumn: "id",
                keyValue: "56b3fc07-e152-42ca-b074-a823900c93b3",
                columns: new[] { "description", "is_enabled", "system_key", "system_kind" },
                values: new object[] { null, true, null, 0 });

            migrationBuilder.UpdateData(
                table: "identity_roles",
                keyColumn: "id",
                keyValue: "9eb9ef78-7737-47a5-89fc-10513d3e9c1b",
                columns: new[] { "description", "is_enabled", "system_key", "system_kind" },
                values: new object[] { null, true, null, 0 });

            migrationBuilder.InsertData(
                table: "identity_roles",
                columns: new[] { "id", "concurrency_stamp", "description", "is_enabled", "name", "normalized_name", "system_key", "system_kind" },
                values: new object[] { "e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6", "e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6", null, true, "Root Administrator Prime", "ROOT ADMINISTRATOR PRIME", "RootAdministratorPrime", 2 });

            migrationBuilder.InsertData(
                table: "permissions",
                columns: new[] { "key", "allowed_in_baseline", "authority", "catalog_version", "category", "display_name", "kind" },
                values: new object[,]
                {
                    { "Authorization.ManageSecurity", false, 1, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Authorization Security", "Manage Protected Authorization and Root Security", 1 },
                    { "BillsOfLading.CorrectDeparted", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Correct Bills Shared with Departed Containers", 1 },
                    { "BillsOfLading.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Create Bills of Lading", 0 },
                    { "BillsOfLading.Read", true, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Read Bills of Lading", 0 },
                    { "BillsOfLading.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Update Bills of Lading", 0 },
                    { "CertificationEmailSettings.Read", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Email Administration", "Read Global Certification Email Settings", 0 },
                    { "CertificationEmailSettings.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Email Administration", "Update Global Certification Email Settings", 0 },
                    { "CertificationEmailTemplates.Read", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Email Administration", "Read Certification Email Templates", 0 },
                    { "CertificationEmailTemplates.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Email Administration", "Update Certification Email Templates", 0 },
                    { "Certifications.BuildPackage", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Certifications", "Build and Export Certification Packages", 1 },
                    { "Certifications.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Certifications", "Upload Certification Documents", 0 },
                    { "Certifications.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Certifications", "Delete Certification Documents", 0 },
                    { "Certifications.Read", true, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Certifications", "Read Certification Documents", 0 },
                    { "Certifications.SendEmail", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Certifications", "Send Certification Package Email", 1 },
                    { "Certifications.TemporarilyCompletePackage", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Certifications", "Temporarily Complete Package Rendering", 1 },
                    { "Chat.Access", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Access Chat", 0 },
                    { "Chat.AdministerChannels", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Administer Any Channel Metadata or Deletion", 1 },
                    { "Chat.CreateCategories", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Create Chat Categories", 1 },
                    { "Chat.CreateChannels", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Create or Duplicate Channels", 1 },
                    { "Chat.CreateConversations", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Create Direct and Group Conversations", 1 },
                    { "Chat.CreatePolls", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Create Chat Polls", 1 },
                    { "Chat.CreateThreads", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Create Channel Threads", 1 },
                    { "Chat.DeleteChannels", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Delete Owned Channels", 1 },
                    { "Chat.DeleteOthersMessages", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Delete Other Users' Messages", 1 },
                    { "Chat.DeleteOwnMessages", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Delete Own Chat Messages", 1 },
                    { "Chat.EditOwnMessages", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Edit Own Chat Messages", 1 },
                    { "Chat.ManageCategories", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Manage Accessible Chat Categories", 1 },
                    { "Chat.ManageGroupConversations", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Manage Group Conversation Membership and Metadata", 1 },
                    { "Chat.ManagePrivateChannelMembers", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Manage Owned Private Channel Members", 1 },
                    { "Chat.PinMessages", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Pin Chat Messages", 1 },
                    { "Chat.React", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "React to Chat Messages", 1 },
                    { "Chat.ReorderChannels", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Reorder and Relocate Accessible Channels", 1 },
                    { "Chat.ScheduleMessages", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Schedule Chat Messages", 1 },
                    { "Chat.SendMessages", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Send Chat Messages", 1 },
                    { "Chat.UpdateChannels", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Update Owned Channel Metadata", 1 },
                    { "Chat.VotePolls", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Chat", "Vote in Chat Polls", 1 },
                    { "ContainerContents.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Replace Container Contents", 0 },
                    { "Containers.CorrectDepartedMetadata", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Correct Departed Container Metadata", 1 },
                    { "Containers.CorrectReceipt", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Correct or Reconcile Container Receipt", 1 },
                    { "Containers.CorrectReceivedQuantities", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Correct Actual Received Quantities", 1 },
                    { "Containers.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Create Containers", 0 },
                    { "Containers.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Delete Containers", 0 },
                    { "Containers.DeleteDeparted", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Delete Departed Containers", 1 },
                    { "Containers.Read", true, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Read Containers", 0 },
                    { "Containers.Receive", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Receive Containers", 1 },
                    { "Containers.Unreceive", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Unreceive Containers", 1 },
                    { "Containers.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Update Containers", 0 },
                    { "Customers.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Customers", "Create Customers", 0 },
                    { "Customers.Read", true, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Customers", "Read Customers", 0 },
                    { "Customers.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Customers", "Update Customers", 0 },
                    { "Development.SendTestEmail", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Development", "Send Development SMTP Test", 1 },
                    { "Development.SetBusinessDate", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Development", "Set Development Business Date", 1 },
                    { "Gages.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Gages", "Create Gages", 0 },
                    { "Gages.Read", true, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Gages", "Read Gages", 0 },
                    { "Gages.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Gages", "Update Gages", 0 },
                    { "GageTypes.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Gages", "Create Gage Types", 0 },
                    { "GageTypes.Read", true, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Gages", "Read Gage Types", 0 },
                    { "GageTypes.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Gages", "Update Gage Types", 0 },
                    { "InspectionCriteria.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspection Criteria", "Create Inspection Criteria Revisions", 0 },
                    { "InspectionCriteria.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspection Criteria", "Delete Inspection Criteria Revisions", 0 },
                    { "InspectionCriteria.Publish", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspection Criteria", "Publish Inspection Criteria Revision", 1 },
                    { "InspectionCriteria.Read", true, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspection Criteria", "Read Inspection Criteria", 0 },
                    { "InspectionCriteria.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspection Criteria", "Update Inspection Criteria Revisions", 0 },
                    { "Inspections.ApproveDeviation", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspections", "Change Deviation Approval", 1 },
                    { "Inspections.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspections", "Create Inspections", 0 },
                    { "Inspections.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspections", "Delete Inspections", 0 },
                    { "Inspections.Duplicate", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspections", "Duplicate Inspection Lots", 1 },
                    { "Inspections.Flip", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspections", "Flip Inspection to Another Part", 1 },
                    { "Inspections.Read", true, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspections", "Read Inspections", 0 },
                    { "Inspections.TransferQuantity", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspections", "Transfer Additional Lot Quantity", 1 },
                    { "Inspections.UndoLineage", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspections", "Undo Lot Lineage Operation", 1 },
                    { "Inspections.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspections", "Update Inspections", 0 },
                    { "InspectionSheets.Export", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspections", "Export Inspection Sheets", 1 },
                    { "MachineDowntime.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Scheduling", "Create Planned Machine Downtime", 0 },
                    { "MachineDowntime.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Scheduling", "Delete Planned Machine Downtime", 0 },
                    { "MachineDowntime.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Scheduling", "Update Planned Machine Downtime", 0 },
                    { "MasterPrints.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspection Criteria", "Delete Master Prints", 0 },
                    { "MasterPrints.Read", true, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspection Criteria", "Read Master Prints", 0 },
                    { "MasterPrints.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Inspection Criteria", "Upload or Replace Master Prints", 0 },
                    { "NominalToleranceSettings.Read", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Global Settings", "Read Nominal Tolerance Settings", 0 },
                    { "NominalToleranceSettings.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Global Settings", "Update Nominal Tolerance Settings", 0 },
                    { "PartFlipDefinitions.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Part Flip Definitions", "Create Part Flip Definitions", 0 },
                    { "PartFlipDefinitions.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Part Flip Definitions", "Delete Part Flip Definitions", 0 },
                    { "PartFlipDefinitions.Read", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Part Flip Definitions", "Read Part Flip Configuration", 0 },
                    { "PartFlipDefinitions.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Part Flip Definitions", "Update Part Flip Definitions", 0 },
                    { "PartMachineEligibility.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Add Part Machine Eligibility", 0 },
                    { "PartMachineEligibility.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Remove Part Machine Eligibility", 0 },
                    { "PartMachineEligibility.Read", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Read Part Machine Eligibility and Rates", 0 },
                    { "PartMachineEligibility.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Update Part Machine Rate or Preference", 0 },
                    { "Parts.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Parts", "Create Parts", 0 },
                    { "Parts.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Parts", "Delete Parts", 0 },
                    { "Parts.Read", true, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Parts", "Read Parts", 0 },
                    { "Parts.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Parts", "Update Parts", 0 },
                    { "PlantCertificationRecipients.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Plant Certification Delivery", "Create Plant Certification Recipients", 0 },
                    { "PlantCertificationRecipients.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Plant Certification Delivery", "Delete Plant Certification Recipients", 0 },
                    { "PlantCertificationRecipients.Read", true, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Plant Certification Delivery", "Read Plant Certification Recipients", 0 },
                    { "PlantCertificationRecipients.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Plant Certification Delivery", "Update Plant Certification Recipients", 0 },
                    { "PlantCertificationSettings.Read", true, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Plant Certification Delivery", "Read Plant Certification Settings", 0 },
                    { "PlantCertificationSettings.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Plant Certification Delivery", "Update Plant Certification Settings", 0 },
                    { "Plants.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Plants", "Create Customer Plants", 0 },
                    { "Plants.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Plants", "Delete Customer Plants", 0 },
                    { "Plants.Read", true, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Plants", "Read Customer Plants", 0 },
                    { "Plants.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Plants", "Update Customer Plants", 0 },
                    { "ProductionCalendars.Read", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Read Default Working Calendar", 0 },
                    { "ProductionCalendars.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Update Default Working Calendar", 0 },
                    { "ProductionDowntimeReasons.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Create Planned Downtime Reasons", 0 },
                    { "ProductionDowntimeReasons.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Delete Planned Downtime Reasons", 0 },
                    { "ProductionDowntimeReasons.Read", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Read Planned Downtime Reasons", 0 },
                    { "ProductionDowntimeReasons.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Update Planned Downtime Reasons", 0 },
                    { "ProductionHolidays.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Create Production Holidays", 0 },
                    { "ProductionHolidays.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Delete Production Holidays", 0 },
                    { "ProductionHolidays.Read", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Read Production Holidays", 0 },
                    { "ProductionHolidays.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Update Production Holidays", 0 },
                    { "ProductionJobs.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Scheduling", "Create Production Jobs", 0 },
                    { "ProductionJobs.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Scheduling", "Delete Production Jobs", 0 },
                    { "ProductionJobs.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Scheduling", "Update Production Jobs", 0 },
                    { "ProductionLogs.CorrectLine", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Tracking", "Correct Production Run Details", 1 },
                    { "ProductionLogs.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Tracking", "Open or Create Production Logs", 0 },
                    { "ProductionLogs.Read", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Tracking", "Read Production Logs", 0 },
                    { "ProductionLogs.ResumeRun", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Tracking", "Resume Production Run Interval", 1 },
                    { "ProductionLogs.StartRun", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Tracking", "Start Production Run Interval", 1 },
                    { "ProductionLogs.StopRun", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Tracking", "Stop Production Run Interval", 1 },
                    { "ProductionLogs.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Tracking", "Update Production Log Comments", 0 },
                    { "ProductionReports.Read", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Reporting", "Read Production Review Reports", 0 },
                    { "ProductionSchedules.AppendContainerPart", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Scheduling", "Append Eligible Container Part", 1 },
                    { "ProductionSchedules.Arrange", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Scheduling", "Arrange Planned Production Work", 1 },
                    { "ProductionSchedules.CompleteWork", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Scheduling", "Complete Planned Production Work", 1 },
                    { "ProductionSchedules.CorrectProgress", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Scheduling", "Correct Cumulative Production Progress", 1 },
                    { "ProductionSchedules.Optimize", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Scheduling", "Apply Production Optimization", 1 },
                    { "ProductionSchedules.Read", true, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Scheduling", "Read Production Schedules", 0 },
                    { "ProductionSchedules.RecordProgress", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Scheduling", "Record Additional Production Progress", 1 },
                    { "ProductionSchedules.StartWork", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Scheduling", "Start Planned Production Work", 1 },
                    { "ProductionSettings.Read", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Read Production Efficiency Settings", 0 },
                    { "ProductionSettings.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Update Production Efficiency Settings", 0 },
                    { "Receiving.BeginInspection", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Receiving", "Begin Inspection from Receipt", 1 },
                    { "Receiving.BumpQuantity", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Receiving", "Add Received Quantity to Inspection", 1 },
                    { "Receiving.Read", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Receiving", "Read Received Parts and Candidates", 0 },
                    { "Receiving.ReverseAllocation", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Receiving", "Reverse Receipt Allocation", 1 },
                    { "Roles.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Role Administration", "Create Ordinary Roles", 0 },
                    { "Roles.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Role Administration", "Delete Ordinary Roles", 0 },
                    { "Roles.ManageInheritance", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Role Administration", "Edit Ordinary Role Parents", 1 },
                    { "Roles.ManagePermissions", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Role Administration", "Edit Ordinary Role Permission Grants", 1 },
                    { "Roles.Read", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Role Administration", "Read Ordinary Roles and Effective Grants", 0 },
                    { "Roles.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Role Administration", "Edit Ordinary Role Metadata", 0 },
                    { "Shipments.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Create Shipments", 0 },
                    { "Shipments.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Delete Shipments", 0 },
                    { "Shipments.Read", true, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Read Shipments", 0 },
                    { "Shipments.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Logistics", "Update Shipments", 0 },
                    { "SortingMachines.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Create Sorting Machines", 0 },
                    { "SortingMachines.Read", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Read Sorting Machine Configuration", 0 },
                    { "SortingMachines.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Configuration", "Update Sorting Machines", 0 },
                    { "SortLogDowntimeCauses.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Tracking", "Create Sort Log Downtime Causes", 0 },
                    { "SortLogDowntimeCauses.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Tracking", "Delete Sort Log Downtime Causes", 0 },
                    { "SortLogDowntimeCauses.Read", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Tracking", "Read Sort Log Downtime Causes", 0 },
                    { "SortLogDowntimeCauses.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Production Tracking", "Update Sort Log Downtime Causes", 0 },
                    { "Suppliers.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Suppliers", "Create Suppliers", 0 },
                    { "Suppliers.Read", true, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Suppliers", "Read Suppliers", 0 },
                    { "Suppliers.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Suppliers", "Update Suppliers", 0 },
                    { "Users.Create", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Users", "Create Users", 0 },
                    { "Users.Delete", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Users", "Delete Users", 0 },
                    { "Users.ManageRoles", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Users", "Assign User Roles", 1 },
                    { "Users.Read", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Users", "Read Users", 0 },
                    { "Users.ResetPasswords", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Users", "Issue Password Reset Links", 1 },
                    { "Users.Update", false, 0, "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544", "Users", "Update Users", 0 }
                });

            migrationBuilder.InsertData(
                table: "role_inheritance",
                columns: new[] { "child_role_id", "parent_role_id" },
                values: new object[,]
                {
                    { "47cd3d4a-0d66-4acf-8556-4017336798d8", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "47cd3d4a-0d66-4acf-8556-4017336798d8", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" }
                });

            migrationBuilder.InsertData(
                table: "role_permissions",
                columns: new[] { "permission_key", "role_id" },
                values: new object[,]
                {
                    { "BillsOfLading.Read", "1b171cb9-9273-42fc-b790-ea934dbb12b9" },
                    { "Certifications.Read", "1b171cb9-9273-42fc-b790-ea934dbb12b9" },
                    { "Containers.Read", "1b171cb9-9273-42fc-b790-ea934dbb12b9" },
                    { "Customers.Read", "1b171cb9-9273-42fc-b790-ea934dbb12b9" },
                    { "Gages.Read", "1b171cb9-9273-42fc-b790-ea934dbb12b9" },
                    { "GageTypes.Read", "1b171cb9-9273-42fc-b790-ea934dbb12b9" },
                    { "InspectionCriteria.Read", "1b171cb9-9273-42fc-b790-ea934dbb12b9" },
                    { "Inspections.Read", "1b171cb9-9273-42fc-b790-ea934dbb12b9" },
                    { "MasterPrints.Read", "1b171cb9-9273-42fc-b790-ea934dbb12b9" },
                    { "Parts.Read", "1b171cb9-9273-42fc-b790-ea934dbb12b9" },
                    { "PlantCertificationRecipients.Read", "1b171cb9-9273-42fc-b790-ea934dbb12b9" },
                    { "PlantCertificationSettings.Read", "1b171cb9-9273-42fc-b790-ea934dbb12b9" },
                    { "Plants.Read", "1b171cb9-9273-42fc-b790-ea934dbb12b9" },
                    { "ProductionSchedules.Read", "1b171cb9-9273-42fc-b790-ea934dbb12b9" },
                    { "Shipments.Read", "1b171cb9-9273-42fc-b790-ea934dbb12b9" },
                    { "Suppliers.Read", "1b171cb9-9273-42fc-b790-ea934dbb12b9" },
                    { "BillsOfLading.CorrectDeparted", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "CertificationEmailSettings.Read", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "CertificationEmailSettings.Update", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "CertificationEmailTemplates.Read", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "CertificationEmailTemplates.Update", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Certifications.TemporarilyCompletePackage", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Chat.AdministerChannels", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Chat.DeleteOthersMessages", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Containers.CorrectDepartedMetadata", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Containers.DeleteDeparted", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Development.SendTestEmail", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Development.SetBusinessDate", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Inspections.Delete", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Inspections.UndoLineage", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "NominalToleranceSettings.Read", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "NominalToleranceSettings.Update", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "PartFlipDefinitions.Create", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "PartFlipDefinitions.Delete", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "PartFlipDefinitions.Read", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "PartFlipDefinitions.Update", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "PartMachineEligibility.Create", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "PartMachineEligibility.Delete", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "PartMachineEligibility.Read", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "PartMachineEligibility.Update", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Parts.Delete", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Plants.Delete", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "ProductionCalendars.Read", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "ProductionCalendars.Update", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "ProductionDowntimeReasons.Create", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "ProductionDowntimeReasons.Delete", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "ProductionDowntimeReasons.Read", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "ProductionDowntimeReasons.Update", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "ProductionHolidays.Create", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "ProductionHolidays.Delete", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "ProductionHolidays.Read", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "ProductionHolidays.Update", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "ProductionSettings.Read", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "ProductionSettings.Update", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Roles.Create", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Roles.Delete", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Roles.ManageInheritance", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Roles.ManagePermissions", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Roles.Read", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Roles.Update", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "SortingMachines.Create", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "SortingMachines.Read", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "SortingMachines.Update", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "SortLogDowntimeCauses.Create", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "SortLogDowntimeCauses.Delete", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "SortLogDowntimeCauses.Read", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "SortLogDowntimeCauses.Update", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Users.Create", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Users.Delete", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Users.ManageRoles", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Users.Read", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Users.ResetPasswords", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "Users.Update", "47cd3d4a-0d66-4acf-8556-4017336798d8" },
                    { "BillsOfLading.Create", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "BillsOfLading.Update", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.Access", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.CreateCategories", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.CreateChannels", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.CreateConversations", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.CreatePolls", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.CreateThreads", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.DeleteChannels", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.DeleteOwnMessages", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.EditOwnMessages", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.ManageCategories", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.ManageGroupConversations", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.ManagePrivateChannelMembers", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.PinMessages", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.React", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.ReorderChannels", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.ScheduleMessages", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.SendMessages", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.UpdateChannels", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Chat.VotePolls", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ContainerContents.Update", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Containers.CorrectReceipt", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Containers.CorrectReceivedQuantities", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Containers.Create", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Containers.Delete", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Containers.Receive", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Containers.Unreceive", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Containers.Update", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Customers.Create", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Customers.Update", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "MachineDowntime.Create", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "MachineDowntime.Delete", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "MachineDowntime.Update", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Parts.Create", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Parts.Update", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Plants.Create", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Plants.Update", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionJobs.Create", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionJobs.Delete", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionJobs.Update", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionLogs.CorrectLine", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionLogs.Create", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionLogs.Read", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionLogs.ResumeRun", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionLogs.StartRun", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionLogs.StopRun", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionLogs.Update", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionReports.Read", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionSchedules.AppendContainerPart", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionSchedules.Arrange", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionSchedules.CompleteWork", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionSchedules.CorrectProgress", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionSchedules.Optimize", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionSchedules.RecordProgress", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "ProductionSchedules.StartWork", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Shipments.Create", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Shipments.Delete", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Shipments.Update", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Suppliers.Create", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "Suppliers.Update", "56b3fc07-e152-42ca-b074-a823900c93b3" },
                    { "BillsOfLading.Create", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "BillsOfLading.Update", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Certifications.BuildPackage", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Certifications.Create", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Certifications.Delete", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Certifications.SendEmail", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.Access", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.CreateCategories", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.CreateChannels", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.CreateConversations", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.CreatePolls", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.CreateThreads", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.DeleteChannels", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.DeleteOwnMessages", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.EditOwnMessages", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.ManageCategories", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.ManageGroupConversations", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.ManagePrivateChannelMembers", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.PinMessages", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.React", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.ReorderChannels", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.ScheduleMessages", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.SendMessages", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.UpdateChannels", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Chat.VotePolls", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "ContainerContents.Update", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Containers.CorrectReceipt", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Containers.CorrectReceivedQuantities", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Containers.Create", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Containers.Delete", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Containers.Receive", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Containers.Unreceive", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Containers.Update", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Customers.Create", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Customers.Update", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Gages.Create", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Gages.Update", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "GageTypes.Create", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "GageTypes.Update", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "InspectionCriteria.Create", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "InspectionCriteria.Delete", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "InspectionCriteria.Publish", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "InspectionCriteria.Update", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Inspections.ApproveDeviation", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Inspections.Create", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Inspections.Duplicate", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Inspections.Flip", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Inspections.TransferQuantity", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Inspections.Update", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "InspectionSheets.Export", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "MasterPrints.Delete", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "MasterPrints.Update", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Parts.Create", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Parts.Update", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "PlantCertificationRecipients.Create", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "PlantCertificationRecipients.Delete", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "PlantCertificationRecipients.Update", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "PlantCertificationSettings.Update", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Plants.Create", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Plants.Update", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Receiving.BeginInspection", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Receiving.BumpQuantity", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Receiving.Read", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Receiving.ReverseAllocation", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Shipments.Create", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Shipments.Delete", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Shipments.Update", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Suppliers.Create", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" },
                    { "Suppliers.Update", "9eb9ef78-7737-47a5-89fc-10513d3e9c1b" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_identity_roles_system_key",
                table: "identity_roles",
                column: "system_key",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_role_system_kind",
                table: "identity_roles",
                sql: "(system_kind = 0 AND system_key IS NULL) OR (system_kind IN (1,2) AND system_key IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_authorization_change_details_history_id",
                table: "authorization_change_details",
                column: "history_id");

            migrationBuilder.CreateIndex(
                name: "IX_authorization_change_history_installation_generation_result~",
                table: "authorization_change_history",
                columns: new[] { "installation_generation", "resulting_epoch" });

            migrationBuilder.CreateIndex(
                name: "IX_authorization_state_baseline_role_id",
                table: "authorization_state",
                column: "baseline_role_id");

            migrationBuilder.CreateIndex(
                name: "IX_authorization_state_root_role_id",
                table: "authorization_state",
                column: "root_role_id");

            migrationBuilder.CreateIndex(
                name: "IX_authorization_state_root_user_id",
                table: "authorization_state",
                column: "root_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_role_inheritance_parent_role_id",
                table: "role_inheritance",
                column: "parent_role_id");

            migrationBuilder.CreateIndex(
                name: "IX_role_permissions_permission_key",
                table: "role_permissions",
                column: "permission_key");

            migrationBuilder.Sql(AuthorizationFoundationSql.Install);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AuthorizationFoundationSql.Uninstall);
            migrationBuilder.DropTable(
                name: "authorization_change_details");

            migrationBuilder.DropTable(
                name: "authorization_state");

            migrationBuilder.DropTable(
                name: "role_inheritance");

            migrationBuilder.DropTable(
                name: "role_permissions");

            migrationBuilder.DropTable(
                name: "authorization_change_history");

            migrationBuilder.DropTable(
                name: "permissions");

            migrationBuilder.DropIndex(
                name: "IX_identity_roles_system_key",
                table: "identity_roles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_role_system_kind",
                table: "identity_roles");

            migrationBuilder.DeleteData(
                table: "identity_roles",
                keyColumn: "id",
                keyValue: "e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6");

            migrationBuilder.DropColumn(
                name: "description",
                table: "identity_roles");

            migrationBuilder.DropColumn(
                name: "is_enabled",
                table: "identity_roles");

            migrationBuilder.DropColumn(
                name: "system_key",
                table: "identity_roles");

            migrationBuilder.DropColumn(
                name: "system_kind",
                table: "identity_roles");
            migrationBuilder.Sql(AuthorizationFoundationSql.DropFunctions);
        }
    }
}
