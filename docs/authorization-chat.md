# Phase 3E-1: chat authorization, privacy and moderation

**Phase 3F status (October 16, 2026):** the catalog-backed role matrix, provenance and impact previews, safe role/user editing, process-local advisory refresh, and channel moderation-history UI are implemented. See [the administration UI record](authorization-administration-ui.md) for behavior, validation, and remaining Phase 3G limits. This supersedes historical matrix/refresh/history-UI deferrals below.

**Phase 3E-2 status (October 16, 2026):** scheduled delegation/lifecycle, scoped renderer execution, create-only bootstrap and guarded Development diagnostics are implemented. See [the background and delegation record](authorization-background-delegation.md) for the complete execution inventory, migration, validation and remaining Phase 3F/3G boundaries. This supersedes historical background/bootstrap deferrals below.

Implemented October 15, 2026. This record supersedes chat deferrals in the historical Phase 1 audit and Phases 3B-3D records. Its original scope covers chat; the Phase 3E-2 record above now completes the subsequent worker/bootstrap slice. ReadOnly remains optional, Root authority remains designation-based, and the 164-key catalog and all initial grants are unchanged. No development/production migration or account modification is authorized or performed by this slice.

## Authorization and existing public-channel behavior

Each independently invoked protected service method starts an ApplicationAuthorization operation using the circuit's authenticated identity, Active/stamp/readiness checks and versioned effective grants. Every chat action expands the existing catalog prerequisites, including Chat.Access. A stored action grant without Access is unusable. No component supplies an actor ID or caches a trusted operation. Legacy IsCurrentUserAdministratorAsync was replaced by CanAdministerChannelsAsync: the answer is capability authority, not a role-name comparison.

The actual existing behavior differs from the README's old discover-and-join description: creating a public channel enrolls **all Active accounts**, and chat operations reconcile missing public memberships with ON CONFLICT DO NOTHING. Newly enrolled users start read through the current latest message. This eligibility is unchanged; enrollment on chat operations now runs **after** Access authorization. Lacking Access does not delete memberships or prevent organization-wide enrollment during channel creation. It simply denies content. Private channels/DMs/groups never use that enrollment. Root and Administrator gain no private content/membership bypass. AdministerChannels replaces the old narrow Administrator ownership alternative for metadata/deletion; the corresponding UpdateChannels/DeleteChannels grant is still necessary. It does not authorize private member management.

GetThreadAsync's conversation and channel-thread projections, StartGroupAsync's duplicate detection, reactions, normal/GIF message persistence and notification-summary composition reuse their caller's operation. Category creation plus a channel move requires both grants. Normalizing sibling order is incidental work inside the authorized operation. All source/destination visibility, descendant membership, creator and cycle rules remain in force. Lists/counts evaluate once per call, not per row. There is no separate full-text message-search endpoint in the current implementation: conversation filtering uses authorized conversation projections, and GIF search handles only public provider content.

## Public ChatService inventory

The table lists all 76 public asynchronous ChatService boundaries (including Phase 3E-2 cancellation), compared with actual signatures by a reflection-backed regression. Permission names below have the `Chat.` prefix. All listed grants include their catalog prerequisites. Once-authorized wrappers delegate to one guarded core, with no second evaluation. Text processing/catalog constants are pure functions, not database boundaries. Historical Phase 1 `Chat.Read` references correspond to current `Chat.Access`; the current 21-key catalog takes precedence over audit-era labels.

