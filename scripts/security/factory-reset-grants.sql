-- Run as the application schema owner after migrations.
-- psql -X -v ON_ERROR_STOP=1 -v database=EXPLICIT_DATABASE -v reset_role=DEDICATED_LOGIN -f factory-reset-grants.sql
\set ON_ERROR_STOP on
SELECT current_database() = :'database'
    AND current_user = pg_get_userbyid(relowner) AS target_ok
    FROM pg_class WHERE oid = 'confast_maintenance.factory_resets'::regclass \gset
\if :target_ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'Refusing Factory Reset grants: database or owner does not match'; END $$;
\endif
BEGIN;
SELECT rolcanlogin AND NOT (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)
    AND NOT EXISTS (SELECT FROM pg_auth_members WHERE member = r.oid)
    AND NOT EXISTS (SELECT FROM pg_database WHERE datname = current_database() AND datdba = r.oid)
    AND NOT EXISTS (SELECT FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname IN ('public', 'confast_maintenance') AND c.relowner = r.oid)
    AND NOT has_table_privilege(r.oid, 'public.identity_users', 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE')
    AS restricted_login FROM pg_roles r WHERE rolname = :'reset_role' \gset
\if :restricted_login
    REVOKE ALL ON SCHEMA confast_maintenance FROM PUBLIC;
    REVOKE ALL ON ALL TABLES IN SCHEMA confast_maintenance FROM PUBLIC;
    REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA confast_maintenance FROM PUBLIC;
    SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), :'reset_role') \gexec
    SELECT format('GRANT USAGE ON SCHEMA confast_maintenance TO %I', :'reset_role') \gexec
    SELECT format('GRANT SELECT ON confast_maintenance.factory_resets TO %I', :'reset_role') \gexec
    SELECT format('GRANT EXECUTE ON FUNCTION %s TO %I', signature, :'reset_role')
    FROM (VALUES
        ('confast_maintenance.request_reset(uuid,text,bytea,uuid,bigint,uuid,uuid)'),
        ('confast_maintenance.advance_reset(uuid,text,text,uuid)'),
        ('confast_maintenance.prepare_reset(uuid,text[],text[])'),
        ('confast_maintenance.execute_reset(uuid,text[],text[],jsonb,text,text)')
    ) f(signature) \gexec
\else
    DO $$ BEGIN RAISE EXCEPTION 'Factory Reset requires a separate, restricted nonowner login'; END $$;
\endif
COMMIT;
