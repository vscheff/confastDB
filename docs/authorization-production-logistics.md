# Phase 3D-2 Logistics and Production Authorization

**Phase 3F status (October 16, 2026):** the catalog-backed role matrix, provenance and impact previews, safe role/user editing, process-local advisory refresh, and channel moderation-history UI are implemented. See [the administration UI record](authorization-administration-ui.md) for behavior, validation, and remaining Phase 3G limits. This supersedes historical matrix/refresh/history-UI deferrals below.

**Phase 3E-2 status (October 16, 2026):** scheduled delegation/lifecycle, scoped renderer execution, create-only bootstrap and guarded Development diagnostics are implemented. See [the background and delegation record](authorization-background-delegation.md) for the complete execution inventory, migration, validation and remaining Phase 3F/3G boundaries. This supersedes historical background/bootstrap deferrals below.

**Phase 3E-1 status (October 15, 2026):** chat permissions, media/subscription privacy and scoped moderation are implemented in [the chat authorization record](authorization-chat.md). Its boundary inventory and verification supersede older chat deferrals below; the full hosted-worker redesign remains Phase 3E-2.

Implemented October 15, 2026; final verification status is recorded below. This record supersedes the historical audit only for the explicitly listed boundaries. The 164-key catalog, grants, optional ReadOnly model, Root designation, EF migrations and manufacturing calculations are unchanged.

## Stage A boundary inventory

Every listed entry creates a fresh trusted operation. Prerequisites come from the existing catalog. Conditional checks reuse that operation, and use persisted records inside the existing serializable transaction. A nonzero missing ID is rejected; it never falls back to creation. No actor ID, role claim or client receipt/departure flag authorizes a command.

| Service / public method | Permission | Internal scope / conditions | Verification |
|---|---|---|---|
| ContainerTrackingService.SearchAsync | Shipments.Read | Container projection/search requires Containers.Read; shipment-owned bill numbers are aggregate rows | LogisticsAuthorizationTests; ContainerTrackingTests |
| GetShipmentAsync | Shipments.Read | One shipment and its owned bill numbers | Same |
| SaveShipmentAsync | Shipments.Create or Shipments.Update | Existing target and owned bill IDs verified; no missing-ID upsert | Same |
| DeleteShipmentAsync | Shipments.Delete | Existing nonempty-shipment restriction | Same |
| GetContainerAsync | Containers.Read | Bounded aggregate, source shipment labels, contents and schedule-readiness status | Same |
| SaveContainerAsync | Containers.Create or Containers.Update | Persisted departed metadata changes additionally require Containers.CorrectDepartedMetadata; never writes receipt/schedule state | Same |
| DeleteContainerAsync | Containers.Delete | Persisted departure additionally requires Containers.DeleteDeparted; allocation history and database references still constrain deletion | Same |
| SaveContentsAsync | ContainerContents.Update | Departure lock applies even to Root; original received part/PO/quantity preserved | Same |
| GetBillsAsync / GetBillAsync | BillsOfLading.Read | Independent bill editor/list | Same |
| SaveBillAsync | BillsOfLading.Create or BillsOfLading.Update | Bills shared with departed containers additionally require BillsOfLading.CorrectDeparted; global uniqueness, supplier validity and serializable checks retained | Same |
| GetPartsAsync | ContainerContents.Update | Part/supplier/customer choices for contents editing | Same |
| GetContainerCreationShipmentAsync | Containers.Create | Only source shipment identity and bill labels; no general shipment-read grant | Same |
| GetContainerReferencesAsync | Containers.Read | Only referenced bills/parts for readers; selectable bills/parts for contents editors | Same |
| GetBillSupplierOptionsAsync | BillsOfLading.Create or BillsOfLading.Update | Active supplier ID/name choices and retained inactive supplier; no supplier editor authority | Same |
| ReceivedPartsService.ReceiveContainerAsync | Containers.Receive for initial receipt; Containers.CorrectReceipt for any existing date | Selected from locked persisted receipt; historical unstamped dates are corrections too; audit and quantity initialization retained | LogisticsAuthorizationTests; ReceivedPartsServiceTests |
| UnreceiveContainerAsync | Containers.Unreceive | Any allocation history continues to prevent unreceive, even after reversal | Same |
| CorrectActualReceivedQuantityAsync | Containers.CorrectReceivedQuantities | Allocated-quantity floor, reason and audit retained | Same |
| GetReceivedPartsAsync / GetCandidatesAsync | Receiving.Read | Bounded inspection statuses use internal purpose-bound helper without a second evaluation or Inspections.Read | Same |
| BeginInspectionAsync | Receiving.BeginInspection + Inspections.Create | Existing receipt-line-bound creation context, inspector eligibility and atomic allocation preserved | Same; InspectorAuthorizationTests |
| BumpUpAsync | Receiving.BumpQuantity (includes Inspections.Update prerequisites) | Matching PO/part/manufacturer lot, quantities and concurrency unchanged | Same |
| ReverseAllocationAsync | Receiving.ReverseAllocation | Reversal/deletion are bounded aggregate workflow, not generic inspection-write authority | Same |

