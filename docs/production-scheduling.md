# Production Scheduling

Open **Production Scheduling** under **Production**. The schedule requires
`ProductionSchedules.Read`. Job edits, queue arrangement, start, progress,
completion, and optimization each require their own permissions. **Scheduling
Settings** shows the sections the user has permission to read and enables edits
with the corresponding update or management permissions.

## Configuration

Configure sorting machines, including manual sorting where applicable, and set
their weekday hours. A machine can follow the default workweek or use its own
hours. New databases start with an eight-hour Monday–Friday default calendar
and 91% efficiency. Configure holidays and downtime reasons, then add eligible
parts with target pieces per hour (PPH). Set a preferred machine for inbound
container work. An optional part box quantity enables job box estimates.

## Planner workflow

1. Select a machine and add a job with part, quantity, PO/MO references, and
   notes. It starts with one pending segment.
2. Optionally enter a shortage date and additional pieces needed. Requirements
   are cumulative per part across its jobs. A blank quantity uses the part's
   total scheduled quantity. The due date is inclusive.
3. Move pending work earlier or later in the queue. Use **Pin** to set a
   start-no-earlier-than date, fix the queue position during optimization, or
   both. A queue pin still allows an explicit move and projected dates can change.
4. Start the front pending segment explicitly. Start requires a matching
   accepted inspection by part number and PO, completed non-sort secondary
   processes on an accepted match, and a completed cut-in predecessor if one
   exists. The service also checks the earliest-start date, machine capacity,
   and whether the machine already has running work.
5. Record additional pieces through the selected day. **Correct Total** replaces
   the cumulative completed quantity and appends an audited checkpoint.
   Complete the segment explicitly. If the final count differs from its
   allocation, confirm the change to the next segment or the job total.
6. Use **Cut-In** to define a cumulative job quantity at which to stop and
   insert another pending segment on the same machine. The queue preserves
   stop → cut-in → resume order. Reassigning running work requires a checkpoint
   from today or yesterday. It closes the performed allocation on its original
   machine and adds a pending remainder to the destination.
7. **Optimize Schedule** previews queue order, forecast dates, and requirement
   risks. A change to the schedule revision or planning date invalidates the
   preview.

## Jobs from inbound containers

Opening the schedule adds eligible lines from containers with an ETD before
today and an ETA. A positive-quantity line for an active part with an active
preferred machine becomes one source-linked job at the end of that machine's
queue. The job carries the line's expected quantity and PO. Ineligible lines
stay unscheduled without blocking eligible lines in the same container. After
fixing eligibility, use the retry action in **Container Tracking** if available.

The earliest forecast date for container work is its ETA until receipt, then
its actual received date. Pending jobs show **Material En Route** until receipt.
The job remains linked to its source line and cannot be deleted from the schedule.

## Forecasts and concurrency

- Effective PPH is target PPH multiplied by efficiency. The scheduler consumes
  decimal hours, carries unused daily capacity forward to the next segment,
  and excludes holidays and full-day downtime. Equivalent days use the
  machine's average positive workday hours and are a display measure. A job's
  box estimate rounds up the job quantity divided by the part's box quantity.
- A progress checkpoint covers work through the end of its date. Remaining
  work forecasts from the following day or the current planning date,
  whichever is later. Completed segments preserve their original estimates,
  checkpoints, and elapsed working days.
- Requirement forecasts combine work for the same part across jobs and
  machines. They compare when each cumulative target is reached against its
  due date. Satisfied targets retain their history.
- Missing rates or capacity and work beyond the ten-year forecast horizon
  produce a forecast error.
- Optimization prioritizes movable work by latest safe start for the next
  unmet requirement. Running work, pins, and cut-in chains constrain the
  reorder. The preview budgets each segment's full remaining quantity and
  shows misses for the planner to review.
- Schedule writes serialize through the shared settings row and use the
  schedule revision to reject stale changes. PostgreSQL constraints protect
  quantities, one running segment per machine, and completed history.
