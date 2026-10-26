# Phase 3D-1 Core Business Authorization

**Phase 3F status (October 16, 2026):** the catalog-backed role matrix, provenance and impact previews, safe role/user editing, process-local advisory refresh, and channel moderation-history UI are implemented. See [the administration UI record](authorization-administration-ui.md) for behavior, validation, and remaining Phase 3G limits. This supersedes historical matrix/refresh/history-UI deferrals below.

**Phase 3E-2 status (October 16, 2026):** scheduled delegation/lifecycle, scoped renderer execution, create-only bootstrap and guarded Development diagnostics are implemented. See [the background and delegation record](authorization-background-delegation.md) for the complete execution inventory, migration, validation and remaining Phase 3F/3G boundaries. This supersedes historical background/bootstrap deferrals below.

**Phase 3E-1 status (October 15, 2026):** chat permissions, media/subscription privacy and scoped moderation are implemented in [the chat authorization record](authorization-chat.md). Its boundary inventory and verification supersede older chat deferrals below; the full hosted-worker redesign remains Phase 3E-2.

**Phase 3D-2 status (October 15, 2026):** logistics, remaining receiving, production configuration/scheduling/tracking/reporting and fixed schedule-read maintenance now use trusted permission boundaries. See [the logistics and production implementation record](authorization-production-logistics.md) for the complete method inventory, verification and remaining Phase 3E boundaries. Historical deferrals below are superseded for this scope.

Implemented October 14–15, 2026. This record supersedes the Phase 1 audit's old authentication-only/name-based descriptions for the boundaries below. It does not declare global authorization cutover.

## Preserved foundation and role model

The existing 164-key catalog, prerequisites, seeds, evaluator, versioned cache and delegated-administration rules are unchanged. ReadOnly is an optional default role: removing it does not restore it on a later update, and no-role users have no implicit grants. Root derives recognized permissions through its protected designation, including without ReadOnly or Administrator membership; business validity still applies. The explicitly provisioned development Root, development database and production database were not modified. The prior Pkcs dependency and chat query-filter fixes were preserved. No new migration or permission key was created in this phase; the pre-existing optional-ReadOnly migration remains intact.

## Trusted service coverage

Every public entry below resolves the trusted actor and current session/account/effective grants. Authorization precedes business work; catalog prerequisites expand through the shared implementation. Save methods that support both creation and editing select the key using the target ID and still verify the persisted target/relationship. Customer creation's Main plant/default Inspection Sheet requirement, Part plant assignments, deletion cleanup, revision children and reverse flip-definition creation are bounded aggregate work within their explicitly authorized command.

