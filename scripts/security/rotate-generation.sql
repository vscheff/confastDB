-- Offline owner operation after restore. Explicit database, expected_epoch, reason and approved=yes required.
\set ON_ERROR_STOP on
BEGIN;
SELECT current_database() = :'database' AND :'approved' = 'yes'
    AND current_user = pg_get_userbyid(relowner) AS target_ok
    FROM pg_class WHERE oid = 'public.authorization_state'::regclass \gset
\if :target_ok
\else
    ROLLBACK;
    DO $$ BEGIN RAISE EXCEPTION 'Operator precondition failed; no changes committed'; END $$;
\endif
SELECT * FROM public.authorization_state WHERE id = 1 FOR UPDATE;
CREATE TEMP TABLE rotation_before ON COMMIT DROP AS SELECT * FROM public.authorization_state;
UPDATE public.authorization_state SET installation_generation = gen_random_uuid()
WHERE id = 1 AND global_epoch = :'expected_epoch'::bigint AND btrim(:'reason') <> '';
SELECT count(*) = 1 AND bool_and(a.installation_generation <> b.installation_generation) AS rotated
FROM public.authorization_state a CROSS JOIN rotation_before b \gset
\if :rotated
\else
    ROLLBACK;
    DO $$ BEGIN RAISE EXCEPTION 'Operator precondition failed; no changes committed'; END $$;
\endif
INSERT INTO public.authorization_change_history
    (operation_id, actor_kind, purpose, reason, occurred_at_utc, previous_epoch, resulting_epoch, installation_generation)
SELECT gen_random_uuid(), 1, 'Recovery.RotateGeneration', :'reason', clock_timestamp(), b.global_epoch, a.global_epoch, a.installation_generation
FROM public.authorization_state a CROSS JOIN rotation_before b;
SET CONSTRAINTS ALL IMMEDIATE;
COMMIT;