`Today` properties are pure business-date accessors, not database operations. The obsolete TrackingAccess role-name helper and its registration were removed. There are no dedicated HTTP/background logistics adapters in Program.cs: interactive components invoke these trusted services directly. General SupplierService boundaries remain as migrated in 3D-1; dependent supplier choices do not call its unrestricted editor projection.

Stage A pages retain their existing layout and use advisory effective grants with independent create/update/delete, contents, receipt and exception controls. Denials refresh advisory state and retain unsaved models/dialog data. No AuthorizedOperation is stored in components.

Stage A checkpoint completed before Stage B: Release build passed with zero warnings/errors; 65 focused logistics, receiving and inspector tests passed (zero failed/skipped). The initial sandboxed test-host IPC attempt aborted before assertions; the completed run used normal process access and only the configured disposable confast_test database.

## Stage B boundary inventory

All methods below use the same trusted ApplicationAuthorization engine. Catalog prerequisites remain required, even when not repeated in the table. ProductionAuthorizationTests invokes every public method directly, including both Create/Update branches and the distinct progress/completion paths. Existing ProductionServiceTests, ProductionTrackingServiceTests, MorningProductionReviewServiceTests, ProductionSchedulerTests and metrics tests retain manufacturing calculations, histories and concurrency expectations.

