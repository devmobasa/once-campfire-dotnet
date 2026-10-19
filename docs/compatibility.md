# Frontend and security contracts

This ASP.NET Core performance rewrite reuses the pinned Campfire frontend and selected Rails behavior. It verifies Rails bcrypt password digests. It accepts Rails avatar URLs when `SECRET_KEY_BASE` matches the original installation. Native protection owns browser sessions and all other signed URLs. Existing Rails cookies and other signed URLs do not work. The port does not guarantee database rollback interoperability.

| Boundary | Current behavior |
|---|---|
| Routing | Direct Minimal API modules, explicit GET/HEAD mappings, typed numeric route/query inputs |
| Wrong method | Uniform 405 with the framework's Allow header, for anonymous and authenticated callers |
| Wrong JSON content type | Framework 415 retained; never rewritten as 405 or an authentication challenge |
| Forms | Native parsing and spooling; bounded bracket adapter for owned frontend fields; duplicate/conflicting fields return 400 |
| JSON | Explicit DTOs with source-generated metadata; malformed JSON returns 400 |
| Body limits | 16 MiB non-form, 32 MiB multipart; oversized requests return 413 |
| Browser authentication | The `session_token` cookie carries a native protected session ID and random row token. Ordinary requests read the current user and session with one SQLite join. Page-cache hits reuse authority until the database changes. Every hit checks credentials, status, expiry and activity rules. Rails-format cookies act as signed-out requests. |
| Sign-in challenge | A protected room redirects to `/session/new?returnUrl=...`; the sign-in form preserves that destination. This challenge's 302 omits Rails' `Cache-Control: no-cache` and HTML Content-Type headers |
| Cookie lifetime | Absolute 30 days, non-sliding; no per-response authentication-cookie rewrite |
| Cable admission | Validate the protected browser ticket and current SQLite authority before and after revocation subscription. The socket bypasses browser authentication/authorization, form/CSRF and session-activity middleware, preserving origin checks, proxy trust and CONNECT IP bans. No browser ticket, principal or request DI scope is kept for the socket lifetime; subscription parameters own plain values |
| Passwords | New and changed passwords use Identity V3. Sign-in verifies Rails bcrypt digests without rewriting them. Missing-user checks use a cached synthetic digest. |
| Browser forgery protection | Unsafe requests require `Sec-Fetch-Site: same-origin`; supplied Origin must match the trusted effective scheme, host and port. Reject same-site, cross-site, none and Origin null with 422. Missing metadata is allowed only on HTTP without forced SSL. Bot-key routes and Cable retain their independent credential checks |
| Frontend token tags | Meta and uncached hidden form fields remain with empty values; cached fragments and broadcasts omit hidden fields. No antiforgery cookie or token service |
| Fragment cache | UTF-8 markup with record-version dependencies, scoped by committed database generation and rendered origin |
| Protected identifiers | Native URLs use purpose-bound Data Protection. Avatars also accept Rails 8.2 User avatar signed IDs when `SECRET_KEY_BASE` matches. Invalid and expired identifiers cannot grant access. Other Rails signed URLs do not work. |
| Browser bot isolation | Bot routes require their independent bot key; a browser cookie is insufficient |
| Proxy trust | Explicit opt-in IP/CIDR list; unlisted peers cannot supply client IP, host or scheme |
| Errors | Styled static HTML 404/500 pages with a home link, or ProblemDetails for JSON; no error-path rerouting or private exception data |
| Compression | Level-6 gzip for selected textual MIME types, including HTTPS. Recorded HTML fragments reuse compressed pieces; their wire bytes can differ from whole-page gzip while decoding to identical HTML. Accepted WebSocket tunnels are exempt |
| Uploads | Multipart message/profile uploads and signed downloads remain; direct-upload creation and raw disk PUT are retired |
| Browser support | No legacy AllowBrowser gate; test the frontend on the selected browsers |
| Framework pages | Turbo Native historical-navigation pages remain; unused mailbox/conductor and room-settings routes are unregistered and return safe 404s |
| Message index | Optional idempotent `(room_id, created_at)` index; matched index state is required for performance comparisons |
| Message creation failures | Message, body, attachment metadata, touches, search indexing and unread updates commit atomically, as in current Rails. FTS or unread SQL errors roll back the transaction and emit no push, broadcast or webhook. Disk uploads run after commit. Upload failures retain the committed rows and stop later callback effects. |

## Deletion recovery

The port adds three SQLite tables for resumable cleanup:

- `campfire_room_deletions` records room deletion progress.
- `campfire_room_deletion_members` retains the former members who need connection resets and room-list updates.
- `campfire_cleanup` retains pending search-index, blob and file cleanup work.

Rails ignores these tables.
Include them in database backups and restore them with the application tables.
Restore the database and blob files from the same consistent backup.
Keep pending cleanup records until the application finishes their work.