| Public method | Permission | Context |
|---|---|---|
| `CancelScheduledMessageAsync` | Access | Phase 3E-2: stored sender only, row lock, terminal cancellation; no new Send/Schedule permission required |
| `DeleteChannelAsync` | DeleteChannels | Owner or AdministerChannels; DeleteChannels still independently required; soft deletion |
| `DuplicateChannelAsync` | CreateChannels | Source-channel member; copies configuration/membership only, never history |
| `MoveChannelToCategoryEdgeAsync` | ReorderChannels | Source-channel member; normalizes existing sibling slots |
| `SetChannelPinnedToTopAsync` | Access | Existing actor-only preferences/read state; applicable conversation/folder membership and visibility |
| `RecordGifRequestAsync` | Access | Public GIPHY metadata or actor-owned favorites/budget; no conversation content in shared cache |
| `ResolveGifSearchAsync` | Access | Public GIPHY metadata or actor-owned favorites/budget; no conversation content in shared cache |
| `ResolveGifsAsync` | Access | Public GIPHY metadata or actor-owned favorites/budget; no conversation content in shared cache |
| `GetGifFavoritesAsync` | Access | Public GIPHY metadata or actor-owned favorites/budget; no conversation content in shared cache |
| `SendGifAsync` | SendMessages | Current conversation member; normal send core, validation and persistence reused |
| `SetGifFavoriteAsync` | Access | Public GIPHY metadata or actor-owned favorites/budget; no conversation content in shared cache |
| `AddConversationMembersAsync` | ManageGroupConversations | Source member; a DM creates a separate group and additionally requires CreateConversations |
| `CreateGroupAsync` | CreateConversations; delegates to StartGroupAsync | Authenticated Active current actor; existing contextual visibility and personal-state rules |
| `GetGroupIconAsync` | Access | Current group member; no supplied requester identity |
| `HideDirectConversationAsync` | Access | Only the actor's DM visibility; no membership/history deletion |
| `LeaveGroupAsync` | None (self-scoped exception) | Universal self-scoped exception: current authenticated Active actor, own group membership only; no content returned |
| `RenameGroupAsync` | ManageGroupConversations; delegates to UpdateGroupAsync | Authenticated Active current actor; existing contextual visibility and personal-state rules |
| `SetConversationMutedAsync` | Access | Existing actor-only preferences/read state; applicable conversation/folder membership and visibility |
| `SetConversationPinnedToTopAsync` | Access | Existing actor-only preferences/read state; applicable conversation/folder membership and visibility |
| `StartGroupAsync` | CreateConversations | At least three distinct Active people; existing exact-member matches use the same operation frame |
| `UpdateGroupAsync` | ManageGroupConversations | Current group member; existing naming and image validation |
| `GetInboxMessagesAsync` | Access | Only current member conversations, unread/mention filters and personal visibility |
| `GetIncomingMessagePreviewAsync` | Access | Current member, incoming undeleted/unread eligible message; personal popup/mute preferences |
| `GetScheduledMessagesAsync` | Access | Actor-owned drafts AND current conversation membership; removed members no longer receive draft content |
| `MarkAllUnreadReadAsync` | Access | Existing actor-only preferences/read state; applicable conversation/folder membership and visibility |
| `MarkMessageReadAsync` | Access | Existing actor-only preferences/read state; applicable conversation/folder membership and visibility |
| `DeleteOthersMessageAsync` | DeleteOthersMessages | Current channel member; another human's supported non-GIF Text; nonempty reason; atomic locked soft deletion/audit |
| `GetModerationHistoryAsync` | DeleteOthersMessages | Current channel member with DeleteOthersMessages; latest 200 records; no global history view |
| `GetNotificationSettingsAsync` | Access | Existing actor-only preferences/read state; applicable conversation/folder membership and visibility |
| `GetNotificationUnreadCountsAsync` | Access | Existing actor-only preferences/read state; applicable conversation/folder membership and visibility |
| `GetNotificationUnreadSummaryAsync` | Access; delegates to GetNotificationUnreadCountsAsync | Existing actor-only preferences/read state; applicable conversation/folder membership and visibility |
| `SetCategoryMutedAsync` | Access | Existing actor-only preferences/read state; applicable conversation/folder membership and visibility |
| `SetCategoryNotificationModeAsync` | Access | Existing actor-only preferences/read state; applicable conversation/folder membership and visibility |
| `SetChannelMutedAsync` | Access | Existing actor-only preferences/read state; applicable conversation/folder membership and visibility |
| `SetChannelNotificationModeAsync` | Access | Existing actor-only preferences/read state; applicable conversation/folder membership and visibility |
| `CreatePollAsync` | CreatePolls | Conversation member and matching thread; future start also ScheduleMessages; catalog includes SendMessages |
| `TogglePollVoteAsync` | VotePolls | Member; valid answer, open poll, serialized existing vote rules; no SendMessages requirement |
| `AddPrivateMemberAsync` | ManagePrivateChannelMembers | Private-channel owner only, including for Root; Active target |
| `AddReactionAsync` | React; delegates to SaveReactionAsync | Member of message conversation; eligible undeleted Text; one serialized reaction core |
| `CanAdministerChannelsAsync` | Access (queries AdministerChannels) | Advisory capability query; returns the current AdministerChannels grant, never role-name authority |
| `CreateChannelAsync` | CreateChannels | Existing public enrollment/private initial-members rules; destination folder visible |
| `CreateChannelGroupAsync` | CreateCategories | Visible parent; if channelId supplied, also ReorderChannels and source-channel membership |
| `CreateChannelThreadAsync` | CreateThreads | Channel member; valid source/reply and initial message; catalog includes SendMessages |
| `DeleteChannelGroupAsync` | ManageCategories | Manageable source; descendant channels/folders promoted without deleting content |
| `DeleteOwnMessageAsync` | DeleteOwnMessages | Member, actual author, Text type; existing own-deletion disappearance retained |
| `EditOwnMessageAsync` | EditOwnMessages | Member, actual author, undeleted editable Text; GIF editing still disallowed |
| `GetActiveChannelThreadsAsync` | Access | Current channel membership; matching channel/thread; no private-channel override |
| `GetActiveUsersAsync` | Access | Existing organization-wide Active-user directory, after Access |
| `GetAttachmentAsync` | Access | Member of the attachment's live conversation; undeleted message; absence/nonmembership returns null |
| `GetChannelGroupsAsync` | Access | Only folders visible through existing member/creator and descendant rules |
| `GetChannelMembersAsync` | Access | Current channel member; narrow owner/admin metadata visibility is not content access |
| `GetChannelThreadsAsync` | Access | Current channel membership; matching channel/thread; no private-channel override |
| `GetConversationsAsync` | Access | Authenticated Active current actor; existing contextual visibility and personal-state rules |
| `GetCurrentUserIdAsync` | Access | Authenticated Active current actor; existing contextual visibility and personal-state rules |
| `GetEmojiTonePreferencesAsync` | Access | Authenticated Active current actor; existing contextual visibility and personal-state rules |
| `GetLastReactionEmojiAsync` | Access | Member of message conversation; eligible undeleted Text; one serialized reaction core |
| `GetThreadAsync` | Access | Member; matching channel thread; history, pins, reactions and polls share one snapshot |
| `GetTotalUnreadCountAsync` | Access | Authenticated Active current actor; existing contextual visibility and personal-state rules |
| `HasUnreadAlertAsync` | Access | Authenticated Active current actor; existing contextual visibility and personal-state rules |
| `HasUnreadMentionAsync` | Access | Authenticated Active current actor; existing contextual visibility and personal-state rules |
| `MarkConversationReadAsync` | Access | Existing actor-only preferences/read state; applicable conversation/folder membership and visibility |
| `MarkReadAsync` | Access | Existing actor-only preferences/read state; applicable conversation/folder membership and visibility |
| `MarkUnreadAsync` | Access | Existing actor-only preferences/read state; applicable conversation/folder membership and visibility |
| `MoveChannelAsync` | ReorderChannels; delegates to MoveLayoutItemAsync | Authenticated Active current actor; existing contextual visibility and personal-state rules |
| `MoveChannelGroupAsync` | ManageCategories; delegates to MoveLayoutItemAsync | Authenticated Active current actor; existing contextual visibility and personal-state rules |
| `MoveLayoutItemAsync` | ManageCategories (folder) OR ReorderChannels (channel) | ManageCategories for folders, ReorderChannels for channels; source/destination/descendant checks retained |
| `OpenDirectAsync` | CreateConversations | Active target; sorted pair uniqueness; self messaging and hidden-DM restoration retained |
| `RemovePrivateMemberAsync` | ManagePrivateChannelMembers | Private-channel owner only, including for Root; existing self-removal restriction |
| `RenameChannelAsync` | UpdateChannels | Owner or AdministerChannels; UpdateChannels still independently required |
| `RenameChannelGroupAsync` | ManageCategories | Manageable source: membership in every descendant channel; existing name rules |
| `SendAsync` | SendMessages; delegates to SendWithAttachmentsAsync | Authenticated Active current actor; existing contextual visibility and personal-state rules |
| `SendWithAttachmentsAsync` | SendMessages | Current conversation member; valid thread/reply; scheduledAtUtc additionally requires ScheduleMessages |
| `SetChannelTopicAsync` | UpdateChannels | Owner or AdministerChannels; UpdateChannels still independently required |
| `SetEmojiTonePreferenceAsync` | Access | Authenticated Active current actor; existing contextual visibility and personal-state rules |
| `SetMessagePinnedAsync` | PinMessages | Member, eligible undeleted Text; pin notice and pin clearing retained |
| `ToggleReactionAsync` | React; delegates to SaveReactionAsync | Member of message conversation; eligible undeleted Text; one serialized reaction core |