| ProductionService public method | Required permission / persisted conditions |
|---|---|
| GetAsync | ProductionSchedules.Read; invokes fixed private catch-up described below |
| GetReadOnlyAsync | ProductionSchedules.Read; no catch-up |
| GetConfigurationAsync | At least one of SortingMachines.Read, ProductionCalendars.Read, ProductionHolidays.Read, ProductionDowntimeReasons.Read, ProductionSettings.Read; independently projects only permitted sections |
| GetPartConfigurationAsync | PartMachineEligibility.Read or dependent Parts.Read; only requested part's rates/preference, machine names/active choices, revision and effective-PPH efficiency input |
| AppendContainerPartAsync | ProductionSchedules.AppendContainerPart; catch-up and append share the one operation context |
| SaveJobAsync | ProductionJobs.Create for ID zero; otherwise ProductionJobs.Update and existing target required |
| DeleteJobAsync | ProductionJobs.Delete; container-source deletion restriction remains |
| SaveRequirementAsync | ProductionJobs.Update; requirements belong to the authorized job/part workflow |
| StartAsync | ProductionSchedules.StartWork; inspection acceptance, process, material, capacity and queue checks remain |
| ProgressAsync | ProductionSchedules.RecordProgress; also ProductionSchedules.CompleteWork when complete=true |
| CompleteSegmentAsync | ProductionSchedules.CompleteWork; explicit quantity reconciliation is aggregate completion work, without generic job CRUD grants |
| CorrectProgressAsync | ProductionSchedules.CorrectProgress; existing running/history constraints remain |
| SetConstraintAsync | ProductionSchedules.Arrange |
| MoveAsync | ProductionSchedules.Arrange |
| ReassignAsync | ProductionSchedules.Arrange; explicit running-allocation closure/remainder is part of this defined workflow |
| CutInAsync | ProductionSchedules.Arrange; intermediate segment insertion remains inside the aggregate transaction |
| ApplyOptimizationAsync | ProductionSchedules.Optimize; server recomputes preview and preserves machine assignments |
| SaveMachineAsync | SortingMachines.Create for ID zero, otherwise SortingMachines.Update; missing nonzero target rejected |
| SaveDefaultWorkingDaysAsync | ProductionCalendars.Update; propagating hours to following machines is bounded calendar workflow |
| SaveRateAsync | PartMachineEligibility.Create or Update, chosen from persisted machine/part eligibility under the planning lock |
| SetPreferredMachineAsync | PartMachineEligibility.Update; old/new preference changes are one transaction |
| RemoveRateAsync | PartMachineEligibility.Delete; replacement preference is part of existing removal workflow |
| SaveEfficiencyAsync | ProductionSettings.Update |
| SaveHolidayAsync | ProductionHolidays.Delete when removing; otherwise Create or Update based on persisted date |
| UpdateHolidayAsync | ProductionHolidays.Update; moving date does not require extra generic create/delete keys |
| SaveReasonAsync | ProductionDowntimeReasons.Create for ID zero, otherwise Update; missing nonzero target rejected |
| DeleteReasonAsync | ProductionDowntimeReasons.Delete; history references still prevent deletion |
| SaveDowntimeAsync | MachineDowntime.Delete when removing; otherwise Create for ID zero or Update for existing nonzero target; no missing-ID upsert |

| ProductionTrackingService public method | Required permission / dependent scope |
|---|---|
| GetDashboardAsync | ProductionLogs.Read; active machine, eligible part/lot, schedule labels and ordered downtime-cause choices are bounded operator inputs, without configuration or inspection editor access |
| GetDowntimeCausesAsync | SortLogDowntimeCauses.Read |
| SaveDowntimeCauseAsync | SortLogDowntimeCauses.Create for ID zero, otherwise Update; missing nonzero target rejected |
| DeleteDowntimeCauseAsync | SortLogDowntimeCauses.Delete; historical references still require deactivation instead |
| OpenOrCreateSortLogAsync | ProductionLogs.Create, including the explicit open-or-create command; existing log selection from Today's Logs uses dashboard Read only |
| SaveCommentsAsync | ProductionLogs.Update |
| StartRunAsync | ProductionLogs.StartRun |
| StopRunAsync | ProductionLogs.StopRun |
| ResumeRunAsync | ProductionLogs.ResumeRun |
| UpdateLineAsync | ProductionLogs.CorrectLine |
| SaveStopDetailsAsync (new) | ProductionLogs.StopRun; only an already stopped line with no recorded downtime cause; recorded details cannot be overwritten through this path, and start time, boundary samples and starting box count remain unchanged |

MorningProductionReviewService.GetAsync requires **ProductionReports.Read** before any schedule/report work. Its internal purpose-bound GetReportScheduleAsync reuses that snapshot, without requiring ProductionSchedules.Read, configuration administration or ProductionLogs.Read. It preserves the former no-catch-up reporting behavior. Date navigation, calculations, changeovers and metrics are unchanged.

The public-signature inventory covers 15 ContainerTrackingService, 8 ReceivedPartsService, 28 ProductionService, 11 ProductionTrackingService and 1 MorningProductionReviewService asynchronous entries (63 total). Pure date properties and private calculation helpers are excluded. Tests exercise 27 logistics branches and 49 production branches; branch counts differ from method counts. No in-scope HTTP API or hosted-worker adapter was found: Razor components call these service boundaries.

## Read-triggered maintenance and compatibility