Room deletion first closes an open room and removes its memberships in one transaction.
Bounded background work then removes its messages and room.
An interruption resumes the recorded phase after restart.
Connection resets and room-list updates can repeat during recovery.
The cleanup preserves search-index removal before dependent blob purges.
File cleanup retries missing files safely and retains failed work for another attempt.
Deletion blocks room updates and membership grants inside their write transactions.
Current Rails has no durable deletion guard, but both implementations remove dependent memberships when the room is destroyed.
Cleanup failures back off independently, so one failed item cannot prevent other room deletions.
Cleanup records retain their original creation time for pending-age reporting.
Debug cleanup logs report pending counts, oldest pending age and oldest due age for both durable queues.
Error logs identify each failed cleanup record or room.
Earlier development databases with a different cleanup-table shape fail at startup and require recreation.

## Password verification work

The first missing-user attempt observes the database's highest valid bcrypt work factor, capped at 12, and creates a synthetic digest. Later attempts reuse it without scanning users. Databases without bcrypt use Identity V3 dummy work.

Known users retain their stored algorithm and cost. Mixed algorithms and different costs still produce timing differences. Reopen the application database after changing its credential formats to refresh the dummy-work policy.

Rejected bcrypt input still performs verification work before it fails. Byte-72 truncation and NUL rejection match Rails.

## Deliberate differences and bounds

These decisions describe the current implementation and the expectations used by its tests. Limits bound resource use; they do not imply interoperability with existing Rails installations.

| Area | Decision and observable behavior |
|---|---|
| Autolink attribute breakout | Escape `<` and `>` inside sanitized attribute values before autolinking. A URL in an attribute cannot turn the rest of that attribute into executable markup. Rails' raw serialization can permit this stored XSS. |
| Name DOM clobbering | Remove rich-text `name` attributes, including `img name="body"`, which can shadow DOM globals. The composer does not need them. |
| Style scrubbing | Keep only plain `color` and `background-color` values used for Lexxy highlights. Other CSS declarations and complex values are removed; message autolinking removes style altogether. |
| Nested attachments and rich text | Render content attachments at most eight levels deep; deeper content renders empty. Bound each rendered body to 1,048,576 UTF-16 code units; longer input follows the existing parse-error path. HTML parsing retains limits of 400 tree levels, 400 attributes per element and 16,777,216 UTF-16 input units. |
| Request bodies | Cap non-form bodies at 16 MiB and multipart at 32 MiB. An oversized body returns 413 without trying to parse it again while rendering the error. |
| Page arithmetic | Saturate page numbers, offsets and next-page arithmetic at `long.MaxValue` instead of wrapping. Excessively large pages return no rows. |
| Cable resource bounds | Defaults: 64 subscriptions per connection, 4 KiB identifiers, 1 MiB incoming messages, and at most 256 queued frames or 4 MiB beyond the current write. Excess subscriptions are ignored; oversize messages close with 1009. Slow consumers disconnect with reconnect requested. Writes time out at 30 seconds; close waits at most five seconds. Commands remain ordered and subscription cleanup runs on disconnect. |
| Cable authority | Recheck session/user authority after subscribing to revocation notifications, closing the admission race for bans, sign-out and deactivation. Compression uses no context takeover and is bounded by the same queue and deadline rules. |
| Public origin and trusted proxies | Shared markup uses `CAMPFIRE_PUBLIC_ORIGIN`, with the first `TLS_DOMAIN` as its HTTPS fallback. An explicit origin supports HTTP and custom ports. Forged request Hosts cannot replace either configured origin. Without configuration, request-derived URLs retain the documented HTTP behavior. Forwarded client IP, scheme and host are honored only for explicitly trusted peers. |
| Opengraph unfurling | Recheck and pin permitted public destinations at each redirect. Cap an unfurl at 10 seconds total, five seconds per connect/read, 16 concurrent unfurls and four concurrent parses. Fetch at most 10 responses and 5 MB per page; retain at most 256 attributes per meta tag. Deadline exhaustion returns 204. |
| Webhooks | Administrator-configured destinations can address internal services and use the environment proxy. Keep the seven-second connect/read timeout, with a 60-second whole-delivery deadline and a 100 MiB decompressed reply cap. Timeout produces the usual text reply; an oversized reply fails delivery. |
| Web Push | Restrict destinations to permitted public HTTPS push services. Limit connect/read to 10 seconds and the entire delivery to 30 seconds. Deadline exhaustion keeps the subscription through the existing timeout handling. |
| VAPID boot checks | The app does not validate VAPID keys at boot, as in Rails. If a key is missing, the app generates a new fallback key, and push services reject its signatures. An invalid P-256 key fails at delivery. For push notifications, set a stable `VAPID_PUBLIC_KEY` and `VAPID_PRIVATE_KEY`: a matching P-256 key pair, encoded as base64url. The port keeps this limitation from Rails. |
| Push failure handling | The app deletes subscriptions only after push-service 404 or 410 responses. It retains subscriptions and logs local key, configuration and TLS errors. Rails also deletes subscriptions on OpenSSL errors. A server misconfiguration must not erase users' subscriptions. |
| Conditional requests | Cache hits, misses, static files and downloads share RFC 9110 conditional checks. ETag lists, wildcards and weak comparisons work consistently. If-None-Match takes precedence over If-Modified-Since. Validators run before range selection. A matching condition returns 304 even when the request includes Range. Rack uses exact matching instead. |
| Front-cache freshness | The cache measures response age from real header-start time and any valid upstream Age. Slow fills consume freshness. Hits emit Age without renewing freshness. Date replacement remains independent of the business clock. |
| Unread notifications | Shared rooms preserve their first unread timestamp. Direct rooms touch each eligible recipient. One worker sends timestamped notices after commit, reading at most 128 eligible memberships per page. It checks cancellation between pages and deliveries. Read timestamps prevent delayed notices from restoring stale unread state. |
| Search NOT behavior | Search quotes each word as a literal FTS5 term, including `NOT` and `AND`. Reachable search selects the newest 100 matches by ID and renders them in ascending ID order, matching current Rails. |
| Jobs and cache storage | Job queues, outbound worker concurrency and cache budgets are bounded and process-local. Full-queue behavior is defined per job kind; queued best-effort work is lost on process exit. Fragments use an in-process 32 MiB store rather than Redis. The page cache defaults to 64 MiB; its viewer isolation, generation invalidation and expiry checks also apply with caching disabled. |
| Rendering validators and message writes | Page validators describe rendered content rather than record timestamps alone. Message creation rolls back on FTS or unread SQL failure and emits no external effects. The optional `(room_id, created_at)` index is an additional schema index, created idempotently. |

