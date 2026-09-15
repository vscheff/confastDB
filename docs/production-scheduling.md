# Production Scheduling

Open **Production Scheduling** in the main navigation. Active authenticated users can read the schedule; Production and Administrator users can plan work. Scheduling settings require Administrator access. The service checks current database roles and account status for every operation.

## Deployment and configuration

Apply the checked-in `AddProductionScheduling` and `AddProductionSchedulingIntegrity` migrations with the normal EF deployment process. They add scheduling tables and a nullable `parts.box_quantity`; existing parts remain intact. The only seeded configuration is application-wide efficiency at 91%. The integrity migration adds deferred allocation/requirement checks and production-history protection triggers.

Configure machines, including a machine named for manual sorting, and their actual weekday hours. No machine names, rates, or workweeks are assumed. Add eligible parts with target PPH; optionally provide the Part's box quantity. Configure global holidays and selectable downtime reasons. Missing rates, inactive machines, missing working capacity, and missing box quantities are shown explicitly. Eligibility cannot be removed while that part has unfinished work on the machine.

These records are authoritative in ConFastDB. When the schedule is opened, each container whose Estimated Departure has passed is picked up once: every positive-quantity container line with an active preferred machine becomes a source-linked job at the end of that machine's queue. Its PO is carried forward and its Estimated Arrival becomes a hard earliest-start constraint. Lines without an active preferred machine stay unscheduled without blocking eligible sibling lines; Container Tracking marks the container as missing eligibility, highlights the affected line, and offers a retry after eligibility is configured. Those jobs retain their ordinary segment status; their pending blocker reads **Material En Route** until the source container is received. The automatic job is retained with its container-line source rather than being deletable as an unrelated planner job.

## Planner workflow

1. Select a machine and add a job with part, PO/MO references, total quantity, and notes. The initial allocation is one pending segment. An unsplit pending job's part and quantity can be edited; references and notes remain editable afterward.
2. Optionally enter its next shortage date and additional pieces needed. A blank quantity fixes the cumulative requirement at the total job quantity. The date is inclusive: finishing on the shortage date is on time.
3. Use Earlier/Later to change pending order. Start/pin sets a visible, removable **start no earlier than** constraint. A pin fixes the queue position during optimization, while projected dates can still change.
4. Start the front pending segment explicitly. Record cumulative completed pieces for that segment through the end of the selected date. Corrections replace the current cumulative amount and append a checkpoint; they are never added as new output. Start and completion cannot be inferred from projected dates.
5. Complete a segment by explicitly recording its full allocation. Other segments of the same job remain pending or running. Job completion follows the quantities across all segments.
6. To interrupt work, create a quantity cut-in, specifying cumulative completion for the original job and another pending segment on the same machine. The scheduler creates stop → interruption → resume dependencies. It never marks the stop segment complete or starts the interruption automatically.
7. Reassignment is explicit and validates the destination. Running reassignment requires a checkpoint from today or yesterday, closes the performed allocation on the original machine, and creates a pending remainder on the destination. Unfinished cut-in dependencies must finish first.
8. Optimize Schedule previews order, dates, and requirement risks before application. A changed scheduling revision or planning date invalidates the preview.

## Calculation conventions

- Effective PPH = target PPH × efficiency / 100. Effective PPH is used once for all duration, remainder, requirement, and optimization calculations. Calendar hours are not reduced again.
- The scheduler allocates decimal hours, retaining unused daily capacity between pending segments. No job is internally rounded to whole days.
- Machine-specific weekdays provide capacity. Global holidays and inclusive full-day downtime remove it; overlapping downtime removes a day only once.
- Equivalent days = hours / the machine's average positive configured weekday hours. This is a display measure, not a substitute for calendar allocation. Elapsed working days count actual calendar working dates through yesterday, or through completion for completed segments. Completed elapsed days are frozen on completion.
- Checkpoints represent cumulative segment production through **end of day**. Remaining work starts no earlier than the next day and the current server-local planning date. A completion checkpoint closes that machine's recorded day for forecasting. Two planned four-hour jobs fit in one eight-hour day; once an end-of-day checkpoint is recorded, forecasts do not claim unused capacity on that date. Explicit actual starts/completions may still share a date. Intraday actual time entry is outside this date-based slice.
- Forecasts never assume unreported production. Stale running checkpoints are marked. An unfinished segment with all pieces reported still requires explicit completion.
- Original duration, target PPH, and efficiency estimates remain immutable. Current forecasts use current settings. Progress history records actor, recorded time, prior and replacement cumulative quantity, checkpoint date, and notes.
- Job boxes = ceiling(job total / Part box quantity). Segment box rounding is not summed. Missing box quantity means the box estimate is unavailable.
- Requirements store stable cumulative targets. Additional requirements accumulate in date order, using the greater of current completed quantity and earlier cumulative targets as the baseline. The simple editor maintains the next unmet requirement. Already satisfied requirements retain history; future cumulative targets are not silently rewritten when another requirement is edited. The model/service supports multiple dated requirements without requiring complete shipment forecasts.
- Requirement forecasts combine production across a job's machine segments and compare when its cumulative target is reached, not when the entire job ends. Remaining balance with no unmet requirement is labeled **No further requirement known** and stays scheduled.
- The forecast horizon is ten years. Impossible capacity and constraints beyond that horizon produce an explanation rather than an invented finish date or an unbounded search.

## Optimization and integrity

The deterministic heuristic ranks movable pending work by latest safe start for its next unmet requirement, accounting for effective rate, weekday capacity, holidays, and downtime. Equal priorities retain queue order; work without a known deadline stays in stable order behind dated work in its movable range. Running work, pins, and explicit cut-in chains form barriers. The preview applies all start constraints and consumes each segment's **full remaining quantity**, even when its earliest requirement needs only part of it. Misses remain visible for planner decisions; the heuristic is not a globally optimal scheduling guarantee.

The service uses a transaction, locks the singleton settings row, and checks a shared scheduling revision. This intentionally serializes planner writes across machines so global calendar/rate changes and optimization previews are consistent. The settings row also uses PostgreSQL `xmin`. Read snapshots use repeatable-read isolation. PostgreSQL checks protect positive rates, valid hours/dates, over-completion, one running segment per machine, job allocation totals, requirement bounds, immutable original/completed history, and append-only audit/checkpoint records.

Completed checkpoint corrections, date-based cut-ins, automatic balancing, rate learning, MISys integration, and automatic production-sheet progress are not implemented. The application currently reads the complete scheduling dataset for planning; archive/paging work can be added when production volume warrants it.