The private MaintainScheduleOnReadAsync has a fixed purpose: clear existing legacy generated arrival constraints, capture missing running forecast baselines, append eligible departed-container source lines, and update the corresponding scheduling flag. It accepts no user-supplied mutation delegate, arbitrary actor or system bypass. It runs only after the enclosing read/append operation is authorized, under the existing settings lock and transaction, writes existing System audit attribution and increments the same revision only when needed. Its source key and fixed transitions preserve idempotency. Read authority does not confer public scheduling writes. No hosted-worker redesign was made.

Three existing missing-ID upserts (machines, planned downtime and planned downtime reasons) now reject vanished nonzero targets. This closes a Create-via-Update bypass and gives stale editors an explicit missing-record error. SaveRate and SaveHoliday select Create/Update from persisted state rather than caller intent. Ordinary receive authority no longer silently corrects an existing/historical receipt. These are intentional authorization tightenings; manufacturing formulas, history relationships and quantities are unchanged. Browser testing also found an existing schedule row-key collision between segment and downtime IDs; typed tuple keys now keep editor rerenders stable. The schedule Cancel button remains enabled after permission denial while protected editor fields disable.

Stop detail entry needed an explicit boundary: the former UI used the correction method immediately after Stop. SaveStopDetailsAsync preserves the same Stop → detail entry workflow using StopRun alone, while UpdateLineAsync requires CorrectLine. This adds no permission and no end-of-day finalization. End of Day remains an ordinary cause. Stop/resume and correction restrictions, good-parts PPH, failed quantity, target-based efficiency and interval downtime calculations are retained.

The shared PermissionComponentBase supplies cached advisory display grants. All in-scope routes have exact policies; the configuration route's OR policy uses existing read keys, not a new catalog permission. Controls use independent action grants, and denial handlers refresh display grants without discarding editor models or storing AuthorizedOperation. The old role-name TrackingAccess helper is gone. ProductionSnapshot's legacy CanEdit/IsAdministrator fields are no longer used to authorize or show production controls.

## Changed-file inventory

All paths below are relative to the repository root; pre-existing changes in these files were retained.

- Services: `src/Confast.Web/Features/ContainerTracking/ContainerTrackingService.cs`, `ReceivedPartsService.cs`, `TrackingModels.cs`; deleted obsolete `TrackingAccess.cs`; `Features/ProductionScheduling/ProductionService.cs`, `ProductionModels.cs`; `Features/ProductionTracking/ProductionTrackingService.cs`; `Features/MorningProductionReview/MorningProductionReviewService.cs`; narrow internal receiving helper in `Features/Inspections/InspectionService.cs`.
- Authorization/composition: `src/Confast.Web/Features/Authorization/ApplicationAuthorization.cs` (configuration OR policy/handler only); `Program.cs` (obsolete helper registration removed).
- UI: `src/Confast.Web/Components/Layout/MainLayout.razor`; `Components/Pages/ContainerTracking/{Index,Detail,BillEditor}.razor`; `ReceivedParts/Index.razor`; `ProductionScheduling/{Index,Settings}.razor`; `ProductionTracking/{Index,Settings}.razor`; `MorningProductionReview/Index.razor`; `Parts/PartProductionSettings.razor`.
- Tests: new `tests/Confast.Web.Tests/LogisticsAuthorizationTests.cs` and `ProductionAuthorizationTests.cs`; adapted existing `ContainerTrackingTests.cs`, `ReceivedPartsServiceTests.cs`, `ProductionServiceTests.cs`, `ProductionTrackingServiceTests.cs`, `MorningProductionReviewServiceTests.cs` to trusted sessions and explicit disposable Root provisioning.
- Documentation: this record plus current-status links in README, audit, design, foundation, administration and core-business coverage records.

No permission definitions, default grant lists, EF mappings, migrations, development accounts or production/development databases were changed. The pre-existing Pkcs and chat query-filter fixes remain intact.

## Verification

