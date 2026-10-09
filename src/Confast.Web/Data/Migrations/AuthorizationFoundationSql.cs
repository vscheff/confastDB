namespace Confast.Web.Data.Migrations;

// Historical Phase 3B SQL. Future catalog changes must use a new reviewed migration.
internal static class AuthorizationFoundationSql
{
    internal const string Install = """
        INSERT INTO authorization_state
            (id, baseline_role_id, root_role_id, global_epoch, catalog_version, readiness)
        VALUES (1, '1b171cb9-9273-42fc-b790-ea934dbb12b9', 'e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6', 0,
            'FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544', 0);

        INSERT INTO identity_user_roles (user_id, role_id)
        SELECT id, '1b171cb9-9273-42fc-b790-ea934dbb12b9' FROM identity_users
        ON CONFLICT DO NOTHING;

        CREATE FUNCTION confast_security_lock() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
        SET search_path = pg_catalog, public AS $$
        BEGIN
            PERFORM 1 FROM public.authorization_state WHERE id = 1 FOR UPDATE;
            IF NOT FOUND THEN RAISE EXCEPTION 'Authorization state is missing' USING ERRCODE = '23514'; END IF;
            RETURN NULL;
        END $$;

        CREATE FUNCTION confast_security_epoch() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
        SET search_path = pg_catalog, public AS $$
        BEGIN
            UPDATE public.authorization_state SET global_epoch = global_epoch + 1 WHERE id = 1;
            IF NOT FOUND THEN RAISE EXCEPTION 'Authorization state is missing' USING ERRCODE = '23514'; END IF;
            RETURN NULL;
        END $$;

        CREATE FUNCTION confast_guard_state() RETURNS trigger LANGUAGE plpgsql
        SET search_path = pg_catalog, public AS $$
        DECLARE owner_name text;
        BEGIN
            IF TG_OP <> 'UPDATE' THEN
                RAISE EXCEPTION 'Authorization singleton cannot be inserted or removed' USING ERRCODE = '23514';
            END IF;
            IF NEW.id <> OLD.id OR NEW.baseline_role_id <> OLD.baseline_role_id OR NEW.root_role_id <> OLD.root_role_id
                OR NEW.global_epoch < OLD.global_epoch THEN
                RAISE EXCEPTION 'Protected authorization state identity/version' USING ERRCODE = '23514';
            END IF;
            IF OLD.readiness = 1 AND (NEW.root_user_id IS DISTINCT FROM OLD.root_user_id OR NEW.readiness <> 1) THEN
                RAISE EXCEPTION 'Ready Root designation requires offline recovery' USING ERRCODE = '23514';
            END IF;
            IF NEW.root_user_id IS DISTINCT FROM OLD.root_user_id OR NEW.readiness <> OLD.readiness
                OR NEW.installation_generation <> OLD.installation_generation OR NEW.catalog_version <> OLD.catalog_version THEN
                SELECT pg_get_userbyid(relowner) INTO owner_name FROM pg_class WHERE oid = 'public.authorization_state'::regclass;
                IF current_user <> owner_name THEN
                    RAISE EXCEPTION 'Installation operator privileges required' USING ERRCODE = '42501';
                END IF;
                NEW.global_epoch := OLD.global_epoch + 1;
            END IF;
            RETURN NEW;
        END $$;

        CREATE FUNCTION confast_guard_role() RETURNS trigger LANGUAGE plpgsql
        SET search_path = pg_catalog, public AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                IF OLD.system_kind <> 0 THEN RAISE EXCEPTION 'Protected system role' USING ERRCODE = '23514'; END IF;
                RETURN OLD;
            END IF;
            IF TG_OP = 'UPDATE' AND (NEW.id <> OLD.id OR NEW.system_kind <> OLD.system_kind
                OR NEW.system_key IS DISTINCT FROM OLD.system_key) THEN
                RAISE EXCEPTION 'Immutable role identity' USING ERRCODE = '23514';
            END IF;
            IF NEW.id = '1b171cb9-9273-42fc-b790-ea934dbb12b9' THEN
                IF NEW.system_kind <> 1 OR NEW.system_key IS DISTINCT FROM 'ReadOnlyBaseline' OR NOT NEW.is_enabled
                    OR NEW.name IS DISTINCT FROM 'ReadOnly' OR NEW.normalized_name IS DISTINCT FROM 'READONLY' THEN
                    RAISE EXCEPTION 'Protected baseline identity' USING ERRCODE = '23514';
                END IF;
            ELSIF NEW.id = 'e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6' THEN
                IF NEW.system_kind <> 2 OR NEW.system_key IS DISTINCT FROM 'RootAdministratorPrime' OR NOT NEW.is_enabled
                    OR NEW.name IS DISTINCT FROM 'Root Administrator Prime' OR NEW.normalized_name IS DISTINCT FROM 'ROOT ADMINISTRATOR PRIME' THEN
                    RAISE EXCEPTION 'Protected Root role identity' USING ERRCODE = '23514';
                END IF;
            ELSIF NEW.system_kind <> 0 OR NEW.system_key IS NOT NULL THEN
                RAISE EXCEPTION 'Unsupported system role' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END $$;

        CREATE FUNCTION confast_guard_grant() RETURNS trigger LANGUAGE plpgsql
        SET search_path = pg_catalog, public AS $$
        BEGIN
            IF NEW.role_id = 'e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6'
                OR NOT EXISTS (SELECT FROM public.permissions WHERE key = NEW.permission_key AND authority = 0)
                OR (NEW.role_id = '1b171cb9-9273-42fc-b790-ea934dbb12b9'
                    AND NOT EXISTS (SELECT FROM public.permissions WHERE key = NEW.permission_key AND allowed_in_baseline)) THEN
                RAISE EXCEPTION 'Unsupported role grant' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END $$;

        CREATE FUNCTION confast_guard_edge() RETURNS trigger LANGUAGE plpgsql
        SET search_path = pg_catalog, public AS $$
        BEGIN
            IF NEW.child_role_id = NEW.parent_role_id
                OR NEW.child_role_id IN ('1b171cb9-9273-42fc-b790-ea934dbb12b9', 'e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6')
                OR NEW.parent_role_id = 'e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6' THEN
                RAISE EXCEPTION 'Protected/self inheritance edge' USING ERRCODE = '23514';
            END IF;
            IF EXISTS (
                WITH RECURSIVE ancestors(id) AS (
                    SELECT NEW.parent_role_id
                    UNION
                    SELECT e.parent_role_id FROM public.role_inheritance e JOIN ancestors a ON e.child_role_id = a.id
                    WHERE TG_OP <> 'UPDATE' OR (e.child_role_id, e.parent_role_id) <> (OLD.child_role_id, OLD.parent_role_id)
                ) SELECT FROM ancestors WHERE id = NEW.child_role_id
            ) THEN RAISE EXCEPTION 'Role inheritance cycle' USING ERRCODE = '23514'; END IF;
            RETURN NEW;
        END $$;

        CREATE FUNCTION confast_enroll_baseline() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
        SET search_path = pg_catalog, public AS $$
        BEGIN
            INSERT INTO public.identity_user_roles (user_id, role_id)
                VALUES (NEW.id, '1b171cb9-9273-42fc-b790-ea934dbb12b9') ON CONFLICT DO NOTHING;
            RETURN NEW;
        END $$;

        CREATE FUNCTION confast_account_epoch() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
        SET search_path = pg_catalog, public AS $$
        BEGIN
            IF (NEW.is_active, NEW.security_stamp, NEW.password_hash, NEW.user_name, NEW.email, NEW.lockout_enabled,
                    NEW.lockout_end, NEW.two_factor_enabled) IS DISTINCT FROM
                (OLD.is_active, OLD.security_stamp, OLD.password_hash, OLD.user_name, OLD.email, OLD.lockout_enabled,
                    OLD.lockout_end, OLD.two_factor_enabled) THEN
                UPDATE public.authorization_state SET global_epoch = global_epoch + 1 WHERE id = 1;
            END IF;
            RETURN NEW;
        END $$;

        CREATE FUNCTION confast_validate_security() RETURNS trigger LANGUAGE plpgsql
        SET search_path = pg_catalog, public AS $$
        DECLARE s public.authorization_state%ROWTYPE;
        BEGIN
            SELECT * INTO s FROM public.authorization_state WHERE id = 1;
            IF NOT FOUND OR s.baseline_role_id <> '1b171cb9-9273-42fc-b790-ea934dbb12b9'
                OR s.root_role_id <> 'e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6' THEN
                RAISE EXCEPTION 'Invalid authorization singleton' USING ERRCODE = '23514';
            END IF;
            IF EXISTS (SELECT FROM public.identity_users u WHERE NOT EXISTS
                (SELECT FROM public.identity_user_roles ur WHERE ur.user_id = u.id AND ur.role_id = s.baseline_role_id)) THEN
                RAISE EXCEPTION 'Every human must retain baseline membership' USING ERRCODE = '23514';
            END IF;
            IF s.readiness = 0 THEN
                IF s.root_user_id IS NOT NULL OR EXISTS (SELECT FROM public.identity_user_roles WHERE role_id = s.root_role_id) THEN
                    RAISE EXCEPTION 'Pending installation cannot have Root membership' USING ERRCODE = '23514';
                END IF;
            ELSIF s.readiness = 1 THEN
                IF NOT EXISTS (SELECT FROM public.identity_users WHERE id = s.root_user_id AND is_active
                    AND password_hash IS NOT NULL AND password_hash <> '')
                    OR (SELECT count(*) FROM public.identity_user_roles WHERE role_id = s.root_role_id) <> 1
                    OR NOT EXISTS (SELECT FROM public.identity_user_roles WHERE role_id = s.root_role_id AND user_id = s.root_user_id) THEN
                    RAISE EXCEPTION 'Ready Root designation/membership/account is inconsistent' USING ERRCODE = '23514';
                END IF;
            ELSE RAISE EXCEPTION 'Invalid readiness' USING ERRCODE = '23514'; END IF;
            RETURN NULL;
        END $$;

        CREATE FUNCTION confast_reject_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'Code manifest/history/protected tables cannot be changed or truncated' USING ERRCODE = '23514'; END $$;

        CREATE TRIGGER auth_lock BEFORE INSERT OR UPDATE OR DELETE ON identity_roles FOR EACH STATEMENT EXECUTE FUNCTION confast_security_lock();
        CREATE TRIGGER auth_epoch AFTER INSERT OR UPDATE OR DELETE ON identity_roles FOR EACH STATEMENT EXECUTE FUNCTION confast_security_epoch();
        CREATE TRIGGER auth_guard BEFORE INSERT OR UPDATE OR DELETE ON identity_roles FOR EACH ROW EXECUTE FUNCTION confast_guard_role();
        CREATE TRIGGER auth_lock BEFORE INSERT OR UPDATE OR DELETE ON role_permissions FOR EACH STATEMENT EXECUTE FUNCTION confast_security_lock();
        CREATE TRIGGER auth_epoch AFTER INSERT OR UPDATE OR DELETE ON role_permissions FOR EACH STATEMENT EXECUTE FUNCTION confast_security_epoch();
        CREATE TRIGGER auth_guard BEFORE INSERT OR UPDATE ON role_permissions FOR EACH ROW EXECUTE FUNCTION confast_guard_grant();
        CREATE TRIGGER auth_lock BEFORE INSERT OR UPDATE OR DELETE ON role_inheritance FOR EACH STATEMENT EXECUTE FUNCTION confast_security_lock();
        CREATE TRIGGER auth_epoch AFTER INSERT OR UPDATE OR DELETE ON role_inheritance FOR EACH STATEMENT EXECUTE FUNCTION confast_security_epoch();
        CREATE TRIGGER auth_guard BEFORE INSERT OR UPDATE ON role_inheritance FOR EACH ROW EXECUTE FUNCTION confast_guard_edge();
        CREATE TRIGGER auth_lock BEFORE INSERT OR UPDATE OR DELETE ON identity_user_roles FOR EACH STATEMENT EXECUTE FUNCTION confast_security_lock();
        CREATE TRIGGER auth_epoch AFTER INSERT OR UPDATE OR DELETE ON identity_user_roles FOR EACH STATEMENT EXECUTE FUNCTION confast_security_epoch();
        CREATE TRIGGER auth_lock BEFORE INSERT OR DELETE OR UPDATE OF is_active, security_stamp, password_hash, user_name, email, lockout_enabled, lockout_end, two_factor_enabled
            ON identity_users FOR EACH STATEMENT EXECUTE FUNCTION confast_security_lock();
        CREATE TRIGGER auth_epoch AFTER INSERT OR DELETE ON identity_users FOR EACH STATEMENT EXECUTE FUNCTION confast_security_epoch();
        CREATE TRIGGER auth_account_epoch AFTER UPDATE ON identity_users FOR EACH ROW EXECUTE FUNCTION confast_account_epoch();
        CREATE TRIGGER auth_enroll AFTER INSERT ON identity_users FOR EACH ROW EXECUTE FUNCTION confast_enroll_baseline();
        CREATE TRIGGER auth_guard BEFORE INSERT OR UPDATE OR DELETE ON authorization_state FOR EACH ROW EXECUTE FUNCTION confast_guard_state();
        CREATE CONSTRAINT TRIGGER auth_valid_state AFTER UPDATE ON authorization_state DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION confast_validate_security();
        CREATE CONSTRAINT TRIGGER auth_valid_user AFTER INSERT OR UPDATE OR DELETE ON identity_users DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION confast_validate_security();
        CREATE CONSTRAINT TRIGGER auth_valid_membership AFTER INSERT OR UPDATE OR DELETE ON identity_user_roles DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION confast_validate_security();
        CREATE TRIGGER auth_manifest BEFORE INSERT OR UPDATE OR DELETE ON permissions FOR EACH STATEMENT EXECUTE FUNCTION confast_reject_immutable();
        CREATE TRIGGER auth_history BEFORE UPDATE OR DELETE ON authorization_change_history FOR EACH STATEMENT EXECUTE FUNCTION confast_reject_immutable();
        CREATE TRIGGER auth_history BEFORE UPDATE OR DELETE ON authorization_change_details FOR EACH STATEMENT EXECUTE FUNCTION confast_reject_immutable();
        CREATE TRIGGER auth_no_truncate BEFORE TRUNCATE ON permissions FOR EACH STATEMENT EXECUTE FUNCTION confast_reject_immutable();
        CREATE TRIGGER auth_no_truncate BEFORE TRUNCATE ON authorization_state FOR EACH STATEMENT EXECUTE FUNCTION confast_reject_immutable();
        CREATE TRIGGER auth_no_truncate BEFORE TRUNCATE ON identity_roles FOR EACH STATEMENT EXECUTE FUNCTION confast_reject_immutable();
        CREATE TRIGGER auth_no_truncate BEFORE TRUNCATE ON identity_users FOR EACH STATEMENT EXECUTE FUNCTION confast_reject_immutable();
        CREATE TRIGGER auth_no_truncate BEFORE TRUNCATE ON identity_user_roles FOR EACH STATEMENT EXECUTE FUNCTION confast_reject_immutable();
        CREATE TRIGGER auth_no_truncate BEFORE TRUNCATE ON role_permissions FOR EACH STATEMENT EXECUTE FUNCTION confast_reject_immutable();
        CREATE TRIGGER auth_no_truncate BEFORE TRUNCATE ON role_inheritance FOR EACH STATEMENT EXECUTE FUNCTION confast_reject_immutable();
        CREATE TRIGGER auth_no_truncate BEFORE TRUNCATE ON authorization_change_history FOR EACH STATEMENT EXECUTE FUNCTION confast_reject_immutable();
        CREATE TRIGGER auth_no_truncate BEFORE TRUNCATE ON authorization_change_details FOR EACH STATEMENT EXECUTE FUNCTION confast_reject_immutable();

        -- No human authorization is inferred from SQL session variables. This installation-only
        -- function is executable by its owner, never PUBLIC/the separately privileged runtime role.
        CREATE FUNCTION provision_confast_root(account_id text, expected_epoch bigint, reason text, excluded_browser_test_account_id text)
        RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
        DECLARE s public.authorization_state%ROWTYPE; history_id bigint;
        BEGIN
            SELECT * INTO STRICT s FROM public.authorization_state WHERE id = 1 FOR UPDATE;
            IF s.readiness <> 0 OR s.root_user_id IS NOT NULL OR s.global_epoch <> expected_epoch THEN
                RAISE EXCEPTION 'Root provisioning requires pending state and current epoch' USING ERRCODE = '23514';
            END IF;
            IF account_id IS NULL OR account_id = excluded_browser_test_account_id OR reason IS NULL OR btrim(reason) = ''
                OR NOT EXISTS (SELECT FROM public.identity_users WHERE id = account_id AND is_active
                    AND password_hash IS NOT NULL AND password_hash <> '' AND security_stamp IS NOT NULL AND security_stamp <> '') THEN
                RAISE EXCEPTION 'Intentional active credentialed non-browser-test account required' USING ERRCODE = '23514';
            END IF;
            UPDATE public.authorization_state SET root_user_id = account_id, readiness = 1 WHERE id = 1;
            INSERT INTO public.identity_user_roles (user_id, role_id) VALUES (account_id, s.root_role_id);
            INSERT INTO public.authorization_change_history
                (operation_id, actor_kind, purpose, reason, occurred_at_utc, previous_epoch, resulting_epoch, installation_generation)
            SELECT gen_random_uuid(), 1, 'Installation.ProvisionRoot', reason, clock_timestamp(), s.global_epoch, global_epoch, s.installation_generation
                FROM public.authorization_state WHERE id = 1 RETURNING id INTO history_id;
            INSERT INTO public.authorization_change_details (history_id, kind, user_id, role_id, was_present, is_present)
                VALUES (history_id, 4, account_id, s.root_role_id, false, true);
        END $$;
        REVOKE ALL ON FUNCTION provision_confast_root(text, bigint, text, text) FROM PUBLIC;
        """;

