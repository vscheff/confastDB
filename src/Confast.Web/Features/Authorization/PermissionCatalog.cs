using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Confast.Web.Features.Authorization;

public enum PermissionKind { Resource, Action }
public enum PermissionAuthority { Ordinary, RootOnly }

public sealed record PermissionDefinition(string Key, string DisplayName, string Category,
    PermissionKind Kind, PermissionAuthority Authority, bool AllowedInBaseline,
    ImmutableArray<string> Prerequisites);

public static class PermissionCatalog
{
    // Reviewed October 9, 2026 manifest. Additions require an explicit seed decision.
    public static ImmutableArray<PermissionDefinition> All { get; } =
    [
        new(Permissions.Users.Read, "Read Users", "Users", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.Users.Create, "Create Users", "Users", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Users.Read]),
        new(Permissions.Users.Update, "Update Users", "Users", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Users.Read]),
        new(Permissions.Users.Delete, "Delete Users", "Users", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Users.Read]),
        new(Permissions.Users.ManageRoles, "Assign User Roles", "Users", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Users.Read]),
        new(Permissions.Users.ResetPasswords, "Issue Password Reset Links", "Users", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Users.Read]),
        new(Permissions.Customers.Read, "Read Customers", "Customers", PermissionKind.Resource, PermissionAuthority.Ordinary, true, []),
        new(Permissions.Customers.Create, "Create Customers", "Customers", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Customers.Read]),
        new(Permissions.Customers.Update, "Update Customers", "Customers", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Customers.Read]),
        new(Permissions.Plants.Read, "Read Customer Plants", "Plants", PermissionKind.Resource, PermissionAuthority.Ordinary, true, []),
        new(Permissions.Plants.Create, "Create Customer Plants", "Plants", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Plants.Read]),
        new(Permissions.Plants.Update, "Update Customer Plants", "Plants", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Plants.Read]),
        new(Permissions.Plants.Delete, "Delete Customer Plants", "Plants", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Plants.Read]),
        new(Permissions.PlantCertificationRecipients.Read, "Read Plant Certification Recipients", "Plant Certification Delivery", PermissionKind.Resource, PermissionAuthority.Ordinary, true, []),
        new(Permissions.PlantCertificationRecipients.Create, "Create Plant Certification Recipients", "Plant Certification Delivery", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.PlantCertificationRecipients.Read]),
        new(Permissions.PlantCertificationRecipients.Update, "Update Plant Certification Recipients", "Plant Certification Delivery", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.PlantCertificationRecipients.Read]),
        new(Permissions.PlantCertificationRecipients.Delete, "Delete Plant Certification Recipients", "Plant Certification Delivery", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.PlantCertificationRecipients.Read]),
        new(Permissions.PlantCertificationSettings.Read, "Read Plant Certification Settings", "Plant Certification Delivery", PermissionKind.Resource, PermissionAuthority.Ordinary, true, []),
        new(Permissions.PlantCertificationSettings.Update, "Update Plant Certification Settings", "Plant Certification Delivery", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.PlantCertificationSettings.Read]),
        new(Permissions.Parts.Read, "Read Parts", "Parts", PermissionKind.Resource, PermissionAuthority.Ordinary, true, []),
        new(Permissions.Parts.Create, "Create Parts", "Parts", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Parts.Read]),
        new(Permissions.Parts.Update, "Update Parts", "Parts", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Parts.Read]),
        new(Permissions.Parts.Delete, "Delete Parts", "Parts", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Parts.Read]),
        new(Permissions.PartFlipDefinitions.Read, "Read Part Flip Configuration", "Part Flip Definitions", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.PartFlipDefinitions.Create, "Create Part Flip Definitions", "Part Flip Definitions", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.PartFlipDefinitions.Read]),
        new(Permissions.PartFlipDefinitions.Update, "Update Part Flip Definitions", "Part Flip Definitions", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.PartFlipDefinitions.Read]),
        new(Permissions.PartFlipDefinitions.Delete, "Delete Part Flip Definitions", "Part Flip Definitions", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.PartFlipDefinitions.Read]),
        new(Permissions.Gages.Read, "Read Gages", "Gages", PermissionKind.Resource, PermissionAuthority.Ordinary, true, []),
        new(Permissions.Gages.Create, "Create Gages", "Gages", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Gages.Read]),
        new(Permissions.Gages.Update, "Update Gages", "Gages", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Gages.Read]),
        new(Permissions.GageTypes.Read, "Read Gage Types", "Gages", PermissionKind.Resource, PermissionAuthority.Ordinary, true, []),
        new(Permissions.GageTypes.Create, "Create Gage Types", "Gages", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.GageTypes.Read]),
        new(Permissions.GageTypes.Update, "Update Gage Types", "Gages", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.GageTypes.Read]),
        new(Permissions.InspectionCriteria.Read, "Read Inspection Criteria", "Inspection Criteria", PermissionKind.Resource, PermissionAuthority.Ordinary, true, []),
        new(Permissions.InspectionCriteria.Create, "Create Inspection Criteria Revisions", "Inspection Criteria", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.InspectionCriteria.Read]),
        new(Permissions.InspectionCriteria.Update, "Update Inspection Criteria Revisions", "Inspection Criteria", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.InspectionCriteria.Read]),
        new(Permissions.InspectionCriteria.Delete, "Delete Inspection Criteria Revisions", "Inspection Criteria", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.InspectionCriteria.Read]),
        new(Permissions.InspectionCriteria.Publish, "Publish Inspection Criteria Revision", "Inspection Criteria", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.InspectionCriteria.Read]),
        new(Permissions.MasterPrints.Read, "Read Master Prints", "Inspection Criteria", PermissionKind.Resource, PermissionAuthority.Ordinary, true, []),
        new(Permissions.MasterPrints.Update, "Upload or Replace Master Prints", "Inspection Criteria", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.MasterPrints.Read]),
        new(Permissions.MasterPrints.Delete, "Delete Master Prints", "Inspection Criteria", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.MasterPrints.Read]),
        new(Permissions.Inspections.Read, "Read Inspections", "Inspections", PermissionKind.Resource, PermissionAuthority.Ordinary, true, []),
        new(Permissions.Inspections.Create, "Create Inspections", "Inspections", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.Inspections.Update, "Update Inspections", "Inspections", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Inspections.Read]),
        new(Permissions.Inspections.Delete, "Delete Inspections", "Inspections", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Inspections.Read]),
        new(Permissions.Inspections.Duplicate, "Duplicate Inspection Lots", "Inspections", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Inspections.Read, Permissions.Inspections.Create]),
        new(Permissions.Inspections.Flip, "Flip Inspection to Another Part", "Inspections", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Inspections.Read, Permissions.Inspections.Create]),
        new(Permissions.Inspections.TransferQuantity, "Transfer Additional Lot Quantity", "Inspections", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Inspections.Read]),
        new(Permissions.Inspections.UndoLineage, "Undo Lot Lineage Operation", "Inspections", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Inspections.Read]),
        new(Permissions.Inspections.ApproveDeviation, "Change Deviation Approval", "Inspections", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Inspections.Read, Permissions.Inspections.Update]),
        new(Permissions.InspectionSheets.Export, "Export Inspection Sheets", "Inspections", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Inspections.Read]),
        new(Permissions.Certifications.Read, "Read Certification Documents", "Certifications", PermissionKind.Resource, PermissionAuthority.Ordinary, true, []),
        new(Permissions.Certifications.Create, "Upload Certification Documents", "Certifications", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Certifications.Read]),
        new(Permissions.Certifications.Delete, "Delete Certification Documents", "Certifications", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Certifications.Read]),
        new(Permissions.Certifications.BuildPackage, "Build and Export Certification Packages", "Certifications", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Inspections.Read, Permissions.Certifications.Read]),
        new(Permissions.Certifications.SendEmail, "Send Certification Package Email", "Certifications", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Inspections.Read, Permissions.Certifications.Read, Permissions.Certifications.BuildPackage]),
        new(Permissions.Certifications.TemporarilyCompletePackage, "Temporarily Complete Package Rendering", "Certifications", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Inspections.Read, Permissions.Certifications.Read, Permissions.Certifications.BuildPackage]),
        new(Permissions.CertificationEmailTemplates.Read, "Read Certification Email Templates", "Email Administration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.CertificationEmailTemplates.Update, "Update Certification Email Templates", "Email Administration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.CertificationEmailTemplates.Read]),
        new(Permissions.CertificationEmailSettings.Read, "Read Global Certification Email Settings", "Email Administration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.CertificationEmailSettings.Update, "Update Global Certification Email Settings", "Email Administration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.CertificationEmailSettings.Read]),
        new(Permissions.NominalToleranceSettings.Read, "Read Nominal Tolerance Settings", "Global Settings", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.NominalToleranceSettings.Update, "Update Nominal Tolerance Settings", "Global Settings", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.NominalToleranceSettings.Read]),
        new(Permissions.Development.SetBusinessDate, "Set Development Business Date", "Development", PermissionKind.Action, PermissionAuthority.Ordinary, false, []),
        new(Permissions.Development.SendTestEmail, "Send Development SMTP Test", "Development", PermissionKind.Action, PermissionAuthority.Ordinary, false, []),
        new(Permissions.Suppliers.Read, "Read Suppliers", "Suppliers", PermissionKind.Resource, PermissionAuthority.Ordinary, true, []),
        new(Permissions.Suppliers.Create, "Create Suppliers", "Suppliers", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Suppliers.Read]),
        new(Permissions.Suppliers.Update, "Update Suppliers", "Suppliers", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Suppliers.Read]),
        new(Permissions.Shipments.Read, "Read Shipments", "Logistics", PermissionKind.Resource, PermissionAuthority.Ordinary, true, []),
        new(Permissions.Shipments.Create, "Create Shipments", "Logistics", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Shipments.Read]),
        new(Permissions.Shipments.Update, "Update Shipments", "Logistics", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Shipments.Read]),
        new(Permissions.Shipments.Delete, "Delete Shipments", "Logistics", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Shipments.Read]),
        new(Permissions.Containers.Read, "Read Containers", "Logistics", PermissionKind.Resource, PermissionAuthority.Ordinary, true, []),
        new(Permissions.Containers.Create, "Create Containers", "Logistics", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Containers.Read]),
        new(Permissions.Containers.Update, "Update Containers", "Logistics", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Containers.Read]),
        new(Permissions.Containers.Delete, "Delete Containers", "Logistics", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Containers.Read]),
        new(Permissions.ContainerContents.Update, "Replace Container Contents", "Logistics", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Containers.Read]),
        new(Permissions.BillsOfLading.Read, "Read Bills of Lading", "Logistics", PermissionKind.Resource, PermissionAuthority.Ordinary, true, []),
        new(Permissions.BillsOfLading.Create, "Create Bills of Lading", "Logistics", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.BillsOfLading.Read]),
        new(Permissions.BillsOfLading.Update, "Update Bills of Lading", "Logistics", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.BillsOfLading.Read]),
        new(Permissions.Containers.Receive, "Receive Containers", "Logistics", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Containers.Read]),
        new(Permissions.Containers.CorrectReceipt, "Correct or Reconcile Container Receipt", "Logistics", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Containers.Read]),
        new(Permissions.Containers.Unreceive, "Unreceive Containers", "Logistics", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Containers.Read]),
        new(Permissions.Containers.CorrectReceivedQuantities, "Correct Actual Received Quantities", "Logistics", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Containers.Read]),
        new(Permissions.Containers.CorrectDepartedMetadata, "Correct Departed Container Metadata", "Logistics", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Containers.Read, Permissions.Containers.Update]),
        new(Permissions.Containers.DeleteDeparted, "Delete Departed Containers", "Logistics", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Containers.Read, Permissions.Containers.Delete]),
        new(Permissions.BillsOfLading.CorrectDeparted, "Correct Bills Shared with Departed Containers", "Logistics", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.BillsOfLading.Read, Permissions.BillsOfLading.Update]),
        new(Permissions.Receiving.Read, "Read Received Parts and Candidates", "Receiving", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.Receiving.BeginInspection, "Begin Inspection from Receipt", "Receiving", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Receiving.Read, Permissions.Inspections.Create]),
        new(Permissions.Receiving.BumpQuantity, "Add Received Quantity to Inspection", "Receiving", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Receiving.Read, Permissions.Inspections.Update]),
        new(Permissions.Receiving.ReverseAllocation, "Reverse Receipt Allocation", "Receiving", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Receiving.Read]),
        new(Permissions.ProductionSchedules.Read, "Read Production Schedules", "Production Scheduling", PermissionKind.Resource, PermissionAuthority.Ordinary, true, []),
        new(Permissions.ProductionJobs.Create, "Create Production Jobs", "Production Scheduling", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.ProductionSchedules.Read]),
        new(Permissions.ProductionJobs.Update, "Update Production Jobs", "Production Scheduling", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.ProductionSchedules.Read]),
        new(Permissions.ProductionJobs.Delete, "Delete Production Jobs", "Production Scheduling", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.ProductionSchedules.Read]),
        new(Permissions.MachineDowntime.Create, "Create Planned Machine Downtime", "Production Scheduling", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.ProductionSchedules.Read]),
        new(Permissions.MachineDowntime.Update, "Update Planned Machine Downtime", "Production Scheduling", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.ProductionSchedules.Read]),
        new(Permissions.MachineDowntime.Delete, "Delete Planned Machine Downtime", "Production Scheduling", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.ProductionSchedules.Read]),
        new(Permissions.ProductionSchedules.Arrange, "Arrange Planned Production Work", "Production Scheduling", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.ProductionSchedules.Read]),
        new(Permissions.ProductionSchedules.Optimize, "Apply Production Optimization", "Production Scheduling", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.ProductionSchedules.Read]),
        new(Permissions.ProductionSchedules.AppendContainerPart, "Append Eligible Container Part", "Production Scheduling", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.ProductionSchedules.Read]),
        new(Permissions.ProductionSchedules.StartWork, "Start Planned Production Work", "Production Scheduling", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.ProductionSchedules.Read]),
        new(Permissions.ProductionSchedules.RecordProgress, "Record Additional Production Progress", "Production Scheduling", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.ProductionSchedules.Read]),
        new(Permissions.ProductionSchedules.CompleteWork, "Complete Planned Production Work", "Production Scheduling", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.ProductionSchedules.Read]),
        new(Permissions.ProductionSchedules.CorrectProgress, "Correct Cumulative Production Progress", "Production Scheduling", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.ProductionSchedules.Read]),
        new(Permissions.SortingMachines.Read, "Read Sorting Machine Configuration", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.SortingMachines.Create, "Create Sorting Machines", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.SortingMachines.Read]),
        new(Permissions.SortingMachines.Update, "Update Sorting Machines", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.SortingMachines.Read]),
        new(Permissions.PartMachineEligibility.Read, "Read Part Machine Eligibility and Rates", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.PartMachineEligibility.Create, "Add Part Machine Eligibility", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.PartMachineEligibility.Read]),
        new(Permissions.PartMachineEligibility.Update, "Update Part Machine Rate or Preference", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.PartMachineEligibility.Read]),
        new(Permissions.PartMachineEligibility.Delete, "Remove Part Machine Eligibility", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.PartMachineEligibility.Read]),
        new(Permissions.ProductionCalendars.Read, "Read Default Working Calendar", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.ProductionCalendars.Update, "Update Default Working Calendar", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.ProductionCalendars.Read]),
        new(Permissions.ProductionHolidays.Read, "Read Production Holidays", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.ProductionHolidays.Create, "Create Production Holidays", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.ProductionHolidays.Read]),
        new(Permissions.ProductionHolidays.Update, "Update Production Holidays", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.ProductionHolidays.Read]),
        new(Permissions.ProductionHolidays.Delete, "Delete Production Holidays", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.ProductionHolidays.Read]),
        new(Permissions.ProductionDowntimeReasons.Read, "Read Planned Downtime Reasons", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.ProductionDowntimeReasons.Create, "Create Planned Downtime Reasons", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.ProductionDowntimeReasons.Read]),
        new(Permissions.ProductionDowntimeReasons.Update, "Update Planned Downtime Reasons", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.ProductionDowntimeReasons.Read]),
        new(Permissions.ProductionDowntimeReasons.Delete, "Delete Planned Downtime Reasons", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.ProductionDowntimeReasons.Read]),
        new(Permissions.ProductionSettings.Read, "Read Production Efficiency Settings", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.ProductionSettings.Update, "Update Production Efficiency Settings", "Production Configuration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.ProductionSettings.Read]),
        new(Permissions.ProductionLogs.Read, "Read Production Logs", "Production Tracking", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.ProductionLogs.Create, "Open or Create Production Logs", "Production Tracking", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.ProductionLogs.Read]),
        new(Permissions.ProductionLogs.Update, "Update Production Log Comments", "Production Tracking", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.ProductionLogs.Read]),
        new(Permissions.ProductionLogs.StartRun, "Start Production Run Interval", "Production Tracking", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.ProductionLogs.Read]),
        new(Permissions.ProductionLogs.StopRun, "Stop Production Run Interval", "Production Tracking", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.ProductionLogs.Read]),
        new(Permissions.ProductionLogs.ResumeRun, "Resume Production Run Interval", "Production Tracking", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.ProductionLogs.Read]),
        new(Permissions.ProductionLogs.CorrectLine, "Correct Production Run Details", "Production Tracking", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.ProductionLogs.Read]),
        new(Permissions.SortLogDowntimeCauses.Read, "Read Sort Log Downtime Causes", "Production Tracking", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.SortLogDowntimeCauses.Create, "Create Sort Log Downtime Causes", "Production Tracking", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.SortLogDowntimeCauses.Read]),
        new(Permissions.SortLogDowntimeCauses.Update, "Update Sort Log Downtime Causes", "Production Tracking", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.SortLogDowntimeCauses.Read]),
        new(Permissions.SortLogDowntimeCauses.Delete, "Delete Sort Log Downtime Causes", "Production Tracking", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.SortLogDowntimeCauses.Read]),
        new(Permissions.ProductionReports.Read, "Read Production Review Reports", "Production Reporting", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.Chat.Access, "Access Chat", "Chat", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.Chat.CreateConversations, "Create Direct and Group Conversations", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Chat.SendMessages, "Send Chat Messages", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Chat.ScheduleMessages, "Schedule Chat Messages", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access, Permissions.Chat.SendMessages]),
        new(Permissions.Chat.CreateThreads, "Create Channel Threads", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access, Permissions.Chat.SendMessages]),
        new(Permissions.Chat.CreatePolls, "Create Chat Polls", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access, Permissions.Chat.SendMessages]),
        new(Permissions.Chat.VotePolls, "Vote in Chat Polls", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Chat.React, "React to Chat Messages", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Chat.PinMessages, "Pin Chat Messages", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Chat.EditOwnMessages, "Edit Own Chat Messages", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Chat.DeleteOwnMessages, "Delete Own Chat Messages", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Chat.DeleteOthersMessages, "Delete Other Users' Messages", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Chat.CreateChannels, "Create or Duplicate Channels", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Chat.UpdateChannels, "Update Owned Channel Metadata", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Chat.DeleteChannels, "Delete Owned Channels", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Chat.ManagePrivateChannelMembers, "Manage Owned Private Channel Members", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Chat.CreateCategories, "Create Chat Categories", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Chat.ManageCategories, "Manage Accessible Chat Categories", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Chat.ReorderChannels, "Reorder and Relocate Accessible Channels", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Chat.ManageGroupConversations, "Manage Group Conversation Membership and Metadata", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Chat.AdministerChannels, "Administer Any Channel Metadata or Deletion", "Chat", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Chat.Access]),
        new(Permissions.Roles.Read, "Read Ordinary Roles and Effective Grants", "Role Administration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, []),
        new(Permissions.Roles.Create, "Create Ordinary Roles", "Role Administration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Roles.Read]),
        new(Permissions.Roles.Update, "Edit Ordinary Role Metadata", "Role Administration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Roles.Read]),
        new(Permissions.Roles.Delete, "Delete Ordinary Roles", "Role Administration", PermissionKind.Resource, PermissionAuthority.Ordinary, false, [Permissions.Roles.Read]),
        new(Permissions.Roles.ManagePermissions, "Edit Ordinary Role Permission Grants", "Role Administration", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Roles.Read]),
        new(Permissions.Roles.ManageInheritance, "Edit Ordinary Role Parents", "Role Administration", PermissionKind.Action, PermissionAuthority.Ordinary, false, [Permissions.Roles.Read]),
        new(Permissions.Authorization.ManageSecurity, "Manage Protected Authorization and Root Security", "Authorization Security", PermissionKind.Action, PermissionAuthority.RootOnly, false, []),
    ];

    public static FrozenDictionary<string, PermissionDefinition> ByKey { get; } =
        All.ToFrozenDictionary(x => x.Key, StringComparer.Ordinal);
    public static FrozenSet<string> BaselineEnvelope { get; } =
        All.Where(x => x.AllowedInBaseline).Select(x => x.Key).ToFrozenSet(StringComparer.Ordinal);

    public static string Version { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", All.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x =>
            $"{x.Key}|{x.DisplayName}|{x.Category}|{x.Kind}|{x.Authority}|{x.AllowedInBaseline}|{string.Join(",", x.Prerequisites.Order(StringComparer.Ordinal))}")))));

    public static ImmutableArray<string> ExpandRequirements(IEnumerable<string> keys)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        void Add(string key)
        {
            if (!ByKey.TryGetValue(key, out var definition))
                throw new AuthorizationDeniedException(AuthorizationDenial.UnknownPermission);
            if (!result.Add(key)) return;
            foreach (var prerequisite in definition.Prerequisites) Add(prerequisite);
        }
        foreach (var key in keys) Add(key);
        return result.Order(StringComparer.Ordinal).ToImmutableArray();
    }
}