The 21 keys are Access, CreateConversations, SendMessages, ScheduleMessages, CreateThreads, CreatePolls, VotePolls, React, PinMessages, EditOwnMessages, DeleteOwnMessages, DeleteOthersMessages, CreateChannels, UpdateChannels, DeleteChannels, ManagePrivateChannelMembers, CreateCategories, ManageCategories, ReorderChannels, ManageGroupConversations and AdministerChannels. All actions require Access. ScheduleMessages, CreateThreads and CreatePolls additionally require SendMessages. VotePolls/React/EditOwnMessages/DeleteOwnMessages/PinMessages do not imply SendMessages.

## Presence, avatars and self-scoped exceptions

UserPresenceService.GetVisibleAsync, GetCurrentAsync, both SetPreferenceAsync overloads, SetCustomStatusAsync and HeartbeatAsync require Access. GetCurrentAsync reuses its frame for the underlying presence projection. ResolveState/EffectivePreference are pure calculations. EndSessionAsync is the existing self-only cleanup exception: it removes only the authenticated current user's matching session and returns no presence/content. Foreign session IDs cannot delete another user's state.

UserProfilePictureService.GetVersionsAsync and GetAsync require Access; the HTTP adapter below reuses its request frame. Avatars remain an organization-wide Active-user profile resource, not a private-conversation resource: knowing an avatar does not establish channel membership. SaveAsync, RemoveAsync and GetCurrentUserDisplayNameAsync are deliberate self-profile exceptions with a fresh authenticated Active operation but no Chat.Access requirement. They can only update/remove the actor's own profile picture or return the actor's own display name. LeaveGroupAsync is the other exception: it validates the current actor and own membership, removes that membership and writes the existing leave notice without returning history. Hiding a DM, preferences, read markers, emoji/GIF favorites and all content reads still require Access.

