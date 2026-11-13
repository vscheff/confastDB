# Container Tracking and Receiving

Open **Container Tracking** under **Receiving & Logistics**. The shipment list
requires `Shipments.Read`. Container details and container-level search also
require `Containers.Read`. Shipment, container, bill of lading (B/L), and
receiving actions each have their own permissions.

## Entering a shipment

1. Create a shipment with at least one shipment bill number and optional
   freight cost.
2. Add containers and enter their identifiers, estimated departure (ETD),
   estimated arrival (ETA), and any applicable charges.
3. Add groups to each container. Select an existing B/L or create one with
   an active supplier. Record group invoice, weight, pallets, and certification
   status as available. Weight is recorded in pounds.
4. Add part, purchase order (PO), and expected quantity lines. The part's
   supplier must match the group's B/L supplier. Each line has its own identity,
   so repeated part and PO combinations are allowed.

Search matches shipment bills and, with container access, container and CBP
numbers, B/Ls, suppliers, invoices, and part numbers. A match returns the
shipment with its containers. B/L numbers are trimmed, uppercased, and unique.
Editing a shared B/L changes it for all groups using it. Inactive suppliers
remain visible on existing B/Ls.

## Departure and receipt

A container is considered departed when its ETD is before the current business
date. ETD day itself remains editable. After departure, groups, B/L assignments,
certification status, and part lines are locked. Correcting departed metadata
requires `Containers.CorrectDepartedMetadata`. Moving ETD to today or later
reopens group editing, subject to the receipt restrictions below. Editing a
shared B/L used on any departed container requires `BillsOfLading.CorrectDeparted`.

Use **Receive container** to record receipt. This records the actor and date
and initializes actual quantities from expected quantities where actuals are
missing. The received date must be today or earlier. Corrections and historical
receipt reconciliation require a reason and the applicable receipt permission.
A received line preserves its part, PO, and expected quantity. Correct actuals
in **Received Parts** with a reason. Actual quantity must cover quantities
already allocated to inspections.

**Received Parts** requires `Receiving.Read`. From there, authorized users can
start an inspection for a received line, bump the received quantity on an
eligible existing inspection, or reverse an allocation. Receipt allocations
retain their source line and history. A container can be unreceived only
before any receipt allocations have been recorded.

Departed container lines can enter the [production schedule](production-scheduling.md#jobs-from-inbound-containers).
The schedule uses ETA for forecasts until a received date is recorded.

## Data integrity

Container metadata and contents share a concurrency version. A stale edit
requires a reload. Shipment bill edits similarly advance the shipment version.
The database enforces B/L uniqueness, and transactions protect shared B/L
changes and receipt allocations from concurrent edits.