| Service entry point (relative to Features/) | Permission / condition | Disposition and tests |
|---|---|---|
| `Customers/CustomerService.cs` · `CreateCustomerAsync` | `Customers.Create` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Customers/CustomerService.cs` · `GetCustomersAsync` | `Customers.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Customers/CustomerService.cs` · `GetCustomerAsync` | `Customers.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Customers/CustomerService.cs` · `SaveCustomerAsync` | `Customers.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Customers/CustomerService.cs` · `GetPlantsAsync` | `Plants.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Customers/CustomerService.cs` · `SavePlantAsync` | `Plants.Create` for a new plant, otherwise `Plants.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Customers/CustomerService.cs` · `DeletePlantAsync` | `Plants.Delete` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Customers/CustomerService.cs` · `GetPlantCertificationDeliveryAsync` | `PlantCertificationRecipients.Read and/or PlantCertificationSettings.Read, independently projected` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Customers/CustomerService.cs` · `SavePlantCertificationRecipientAsync` | `PlantCertificationRecipients.Create` for a new recipient, otherwise `PlantCertificationRecipients.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Customers/CustomerService.cs` · `DeletePlantCertificationRecipientAsync` | `PlantCertificationRecipients.Delete` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Customers/CustomerService.cs` · `SavePlantCertificationConfigurationAsync` | `PlantCertificationSettings.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Parts/PartService.cs` · `GetPartsAsync` | `Parts.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Parts/PartService.cs` · `GetPartsForCustomerAsync` | `Parts.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Parts/PartService.cs` · `GetCustomerOptionsAsync` | `Parts.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Parts/PartService.cs` · `GetPlantOptionsAsync` | `Parts.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Parts/PartService.cs` · `GetSupplierOptionsAsync` | `Parts.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Parts/PartService.cs` · `GetPartAsync` | `Parts.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Parts/PartService.cs` · `HasEligibleMachineAsync` | `Parts.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Parts/PartService.cs` · `GetPartForDeleteAsync` | `Parts.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Parts/PartService.cs` · `CreatePartAsync` | `Parts.Create` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Parts/PartService.cs` · `SavePartAsync` | `Parts.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Parts/PartService.cs` · `DeletePartAsync` | `Parts.Delete` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Suppliers/SupplierService.cs` · `SearchAsync` | `Suppliers.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Suppliers/SupplierService.cs` · `SaveAsync` | `Suppliers.Create` for a new supplier, otherwise `Suppliers.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Gages/GageService.cs` · `GetCriteriaMethodChoicesAsync` | `InspectionCriteria.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Gages/GageService.cs` · `GetGageMethodChoicesAsync` | `Gages.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Gages/GageService.cs` · `GetGageTypesAsync` | `GageTypes.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Gages/GageService.cs` · `GetGageTypeChoicesAsync` | `GageTypes.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Gages/GageService.cs` · `GetGagesAsync` | `Gages.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Gages/GageService.cs` · `SaveGageTypeAsync` | `GageTypes.Create` for a new type, otherwise `GageTypes.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Gages/GageService.cs` · `SaveGageAsync` | `Gages.Create` for a new gage, otherwise `Gages.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `GetUnitChoicesAsync` | `InspectionCriteria.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `GetSecondaryProcessTypeChoicesAsync` | `InspectionCriteria.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `GetCertificationTypeChoicesAsync` | `InspectionCriteria.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `GetPartSummaryAsync` | `InspectionCriteria.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `GetRevisionHistoryAsync` | `InspectionCriteria.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `GetRevisionAsync` | `InspectionCriteria.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `GetCriterionAsync` | `InspectionCriteria.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `CreateDraftRevisionAsync` | `InspectionCriteria.Create` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `PublishRevisionAsync` | `InspectionCriteria.Publish` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `DeleteRevisionAsync` | `InspectionCriteria.Delete` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `SaveRevisionHeaderAsync` | `InspectionCriteria.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `GetMasterPrintAsync` | `MasterPrints.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `UploadMasterPrintAsync` | `MasterPrints.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `DeleteMasterPrintAsync` | `MasterPrints.Delete` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `AddCriterionAsync` | `InspectionCriteria.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `SaveCriterionAsync` | `InspectionCriteria.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `DeleteCriterionAsync` | `InspectionCriteria.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `MoveCriterionAsync` | `InspectionCriteria.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `MoveCriterionBeforeAsync` | `InspectionCriteria.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `AlignCriterionDecimalPlacesAsync` | `InspectionCriteria.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `AddSecondaryProcessRequirementAsync` | `InspectionCriteria.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `SaveSecondaryProcessRequirementAsync` | `InspectionCriteria.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `DeleteSecondaryProcessRequirementAsync` | `InspectionCriteria.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `InspectionCriteria/InspectionCriteriaService.cs` · `SaveCertificationRequirementsAsync` | `InspectionCriteria.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Parts/PartFlipService.cs` · `GetConfigurationAsync` | `PartFlipDefinitions.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Parts/PartFlipService.cs` · `GetCurrentCriterionOptionsAsync` | `PartFlipDefinitions.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Parts/PartFlipService.cs` · `SaveDefinitionAsync` | `PartFlipDefinitions.Create` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Parts/PartFlipService.cs` · `ReplaceMappingsAsync` | `PartFlipDefinitions.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Parts/PartFlipService.cs` · `DeleteDefinitionAsync` | `PartFlipDefinitions.Delete` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/NominalToleranceSettings.cs` · `GetAsync` | `NominalToleranceSettings.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/NominalToleranceSettings.cs` · `SaveAsync` | `NominalToleranceSettings.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/CertificationEmailTemplates.cs` · `ResolveEmailTemplateAsync` | `Certifications.SendEmail` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/CertificationEmailTemplates.cs` · `GetAllAsync` | `CertificationEmailTemplates.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/CertificationEmailTemplates.cs` · `SaveAsync` | `CertificationEmailTemplates.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/CertificationEmailTemplates.cs` · `GetSettingsAsync` | `CertificationEmailSettings.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/CertificationEmailTemplates.cs` · `SaveSettingsAsync` | `CertificationEmailSettings.Update` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `GetInspectionsAsync` | `Inspections.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `GetInspectionsAsync` | `Inspections.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `FindInspectionsAsync` | `Inspections.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `GetPartOptionsAsync` | `Inspections.Create` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `GetCertificationPackageLotOptionsAsync` | `Certifications.BuildPackage` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `GetCertificationPackagePlantOptionsAsync` | `Certifications.BuildPackage` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `GetInspectionForDeleteAsync` | `Inspections.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `CreateInspectionAsync` | `Inspections.Create only; bounded creation references and eligibility` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `DuplicateInspectionAsync` | `Inspections.Duplicate` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `GetFlipPreviewAsync` | `Inspections.Flip` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `FlipInspectionAsync` | `Inspections.Flip` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `UndoLineageOperationAsync` | `Inspections.UndoLineage` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `MoveAdditionalLineageQuantityAsync` | `Inspections.TransferQuantity` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `GetInspectionAsync` | `Inspections.Read`; certification document metadata is included only with `Certifications.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `GetPrintableInspectionAsync` | `InspectionSheets.Export OR exact static delegated capability` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `UploadCertificationDocumentAsync` | `Certifications.Create` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `GetCertificationDocumentAsync` | `Certifications.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `GetCertificationDocumentsForPdfAsync` | `Certifications.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `GetCertificationDocumentPreviewAsync` | `Certifications.Read` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `DeleteCertificationDocumentAsync` | `Certifications.Delete` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `SaveInspectionAsync` | `Inspections.Update; ApproveDeviation on any persisted approval change; fresh inspector eligibility only on changed attribution` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/InspectionService.cs` · `DeleteInspectionAsync` | `Inspections.Delete` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/CertificationPackageService.cs` · `BuildAsync` | `Certifications.BuildPackage; TemporarilyCompletePackage when requested (no implicit InspectionSheets.Export)` | Migrated; direct-service denial matrix and relevant existing business regressions |
| `Inspections/CertificationEmailService.cs` · `SendAsync` | `Certifications.SendEmail; server-derived actor sender and internal implicit CC` | Migrated; direct-service denial matrix and relevant existing business regressions |

