using System.Globalization;
using System.Text.RegularExpressions;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Backups;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using Npgsql;
using Keys = Confast.Web.Features.Authorization.Permissions.DatabaseBackups;

namespace Confast.Web.Components.Pages.Administration;

public partial class BackupManagerPage
{
    [Inject] private BackupManager Manager { get; set; } = null!;
    [Inject] private TimeProvider Clock { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private ILogger<BackupManagerPage> Logger { get; set; } = null!;

    private enum Panel { None, Create, Details, Delete, Unpin }
    private static readonly TimeZoneInfo ServerZone = TimeZoneInfo.Local;
    private static readonly DayOfWeek[] Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
    private static readonly (BackupNotice Kind, string Label, string Tooltip)[] NoticeChoices =
    [
        (BackupNotice.Failure, "Backup Failure", "Email when a backup attempt fails or is interrupted."),
        (BackupNotice.Integrity, "Integrity Failure", "Email when a backup is corrupt or fails verification."),
        (BackupNotice.Storage, "Unavailable Storage", "Email when the backup destination or the shared control directory is unavailable."),
        (BackupNotice.Capacity, "Insufficient Storage", "Email when there is too little room for a backup plus the reserve, or free space cannot be measured."),
        (BackupNotice.Overdue, "Overdue Backups", "Email when a scheduled backup is overdue or the schedule needs review after a server time zone change."),
        (BackupNotice.Retention, "Retention Failure", "Email when automatic cleanup fails or is paused pending review."),
        (BackupNotice.Catalog, "Catalog Inconsistencies", "Email when reconciliation finds issues such as missing backup sets, duplicates, or metadata mismatches."),
        (BackupNotice.Success, "Successful Backups", "Email when a manual or scheduled backup completes successfully.")
    ];
    private readonly CancellationTokenSource lifetime = new();
    private BackupOverview? overview;
    private BackupHistory history = new([], 0);
    private BackupHistoryFilter filter = new();
    private BackupDetails? details;
    private BackupSettings? reviewedSettings;
    private Task? polling;
    private IJSObjectReference? module;
    private ElementReference dialog;
    private string? browserZoneId;
    private TimeZoneInfo browserZone = ServerZone;
    private DateTimeOffset scheduleReference;
    private bool draftLoaded;
    private bool busy;
    private bool refreshing;
    private string? message;
    private bool messageError;
    private string? testMessage;
    private bool testError;
    private string? dialogMessage;
    private bool dialogError;
    private Panel panel;
    private bool showDialog;
    private Guid draftRevision;
    private string destination = "";
    private bool enabled;
    private bool catchUp;
    private bool fullVerification;
    private int retain;
    private decimal reserveGiB;
    private readonly List<RecipientEditor> recipients = [];
    private sealed class RecipientEditor
    {
        public string Email { get; set; } = "";
        public BackupNotice Notices { get; set; } = BackupNotice.Default;
    }
    private BackupSchedule schedule = new(BackupFrequency.Daily, new TimeOnly(2, 0));
    private BackupSchedule originalSchedule = new(BackupFrequency.Daily, new TimeOnly(2, 0));
    private string? search;
    private BackupOutcome? outcome;
    private BackupTrigger? trigger;
    private string pinFilter = "";
    private BackupAvailability? availability;
    private DateOnly? fromDate;
    private DateOnly? throughDate;
    private BackupHistorySort sort;
    private string? manualLabel;
    private string? manualNotes;
    private bool manualPin;
    private string? editLabel;
    private string? editNotes;
    private Guid editVersion;
    private bool editing;
    private bool showSavedDetails;

    private int FirstVisible => filter.Page * BackupHistory.PageSize + 1;
    private int LastVisible => FirstVisible + history.Records.Count - 1;

    protected override Task OnInitializedAsync() => RefreshAsync();
    protected override Task OnPermissionsChangedAsync() => RefreshAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                module = await JS.InvokeAsync<IJSObjectReference>("import", lifetime.Token,
                    "./Components/Pages/Administration/BackupManagerPage.razor.js");
                browserZoneId = await module.InvokeAsync<string?>("timeZone", lifetime.Token);
                browserZone = BackupSchedule.BrowserZone(browserZoneId, ServerZone, out _);
                if (overview is { } current && schedule == originalSchedule)
                    schedule = originalSchedule = current.Settings.Schedule.InZone(ServerZone, browserZone, scheduleReference);
            }
            catch (JSException ex) { Logger.LogDebug("Backup Manager browser time zone is unavailable ({FailureType}).", ex.GetType().Name); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            polling = PollAsync(lifetime.Token);
            StateHasChanged();
        }

