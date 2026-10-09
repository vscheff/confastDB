namespace Confast.Web.Features.Authorization;

public static class Permissions
{
    public static class Users
    {
        public const string Read = "Users.Read";
        public const string Create = "Users.Create";
        public const string Update = "Users.Update";
        public const string Delete = "Users.Delete";
        public const string ManageRoles = "Users.ManageRoles";
        public const string ResetPasswords = "Users.ResetPasswords";
    }

    public static class Customers
    {
        public const string Read = "Customers.Read";
        public const string Create = "Customers.Create";
        public const string Update = "Customers.Update";
    }

    public static class Plants
    {
        public const string Read = "Plants.Read";
        public const string Create = "Plants.Create";
        public const string Update = "Plants.Update";
        public const string Delete = "Plants.Delete";
    }

    public static class PlantCertificationRecipients
    {
        public const string Read = "PlantCertificationRecipients.Read";
        public const string Create = "PlantCertificationRecipients.Create";
        public const string Update = "PlantCertificationRecipients.Update";
        public const string Delete = "PlantCertificationRecipients.Delete";
    }

    public static class PlantCertificationSettings
    {
        public const string Read = "PlantCertificationSettings.Read";
        public const string Update = "PlantCertificationSettings.Update";
    }

    public static class Parts
    {
        public const string Read = "Parts.Read";
        public const string Create = "Parts.Create";
        public const string Update = "Parts.Update";
        public const string Delete = "Parts.Delete";
    }

    public static class PartFlipDefinitions
    {
        public const string Read = "PartFlipDefinitions.Read";
        public const string Create = "PartFlipDefinitions.Create";
        public const string Update = "PartFlipDefinitions.Update";
        public const string Delete = "PartFlipDefinitions.Delete";
    }

    public static class Gages
    {
        public const string Read = "Gages.Read";
        public const string Create = "Gages.Create";
        public const string Update = "Gages.Update";
    }

    public static class GageTypes
    {
        public const string Read = "GageTypes.Read";
        public const string Create = "GageTypes.Create";
        public const string Update = "GageTypes.Update";
    }

    public static class InspectionCriteria
    {
        public const string Read = "InspectionCriteria.Read";
        public const string Create = "InspectionCriteria.Create";
        public const string Update = "InspectionCriteria.Update";
        public const string Delete = "InspectionCriteria.Delete";
        public const string Publish = "InspectionCriteria.Publish";
    }

    public static class MasterPrints
    {
        public const string Read = "MasterPrints.Read";
        public const string Update = "MasterPrints.Update";
        public const string Delete = "MasterPrints.Delete";
    }

    public static class Inspections
    {
        public const string Read = "Inspections.Read";
        public const string Create = "Inspections.Create";
        public const string Update = "Inspections.Update";
        public const string Delete = "Inspections.Delete";
        public const string Duplicate = "Inspections.Duplicate";
        public const string Flip = "Inspections.Flip";
        public const string TransferQuantity = "Inspections.TransferQuantity";
        public const string UndoLineage = "Inspections.UndoLineage";
        public const string ApproveDeviation = "Inspections.ApproveDeviation";
    }

    public static class InspectionSheets
    {
        public const string Export = "InspectionSheets.Export";
    }

    public static class Certifications
    {
        public const string Read = "Certifications.Read";
        public const string Create = "Certifications.Create";
        public const string Delete = "Certifications.Delete";
        public const string BuildPackage = "Certifications.BuildPackage";
        public const string SendEmail = "Certifications.SendEmail";
        public const string TemporarilyCompletePackage = "Certifications.TemporarilyCompletePackage";
    }

    public static class CertificationEmailTemplates
    {
        public const string Read = "CertificationEmailTemplates.Read";
        public const string Update = "CertificationEmailTemplates.Update";
    }

    public static class CertificationEmailSettings
    {
        public const string Read = "CertificationEmailSettings.Read";
        public const string Update = "CertificationEmailSettings.Update";
    }

    public static class NominalToleranceSettings
    {
        public const string Read = "NominalToleranceSettings.Read";
        public const string Update = "NominalToleranceSettings.Update";
    }

    public static class Development
    {
        public const string SetBusinessDate = "Development.SetBusinessDate";
        public const string SendTestEmail = "Development.SendTestEmail";
    }

    public static class Suppliers
    {
        public const string Read = "Suppliers.Read";
        public const string Create = "Suppliers.Create";
        public const string Update = "Suppliers.Update";
    }

    public static class Shipments
    {
        public const string Read = "Shipments.Read";
        public const string Create = "Shipments.Create";
        public const string Update = "Shipments.Update";
        public const string Delete = "Shipments.Delete";
    }

    public static class Containers
    {
        public const string Read = "Containers.Read";
        public const string Create = "Containers.Create";
        public const string Update = "Containers.Update";
        public const string Delete = "Containers.Delete";
        public const string Receive = "Containers.Receive";
        public const string CorrectReceipt = "Containers.CorrectReceipt";
        public const string Unreceive = "Containers.Unreceive";
        public const string CorrectReceivedQuantities = "Containers.CorrectReceivedQuantities";
        public const string CorrectDepartedMetadata = "Containers.CorrectDepartedMetadata";
        public const string DeleteDeparted = "Containers.DeleteDeparted";
    }

    public static class ContainerContents
    {
        public const string Update = "ContainerContents.Update";
    }

