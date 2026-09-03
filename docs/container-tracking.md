# Container Tracking

The workspace is at `/container-tracking`; Supplier master data is at `/suppliers`.
`AddContainerTracking` adds seven tables: suppliers, shipments, shipment bill numbers,
containers, bills of lading, container groups, and container group parts. Existing
Parts remain authoritative. There is no FileMaker import or synchronization in this slice.

## Workflow

1. Create the supplier (or select an existing active supplier).
2. Create a shipment with one or more individually stored bill numbers.
3. Add containers to that shipment and enter their dates and charges.
4. Add groups. Select an existing B/L, or create one inline with its supplier and duty.
5. Add Part/PO/quantity lines. Save the groups together. Each line keeps its own ID;
   repeated Parts and repeated Part/PO combinations are allowed.

The search includes shipment bills, container numbers, B/L numbers, supplier names,
and Part numbers. A match displays the entire shipment, including its other containers.
B/L duty and supplier edits affect every group referencing that B/L. Supplier names
are never copied into tracking records. Inactive suppliers remain visible on existing
B/L records but cannot be newly assigned.

## Decisions and boundaries

- Supplier follows Customer's numeric ID, active/inactive, and `xmin` pattern.
  No extra contact/address domain or hard-delete UI was added.
- Active authenticated users can read. Administrator, Quality, and Production can
  edit. The service checks current database roles and account status for every operation.
- Departure means the server's local calendar date is strictly greater than ETD.
  A missing ETD leaves the container editable; ETD day itself is editable.
- Groups, certifications status, B/L assignments, and part lines lock after departure.
  Normal users cannot change the container number, ETD, ETA, quoted rate, or drayage.
- Received date and production-schedule status remain updateable after departure.
  This is the interpretation of operational fields that must still work at arrival.
  Neither field triggers any other behavior.
- Administrators may correct container metadata, including ETD. Correcting ETD to
  today or later reopens normal content editing. Administrators do not directly bypass
  the content lock. Shared B/L edits require administrator access if any referencing
  container has departed.
- Monetary values are optional nonnegative decimals with two decimal places; unknown
  differs from zero. Weight is in pounds with up to three decimal places. Pallets and
  quantities are nonnegative whole numbers. PO is required. No additional date-order,
  container-number uniqueness, or Part/PO uniqueness rules are imposed.
- B/L numbers are trimmed and uppercased before checking uniqueness; PostgreSQL enforces
  canonical storage and global uniqueness. A concurrent duplicate returns the existing
  B/L identity so the editor can select it.
- Container saves use one `xmin` token for metadata and all groups/lines. Each content
  save advances that token. Shipment bill edits likewise advance the shipment token via
  `UpdatedAtUtc`. Serializable transactions protect shared-B/L/departure checks; conflicts
  return a reload message rather than overwriting another user's work.

## Migration and verification

Apply the reviewed migration through the normal deployment process:

```powershell
dotnet ef database update --project src/Confast.Web --startup-project src/Confast.Web
```

The implementation was built in Release and the migration applied to the configured
disposable PostgreSQL test database. The full test suite passed. Added tests cover
Supplier validation/deactivation, authorization, multiple shipment bills, normalized
and concurrent B/L uniqueness, shared B/Ls, repeated Parts, parent/child integrity,
negative values, stale saves, departure locks, receipt, and administrator correction.

Authenticated Chromium checks used the configured Development BrowserTestUser through
the real login UI against the disposable database. They exercised the creation workflow,
Part/PO rows, departure locking, receipt, and search at desktop and tablet viewport sizes.
This does not claim testing on a physical iPad or in Safari. The normal development
database was not migrated during verification.

## Deferred

Receiving inspector screens, inspections, inspection correlation, lot numbers, bump-ups,
inventory, and container-to-inspection relationships remain intentionally absent.
