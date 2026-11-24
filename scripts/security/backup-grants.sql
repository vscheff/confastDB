-- Run after migrations as the owner of the approved application database.
-- psql -X -v ON_ERROR_STOP=1 -v database=EXPLICIT_DATABASE -v backup_role=EXISTING_LOGIN -f backup-grants.sql
\set ON_ERROR_STOP on
SELECT current_database() = :'database'
    AND current_user = pg_get_userbyid(relowner) AS target_ok
    FROM pg_class WHERE oid = 'public.backup_settings'::regclass \gset
\if :target_ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'Refusing backup grants: database or owner does not match'; END $$;
\endif
BEGIN;
SELECT rolcanlogin AND NOT (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)
    AND NOT EXISTS (SELECT FROM pg_auth_members WHERE member = r.oid)
    AND NOT EXISTS (SELECT FROM pg_database WHERE datname = current_database() AND datdba = r.oid)
    AND NOT EXISTS (SELECT FROM pg_class c WHERE c.relowner = r.oid AND c.relnamespace = 'public'::regnamespace)
    AS restricted_login FROM pg_roles r WHERE rolname = :'backup_role' \gset
\if :restricted_login
    SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), :'backup_role') \gexec
    SELECT format('GRANT USAGE ON SCHEMA public TO %I', :'backup_role') \gexec
    SELECT format('GRANT SELECT ON ALL TABLES IN SCHEMA public TO %I', :'backup_role') \gexec
    SELECT format('GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO %I', :'backup_role') \gexec
    SELECT format('GRANT SELECT ON LARGE OBJECT %s TO %I', oid, :'backup_role')
        FROM pg_largeobject_metadata \gexec
\else
    DO $$ BEGIN RAISE EXCEPTION 'The backup login must be a dedicated restricted nonowner'; END $$;
\endif
COMMIT;
