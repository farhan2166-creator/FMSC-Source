using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Administration;

/// <summary>One consistent draft, save, reload and recovery flow for restricted access maintenance.</summary>
public abstract partial class AccessEditorPage : V2Page, IUnsavedChanges, IBusyPage
{
    private protected readonly IAdministrationStore Store;
    private protected readonly AccessEditorKind Kind;
    private readonly bool canRead, canManage;
    private readonly int owner = CurrentUser.UserId;
    private int generation;
    private bool loading, saving, applying, initialized, committedWaitingForReload;
    private AccessDraft? baseline;
    internal AccessCatalog? Catalog { get; private set; }
    internal AccessRecord? Original { get; private set; }
    internal AccessWriteIntent? Unconfirmed { get; private set; }
    internal bool NeedsReload { get; private set; }
    internal bool Creating { get; private set; }
    internal bool HasEditor => Creating || Original is not null;
    internal Func<string, bool>? ConfirmForTests { get; set; }
    public bool IsBusy => loading || saving;
    public string BusyMessage => "Wait for the user/role operation to finish before leaving.";
    public bool HasUnsavedChanges => Unconfirmed is not null || !committedWaitingForReload && HasEditor &&
        Original?.Protected != true && (baseline is null || !CaptureDraft().SameValues(baseline) || ExtraDirty);
    public string UnsavedChangesMessage => Unconfirmed is not null
        ? "These access changes may already have been saved. Use Check Saved Changes before retrying. Leave this draft?"
        : "Discard the unsaved account/access changes? Previously saved access stays unchanged.";
    private protected virtual bool ExtraDirty => false;
    private protected virtual string? CreatePassword => null;
    private protected virtual bool ValidateExtra() => true;
    private protected virtual void ClearSecrets() { }
    private protected virtual object? CaptureExtra() => null;
    private protected virtual void RestoreExtra(object? state) { }
    private protected virtual void ModeChanged() { }
    private protected virtual void ExtraEnablement(bool editable) { }

    private protected AccessEditorPage(AccessEditorKind kind, IAdministrationStore store, AccessRules rules)
        : base(kind == AccessEditorKind.Users ? "Users" : "Roles & Permissions",
            "Account administration · choose a record, edit its draft, then save. Access menus refresh at the next sign-in.")
    {
        Kind = kind; Store = store;
        canRead = kind == AccessEditorKind.Users ? rules.CanReadUsers : rules.CanReadRoles;
        canManage = kind == AccessEditorKind.Users ? rules.CanManageUsers : rules.CanManageRoles;
        BuildLayout();
        Disposed += (_, _) => { generation++; searchTimer.Dispose(); choiceTimer.Dispose(); recordsData?.Dispose(); choicesData?.Dispose(); };
    }
    private protected void CompleteLayout(bool initialize)
    {
        initialized = true;
        ModeChanged(); UpdateActions();
        FormFlow.EnterMovesToNext(this);
        SetSaveShortcut(SaveAction);
        if (!canRead) SetStatus("Your account needs the viewing permissions for this maintenance screen.",PageStatusKind.Warning);
        else if (initialize) Defer(InitializeAsync);
    }
    private bool IsCurrent(int version) => !IsDisposed && version == generation && CurrentUser.UserId == owner;
    private bool Confirm(string message) => ConfirmForTests?.Invoke(message)
        ?? ModernMessageDialog.Confirm(FindForm(),message,"Review access changes",defaultYes:false);
    private bool MayReplaceDraft() => !HasUnsavedChanges || Confirm(UnsavedChangesMessage);
    internal AccessDraft CaptureDraft() => new(Original?.Id ?? 0,NameInput.Text,
        Kind == AccessEditorKind.Roles ? DescriptionInput.Text : "",Kind == AccessEditorKind.Users && ForceChange.Checked,
        choicesData?.AsEnumerable().Where(row => (bool)row["Selected"]).Select(row => (int)row["ChoiceId"]).Order().ToArray() ?? Array.Empty<int>());