`CoreBusinessAuthorizationTests.EveryPublicBoundaryRejectsUntrustedOrUnauthorizedActors` invokes the actual public APIs with anonymous, inactive, no-role, stale-session, revoked, legacy-Administrator-claim and PendingRoot actors. `ResourceReadDoesNotPermitAnySupportedWrites`, `CreateDoesNotRequireOrGrantUpdate`, `UpdateDoesNotRequireOrGrantCreate` and `DeleteIsIndependentOfUpdate` cover supported action separation. The existing criteria/inspection/plant/gage/supplier tests retain historical, quantity, lot, lineage, concurrency and business-invalid Root assertions.

The final inventory review compared 91 public async method signatures in the covered service files with this table and direct-call test references; none was missing. That inventory check supplements the behavioral tests and source review; it is not evidence that later-phase services have been migrated.

| Boundary / regression | Specific test coverage beyond the common denial matrix |
|---|---|
| Customer, plant, part, supplier, gage/type and recipient action separation | `ResourceReadDoesNotPermitAnySupportedWrites`, `CreateDoesNotRequireOrGrantUpdate`, `UpdateDoesNotRequireOrGrantCreate`, `DeleteIsIndependentOfUpdate`; existing `PlantTests`, `PlantCertificationDeliveryTests`, `GageServiceTests`, `ContainerTrackingTests` |
| Independent recipient/settings reads | `SettingsAndRecipientReadsAreIndependent` |
| Criteria publishing, master prints and historical protection | `CriteriaUpdateDoesNotPublishOrManageMasterPrints_AndRootRetainsHistoryRestrictions`, `IndependentPublishAndMasterPrintAuthorityWorksWithoutCriteriaUpdate`; `InspectionCriteriaServiceTests` |
| Flip definition create/update/delete separation | `FlipDefinitionCreationMappingUpdatesAndDeletionRemainIndependent`; `FlipExecutionUsesBoundedMappingsWithoutGrantingTheConfigurationEditor` for execution without editor authority |
| Inspection actions, deviation changes and revocation | `SpecializedInspectionActionsDoNotConferOtherWorkflowAuthority`, `DeviationChangesAreDetectedFromPersistedState_AndRevocationIsFresh`, `TransferAndUndoNeedTheirAction_ButDoNotNeedGenericUpdateOrDelete`; `InspectionServiceTests`, `InspectorAuthorizationTests`, `ReceivedPartsServiceTests` |
| Certification documents, package/render and delivery | `BuildDoesNotSendEmailOrTemporarilyComplete_AndDependentReadersNeedNoEditorGrants`, `BuildingAnApprovedDocumentPackageEvaluatesOnce_AndRevocationStopsReexport`; `InspectionCertificationTests`, `InspectionPrintRenderTokenServiceTests`, `CertificationEmailAuthorizationTests`, `CertificationEmailTemplatePersistenceTests` |
| Direct HTTP paths and fixed renderer route | `AuthorizationEndpointTests`: valid file bytes, wrong child association, revoked/anonymous/forged-role denials, protected rendering options, no interactive chat on the static route |
| Optional ReadOnly and designated Root | `PrerequisitesAreChecked_AndOptionalReadOnlyIsNeverRestored`; existing `AuthorizationFoundationTests`, `DelegatedAdministrationTests`, `RoleGraphTests`, `RoleCheckboxesTests` and actual Root/Administrator endpoint cases |
| Bounded evaluations/queries and component parameters | `InspectionReadsAndFindsEvaluateOnce_RegardlessOfListSize`, `ListPermissionQueriesAreBoundedAsTheResultCountGrows`, package test above, `AdvisoryComponentsPreserveParametersBeforeAwaiting_AndDoNotQueryForEveryControl` |