        if (showDialog && module is not null)
        {
            showDialog = false;
            try { await module.InvokeVoidAsync("open", lifetime.Token, dialog); }
            catch (JSDisconnectedException) { /* The browser already left the page. */ }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await InvokeAsync(async () =>
                {
                    if (busy || refreshing) return;
                    try { await ReloadAsync(); }
                    catch (AuthorizationDeniedException) { ShowDenied(); }
                    catch (Exception ex) when (ex is NpgsqlException or IOException or BackupException)
                    {
                        ShowMessage("Backup status could not be refreshed.", error: true);
                    }
                    StateHasChanged();
                });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task ReloadAsync()
    {
        refreshing = true;
        try
        {
            var current = await Manager.GetOverviewAsync(lifetime.Token);
            overview = current;
            if (!draftLoaded) ResetDraft();
            await LoadHistoryAsync();
            if (panel == Panel.Details && details is { } selected)
            {
                var refreshed = await Manager.GetDetailsAsync(selected.Execution.Id, lifetime.Token);
                if (details?.Execution.Id == refreshed.Execution.Id && panel == Panel.Details) details = refreshed;
            }
        }
        finally { refreshing = false; }
    }

    private Task RefreshAsync() => PerformAsync(ReloadAsync);

    private void ResetDraft()
    {
        if (overview?.Settings is not { } value) return;
        draftLoaded = true;
        draftRevision = value.Revision;
        (destination, enabled, catchUp, fullVerification, retain, reserveGiB) =
            (value.Destination ?? "", value.Destination is null || value.Enabled,
                value.CatchUp, value.FullVerification, value.Retain, value.ReserveBytes / 1073741824m);
        recipients.Clear();
        recipients.AddRange(value.Recipients.OrderBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .Select(x => new RecipientEditor { Email = x.Email, Notices = x.Notices }));
        scheduleReference = Clock.GetUtcNow();
        schedule = originalSchedule = value.Schedule.InZone(ServerZone, browserZone, scheduleReference);
        reviewedSettings = null;
        ClearTest();
    }

    private void ReviewLatestSettings()
    {
        if (overview?.Settings is not { } current) return;
        reviewedSettings = current;
        draftRevision = current.Revision;
        if (schedule == originalSchedule)
        {
            scheduleReference = Clock.GetUtcNow();
            schedule = originalSchedule = current.Schedule.InZone(ServerZone, browserZone, scheduleReference);
        }
    }

    private void ClearTest() => testMessage = null;
    private void SetFrequency(BackupFrequency value) => schedule = schedule with { Frequency = value, MonthShift = 0 };
    private void SetHours(int value) => schedule = schedule with { Hours = value };
    private void SetTime(TimeOnly value) => schedule = schedule with { Time = value };
    private void SetMonthDay(int value) => schedule = schedule with { MonthDay = value, MonthShift = 0 };
    private bool IsSelected(DayOfWeek day) => (schedule.Weekdays & 1 << (int)day) != 0;
    private void ToggleDay(DayOfWeek day, bool selected) => schedule = schedule with
    {
        Weekdays = selected ? schedule.Weekdays | 1 << (int)day : schedule.Weekdays & ~(1 << (int)day)
    };
    private static void ToggleNotice(RecipientEditor recipient, BackupNotice kind, bool selected) =>
        recipient.Notices = selected ? recipient.Notices | kind : recipient.Notices & ~kind;

    private (BackupSchedule Rule, IReadOnlyList<DateTimeOffset> Times)? GetPreview()
    {
        try
        {
            schedule.Validate();
            var rule = Can(Keys.Configure) && schedule != originalSchedule
                ? schedule.InZone(browserZone, ServerZone, scheduleReference)
                : overview!.Settings.Schedule;
            rule.Validate();
            return (rule, rule.Preview(Clock.GetUtcNow(), ServerZone));
        }
        catch (Exception ex) when (ex is BackupException or ArgumentException or ArithmeticException) { return null; }
    }

    private Task SaveSettingsAsync() => PerformAsync(async () =>
    {
        if (retain is < 1 or > 1000 || reserveGiB is < 0 or > 10240)
            throw new BackupException("RetentionSettings", "Enter a retention count of 1–1,000 and a reserve between 0 and 10,240 GiB.");
        await Manager.SaveSettingsAsync(new(draftRevision, destination, enabled, catchUp, fullVerification, retain,
            (long)(reserveGiB * 1073741824m), recipients.Select(x => new BackupRecipientDraft(x.Email, x.Notices)).ToArray(),
            schedule == originalSchedule ? null : schedule, browserZoneId, scheduleReference), lifetime.Token);
        await ReloadAsync();
        ResetDraft();
        ShowMessage("Backup configuration saved.");
    });

    private Task TestDestinationAsync() => PerformAsync(async () =>
    {
        var tested = await Manager.TestDestinationAsync(destination, lifetime.Token);
        testError = false;
        testMessage = tested.FreeBytes is { } free
            ? $"Destination passed the read, write, rename, and delete checks. {Bytes(free)} free."
            : "Destination passed the read, write, rename, and delete checks. Free space is unknown.";
    }, destinationTest: true);

    private Task ApplyFiltersAsync() => PerformAsync(async () =>
    {
        if (fromDate > throughDate || fromDate is { Year: < 1900 or > 9900 } || throughDate is { Year: < 1900 or > 9900 })
            throw new BackupException("FilterDates", "Enter a valid date range.");
        filter = new(search, outcome, trigger, bool.TryParse(pinFilter, out var pin) ? pin : null, availability,
            fromDate is { } from ? BackupSchedule.ToUtc(from.ToDateTime(TimeOnly.MinValue), browserZone) : null,
            throughDate is { } through ? BackupSchedule.ToUtc(through.AddDays(1).ToDateTime(TimeOnly.MinValue), browserZone) : null, sort);
        await LoadHistoryAsync();
    });

    private Task ClearFiltersAsync() => PerformAsync(async () =>
    {
        search = null;
        outcome = null;
        trigger = null;
        pinFilter = "";
        availability = null;
        fromDate = throughDate = null;
        sort = BackupHistorySort.Newest;
        filter = new();
        await LoadHistoryAsync();
    });

    private Task SetPageAsync(int page) => PerformAsync(async () =>
    {
        filter = filter with { Page = page };
        await LoadHistoryAsync();
    });

    private async Task LoadHistoryAsync()
    {
        var current = filter;
        var result = await Manager.GetHistoryAsync(current, lifetime.Token);
        if (filter != current) return;
        history = result;
        filter = filter with { Page = Math.Min(filter.Page, Math.Max(0, (history.Total - 1) / BackupHistory.PageSize)) };
    }

    private Task ReconcileAsync() => PerformAsync(async () =>
    {
        await Manager.RequestReconciliationAsync(lifetime.Token);
        ShowMessage("Catalog reconciliation queued.");
    });

    private void OpenCreate()
    {
        manualLabel = manualNotes = null;
        manualPin = false;
        OpenDialog(Panel.Create);
    }

    private Task CreateAsync() => PerformAsync(async () =>
    {
        await Manager.RequestBackupAsync(manualPin, manualLabel, manualNotes, lifetime.Token);
        await CloseDialogAsync();
        await ReloadAsync();
        ShowMessage("Backup queued. Follow its progress in Backup Status and Backup History.");
    }, inDialog: true);

    private Task OpenDetailsAsync(Guid id) => PerformAsync(async () =>
    {
        details = await Manager.GetDetailsAsync(id, lifetime.Token);
        editing = false;
        OpenDialog(Panel.Details);
    });

    private Task RefreshDetailsAsync() => PerformAsync(async () =>
    {
        details = await Manager.GetDetailsAsync(details!.Execution.Id, lifetime.Token);
        if (editing)
        {
            editVersion = details.Execution.MetadataVersion;
            showSavedDetails = true;
        }
    }, inDialog: true);

    private void BeginEdit()
    {
        var current = details!.Execution;
        (editLabel, editNotes, editVersion) = (current.Label, current.Notes, current.MetadataVersion);
        editing = true;
        showSavedDetails = false;
        dialogMessage = null;
    }

    private Task SaveMetadataAsync() => PerformAsync(async () =>
    {
        await Manager.EditMetadataAsync(details!.Execution.Id, editVersion, editLabel, editNotes, lifetime.Token);
        editing = false;
        await ReloadAsync();
        dialogMessage = "Backup details saved.";
    }, inDialog: true);

    private Task TogglePinAsync()
    {
        if (details is not { } selected) return Task.CompletedTask;
        if (selected.Execution.Pinned && selected.LatestAvailable)
        {
            panel = Panel.Unpin;
            dialogMessage = null;
            return Task.CompletedTask;
        }
        return PerformAsync(async () =>
        {
            try
            {
                await Manager.SetPinnedAsync(selected.Execution.Id, selected.Execution.MetadataVersion,
                    !selected.Execution.Pinned, confirmedLatest: false, lifetime.Token);
                await ReloadAsync();
            }
            catch (BackupException ex) when (ex.Code == "ConfirmLatest")
            {
                details = await Manager.GetDetailsAsync(selected.Execution.Id, lifetime.Token);
                panel = Panel.Unpin;
            }
        }, inDialog: true);
    }

    private Task VerifyAsync() => PerformAsync(async () =>
    {
        await Manager.RequestVerificationAsync(details!.Execution.Id, lifetime.Token);
        details = await Manager.GetDetailsAsync(details.Execution.Id, lifetime.Token);
    }, inDialog: true);

    private void OpenDelete()
    {
        panel = Panel.Delete;
        dialogMessage = null;
    }

    private void BackToDetails()
    {
        panel = Panel.Details;
        dialogMessage = null;
    }

    private Task ConfirmActionAsync() => PerformAsync(async () =>
    {
        var selected = details!;
        try
        {
            if (panel == Panel.Delete)
                await Manager.DeleteAsync(selected.Execution.Id, selected.Execution.MetadataVersion, confirmed: true,
                    selected.LatestAvailable, lifetime.Token);
            else
                await Manager.SetPinnedAsync(selected.Execution.Id, selected.Execution.MetadataVersion, pin: false,
                    selected.LatestAvailable, lifetime.Token);
            panel = Panel.Details;
            await ReloadAsync();
        }
        catch (BackupException ex) when (ex.Code == "ConfirmLatest")
        {
            details = await Manager.GetDetailsAsync(selected.Execution.Id, lifetime.Token);
        }
    }, inDialog: true);

    private void OpenDialog(Panel value)
    {
        panel = value;
        dialogMessage = null;
        showDialog = true;
    }

    private void DialogClosed()
    {
        panel = Panel.None;
        editing = false;
        details = null;
    }

    private async Task CloseDialogAsync()
    {
        if (module is not null) await module.InvokeVoidAsync("close", lifetime.Token, dialog);
        DialogClosed();
    }

    private async Task PerformAsync(Func<Task> action, bool inDialog = false, bool destinationTest = false)
    {
        if (busy) return;
        busy = true;
        (message, dialogMessage) = (null, null);
        dialogError = false;
        try { await action(); }
        catch (AuthorizationDeniedException ex)
        {
            if (!Can(Keys.View)) ShowDenied();
            else Report(ex.Message);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or NpgsqlException or DbUpdateException)
        {
            Logger.LogDebug("Backup Manager action failed ({FailureType}).", ex is BackupException backup ? backup.Code : ex.GetType().Name);
            Report(ex is BackupException expected ? expected.Message : "The backup operation could not finish. Check server availability and try again.");
        }
        finally { busy = false; }

        void Report(string text)
        {
            if (inDialog && panel != Panel.None) (dialogMessage, dialogError) = (text, true);
            else if (destinationTest) (testMessage, testError) = (text, true);
            else ShowMessage(text, error: true);
        }
    }

    private void ShowMessage(string text, bool error = false) => (message, messageError) = (text, error);

    private void ShowDenied()
    {
        overview = null;
        DialogClosed();
        ShowMessage("You no longer have access to Backup Manager.", error: true);
    }

    private static bool Manageable(BackupExecution row) => row.Availability is BackupAvailability.Available or BackupAvailability.Corrupted;
    private static string SchedulerStatus(BackupOverview overview) =>
        !overview.Configured ? "Not Configured" : !overview.Settings.Enabled ? "Disabled" :
        overview.Settings.ServerZoneId != ServerZone.Id ? "Needs Schedule Review" : "Enabled";

    private static string ScheduleText(BackupSchedule rule, string? zone)
    {
        var time = rule.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
        var description = rule.Frequency switch
        {
            BackupFrequency.Hourly => $"Every {rule.Hours} hour(s) from {time}",
            BackupFrequency.Weekly => $"{string.Join(", ", Weekdays.Where(day => (rule.Weekdays & 1 << (int)day) != 0))} at {time}",
            BackupFrequency.Monthly => $"Monthly on day {rule.MonthDay}" +
                (rule.MonthShift == 0 ? "" : $" with a {rule.MonthShift:+#;-#} day shift") + $" at {time}",
            _ => $"Daily at {time}"
        };
        return $"{description} server time ({zone ?? ServerZone.Id})";
    }

    private string When(DateTimeOffset? utc) => utc is { } time
        ? TimeZoneInfo.ConvertTime(time, browserZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    private static string Bytes(double? bytes)
    {
        if (bytes is not { } number) return "Unknown";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        var unit = 0;
        while (number >= 1024 && unit < units.Length - 1) { number /= 1024; ++unit; }
        return $"{number:0.##} {units[unit]}";
    }

    private static string Duration(double? ms) => ms is { } time
        ? TimeSpan.FromMilliseconds(time).ToString(time >= 86_400_000 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss", CultureInfo.InvariantCulture) : "—";
    private string Age(DateTimeOffset completed)
    {
        var age = Clock.GetUtcNow() - completed;
        if (age.TotalDays >= 1) return $"{(int)age.TotalDays}d {age.Hours}h";
        if (age.TotalHours >= 1) return $"{(int)age.TotalHours}h {age.Minutes}m";
        return $"{Math.Max(0, age.Minutes)}m";
    }

    private static string Stage(BackupStage stage) => EventText(stage.ToString());
    private static string EventText(string action) => Regex.Replace(action, "(?<=[a-z])(?=[A-Z])", " ");
    private static string VerificationText(BackupVerification verification) => verification switch
    {
        BackupVerification.ArchiveListed => "Archive Listed · SHA-256 Recorded",
        BackupVerification.Verified => "Archive Read · SHA-256 Checked",
        BackupVerification.Failed => "Failed",
        _ => "—"
    };

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (polling is not null) await polling;
        if (module is not null)
        {
            try { await module.DisposeAsync(); }
            catch (JSDisconnectedException) { /* The browser already left the page. */ }
        }
        lifetime.Dispose();
    }
}