    internal Task InitializeAsync() => InitializeCoreAsync(true);
    private async Task InitializeCoreAsync(bool confirm)
    {
        if (!canRead || IsBusy || confirm && !MayReplaceDraft()) return;
        int version = ++generation; loading = true; UpdateActions();
        try
        {
            using var progress = BeginLoading("Loading access definitions…");
            var catalog = await Store.LoadCatalogAsync();
            if (!IsCurrent(version)) return;
            ApplyFrame(catalog,null,false);
            SetStatus(Kind==AccessEditorKind.Users&&!catalog.Roles.Any(role=>AdministrativeAccess.IsAccountant(role.Name))
                ? "The Accountant role is missing. Apply the database migration before creating users."
                : Kind==AccessEditorKind.Roles ? "Choose Accountant to review its permissions. Administrator always has full authority."
                : "Choose a user on the left, or create a new one.");
        }
        catch (Exception ex) { if (IsCurrent(version)) ReadFailed(ex); }
        finally { if (version == generation) loading = false; if (!IsDisposed) UpdateActions(); }
    }
    internal async Task<bool> SelectAsync(int id)
    {
        if (!canRead || IsBusy || id <= 0) { RestoreListSelection(); return false; }
        if (Original?.Id == id && !NeedsReload && Unconfirmed is null) { RestoreListSelection(); return true; }
        if (!MayReplaceDraft()) { RestoreListSelection(); return false; }
        return await ReadSelectedAsync(id);
    }
    private async Task<bool> ReadSelectedAsync(int id)
    {
        int version = ++generation; loading = true; UpdateActions();
        try
        {
            using var progress = BeginLoading("Opening access details…");
            var frame = await Store.LoadAsync(id);
            if (!IsCurrent(version)) return false;
            if(frame.Record.Id!=id)throw new InvalidDataException("The read returned a different selected record.");
            ApplyFrame(frame.Catalog,frame.Record,false);
            SetStatus(frame.Record.Protected ? "Protected Administrator. Use the established account recovery/setup flow; access is read-only here."
                : !canManage ? "Read-only access. Your account can inspect this record but cannot change it."
                : "Edit the displayed draft, then save. Nothing changes until Save.");
            return true;
        }
        catch (Exception ex) { if (IsCurrent(version)) ReadFailed(ex); RestoreListSelection(); return false; }
        finally { if (version == generation) loading = false; if (!IsDisposed) UpdateActions(); }
    }
    internal async Task ReloadAsync()
    {
        if (!canRead || IsBusy) return;
        if (Unconfirmed is not null) { await CheckAsync(); return; }
        if (!MayReplaceDraft()) return;
        if (Original is { } original) await ReadSelectedAsync(original.Id);
        else await InitializeCoreAsync(false);
    }
    internal void NewRecord()
    {
        if (Kind == AccessEditorKind.Roles) return;
        if (!canManage || IsBusy || NeedsReload || Unconfirmed is not null || Catalog is null || !MayReplaceDraft()) return;
        if (Kind == AccessEditorKind.Users && !Catalog.Roles.Any(role => AdministrativeAccess.IsAccountant(role.Name)))
        { SetStatus("The Accountant role is missing. Apply the database migration before creating users.",PageStatusKind.Warning); return; }
        ++generation; ApplyFrame(Catalog,null,true);
        SetStatus(Kind == AccessEditorKind.Users
            ? "New user: enter details and password, select Accountant access if needed, then Save User. Unchecked means no access."
            : "New role: enter its name and description, choose permissions, then Save Role. Empty permissions mean no access.");
        NameInput.Focus();
    }
    private void ReadFailed(Exception ex)
    {
        AppExceptionHandler.LogNonFatal(ex,"Reading restricted administration details");
        SetStatus("Could not load the details. The current draft has been retained. " + DbMessage.UserMessage(ex),PageStatusKind.Warning);
    }
    private void DraftChanged()
    {
        if (!initialized || applying || IsDisposed) return;
        UpdateSummary(); UpdateActions();
    }
    private protected void PasswordDraftChanged() => DraftChanged();