## Internal composition, dependent values and universal reads

| Entry / projection | Disposition |
|---|---|
| Inspection creation references, nominal values and selected-inspector/caliper validation | Bounded creation operation; Create has no Read prerequisite. Inspector candidates still batch Active users/assignments and use effective Create. |
| Inspection save | One transaction/evaluation for Update, persisted deviation changes and changed inspector eligibility. Unchanged historical attribution remains valid even when the attributed user is inactive. Acting-user authorization is never optional. |
| Inspection duplication | Existing historical criteria/results/approval/certification copies remain bounded duplication aggregate work under Duplicate. This does not confer generic criteria, deviation, document-upload or settings-editor authority. |
| `GetPackageStatusesAsync` / status core | Internal to the exact BuildPackage frame and active-inspection scope; no second permission evaluation or per-lot guard. |
| `GetInspectionForExportAsync`, `GetDocumentsForExportAsync` | Internal to the exact Export frame/inspection. Archive attachment inclusion additionally requires Certifications.Read before rendering. |
| HTTP Master Print/document adapters | Internal to the matching frame/ID, never public requester-ID proof or an ambient circuit identity. |
| `GetCriteriaMethodChoicesAsync`, `GetGageMethodChoicesAsync` | Narrow method-choice projections authorized by InspectionCriteria.Read or Gages.Read respectively; no full GageTypes editor grant. |
| Part customer/plant/supplier/eligibility choices | Limited Part-workflow projections under Parts.Read; full neighboring editors retain their own keys. |
| `PartFlipService.CurrentCriteriaAsync` and mapping helpers | Internal configuration/flip business validation; inspection Flip does not grant the configuration editor. |
| Nominal settings consumed by inspection calculation | Private inspection core; no settings-editor authority conferred. |
| Single template resolution | SendEmail-authorized bounded template choice, distinct from GetAll/editor Read and Update. |
| Implicit CC consumed by delivery | Specific internal query in the SendEmail operation; no global settings-editor authority. |
| `CurrentEmailSender.GetAsync` | Universal/self-scoped display identity lookup; external delivery independently authorizes and derives the sender from the trusted actor, ignoring request-supplied sender identity. |
| SMTP transport interface/implementation | Internal infrastructure, no public unguarded external-delivery service. Certification delivery requires SendEmail. The existing fixed Development SMTP test is a separate legacy Development/admin utility pending its wider utility/antiforgery migration. |
| Token validation and pure formatters/evaluators/sanitizers | Universal pure validation/formatting; do not issue authority or read protected business records. |

