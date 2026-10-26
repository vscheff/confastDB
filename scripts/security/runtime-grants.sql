-- psql -X -v ON_ERROR_STOP=1 -v database=EXPLICIT_DATABASE -v runtime_role=EXISTING_ROLE -f runtime-grants.sql
-- Run as the schema owner after migrations; never from normal application startup.
\set ON_ERROR_STOP on
SELECT current_database() = :'database'
    AND current_user = pg_get_userbyid(relowner) AS target_ok
    FROM pg_class WHERE oid = 'public.authorization_state'::regclass \gset
\if :target_ok
\else
    \echo 'Refusing grants: database or schema owner does not match.'
    DO $$ BEGIN RAISE EXCEPTION 'Operator precondition failed; no changes committed'; END $$;
\endif
BEGIN;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SELECT format('REVOKE CREATE ON DATABASE %I FROM %I', current_database(), :'runtime_role') \gexec
SELECT format('REVOKE ALL ON SCHEMA public FROM %I', :'runtime_role') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), :'runtime_role') \gexec
SELECT format('GRANT USAGE ON SCHEMA public TO %I', :'runtime_role') \gexec
-- Refuse pre-existing powerful roles; this script does not modify role attributes.
SELECT NOT (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)
    AND NOT pg_has_role(:'runtime_role', current_user, 'MEMBER')
    AND NOT EXISTS (SELECT FROM pg_auth_members WHERE member = r.oid)
    AND NOT EXISTS (SELECT FROM pg_database WHERE datname = current_database() AND datdba = r.oid)
    AND NOT EXISTS (SELECT FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relowner=r.oid)
    AND NOT EXISTS (SELECT FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public' AND p.proowner=r.oid)
    AS restricted_role FROM pg_roles r WHERE rolname = :'runtime_role' \gset
\if :restricted_role
\else
    \echo 'Runtime role must be an existing nonowner without operator membership or elevated attributes.'
    ROLLBACK;
    DO $$ BEGIN RAISE EXCEPTION 'Operator precondition failed; no changes committed'; END $$;
\endif
-- Explicit current object grants, reapplied and reviewed after each schema migration.
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM PUBLIC;
SELECT format('GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.%I TO %I', tablename, :'runtime_role')
FROM pg_tables WHERE schemaname = 'public' AND tablename NOT IN
    ('permissions', 'authorization_state', 'authorization_change_history', 'authorization_change_details',
     'chat_moderation_history', '__EFMigrationsHistory') \gexec
SELECT format('REVOKE ALL ON TABLE public.%I FROM %I', tablename, :'runtime_role')
FROM pg_tables WHERE schemaname = 'public' AND tablename IN
    ('permissions', 'authorization_state', 'authorization_change_history', 'authorization_change_details',
     'chat_moderation_history', '__EFMigrationsHistory') \gexec
SELECT format('GRANT SELECT ON public.permissions, public.authorization_state TO %I', :'runtime_role') \gexec
SELECT format('GRANT SELECT, INSERT ON public.authorization_change_history, public.authorization_change_details, public.chat_moderation_history TO %I', :'runtime_role') \gexec
SELECT format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO %I', :'runtime_role') \gexec
-- Trigger execution does not require runtime EXECUTE. No operator function is exposed.
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC;
SELECT format('REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA public FROM %I', :'runtime_role') \gexec
SELECT format('GRANT EXECUTE ON FUNCTION public.confast_lock_authorization() TO %I', :'runtime_role') \gexec
-- Do not default-grant future tables/functions: every migration needs privilege review.
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
COMMIT;