    internal async Task SaveAsync()
    {
        if (!canManage || IsBusy || !HasEditor || Original?.Protected == true || NeedsReload || Unconfirmed is not null || Catalog is null) return;
        if (!Assignments.EndEdit()) { SetStatus("Finish the current access selection before saving.",PageStatusKind.Warning); return; }
        var draft = CaptureDraft().Clean();
        if (AdministrativeAccess.Validate(Kind,draft,Catalog) is { } problem)
        { SetStatus(problem,PageStatusKind.Warning); NameInput.Focus(); return; }
        if (!ValidateExtra()) return;
        if (!Creating && baseline is not null && draft.SameValues(baseline.Clean()) && !ExtraDirty)
        {
            applying = true;
            try { NameInput.Text=draft.Name;DescriptionInput.Text=draft.Description; }
            finally { applying=false; }
            SetStatus("No changes to save."); UpdateSummary();UpdateActions(); return;
        }
        if (!ReviewAccessChange(draft)) return;
        string snapshot = Original?.Snapshot ?? "", catalogToken = Catalog.Token;
        string? password = Creating ? CreatePassword : null;
        int version = ++generation; saving = true; UpdateActions();
        try
        {
            using var progress = BeginLoading(Kind == AccessEditorKind.Users ? "Saving user…" : "Saving role and permissions…");
            var result = await Store.SaveAsync(draft,snapshot,catalogToken,password);
            if (!IsCurrent(version)) return;
            await HandleSaveAsync(result,draft,version);
        }
        catch (Exception ex)
        {
            if (IsCurrent(version))
            {
                AppExceptionHandler.LogNonFatal(ex,"Restricted administration save");
                SetStatus("The operation could not complete. Your draft is retained. " + DbMessage.UserMessage(ex),PageStatusKind.Warning);
            }
        }
        finally { if (version == generation) saving = false; if (!IsDisposed) UpdateActions(); }
    }
    private bool ReviewAccessChange(AccessDraft draft)
    {
        if (Catalog is null) return false;
        var previous = Kind == AccessEditorKind.Users ? AdministrativeAccess.EffectivePermissions(Catalog,Original?.Choices ?? Array.Empty<int>())
            : (Original?.Choices ?? Array.Empty<int>()).ToHashSet();
        var proposed = Kind == AccessEditorKind.Users ? AdministrativeAccess.EffectivePermissions(Catalog,draft.Choices) : draft.Choices.ToHashSet();
        int removed = previous.Except(proposed).Count();
        var added = proposed.Except(previous).ToHashSet();
        bool privileged = Catalog.Permissions.Any(permission => added.Contains(permission.Id) &&
            (permission.Code.StartsWith("System.",StringComparison.OrdinalIgnoreCase) &&
                (permission.Code.EndsWith(".Manage",StringComparison.OrdinalIgnoreCase) || permission.Code.Equals("System.Administration",StringComparison.OrdinalIgnoreCase))));
        if (removed == 0 && !privileged) return true;
        string target = Original?.Name ?? draft.Name;
        string impact = Kind == AccessEditorKind.Roles && Original is { MemberCount: >0 } role
            ? $" This role is assigned to {role.MemberCount} account(s)." : "";
        return Confirm($"Save access changes for {target}? {added.Count} permission(s) added and {removed} removed.{impact}" +
            (privileged ? " Administrative capabilities are being granted." : "") +
            (Kind == AccessEditorKind.Users && draft.Choices.Length == 0 ? " No assigned roles means this account has no access." : ""));
    }
    private async Task HandleSaveAsync(AccessSaveResult result, AccessDraft draft, int version)
    {
        if (result.Error is { } error) AppExceptionHandler.LogNonFatal(error,"Restricted administration write result");
        if (result.Outcome == AccessSaveOutcome.Rejected)
        {
            if (result.Error is AdministrativeStaleStateException) NeedsReload = true;
            SetStatus("Changes were not saved. Your draft is retained. " + DbMessage.UserMessage(result.Error ?? new IOException("The write was refused.")),PageStatusKind.Warning);
            return;
        }
        if (result.Outcome == AccessSaveOutcome.Unconfirmed)
        {
            Unconfirmed = result.Intent ?? new(Kind,draft,OriginalSnapshot:Original?.Snapshot ?? "");
            NeedsReload = true;
            SetStatus("The save outcome is not confirmed. Entries are retained. Use Check Saved Changes; do not submit again until checked.",PageStatusKind.Warning);
            return;
        }
        await ConfirmedSaveAsync(result.Id,draft,version);
    }
    private async Task ConfirmedSaveAsync(int id, AccessDraft draft, int version)
    {
        if (id <= 0 || draft.Id > 0 && id != draft.Id) throw new InvalidDataException("The confirmed administrative ID is invalid.");
        Unconfirmed=null;NeedsReload=true;committedWaitingForReload=true;Creating=false;
        var saved = draft.Clean() with { Id=id };
        Original = new(id,saved.Name,saved.Description,saved.ForcePasswordChange,false,saved.Choices,"",MemberCount:Original?.MemberCount ?? 0);
        baseline=saved;
        LookupDataCache.Invalidate();
        try
        {
            applying=true;
            try { NameInput.Text=saved.Name;DescriptionInput.Text=saved.Description;ForceChange.Checked=saved.ForcePasswordChange;ClearSecrets();ModeChanged(); }
            finally { applying=false; }
            var frame = await Store.LoadAsync(id);
            if (!IsCurrent(version)) return;
            if(frame.Record.Id!=id)throw new InvalidDataException("The refresh returned a different saved record.");
            ApplyFrame(frame.Catalog,frame.Record,false);
            SetStatus("Changes saved. Sign out and back in to refresh affected users' menus and access. Database operations check current permissions.",PageStatusKind.Success);
        }
        catch (Exception ex)
        {
            if (!IsCurrent(version)) return;
            AppExceptionHandler.LogNonFatal(ex,"Refreshing committed administration changes");
            SetStatus("Changes were saved, but the screen could not refresh. Reload before further edits; sign out and back in if your own access changed. Do not save the same changes again.",PageStatusKind.Warning);
        }
    }