## HTTP and delegated printing

`BusinessHttpBoundary` metadata opens one request-scoped frame after authentication. Standard Permission policies and the internal service adapter share that live frame with actor/stamp/purpose/scope validation. Independent requests, page loads and callbacks still start fresh decisions. Frames are disposed and removed at request completion; no caller can supply one through request binding.

| URL / delivery path | Required authority |
|---|---|
| `/parts/{partId}/inspection-criteria/{revisionId}/master-print` | MasterPrints.Read, exact Part/revision association |
| `/inspections/{inspectionId}/certifications/documents/{documentId}` and `/preview`, `/download` | Certifications.Read, exact inspection/document association |
| `/inspections/{inspectionId}/print/download` | InspectionSheets.Export |
| `/inspections/{inspectionId}/inspection-sheet/download` | InspectionSheets.Export and Certifications.Read for included attachments |
| `/inspections/{inspectionId}/print` | Human Export policy OR the exact two-minute static renderer capability |
| Package generation/download from inspection component | Guarded BuildAsync; a later download rebuilds/revalidates using the saved generation inputs, including temporary-completion authorization. There is no separate package-download HTTP route. |
| Certification email | Current SendEmail at trusted delivery; BuildPackage alone cannot send; SMTP tests use a capturing transport. |

Token issuance is internal to a validated Export or BuildPackage operation. The v2 protected payload binds inspection ID, expiration, temporary completion and extended-notes options. BuildPackage does not gain Export implicitly. Temporary completion alters only the returned rendering model, never persisted inspection/process state. The renderer has no human role grants, cannot use another document/admin/chat URL, and cannot open an interactive circuit through the static print route. Already-issued valid capabilities may complete within their two-minute lifetime under the existing in-flight contract; they are not globally revocable/one-use tokens. The v1 payload is intentionally invalid after this change, affecting only short-lived outstanding renders. Full worker/delegated authentication redesign remains Phase 3E.

HTTP tests exercise no-role/forged-role and anonymous denials, actual authorized document bytes, wrong inspection/document associations and grant revocation on the next request. Token tests cover purpose, scope, permissions, disposed contexts, tampering and protected options. Successful file responses use private/no-store caching and nosniff.

## UI and routing

Customers, Parts, Gages, Suppliers, criteria/revision and inspection views use their resource Read policies; create/delete/editor-only routes use the corresponding action. Nominal Tolerance, templates and flip configuration now use catalog policies instead of Administrator-name gates. Separate `/gage-types` and `/admin/certification-email-settings` routes preserve independent reader access. Navigation uses one advisory snapshot, refreshed on navigation; components use one advisory snapshot for controls, refresh after denial, and never store an AuthorizedOperation.