    internal const string Uninstall = """
        DO $$ BEGIN IF EXISTS (SELECT FROM authorization_state WHERE readiness = 1) THEN
            RAISE EXCEPTION 'Ready installation rollback requires controlled offline recovery'; END IF; END $$;
        DROP TRIGGER auth_lock ON identity_users;
        DROP TRIGGER auth_epoch ON identity_users;
        DROP TRIGGER auth_account_epoch ON identity_users;
        DROP TRIGGER auth_enroll ON identity_users;
        DROP TRIGGER auth_valid_user ON identity_users;
        DROP TRIGGER auth_no_truncate ON identity_users;
        DROP TRIGGER auth_lock ON identity_user_roles;
        DROP TRIGGER auth_epoch ON identity_user_roles;
        DROP TRIGGER auth_valid_membership ON identity_user_roles;
        DROP TRIGGER auth_no_truncate ON identity_user_roles;
        DROP TRIGGER auth_lock ON identity_roles;
        DROP TRIGGER auth_epoch ON identity_roles;
        DROP TRIGGER auth_guard ON identity_roles;
        DROP TRIGGER auth_no_truncate ON identity_roles;
        DROP FUNCTION provision_confast_root(text, bigint, text, text);
        """;

    internal const string DropFunctions = """
        DROP FUNCTION confast_security_lock();
        DROP FUNCTION confast_security_epoch();
        DROP FUNCTION confast_guard_state();
        DROP FUNCTION confast_guard_role();
        DROP FUNCTION confast_guard_grant();
        DROP FUNCTION confast_guard_edge();
        DROP FUNCTION confast_enroll_baseline();
        DROP FUNCTION confast_account_epoch();
        DROP FUNCTION confast_validate_security();
        DROP FUNCTION confast_reject_immutable();
        """;
}