## HTTP, media and subscriptions

| Route | Trusted boundary | Additional restriction |
|---|---|---|
| `/chat/attachments/{id}` | ChatAttachment / Access | Current conversation membership, live conversation, undeleted message; only image/video inline |
| `/chat/attachments/{id}/download` | Same operation | Same membership/deletion checks; safe download filename and application/octet-stream |
| `/chat/group-icons/{conversationId}` | ChatGroupIcon / Access | Current group membership |
| `/profile-pictures/{userId}` | ChatProfilePicture / Access | Active target profile; organization-wide avatar semantics |

All four routes retain the real Identity application-cookie scheme. BusinessHttpAuthorization creates one request frame before policy/service composition; internal file adapters require its matching purpose/scope and derive the actor from it. No public requester-ID or authorization-bypass parameter remains. All guarded requests use private, no-store caching; successful media responses and permission denials add nosniff; existing range processing for inline media is retained. Missing/inaccessible attachments return 404. Inaccessible group icons retain the existing 401 result. Access denial returns 403. No chat attachment/icon bytes are placed under static assets. Public emoji assets and genuinely public GIPHY CDN resources remain public. GIPHY IDs/metadata are validated; shared caches contain public provider metadata, not private messages or attachment bytes. Missing-ID budget accounting now occurs inside the authorized GIF lookup instead of a redundant nested browser callback.

ChatNotifications.Subscribe/Publish are server-side process-local infrastructure. Payloads contain only an optional message ID, never body/attachment bytes/recipient lists. Per-user targeting is a routing hint, not enduring authority. Each notification callback performs new authorized reads; incoming previews, inbox, polls, threads and attachments independently recheck current grants/membership. Removed members stop targeted conversation updates and subsequent private fetches deny. Existing open panels refresh advisory grants on interactions, normal unread/presence refresh and callbacks. Access loss redacts conversation/thread/pinned/inbox/media/profile view models and hides the launcher. Unsent composer draft strings/uploads are retained. Existing ten-second unread/presence refresh is reused; no additional authorization-polling worker or forced logout was added. New operations always check the database version even before the display refresh occurs. The existing process-local publisher still requires a future distributed backplane for multi-instance delivery.