- Release solution build on the final production source: **zero warnings, zero errors**. The browser helper also built cleanly after its NuGet vulnerability audit was refreshed with network access; no warnings were suppressed.
- Stage A checkpoint: **65 passed**, including focused authorization and existing container/receiving/inspector regressions, before Stage B began.
- Existing production/scheduler/tracking/reporting/metrics regressions: **57 passed**. New production authorization matrix: **65 passed**, including all 49 public-operation branches, invalid actor matrix, one-snapshot composition, bounded query volume, fixed catch-up idempotency, optimizer assignments and exact good-parts PPH/efficiency.
- Final production-source full PostgreSQL run: **788 passed, zero failed/skipped**, **4m28s**. Log: `C:\Users\vsche\.codex\visualizations\2026\10\15\01a14010-a47f-7ac3-8a5c-a22f292bb427\full-suite-final.log`. An earlier full checkpoint also passed 788 (4m29s).
- One additional simultaneous-edit test was added afterward and passed separately: **1 passed**. Two independently authorized writes against one revision produce exactly one committed job edit/audit and one stale-revision rejection. The repository now contains 789 cases; the latest single full-suite invocation contained 788, followed by this additional focused case. No production code changed between these checks.
- EF Core `has-pending-model-changes`: **no changes since the last migration**. No migration was created or applied to development/production.
- `git diff --check`: passed. Existing unrelated chat, dependency, administration and core-business changes were retained; no permission catalog or default grant change was made.

The first sandboxed test-host IPC attempt aborted before assertions. Completed runs used normal process access, the configured disposable confast_test database and its suite advisory lock, one runner at a time. A first new production-matrix attempt exposed fixture-owned machine/cause reset errors; the fixture now resets its own production configuration and mutates its own downtime cause. Neither failure was addressed by weakening domain rules or grants.

Authenticated browser verification completed on the final source: **17 checkpoints passed, zero browser page errors and no unhandled circuit exceptions**. Both contexts authenticated with the configured Development BrowserTestUser credentials through the real login UI, on an isolated local server backed only by confast_test. Verified:

- Container list/detail read-only mode and disabled metadata fields; independent departed metadata and shared B/L exceptions, with contents still locked and supplier choices available without supplier editor access.
- Receiving-only access without Inspections.Read; Begin Inspection and eligible-inspector selection independently of Bump/correction authority.
- Initial Receive and CorrectReceipt as separate successful UI operations. Revocation between opening the receipt dialog and Confirm denied the write, retained the date, refreshed controls and left the database unchanged; Cancel and Search remained responsive.
- Schedule read-only forecasts; configuration read-only fields; the configuration OR policy with ProductionHolidays.Read alone; independent Arrange versus Optimize controls.
- Stale job Save denied after revocation, unsaved notes retained, original persisted notes unchanged, disabled protected fields, usable Cancel and Reload. This also retested the corrected segment/downtime row-key collision.
- Report-only Morning Review without schedule/log editor grants; Previous Day, Next Day and explicit historical date selection.
- Chromium iPad emulation at **834 × 1194 CSS pixels** (touch/mobile settings), plus desktop **1440 × 1000**. Stale StartRun was denied without creating a line, retained initials and disabled start inputs. StartRun, StopRun and ResumeRun produced independent visible controls and persisted transitions. StopRun alone saved pass/fail quantities and End of Day; finalized line editing then succeeded with CorrectLine alone. No physical iPad was used.

Proof screenshots, the browser harness and `browser-results.json` are in `C:\Users\vsche\.codex\visualizations\2026\10\15\01a14010-a47f-7ac3-8a5c-a22f292bb427`. The isolated server was stopped afterward. No production/development database, real employee account, real operator log or development Root was modified.

## Remaining phase boundaries

Phase 3E remains responsible for chat content/media/subscriptions/moderation history, delayed senders, broader delegated/background/print capability integration, and bootstrap/Development utility isolation. The fixed schedule-read catch-up is explicitly scoped here; it is not a general system-user authorization scheme. Phase 3F's complete permission matrix/live UI and Phase 3G's restore, production privilege, multi-instance and cutover rehearsals remain deferred. This slice does not claim global authorization cutover and does not begin Phase 3E.
