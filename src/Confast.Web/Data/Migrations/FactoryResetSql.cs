namespace Confast.Web.Data.Migrations;

internal static class FactoryResetSql
{
    internal const string Install = """
        CREATE SCHEMA confast_maintenance;
        REVOKE ALL ON SCHEMA confast_maintenance FROM PUBLIC;
        CREATE TABLE confast_maintenance.factory_resets (
            id uuid PRIMARY KEY,
            actor_id text NOT NULL,
            actor_name text NOT NULL,
            generation uuid NOT NULL,
            authorization_epoch bigint NOT NULL,
            deployment_id uuid NOT NULL,
            backup_id uuid NOT NULL UNIQUE,
            storage_id uuid,
            requested_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            phase text NOT NULL CHECK (phase IN ('Draining', 'BackingUp', 'Resetting', 'CleaningStorage',
                'Verifying', 'Finalizing', 'Completed', 'Aborted')),
            finished_at timestamptz,
            CHECK ((finished_at IS NOT NULL) = (phase IN ('Completed', 'Aborted')))
        );
        CREATE UNIQUE INDEX one_active_reset ON confast_maintenance.factory_resets ((1)) WHERE finished_at IS NULL;

        CREATE VIEW confast_maintenance.activity AS
            SELECT s.installation_generation AS generation, r.phase,
                (SELECT phase FROM confast_maintenance.factory_resets WHERE finished_at IS NOT NULL
                    ORDER BY requested_at DESC LIMIT 1) AS last_outcome
            FROM public.authorization_state s
            LEFT JOIN confast_maintenance.factory_resets r ON r.finished_at IS NULL
            WHERE s.id = 1;

        CREATE FUNCTION confast_maintenance.guard_migrations() RETURNS trigger
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
        BEGIN
            IF EXISTS (SELECT FROM confast_maintenance.factory_resets WHERE finished_at IS NULL) THEN
                RAISE EXCEPTION 'Migrations require Factory Reset to finish' USING ERRCODE = '55000';
            END IF;
            RETURN NULL;
        END $$;
        CREATE TRIGGER confast_reset_migration_guard BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
            ON public."__EFMigrationsHistory" FOR EACH STATEMENT EXECUTE FUNCTION confast_maintenance.guard_migrations();

        CREATE FUNCTION confast_maintenance.request_reset(p_id uuid, p_actor text, p_proof bytea,
            p_generation uuid, p_epoch bigint, p_deployment uuid, p_backup uuid) RETURNS void
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
        DECLARE actor_name text;
        BEGIN
            IF NOT pg_try_advisory_xact_lock_shared(1703373, 841538) THEN
                RAISE EXCEPTION 'Maintenance is already active' USING ERRCODE = '55P03';
            END IF;
            SELECT u.user_name INTO STRICT actor_name
              FROM public.authorization_state s JOIN public.identity_users u ON u.id = s.root_user_id
             WHERE s.id = 1 AND s.readiness = 1 AND s.installation_generation = p_generation
               AND s.global_epoch = p_epoch AND u.id = p_actor AND u.is_active
               AND sha256(convert_to(u.security_stamp || p_id::text, 'UTF8')) = p_proof
               AND u.password_hash <> '' AND EXISTS (SELECT FROM public.identity_user_roles m
                   WHERE m.user_id = u.id AND m.role_id = s.root_role_id);
            IF NOT EXISTS (SELECT FROM public.backup_settings b WHERE b.id = 1 AND b.deployment_id = p_deployment
                    AND b.destination_id IS NOT NULL AND b.destination IS NOT NULL) THEN
                RAISE EXCEPTION 'Backup destination is not configured' USING ERRCODE = '55000';
            END IF;
            INSERT INTO confast_maintenance.factory_resets
                (id, actor_id, actor_name, generation, authorization_epoch, deployment_id, backup_id, phase)
            VALUES (p_id, p_actor, actor_name, p_generation, p_epoch, p_deployment, p_backup, 'Draining');
        END $$;

        CREATE FUNCTION confast_maintenance.require_exclusive() RETURNS void
        LANGUAGE plpgsql SET search_path = pg_catalog AS $$
        BEGIN
            IF NOT EXISTS (SELECT FROM pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid()
                    AND classid = 1703373 AND objid = 841538 AND objsubid = 2 AND granted AND mode = 'ExclusiveLock') THEN
                RAISE EXCEPTION 'Factory Reset requires the exclusive application lease' USING ERRCODE = '55000';
            END IF;
        END $$;

        CREATE FUNCTION confast_maintenance.advance_reset(p_id uuid, p_from text, p_to text, p_storage uuid) RETURNS void
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
        BEGIN
            -- The backup transaction holds the exclusive lease and schema locks when a
            -- second connection publishes Resetting. This transition cannot commit a reset.
            IF (p_from, p_to) <> ('BackingUp', 'Resetting') THEN
                PERFORM confast_maintenance.require_exclusive();
            END IF;
            IF NOT ((p_from = 'Draining' AND p_to = 'BackingUp' AND p_storage IS NOT NULL) OR
                (p_from = 'BackingUp' AND p_to = 'Resetting') OR
                (p_from = 'CleaningStorage' AND p_to = 'Verifying') OR
                (p_from = 'Verifying' AND p_to = 'Finalizing') OR
                (p_from = 'Finalizing' AND p_to = 'Completed') OR
                (p_from IN ('Draining', 'BackingUp', 'Resetting') AND p_to = 'Aborted')) THEN
                RAISE EXCEPTION 'Invalid Factory Reset phase' USING ERRCODE = '55000';
            END IF;
            UPDATE confast_maintenance.factory_resets
               SET phase = p_to, storage_id = coalesce(storage_id, p_storage),
                   finished_at = CASE WHEN p_to IN ('Completed', 'Aborted') THEN clock_timestamp() END
             WHERE id = p_id AND phase = p_from AND finished_at IS NULL;
            IF NOT FOUND THEN RAISE EXCEPTION 'Factory Reset phase changed' USING ERRCODE = '55000'; END IF;
        END $$;

        CREATE FUNCTION confast_maintenance.check_scope(p_tables text[], p_migrations text[]) RETURNS void
        LANGUAGE plpgsql SET search_path = pg_catalog AS $$
        DECLARE
            names text[];
            owner_id oid := (SELECT relowner FROM pg_class WHERE oid = 'public.authorization_state'::regclass);
        BEGIN
            IF (SELECT array_agg("MigrationId"::text ORDER BY "MigrationId" COLLATE "C") FROM public."__EFMigrationsHistory")
                IS DISTINCT FROM p_migrations THEN
                RAISE EXCEPTION 'Migration history differs from the Factory Reset model' USING ERRCODE = '55000';
            END IF;
            SELECT array_agg(c.relname::text ORDER BY c.relname COLLATE "C") INTO names FROM pg_class c
             WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p', 'f', 'm');
            IF names IS DISTINCT FROM p_tables OR EXISTS (
                SELECT FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relname = ANY(p_tables)
                    AND (c.relowner <> owner_id OR c.relkind <> 'r' OR c.relispartition OR c.relrowsecurity
                        OR EXISTS (SELECT FROM pg_inherits WHERE c.oid IN (inhparent, inhrelid))
                        OR EXISTS (SELECT FROM pg_depend d WHERE d.classid = 'pg_class'::regclass
                            AND d.objid = c.oid AND d.deptype = 'e'))
                ) OR EXISTS (
                SELECT FROM pg_constraint f JOIN pg_class target ON target.oid = f.confrelid
                    JOIN pg_class source ON source.oid = f.conrelid
                 WHERE f.contype = 'f' AND target.relnamespace = 'public'::regnamespace AND target.relname = ANY(p_tables)
                    AND source.relnamespace <> 'public'::regnamespace
                ) OR EXISTS (SELECT FROM pg_largeobject_metadata) THEN
                RAISE EXCEPTION 'Unexpected tables, dependencies, or storage in the application schema' USING ERRCODE = '55000';
            END IF;
        END $$;

        CREATE FUNCTION confast_maintenance.prepare_reset(p_id uuid, p_tables text[], p_migrations text[]) RETURNS void
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
        DECLARE name text;
        BEGIN
            PERFORM confast_maintenance.require_exclusive();
            IF NOT EXISTS (SELECT FROM confast_maintenance.factory_resets r JOIN public.authorization_state s ON s.id = 1
                    WHERE r.id = p_id AND r.phase = 'BackingUp' AND s.readiness = 1 AND s.root_user_id = r.actor_id
                        AND r.requested_at >= clock_timestamp() - interval '5 minutes'
                        AND s.installation_generation = r.generation AND s.global_epoch = r.authorization_epoch) THEN
                RAISE EXCEPTION 'Factory Reset authorization changed while draining' USING ERRCODE = '55000';
            END IF;
            LOCK TABLE public."__EFMigrationsHistory" IN ACCESS SHARE MODE;
            PERFORM confast_maintenance.check_scope(p_tables, p_migrations);
            FOREACH name IN ARRAY p_tables LOOP
                -- The only writers while the gate is closed are the reset and its backup
                -- publisher. A SHARE lock still allows pg_dump's consistent snapshot.
                IF name NOT IN ('__EFMigrationsHistory', 'backup_executions', 'backup_audit') THEN
                    EXECUTE format('LOCK TABLE public.%I IN SHARE MODE', name);
                END IF;
            END LOOP;
        END $$;

        CREATE FUNCTION confast_maintenance.execute_reset(p_id uuid, p_tables text[], p_migrations text[],
            p_seeds jsonb, p_bootstrap_role text, p_catalog text) RETURNS void
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
        DECLARE
            r confast_maintenance.factory_resets%ROWTYPE;
            s public.authorization_state%ROWTYPE;
            name text;
            columns text;
            replacements text;
            count_expected bigint;
            count_actual bigint;
            row_missing boolean;
            seed jsonb;
            guard text[];
            sequence_row record;
            next_value numeric;
            -- These tables either survive or need row-level cleanup because a surviving
            -- table references them. All other EF application tables truncate by default.
            kept constant text[] := ARRAY['__EFMigrationsHistory', 'authorization_state', 'permissions',
                'identity_users', 'identity_roles', 'identity_user_logins', 'identity_user_tokens',
                'gages', 'gage_types', 'backup_settings', 'backup_notification_recipients', 'backup_executions'];
            mutable_defaults constant text[] := ARRAY['identity_roles', 'gages', 'gage_types'];
            truncate_guards constant text[][] := ARRAY[
                ['identity_user_roles', 'auth_no_truncate', 'confast_reject_immutable'],
                ['role_permissions', 'auth_no_truncate', 'confast_reject_immutable'],
                ['role_inheritance', 'auth_no_truncate', 'confast_reject_immutable'],
                ['authorization_change_details', 'auth_no_truncate', 'confast_reject_immutable'],
                ['authorization_change_history', 'auth_no_truncate', 'confast_reject_immutable'],
                ['password_reset_delegations', 'auth_no_truncate', 'confast_reject_immutable'],
                ['chat_moderation_history', 'chat_moderation_no_truncate', 'protect_chat_moderation_history'],
                ['backup_audit', 'backup_audit_no_truncate', 'confast_protect_backup_audit']
            ];
            truncate_list text;
        BEGIN
            PERFORM confast_maintenance.require_exclusive();
            PERFORM confast_maintenance.check_scope(p_tables, p_migrations);
            SELECT * INTO STRICT r FROM confast_maintenance.factory_resets WHERE id = p_id AND phase = 'Resetting' FOR UPDATE;
            SELECT * INTO STRICT s FROM public.authorization_state WHERE id = 1 FOR UPDATE;
            IF s.readiness <> 1 OR s.root_user_id <> r.actor_id OR s.installation_generation <> r.generation
                OR s.global_epoch <> r.authorization_epoch OR s.catalog_version <> p_catalog
                OR NOT EXISTS (SELECT FROM public.identity_users WHERE id = r.actor_id AND is_active AND password_hash <> '')
                OR NOT EXISTS (SELECT FROM public.backup_executions b WHERE b.id = r.backup_id AND b.pinned AND b.verification = 2
                    AND b.outcome = 2 AND b.availability = 1 AND b.stage = 7 AND b.deployment_id = r.deployment_id
                    AND b.requested_by = r.actor_id AND b.requested_generation = r.generation AND b.label = 'Pre-reset backup') THEN
                RAISE EXCEPTION 'Factory Reset requires current Root approval and a registered verified pinned backup' USING ERRCODE = '55000';
            END IF;

            -- Suspend just the explicit immutability guards for data slated for
            -- truncation. FK and row triggers remain enabled throughout.
            FOREACH guard SLICE 1 IN ARRAY truncate_guards LOOP
                IF NOT EXISTS (SELECT FROM pg_trigger
                    WHERE tgrelid = to_regclass(format('public.%I', guard[1])) AND tgname = guard[2]
                        AND tgenabled = 'O' AND tgfoid = to_regprocedure(format('public.%I()', guard[3]))) THEN
                    RAISE EXCEPTION 'Factory Reset requires the installed % trigger on %', guard[2], guard[1] USING ERRCODE = '55000';
                END IF;
                EXECUTE format('ALTER TABLE public.%I DISABLE TRIGGER %I', guard[1], guard[2]);
            END LOOP;
            SELECT string_agg(format('ONLY public.%I', t), ', ' ORDER BY t COLLATE "C") INTO truncate_list
              FROM unnest(p_tables) t WHERE NOT t = ANY(kept);
            EXECUTE 'TRUNCATE ' || truncate_list || ' RESTART IDENTITY RESTRICT';
            FOREACH guard SLICE 1 IN ARRAY truncate_guards LOOP
                EXECUTE format('ALTER TABLE public.%I ENABLE TRIGGER %I', guard[1], guard[2]);
            END LOOP;

            DELETE FROM public.identity_user_logins WHERE user_id <> r.actor_id;
            DELETE FROM public.identity_user_tokens WHERE user_id <> r.actor_id;
            DELETE FROM public.identity_users WHERE id <> r.actor_id;
            SELECT string_agg(format('%I = DEFAULT, ', attname), '') INTO columns FROM pg_attribute
              WHERE attrelid = 'public.identity_users'::regclass AND attnum > 0 AND NOT attisdropped AND attgenerated = ''
                AND attname NOT IN ('id', 'user_name', 'normalized_user_name', 'password_hash', 'email', 'normalized_email',
                    'email_confirmed', 'is_active', 'phone_number', 'phone_number_confirmed', 'two_factor_enabled',
                    'display_name', 'lockout_enabled', 'lockout_end', 'access_failed_count', 'security_stamp', 'concurrency_stamp');
            EXECUTE format('UPDATE public.identity_users SET %ssecurity_stamp = replace(gen_random_uuid()::text, ''-'', ''''), '
                'concurrency_stamp = gen_random_uuid()::text WHERE id = $1', columns) USING r.actor_id;
            DELETE FROM public.gages;
            DELETE FROM public.gage_types;
            DELETE FROM public.identity_roles WHERE id NOT IN (s.baseline_role_id, s.root_role_id);

            FOR seed IN SELECT value FROM jsonb_array_elements(p_seeds) LOOP
                name := seed->>'Table';
                IF NOT name = ANY(p_tables) OR (name = ANY(kept) AND NOT name = ANY(mutable_defaults)) THEN
                    IF name IN ('permissions', 'backup_settings') THEN CONTINUE; END IF;
                    RAISE EXCEPTION 'Installation defaults reference a protected or unknown table' USING ERRCODE = '55000';
                END IF;
                SELECT string_agg(format('%I', col), ', '),
                       string_agg(format('%I = EXCLUDED.%I', col, col), ', ') FILTER (WHERE col <> 'id')
                  INTO columns, replacements FROM jsonb_array_elements_text(seed->'Columns') col;
                EXECUTE format('INSERT INTO public.%1$I (%2$s) OVERRIDING SYSTEM VALUE SELECT %2$s '
                    'FROM jsonb_populate_recordset(NULL::public.%1$I, $1)%3$s', name, columns,
                    CASE WHEN name = 'identity_roles' THEN ' ON CONFLICT (id) DO UPDATE SET ' || replacements ELSE '' END)
                    USING seed->'Rows';
            END LOOP;

            INSERT INTO public.identity_user_roles (user_id, role_id)
                SELECT r.actor_id, id FROM unnest(ARRAY[s.baseline_role_id, s.root_role_id, p_bootstrap_role]) id;
            UPDATE public.authorization_state SET installation_generation = gen_random_uuid() WHERE id = 1;
            UPDATE public.backup_settings SET reconcile_requested_by = NULL, reconcile_generation = NULL,
                revision = gen_random_uuid() WHERE id = 1;
            UPDATE public.backup_executions SET verify_requested_by = NULL, verify_generation = NULL,
                verify_started_at_utc = NULL WHERE verify_requested_by IS NOT NULL OR verify_started_at_utc IS NOT NULL;
            UPDATE public.backup_executions SET outcome = 4, stage = 7, diagnostic = 'Cancelled by Factory Reset.',
                finished_at_utc = clock_timestamp() WHERE outcome IN (0, 1) AND directory_name IS NULL;

            FOR name IN SELECT t FROM unnest(p_tables) t WHERE NOT t = ANY(kept) OR t = ANY(mutable_defaults) OR t = 'permissions' LOOP
                SELECT coalesce(sum(jsonb_array_length(x->'Rows')), 0) INTO count_expected
                  FROM jsonb_array_elements(p_seeds) x WHERE x->>'Table' = name;
                IF name = 'identity_user_roles' THEN count_expected := 3; END IF;
                EXECUTE format('SELECT count(*) FROM public.%I', name) INTO count_actual;
                IF count_actual <> count_expected THEN
                    RAISE EXCEPTION 'Unexpected row count after reseeding %', name USING ERRCODE = '55000';
                END IF;
                FOR seed IN SELECT x->'Rows' FROM jsonb_array_elements(p_seeds) x WHERE x->>'Table' = name LOOP
                    EXECUTE format('SELECT EXISTS (SELECT FROM jsonb_array_elements($1) expected '
                        'WHERE NOT EXISTS (SELECT FROM public.%I t WHERE to_jsonb(t) @> expected))', name)
                        INTO row_missing USING seed;
                    IF row_missing THEN RAISE EXCEPTION 'Installation defaults differ for %', name USING ERRCODE = '55000'; END IF;
                END LOOP;
            END LOOP;

            -- PostgreSQL's ALTER SEQUENCE RESTART is transactional, unlike setval.
            FOR sequence_row IN SELECT seq.oid::regclass AS sequence_name, ps.seqstart, ps.seqincrement,
                       tbl.relname AS table_name, att.attname AS column_name
                FROM pg_sequence ps JOIN pg_class seq ON seq.oid = ps.seqrelid
                JOIN pg_depend d ON d.classid = 'pg_class'::regclass AND d.objid = seq.oid
                    AND d.refclassid = 'pg_class'::regclass AND d.deptype IN ('a', 'i')
                JOIN pg_class tbl ON tbl.oid = d.refobjid
                JOIN pg_attribute att ON att.attrelid = tbl.oid AND att.attnum = d.refobjsubid
                WHERE tbl.relnamespace = 'public'::regnamespace AND tbl.relname = ANY(p_tables)
                    AND (NOT tbl.relname = ANY(kept) OR tbl.relname = ANY(mutable_defaults)) LOOP
                EXECUTE format('SELECT %s(%I)::numeric FROM public.%I',
                    CASE WHEN sequence_row.seqincrement > 0 THEN 'max' ELSE 'min' END,
                    sequence_row.column_name, sequence_row.table_name) INTO next_value;
                next_value := CASE WHEN sequence_row.seqincrement > 0
                    THEN greatest(sequence_row.seqstart, next_value + sequence_row.seqincrement)
                    ELSE least(sequence_row.seqstart, next_value + sequence_row.seqincrement) END;
                EXECUTE format('ALTER SEQUENCE %s RESTART WITH %s', sequence_row.sequence_name, next_value);
            END LOOP;
            SET CONSTRAINTS ALL IMMEDIATE;
            UPDATE confast_maintenance.factory_resets SET phase = 'CleaningStorage' WHERE id = p_id;
        END $$;

        REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA confast_maintenance FROM PUBLIC;
        """;
}