## Moderation and additive persistence

DeleteOthersMessageAsync is separate from own deletion. It accepts only another human's non-GIF Text in a live channel or its valid thread, with current channel membership and DeleteOthersMessages/Access. DM/group conversations, polls/system notices, own messages, missing/deleted targets, missing/oversized/invalid reasons and nonmembers (including Root) deny. Text attachments are retained in storage, but tombstone reads and file endpoints withhold their data.

Within one database transaction, the service locks the message FOR UPDATE and the moderator's membership FOR SHARE, verifies scope/state, records DeletedByUserId without changing SenderUserId, clears pins and appends ChatModerationHistory. It commits before publishing member-targeted ID-only updates. Concurrent deletions serialize; the unique message history index additionally prevents duplicate records. Own deletion preserves its existing disappearance behavior; moderation returns the existing deleted-message representation, preserving surrounding history while withholding body, attachment metadata/text, GIF IDs, tags and reactions.

The new `20261015184700_AddChatModerationHistory` migration creates only chat_moderation_history, its checks/indexes/restrictive FKs and triggers/functions. Fields: message/conversation/thread IDs, moderator/original-sender IDs, deletion timestamp, trimmed reason (1-1000), operation UUID and WasPinned snapshot. Composite FKs bind messages/threads to the recorded conversation. A deferred insertion trigger verifies the committed channel deletion's sender, moderator, timestamp, thread, membership and cleared-pin state. UPDATE, DELETE and TRUNCATE triggers enforce append-only history. No body or attachment bytes are copied into history or generic security logs. As with the existing security schema, a database owner can deliberately disable triggers: local owner-backed tests do not establish production privilege separation.

GetModerationHistoryAsync exposes only the current channel's latest 200 entries to current participants with moderation authority. A history-review UI is deferred; there is no global private-chat surveillance page. The existing message action menu supplies Delete Message only for eligible messages, and its confirmation requires a reason. Successful moderation reloads the normal tombstone and notifies current participants. Authorization denials refresh controls and are handled without tearing down the Blazor circuit.

## Scheduling and the Phase 3E-2 boundary

SendWithAttachmentsAsync checks ScheduleMessages whenever scheduledAtUtc is supplied. Future-start polls require CreatePolls, SendMessages and ScheduleMessages. Existing persistence, validation and close/expiry behavior remain. Phase 3E-2 adds self-owned scheduled cancellation and retained terminal delivery receipts; see its linked lifecycle record. There is no public queued-draft editing API. GetScheduledMessagesAsync now also requires current membership: removed members retain stored personal drafts but do not retrieve their content.

DeliverDueMessagesAsync is now internal, called only by the existing hosted worker and integration tests. A narrow security correction evaluates the sender of each locked persisted queue row using the existing effective-permission engine inside a Repeatable Read delivery transaction; it checks ScheduleMessages and its Access/SendMessages prerequisites, plus existing Active/membership/thread/conversation rules. It cannot borrow the worker's authority. Loss of authority uses the existing draft Failure state and preserves the draft. SKIP LOCKED delivery/dequeue atomicity is retained. This is **not** the full hosted-worker migration: explicit delegated capability provenance, lifecycle/expiry/retry/recovery policy, issuer credential-change semantics, generic background authority, broader PDF/delegated flows and multi-instance cutover remain Phase 3E-2/later work.

## Changed files

- Chat services: ChatService.cs plus ChannelActions, Groups, Gifs, GifCache, Inbox, Notifications, Polls and Scheduling partials; UserPresenceService.cs; new ChatService.Moderation.cs and ChatModeration.cs; ChatMapping.cs.
- HTTP/avatar: Program.cs and Features/Identity/UserProfilePicture.cs; the narrow stored-sender adapter in Features/Authorization/EffectivePermissionService.cs.
- UI: Components/Layout/ChatPanel.razor and its scoped CSS; ChatNewMessage, ChatInbox, ChatPollComposer, ChatPollCard, ChatGifPicker, ChatGifImage and ChatGroupIcon.
- Persistence: additive AddChatModerationHistory migration/designer and AppDbContextModelSnapshot. Existing query-filter/dependency fixes remain intact.
- Tests: new ChatAuthorizationTests.cs and ChatTestAuthorization.cs; ChatServiceTests, UserPresenceServiceTests, UserProfilePictureServiceTests, AuthorizationEndpointTests and the disposable database owner's trigger-reset list in PostgresTestDatabase.cs.
- Documentation: this implementation record and current-status links in README plus the preceding authorization records.

