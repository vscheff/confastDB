using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations;

public partial class MakeReadOnlyDefaultOnly : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Keep initial enrollment, protected role definitions and Root consistency;
        // remove only the permanent-membership rule. Existing assignments are untouched.
        migrationBuilder.Sql("""
                SELECT id FROM public.authorization_state WHERE id = 1 FOR UPDATE;
                CREATE OR REPLACE FUNCTION confast_validate_security() RETURNS trigger LANGUAGE plpgsql
                SET search_path = pg_catalog, public AS $$
                DECLARE s public.authorization_state%ROWTYPE;
                BEGIN
                    SELECT * INTO s FROM public.authorization_state WHERE id = 1;
                    IF NOT FOUND OR s.baseline_role_id <> '1b171cb9-9273-42fc-b790-ea934dbb12b9'
                        OR s.root_role_id <> 'e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6' THEN
                        RAISE EXCEPTION 'Invalid authorization singleton' USING ERRCODE = '23514';
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
                UPDATE public.authorization_state SET global_epoch = global_epoch + 1 WHERE id = 1;
                """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // A downgrade must not silently grant ReadOnly back to accounts that lost it deliberately.
        migrationBuilder.Sql("""
                SELECT id FROM public.authorization_state WHERE id = 1 FOR UPDATE;
                DO $$ BEGIN
                    IF EXISTS (SELECT FROM public.identity_users u WHERE NOT EXISTS
                        (SELECT FROM public.identity_user_roles ur JOIN public.authorization_state s
                            ON s.id = 1 AND ur.role_id = s.baseline_role_id WHERE ur.user_id = u.id)) THEN
                        RAISE EXCEPTION 'Cannot restore mandatory ReadOnly while accounts lack it; review assignments before downgrade'
                            USING ERRCODE = '23514';
                    END IF;
                END $$;
                CREATE OR REPLACE FUNCTION confast_validate_security() RETURNS trigger LANGUAGE plpgsql
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
                UPDATE public.authorization_state SET global_epoch = global_epoch + 1 WHERE id = 1;
                """);
    }
}