    internal async Task CheckAsync()
    {
        if (!canRead || IsBusy || Unconfirmed is not { } intent) return;
        int version=++generation;saving=true;UpdateActions();
        try
        {
            using var progress=BeginLoading("Checking saved access changes…");
            var checkedResult=await Store.CheckAsync(intent);
            if (!IsCurrent(version)) return;
            if (checkedResult.Outcome == AccessCheckOutcome.Saved && checkedResult.Record is { } saved)
            {
                ApplyFrame(checkedResult.Catalog,saved,false);
                LookupDataCache.Invalidate();
                SetStatus("The saved values were verified. No write was repeated. Sign out and back in to refresh affected users' menus.",PageStatusKind.Success);
            }
            else if (checkedResult.Outcome == AccessCheckOutcome.NotApplied)
            {
                // Keep the operator's exact draft; a read-only check established the old state/absence.
                bool changedCatalog=Catalog?.Token!=checkedResult.Catalog.Token;
                Unconfirmed=null;NeedsReload=changedCatalog;committedWaitingForReload=false;
                if(!changedCatalog)Catalog=checkedResult.Catalog;
                SetStatus(changedCatalog ? "The save was not applied, but access definitions changed. Your draft is retained; reload and review before saving."
                    : "The checked save was not applied. Your draft is retained; you may review it and deliberately save again.",PageStatusKind.Warning);
            }
            else
            {
                Unconfirmed=null;NeedsReload=true;
                SetStatus("The stored values differ from this draft/original. Reload and review before further changes; your entries are retained here.",PageStatusKind.Warning);
            }
        }
        catch (Exception ex)
        {
            if (IsCurrent(version)) { AppExceptionHandler.LogNonFatal(ex,"Checking an uncertain administration save");SetStatus("Could not confirm the save. The draft is retained; Check Saved Changes remains available. " + DbMessage.UserMessage(ex),PageStatusKind.Warning); }
        }
        finally { if (version==generation) saving=false;if(!IsDisposed)UpdateActions(); }
    }

    private protected async Task<AccessSaveResult> ResetAsync(string password, bool forceChange)
    {
        if (Kind != AccessEditorKind.Users || !canManage || IsBusy || Original is not { Protected:false } original || Creating ||
            HasUnsavedChanges || NeedsReload || Unconfirmed is not null || Catalog is null)
            return new(AccessSaveOutcome.Rejected,Error:new AdministrativeStaleStateException("Save/discard and reload the user draft before resetting its password."));
        int version=++generation;saving=true;UpdateActions();
        try
        {
            using var progress=BeginLoading("Resetting password…");
            var result=await Store.ResetPasswordAsync(original,Catalog.Token,password,forceChange);
            if (IsCurrent(version)) await HandleSaveAsync(result,AccessDraft.From(original) with { ForcePasswordChange=forceChange },version);
            return result;
        }
        catch (Exception ex) { return new(AccessSaveOutcome.Rejected,Error:ex); }
        finally { if(version==generation)saving=false;if(!IsDisposed)UpdateActions(); }
    }
}
