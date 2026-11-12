using Confast.Web.Features.Authorization;
using Confast.Web.Features.Navigation;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using Npgsql;

namespace Confast.Web.Components.Pages;

public partial class HomeQuickAccess : IAsyncDisposable
{
    [Inject] private QuickAccessService QuickAccess { get; set; } = null!;
    [Inject] private AuthorizationChangeNotifications AuthorizationChanges { get; set; } = null!;
    [Inject] private ILogger<HomeQuickAccess> Logger { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [CascadingParameter] private EffectivePermissionSnapshot? Permissions { get; set; }

    private enum Panel { Tile, Add, Settings }

    private EffectivePermissionSnapshot? loadedPermissions;
    private QuickAccessView? view;
    private Panel? panel;
    private string? selectedId, error, status, pendingFocus;
    private string search = "";
    private bool busy, loading, confirmReset, openDialog, disposed;
    private long loadRequest;
    private ElementReference dialog;
    private IJSObjectReference? dialogModule;

    private QuickAccessTile? SelectedTile => view?.Tiles.FirstOrDefault(tile => tile.Destination.Id == selectedId);

    private string PanelTitle => panel switch
    {
        Panel.Tile => SelectedTile?.Destination.Label ?? "Shortcut options",
        Panel.Add => "Add shortcut",
        _ => confirmReset ? "Reset Quick Access?" : "Manage Quick Access"
    };

    private IEnumerable<NavigationItem> MatchingDestinations
    {
        get
        {
            if (view is null) return [];
            var pinned = view.Tiles.Where(tile => tile.IsPinned).Select(tile => tile.Destination.Id).ToHashSet();
            var query = search.Trim();
            return view.Destinations.Where(item => !pinned.Contains(item.Id)
                && (item.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || NavigationCatalog.CategoryFor(item).Label.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (loadedPermissions?.Version == Permissions?.Version && loadedPermissions?.ActorUserId == Permissions?.ActorUserId)
            return;

        loadedPermissions = Permissions;
        loadRequest++;
        view = null;
        PanelClosed();
        pendingFocus = null;
        status = null;
        loading = false;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (Permissions is not { } snapshot) return;
        var request = ++loadRequest;
        error = null;
        loading = true;
        try
        {
            var loaded = await QuickAccess.LoadAsync(snapshot);
            if (IsCurrent(request)) view = loaded;
        }
        catch (Exception exception) when (exception is NpgsqlException or DbUpdateException)
        {
            Logger.LogWarning(exception, "Could not load Quick Access preferences");
            if (IsCurrent(request)) error = "Quick Access could not be loaded. Your department links are still available.";
        }
        finally
        {
            if (IsCurrent(request)) loading = false;
        }
    }

    private async Task SaveAsync(QuickAccessChange change, string success, bool closePanel = false, string? focusId = null)
    {
        if (busy || Permissions is not { } snapshot) return;
        var request = loadRequest;
        busy = true;
        error = null;
        status = null;
        try
        {
            var result = await QuickAccess.ChangeAsync(snapshot, change);
            if (!IsCurrent(request)) return;

            view = result.View;
            error = result.Error;
            if (result.Error is null)
            {
                status = success;
                if (panel is not null) pendingFocus = focusId;
            }

            if (panel == Panel.Tile && SelectedTile is null)
            {
                pendingFocus = "quick-access-add";
                await ClosePanelAsync();
            }
            else if (result.Error is null && closePanel)
            {
                await ClosePanelAsync();
            }

            // Another process may have committed a newer authorization epoch. The save
            // used that snapshot, so ask the shell to refresh its visibility as well.
            if (snapshot.Version != result.Permissions.Version) AuthorizationChanges.Publish();
        }
        catch (AuthorizationDeniedException)
        {
            if (!IsCurrent(request)) return;
            view = null;
            PanelClosed();
            error = "Your access to Quick Access changed. Refresh the page and try again.";
            AuthorizationChanges.Publish();
        }
        catch (Exception exception) when (exception is NpgsqlException or DbUpdateException)
        {
            Logger.LogWarning(exception, "Could not save Quick Access change {Action}", change.Action);
            if (IsCurrent(request)) error = "Quick Access could not be updated. Try again.";
        }
        finally { busy = false; }
    }

    private bool IsCurrent(long request) => !disposed && request == loadRequest;

    private Task ChangeCapacityAsync(int capacity) =>
        SaveAsync(new(QuickAccessAction.SetCapacity, Capacity: capacity), $"Quick Access now has {capacity} slots.");

    private Task RestoreAllAsync() => SaveAsync(new(QuickAccessAction.RestoreAll), "Restored all removed shortcuts.",
        focusId: "quick-access-capacity");

    private Task ResetAsync() => SaveAsync(new(QuickAccessAction.Reset), "Quick Access preferences reset.", closePanel: true);

    private Task ChangeTileAsync(QuickAccessAction action, NavigationItem item)
    {
        var message = action switch
        {
            QuickAccessAction.Pin => $"Pinned {item.Label}.",
            QuickAccessAction.Unpin => $"Unpinned {item.Label}.",
            QuickAccessAction.Remove => $"Removed {item.Label} from Quick Access.",
            QuickAccessAction.Restore => $"Restored {item.Label}.",
            QuickAccessAction.MoveEarlier => $"Moved {item.Label} earlier.",
            QuickAccessAction.MoveLater => $"Moved {item.Label} later.",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return SaveAsync(new(action, item.Id), message, closePanel: action != QuickAccessAction.Restore,
            focusId: action == QuickAccessAction.Restore ? "quick-access-capacity" : null);
    }

    private void OpenPanel(Panel requested, NavigationItem? item = null)
    {
        if (busy) return;
        panel = requested;
        selectedId = item?.Id;
        confirmReset = false;
        search = "";
        error = null;
        openDialog = true;
    }

    private void AskToReset()
    {
        confirmReset = true;
        error = null;
        pendingFocus = "quick-access-cancel-reset";
    }

    private void CancelReset()
    {
        confirmReset = false;
        error = null;
        pendingFocus = "quick-access-reset";
    }

    private static string OptionsId(NavigationItem item) => $"shortcut-options-{item.Id}";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            if (openDialog && panel is { } openPanel)
            {
                openDialog = false;
                var opener = openPanel switch
                {
                    Panel.Tile => $"shortcut-options-{selectedId}",
                    Panel.Add => "quick-access-add",
                    _ => "quick-access-manage"
                };
                dialogModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/HomeQuickAccess.razor.js");
                if (!disposed && panel == openPanel) await dialogModule.InvokeVoidAsync("show", dialog, opener);
            }

            if (pendingFocus is { } id && !busy && dialogModule is not null)
            {
                pendingFocus = null;
                await dialogModule.InvokeVoidAsync("focus", id);
            }
        }
        catch (JSDisconnectedException) { }
    }

    private async Task ClosePanelAsync()
    {
        if (panel is null || dialogModule is null) return;
        try { await dialogModule.InvokeVoidAsync("close", dialog); }
        catch (JSDisconnectedException) { }
    }

    private void PanelClosed()
    {
        panel = null;
        selectedId = null;
        confirmReset = false;
        openDialog = false;
        error = null;
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        if (dialogModule is null) return;
        try { await dialogModule.DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }
}