    public static class BillsOfLading
    {
        public const string Read = "BillsOfLading.Read";
        public const string Create = "BillsOfLading.Create";
        public const string Update = "BillsOfLading.Update";
        public const string CorrectDeparted = "BillsOfLading.CorrectDeparted";
    }

    public static class Receiving
    {
        public const string Read = "Receiving.Read";
        public const string BeginInspection = "Receiving.BeginInspection";
        public const string BumpQuantity = "Receiving.BumpQuantity";
        public const string ReverseAllocation = "Receiving.ReverseAllocation";
    }

    public static class ProductionSchedules
    {
        public const string Read = "ProductionSchedules.Read";
        public const string Arrange = "ProductionSchedules.Arrange";
        public const string Optimize = "ProductionSchedules.Optimize";
        public const string AppendContainerPart = "ProductionSchedules.AppendContainerPart";
        public const string StartWork = "ProductionSchedules.StartWork";
        public const string RecordProgress = "ProductionSchedules.RecordProgress";
        public const string CompleteWork = "ProductionSchedules.CompleteWork";
        public const string CorrectProgress = "ProductionSchedules.CorrectProgress";
    }

    public static class ProductionJobs
    {
        public const string Create = "ProductionJobs.Create";
        public const string Update = "ProductionJobs.Update";
        public const string Delete = "ProductionJobs.Delete";
    }

    public static class MachineDowntime
    {
        public const string Create = "MachineDowntime.Create";
        public const string Update = "MachineDowntime.Update";
        public const string Delete = "MachineDowntime.Delete";
    }

    public static class SortingMachines
    {
        public const string Read = "SortingMachines.Read";
        public const string Create = "SortingMachines.Create";
        public const string Update = "SortingMachines.Update";
    }

    public static class PartMachineEligibility
    {
        public const string Read = "PartMachineEligibility.Read";
        public const string Create = "PartMachineEligibility.Create";
        public const string Update = "PartMachineEligibility.Update";
        public const string Delete = "PartMachineEligibility.Delete";
    }

    public static class ProductionCalendars
    {
        public const string Read = "ProductionCalendars.Read";
        public const string Update = "ProductionCalendars.Update";
    }

    public static class ProductionHolidays
    {
        public const string Read = "ProductionHolidays.Read";
        public const string Create = "ProductionHolidays.Create";
        public const string Update = "ProductionHolidays.Update";
        public const string Delete = "ProductionHolidays.Delete";
    }

    public static class ProductionDowntimeReasons
    {
        public const string Read = "ProductionDowntimeReasons.Read";
        public const string Create = "ProductionDowntimeReasons.Create";
        public const string Update = "ProductionDowntimeReasons.Update";
        public const string Delete = "ProductionDowntimeReasons.Delete";
    }

    public static class ProductionSettings
    {
        public const string Read = "ProductionSettings.Read";
        public const string Update = "ProductionSettings.Update";
    }

    public static class ProductionLogs
    {
        public const string Read = "ProductionLogs.Read";
        public const string Create = "ProductionLogs.Create";
        public const string Update = "ProductionLogs.Update";
        public const string StartRun = "ProductionLogs.StartRun";
        public const string StopRun = "ProductionLogs.StopRun";
        public const string ResumeRun = "ProductionLogs.ResumeRun";
        public const string CorrectLine = "ProductionLogs.CorrectLine";
    }

    public static class SortLogDowntimeCauses
    {
        public const string Read = "SortLogDowntimeCauses.Read";
        public const string Create = "SortLogDowntimeCauses.Create";
        public const string Update = "SortLogDowntimeCauses.Update";
        public const string Delete = "SortLogDowntimeCauses.Delete";
    }

    public static class ProductionReports
    {
        public const string Read = "ProductionReports.Read";
    }

    public static class Chat
    {
        public const string Access = "Chat.Access";
        public const string CreateConversations = "Chat.CreateConversations";
        public const string SendMessages = "Chat.SendMessages";
        public const string ScheduleMessages = "Chat.ScheduleMessages";
        public const string CreateThreads = "Chat.CreateThreads";
        public const string CreatePolls = "Chat.CreatePolls";
        public const string VotePolls = "Chat.VotePolls";
        public const string React = "Chat.React";
        public const string PinMessages = "Chat.PinMessages";
        public const string EditOwnMessages = "Chat.EditOwnMessages";
        public const string DeleteOwnMessages = "Chat.DeleteOwnMessages";
        public const string DeleteOthersMessages = "Chat.DeleteOthersMessages";
        public const string CreateChannels = "Chat.CreateChannels";
        public const string UpdateChannels = "Chat.UpdateChannels";
        public const string DeleteChannels = "Chat.DeleteChannels";
        public const string ManagePrivateChannelMembers = "Chat.ManagePrivateChannelMembers";
        public const string CreateCategories = "Chat.CreateCategories";
        public const string ManageCategories = "Chat.ManageCategories";
        public const string ReorderChannels = "Chat.ReorderChannels";
        public const string ManageGroupConversations = "Chat.ManageGroupConversations";
        public const string AdministerChannels = "Chat.AdministerChannels";
    }

    public static class Roles
    {
        public const string Read = "Roles.Read";
        public const string Create = "Roles.Create";
        public const string Update = "Roles.Update";
        public const string Delete = "Roles.Delete";
        public const string ManagePermissions = "Roles.ManagePermissions";
        public const string ManageInheritance = "Roles.ManageInheritance";
    }

    public static class Authorization
    {
        public const string ManageSecurity = "Authorization.ManageSecurity";
    }

}