Keep sanitization, attribute escaping, DOM-clobbering protection, attachment-depth limits, origin checks, resource permissions and current member names. External message effects run after commit. In-process jobs are bounded best-effort work. Limits and caches are process-local.

Empty frontend tags leave layout bytes stable. Recorded page parts contribute their ordered content digests to a weak SHA-256 validator; bodies without recorded fragments retain a whole-body digest. These values differ from Rails' whole-body ETags. Messages also validate rendered content, not record timestamps: an unchanged timestamp cannot validate edited content, and If-Modified-Since alone returns 200. Gzip, identity and HEAD share the content validator. HEAD/304/aborted responses retain pooled-buffer ownership and disposal. Protocol-specific Cable commands and JSON shapes remain part of the frontend contract; authentication formats do not.

A separate read-only SQLite observer captures committed database changes before authentication on fragment-rendering routes, and before detached rendering. Endpoints such as `/up` skip the observation. Fragment namespaces retain that captured generation. The store keeps the newest four generations within a shared 32 MiB budget, dropping the oldest first under pressure. Retained generations accept late renders into their own namespace; dropped generations reject stores. Observer failure disables fragment reuse. Only retained entries are charged to the bounded store.

Paged messages with validators use `max-age=0, private, must-revalidate`. A message posted after its room disappears returns the composer's deleted-room page with 200, without creating a message or refreshing session activity.

Protected public URLs reuse their token while present in the bounded in-memory cache. Key-ring persistence keeps previously issued URLs valid after restart; cache eviction or restart can change newly generated URL strings. Fragment keys use record versions, not randomized protected URLs. Native identifiers never resolve through an unsigned fallback.

Normal avatar URLs have no token expiry. An explicitly expiring avatar URL is private, with a cache lifetime capped at its remaining validity; the shared front cache cannot retain it, including conditional responses.

For public cacheable responses, the front cache can reuse an uncompressed representation for clients that accept gzip. It honors explicit identity rejection, including `identity;q=0` and applicable `*;q=0`, and preserves the other `Vary` fields. Cached bytes retain their actual content encoding.

Authenticated room, messages, sidebar and search responses have a separate bounded, viewer-specific cache. It retains stable body headers only; cookies and request IDs are produced live. SQLite generation changes invalidate entries even when record timestamps are unchanged. Incoming signed rich-text attachments constrain both fragment and page lifetimes, so expiry does not require a database edit. Disabling the page cache preserves these freshness rules.

The early page-hit branch reuses positive session/user authority and room-access reads only within the freshly observed SQLite generation. Its shared 2 MiB budget includes conservative per-entry metadata charges, and a generation advance swaps out the old table. Cached values contain immutable identifiers, role/status, stored token and last-activity time; every hit still compares the ticket token in constant time and checks active/non-bot status and whether activity maintenance is due. Cold lookups and all rejections continue through ordinary authentication.

A separate 4 MiB LRU memo holds only the plaintext credential and expiry for an exact reassembled browser cookie, never the ticket or claims graph. Expiry is checked on each request. Data Protection notifications, a changed framework key-ring instance or a changed ticket format clear the memo; failed decrypts are never stored. The ring follows the framework's refresh semantics, including asynchronous refresh. Disabling the page cache bypasses both authentication caches on the early branch.

Admitted GET misses for the same viewer key and database generation share one render. At most 32 requests wait on a render, for at most 250 ms; excess or timed-out requests render independently. A failed or cancelled leader releases waiters to retry. Superseded renders can finish for their existing waiters but cannot enter the newest cache generation. HEAD misses retain their independent rendering behavior, and cache 0 disables coalescing.

Compare actual frontend behavior and equivalent work, and report differences explicitly. Do not normalize away missing messages, invalid authorization, skipped browser actions or incomplete Cable delivery. See [testing](development.md) and [benchmarks](benchmarks.md).
