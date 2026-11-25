# Factory Reset

Factory Reset returns the application to the state immediately after Root Administrator Prime provisioning. It takes the application offline for the duration of the operation and signs out all existing sessions on success.

## Deployment setup

1. Stop old application instances and apply `20261125140000_AddFactoryReset` as the PostgreSQL schema owner. Reapply [runtime-grants.sql](../scripts/security/runtime-grants.sql) and [backup-grants.sql](../scripts/security/backup-grants.sql). Even when reset is disabled, the restricted runtime needs read access to the maintenance view so it can honor an operation started on another instance.
2. Configure and test a [Backup Manager destination](backup-manager.md). Use persistent private backup and control storage with space for a complete backup in addition to the configured reserve. Keep the Data Protection ring and service configuration in the infrastructure backup.
3. Provision a dedicated, restricted PostgreSQL login for Factory Reset. It needs no role memberships, ownership, elevated login attributes, or ordinary application table access. Set its password through the deployment's credential management. As the schema owner, apply the reset grants to the explicitly named database and login.

   ```powershell
   psql -X -v ON_ERROR_STOP=1 -d APPROVED_DATABASE -v database=APPROVED_DATABASE -v reset_role=DEDICATED_RESET_LOGIN -f scripts/security/factory-reset-grants.sql
   ```

4. Set `ConnectionStrings:DatabaseReset` in deployment secrets using the dedicated login and the same primary PostgreSQL host, port, and database as `ConnectionStrings:Confast`. Use direct PostgreSQL connections or session pooling for the maintenance and backup advisory locks. Keep all running replicas on the same application build.
5. By default, managed files are under the application content root's `App_Data`. A multi-instance deployment must use a single shared physical directory for the managed files. Set `ManagedFiles:Directory` to the private shared directory on every replica and move the existing `chat-gif-category-backgrounds.json` there during offline deployment. Backups and reset both use that location. Keep managed files, backup stores, the backup control directory, Data Protection keys, and the web root in disjoint locations. Use storage that supports exclusive file sharing, atomic same-volume renames, and durable writes.
6. Set `DatabaseReset:Enabled` to `true` in deployment configuration when needed. The default is `false`. Environment-based deployments can use `DatabaseReset__Enabled=true`. Keep the reset login and storage configuration available until any accepted operation has reached its terminal state.

Only the Root Administrator Prime can see **Administration → Factory Reset** or submit the form. The privilege is intrinsic to the designated Root account. The form requires the current password, the exact case-sensitive phrase `RESET DATABASE`, an acknowledgement, and a final confirmation. The server verifies the current account, authorization state, password, and confirmation independently of the browser. Disable the setting again when the operation is finished.

## What changes

| Resource | Result |
| --- | --- |
| Business data, chat, attachments, profile pictures, stored PDFs, activity, audits, and application queues | Deleted |
| Account settings and preferences, custom roles and grants, and application configuration stored in PostgreSQL | Reset to installation defaults |
| Root identity and authentication | Same Identity key, username, account identity, password hash, external login bindings, and Identity tokens such as authenticator and recovery-code data. Root's security and concurrency stamps are rotated. |
| Root authorization | The existing protected designation survives. Role memberships return to the initial Administrator, ReadOnly, and Root memberships. |
| Schema and migration history | Preserved. The installation generation and authorization epoch advance, invalidating older installation-bound instructions and print links. |
| Backup catalogs, archives, pins, destinations, retention, and schedule | Preserved. Old queued executions, verification, reconciliation and alert work are cancelled. The settings revision changes to fence old schedule retries. Review the inventory and save the preserved settings to re-arm automatic retention for the new generation. |
| Deployment configuration, Data Protection, and other infrastructure | Preserved |

The reset derives table scope and seeded rows from the current EF model and reuses the canonical migration data for the inspection sheet certification type and certification email defaults. New EF-managed tables in the application schema are included automatically. Maintain any new migration-only installation data in the reset plan and include it in the fresh-installation comparison. Keep unrelated tables outside `public`. An unexpected table, mismatched migration history, foreign-key dependency into the application from another schema, unsupported table type, or PostgreSQL large object causes the reset to stop before committing. The current app stores binaries in ordinary database columns.

The fresh-installation test compares every table in the application schema. It allows for preserved Root identity and credentials, rotated security metadata, preserved backup infrastructure, newly generated seed timestamps, and removal of the original provisioning audit event. It also verifies that a scheduled chat message from the previous installation cannot be delivered after reset.

The only persistent managed file outside PostgreSQL today is `chat-gif-category-backgrounds.json`. Reset removes it and a recognized interrupted write of that file. A storage identity marker identifies the shared volume during recovery. Other files in `App_Data`, including deployment files, are outside the managed set. Ephemeral renderer work in the service account's system temporary directory follows the existing renderer/host cleanup lifecycle.

## Maintenance and recovery

The request durably enters maintenance after password approval. HTTP requests, Blazor operations, EF connections, backup and scheduled chat workers share a PostgreSQL activity lock. Maintenance rejects new work and drains the holders before any destruction. Browser progress is independent of the initiating connection. An expired request fails before backup once active work has drained.

The worker then acquires Backup Manager's existing filesystem and database lease. It makes a full database and managed-file backup while application mutations are suspended. The backup is catalogued as **Pre-reset backup** with the actor, request time and reset identifier. It is pinned, read through `pg_restore`, checked against the SHA-256 manifest, and durably registered before deletion starts. The workflow provides no backup bypass. It skips rolling retention during the operation.

The reset transaction uses restricted truncation, explicit row-level preservation for Identity and backup infrastructure, canonical reseeding, transactional sequence restarts, and verification before committing. The database and the cleanup phase commit together. Before this commit point, an error rolls back data changes and leaves source files intact. A worker that takes over after an interruption during backup or reset records an abort. A published pinned backup can still be recovered through catalog reconciliation if an interruption occurred during registration.

After commit, the service renames only recognized files into same-directory quarantine and removes them. Cleanup is idempotent. The application stays in maintenance until the original managed storage, fresh database, and pinned backup pass verification. On restart, an instance with the same deployment configuration and storage automatically continues post-commit work, including if new requests have been disabled. The external record is `factory-reset-<operation-id>.jsonl` under the backup control directory. The journal schema remains in PostgreSQL and its operation rows are intentionally excluded from dumps so a restored archive does not start an old reset.

If maintenance does not clear, check the external reset record and service logs. Restore access to the original storage, database executor login, and backup directory, then let a correctly configured instance resume. A file or marker conflict requires inspecting the exact paths and ownership before changing anything. Keep the application closed while resolving post-commit errors. If storage cannot be recovered or you want the pre-reset state back, follow offline restoration.

## Restore a pre-reset backup

Stop all application instances. Identify the pinned set by the reset identifier or its external log, and follow [Backup Manager offline restore](backup-manager.md#offline-restore) into a new, isolated database. Restore the listed artwork file to the configured managed directory. Reapply the relevant grants and verify Root sign-in, representative data, documents, chat, and backup inventory before switching the deployment to the restored database. Rotate the installation generation and decide how to invalidate cookies minted before the backup, since restoration also restores credential stamps. Reconcile backup inventory before re-arming retention. [Offline authorization recovery](deployment-readiness.md#backup-restore-and-offline-recovery)

Use a disposable database and copied storage to rehearse both reset and restore. Schedule the operational reset for a maintenance window and protect the backup and log directories with private ACLs and independent copies. Run migrations, direct schema-owner changes, and restore cutovers offline.
