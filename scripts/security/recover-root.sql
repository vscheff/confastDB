-- OFFLINE ONLY: stop application writes, verify backup, approve intentional replacement identity.
-- Variables: database, approved=yes, expected_epoch, expected_root, account_id, excluded_browser_id, reason.
-- Credentials for the active replacement account must already be initialized and sign-in verified.
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
SELECT EXISTS (SELECT FROM public.authorization_state WHERE id = 1 AND global_epoch = :'expected_epoch'::bigint
    AND coalesce(root_user_id, '') = :'expected_root')
    AND btrim(:'reason') <> '' AND :'account_id' <> :'excluded_browser_id'
    AND EXISTS (SELECT FROM public.identity_users WHERE id = :'account_id' AND is_active
        AND password_hash IS NOT NULL AND password_hash <> '' AND security_stamp IS NOT NULL AND security_stamp <> '') AS recovery_ok \gset
\if :recovery_ok
\else
    ROLLBACK;
    DO $$ BEGIN RAISE EXCEPTION 'Operator precondition failed; no changes committed'; END $$;
\endif
CREATE TEMP TABLE recovery_before ON COMMIT DROP AS SELECT * FROM public.authorization_state;
-- Only the owner can suspend this one guard. Deferred consistency guards remain enabled.
ALTER TABLE public.authorization_state DISABLE TRIGGER auth_guard;
UPDATE public.authorization_state SET root_user_id = :'account_id', readiness = 1,
    installation_generation = gen_random_uuid(), global_epoch = global_epoch + 1 WHERE id = 1;
DELETE FROM public.identity_user_roles WHERE role_id = 'e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6';
INSERT INTO public.identity_user_roles (user_id, role_id) VALUES (:'account_id', 'e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6');
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE public.authorization_state ENABLE TRIGGER auth_guard;
WITH recorded AS (
    INSERT INTO public.authorization_change_history
        (operation_id, actor_kind, purpose, reason, occurred_at_utc, previous_epoch, resulting_epoch, installation_generation)
    SELECT gen_random_uuid(), 1, 'Recovery.ReplaceRoot', :'reason', clock_timestamp(), b.global_epoch, a.global_epoch, a.installation_generation
    FROM public.authorization_state a CROSS JOIN recovery_before b RETURNING id
)
INSERT INTO public.authorization_change_details (history_id, kind, user_id, role_id, was_present, is_present,
    field_name, previous_value, resulting_value)
SELECT r.id, 4, :'account_id', 'e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6', false, true,
    'RootUserId', b.root_user_id, :'account_id' FROM recorded r CROSS JOIN recovery_before b;
SET CONSTRAINTS ALL IMMEDIATE;
COMMIT;