Read views keep controls disabled or unavailable according to the specific create/update/delete/publish/upload/lineage/export/send key. Master Print and criteria editing remain independent. Disabled form controls retain the existing layouts; read-only HTML template content uses sanitized display instead of an editable rich-text control. Optional neighboring sections load only when their Read key is present. Full parent-detail routes still require their parent resource Read, while service actions enforce their own exact keys. Saves remain authoritative after an earlier page/control decision, including ordinary permission revocation without forced logout or polling. Inspection autosave/create, package open/plant refresh, customer plant selection, criteria method selection and flip-target callbacks catch authorization denials and refresh their advisory controls instead of leaving stale editors or an unhandled circuit failure.

## Validation

Validation performed October 14–15, 2026. The original October 14 stopping point is retained in [the historical checkpoint handoff](authorization-core-business-handoff.md).

- Release solution build: successful, zero warnings and zero errors, including the final source changes.
- Focused authorization and relevant service regressions: successful runs of 159 and subsequently 101 tests on October 14; the October 15 core/endpoint/email authorization run passed all 83 tests, including the additional flip-definition action-separation regression. The advisory component regression passed with ordinary and cascading parameters. Tests cover current grants, prerequisite separation, persisted deviation changes, direct HTTP denial, fixed delegated rendering, and operation/query-count bounds.
- Full PostgreSQL regression suite: the final reviewed-source run passed **705 tests, zero failed/skipped**, in **3 minutes 47 seconds**, exit code 0. This includes all final callback-catch additions. Log: `C:\Users\vsche\AppData\Local\Temp\confast-phase3d1\full-reviewed-final.log`. The preceding October 15 run also passed 705 tests; the October 14 checkpoint passed 704.
- EF Core pending-model validation: no changes since the last migration. This slice creates no migration and applies none to development or production.
- Actual BrowserTestUser login and browser checks: permitted customer/part views; restricted customer, part, criteria, inspection, gage, template and nominal-tolerance controls; advisory navigation; gage sorting; and revocation between page load and Save. The stale customer Save was denied and the database retained its original name. The read-only inspection's received quantity was disabled and its export controls absent.
- Actual static print and PDF: the final static page renders without the chat launcher; an authorized extended-notes download produced a valid two-page PDF, 120,616 bytes, with the fixture part/lot and Certificate of Inspection. No chat disposal failure recurred. The earlier one-page PDF check also succeeded.
- Actual temporary package generation/download: a restricted fixture role granted the exact temporary-completion key and prerequisites, without Administrator or Root. The one-page package PDF (127,579 bytes) shows Heat Treat Complete and inspection Completed, while PostgreSQL retained `CompletedProcesses=0` of one process and the page remained incomplete. Revoking fixture grants after generation caused the existing Download PDF button to show the denial and disable further generation/download; cached bytes were not re-exported. Email PDF stayed disabled because no delivery recipients were configured; no real email was sent.
- Actual inspection autosave revocation: typed a changed lot into the already-loaded writable field, then triggered its normal change event after grants were revoked. The denial appeared, fields became disabled, and PostgreSQL retained `BROWSER-LOT`. The unsaved text was retained visibly for the user.
- Actual package-dialog revocation on the final rebuilt source: revoked grants after the package opener was available, then clicked it. A useful denial replaced the attempted dialog, controls became disabled, and the circuit remained responsive (the local Find dialog still opened). No unhandled component exception occurred.
- Actual denied print/document navigation: both direct URLs now show the plain-text authorization-denied message in the browser with the explicit no-store/nosniff denial response. This closes the October 14 `ERR_BLOCKED_BY_CLIENT` browser limitation. Automated HTTP tests separately verify the 403 status, file bytes/associations and revocation. Physical iPad interaction remains unverified.