## Validation

- Final Release solution build: **zero warnings, zero errors** (`dotnet build Confast.sln --no-restore -c Release -m:1`). No dependency warning was suppressed. The isolated browser harness's initial restricted restore could not reach the NuGet audit feed; its audit was refreshed successfully with network access before verification.
- EF Core Release `has-pending-model-changes`: **no changes since the last migration**. The additive `20261015184700_AddChatModerationHistory` migration was reviewed and applied only by the disposable PostgreSQL fixture. No development/production schema or Root account was modified.
- Focused security/HTTP checkpoint: **39 passed, zero failed/skipped**, including the reflection-backed inventory of all 75 public ChatService methods (74 protected methods plus the self-leave exception), all 21 permission cases, membership, moderation, scheduling revocation and actual-cookie direct media requests. Focused avatar/HTTP checkpoint: **3 passed, zero failed/skipped**. These checkpoint counts overlap in the HTTP case and are not an aggregate count. Existing chat/presence regressions passed in the preceding focused checkpoint and are included in the final full run.
- Final full PostgreSQL suite: **833 passed, zero failed/skipped**, **5m05s**. This includes the final avatar exception test, two distinct concurrent moderators, and the worker's Repeatable Read permission snapshot correction. An earlier full checkpoint passed 832 before those final changes. All completed runs used only configured disposable confast_test, one runner at a time under its suite advisory lock.
- `git diff --check`: passed. Existing Phase 3D, dependency, administration, role and query-filter changes were preserved. Permission catalog, constants and initial-grant files were not changed.

Authenticated browser verification: **32 checkpoints passed, zero page errors and zero unhandled circuit terminations**, using the configured Development BrowserTestUser credentials through the actual login form. The isolated server used only confast_test and was stopped afterward. Verified at desktop **1440 x 1000 CSS pixels** and Chromium iPad emulation **834 x 1194 CSS pixels**, with mobile/touch settings; no physical iPad was used:

- Launcher absent without Access, subsequent grant making it available without forced logout, private nonmember channel hidden, read-only main/profile-popover composer/send controls, and independently enabled voting/reactions without SendMessages.
- Authorized send and own edit; moderation action/reason validation, committed tombstone with surrounding history, and exactly one immutable audit row per confirmed deletion.
- Moderation permission revoked after opening the dialog: confirmation denied, reason retained, controls refreshed, Cancel/close/reopen still responsive.
- CreateCategories versus ManageCategories controls remain independent.
- Access revoked while chat is open: launcher/history/media UI redacted; regrant restores membership-scoped read access and the unsent draft.
- A nested GIF favorites request after Access revocation immediately redacts the parent panel; GIF image/picker/group-icon callbacks and the nested group editor propagate denials without terminating the circuit.
- iPad read-only state, touch-opened message action, reason dialog, moderation/tombstone, and private membership removal clearing the channel/history on the normal refresh.

Browser testing found and fixed a real first-render presence-heartbeat denial that initially terminated a no-Access circuit. Initialization now waits for Access, and lifecycle/heartbeat denials refresh/redact safely. Later harness-only fixes synchronized asynchronous renders and accounted for unread badge/ARIA markup. A final review also connected nested GIF/image/group-icon denial callbacks and the group editor to parent redaction; the passing 32-check browser matrix includes this correction and read-only profile-popover controls. The final 833-test full suite passed on that exact compiled source, including the worker transaction isolation correction.

Proof logs, the real-login browser harness, browser-results.json and screenshots are in `C:\Users\vsche\.codex\visualizations\2026\10\15\01a140de-504f-7e93-a4ed-9c313a609832`. Key logs are release-build.log, chat-security.log, profile-http-focused.log and full-suite-final.log. The first sandboxed test-host attempt aborted before assertions because local IPC could not connect; the completed runs used normal local process access. No authentication bypass, employee account, development database migration or development Root mutation was used.