Restricted test-host IPC initially timed out before assertions; subsequent tests use normal process access and only `CONFAST_TEST_CONNECTION_STRING` for disposable `confast_test`, with the existing single-runner advisory lock. Browser fixtures and the isolated server also used that disposable database only after test runners exited. The isolated server is stopped at completion. The development/production databases and provisioned developer Root were not changed. No real email was sent.

Browser proof files are in `C:\Users\vsche\.codex\visualizations\2026\10\14\01a13b9e-073f-71a1-b5d7-aacece4bc198`: `authorization-direct-print-denied.png`, `authorization-temporary-package.png`, and `authorization-stale-inspection-autosave.png`. These are local workstation artifacts, not repository assets.

## Remaining phase boundaries

| Audit areas not migrated here | Disposition / reason |
|---|---|
| Containers, shipments, bills of lading and TrackingAccess | Completed in [Phase 3D-2](authorization-production-logistics.md); obsolete TrackingAccess removed. SupplierService remains independently guarded. |
| Remaining receiving metadata/correction/unreceive operations | Completed in Phase 3D-2; receipt/correction/unreceive keys are independent. Phase 3C Begin/Bump/Reverse composition remains. |
| Scheduling, production settings/tracking/reports and read-triggered catch-up | Completed in Phase 3D-2; exact keys, bounded dependent projections and fixed read maintenance are recorded in its inventory. |
| Chat content/media/subscriptions/moderation and scheduled senders | Deferred to Phase 3E; current privacy rules and the pre-existing chat fixes are preserved. |
| Full delegated PDF/worker, bootstrap/Development utility migration | Deferred to Phase 3E; this phase secures business initiation, export and exact print completion only. |
| Full permissions-matrix UI, broader event-driven session polish | Deferred to Phase 3F; the current advisory refresh is bounded and does not poll. |
| Production privileges/cutover, offline recovery rehearsal, multi-instance deployment | Deferred to Phase 3G; local owner-backed tests do not prove a production privilege boundary. |

Phase 3D-2 subsequently completed logistics/receiving before production/configuration/tracking/reporting, preserving departure, allocation, audit and catch-up rules. Its implementation record identifies the remaining Phase 3E boundaries.

## File inventory

New production files: `Components/PermissionComponentBase.cs`, `Components/Pages/Gages/Types.razor`, `Components/Pages/Administration/CertificationEmailSettings.razor`, `Features/Authorization/BusinessHttpAuthorization.cs`.

Modified production files: the service files in the coverage table; `Features/Authorization/ApplicationAuthorization.cs`; `Features/Inspections/EmailService.cs`, `InspectionPrintRenderTokenService.cs`, `InspectionPrintRenderAuthenticationHandler.cs`; `Program.cs`; `Components/Layout/MainLayout.razor`; the existing Customers, Parts, Gages, Suppliers, InspectionCriteria, Inspections and relevant Administration Razor pages; `Parts/PartForm.razor`.

New tests: `CoreBusinessAuthorizationTests.cs`, `CertificationEmailAuthorizationTests.cs`, `PrintTestCapability.cs`. Modified tests: `AuthorizationTestSession`, `AuthorizationEndpointTests`, `AuthorizationFoundationTests`, `InspectorAuthorizationTests`, `InspectionPrintRenderTokenServiceTests`, `EmailServiceTests`, `CertificationEmailTemplatePersistenceTests`, `ContainerTrackingTests`, `ReceivedPartsServiceTests`, `InspectionServiceTests`, `InspectionCertificationTests`, `InspectionCriteriaServiceTests`, `GageServiceTests`, `PlantTests`, `PlantCertificationDeliveryTests`.

Documentation: this record, README, and status/optional-ReadOnly corrections in audit, design, foundation and administration records. Paths in the inventories are relative to `src/Confast.Web/`, `tests/Confast.Web.Tests/`, or the repository documentation directory as applicable. Pre-existing dirty files outside this slice remain in the worktree.
