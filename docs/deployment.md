# Deployment

The default Docker image runs JIT on the pinned ASP.NET Core runtime. JIT is faster on the measured page and posting workloads. The `aot` target uses less memory, starts faster and needs no .NET runtime. Both builds use the same source and Kestrel public HTTP/TLS listeners. Validate each artifact on its deployment architecture.

Run one application process. Persist `/rails/storage`, which holds the database, uploaded files and native Data Protection keys in `keys/`. Losing this key ring invalidates native cookies and protected URLs. Real-time broadcasts and connection revocation stay inside one process. Shared storage and keys do not coordinate replicas. Rails bcrypt passwords support sign-in without rewriting stored digests. Set the original `SECRET_KEY_BASE` to keep Rails avatar URLs working. Other Rails cookies and signed URLs do not work.

## HTTPS and certificates

The `/up` health page remains available over HTTP without authentication or a database query.

Set `TLS_DOMAIN` to your domain, expose ports 80 and 443 and point DNS at the server. Multiple names may be comma-separated; the first determines canonical shared URLs. Kestrel negotiates HTTP/1.1 and HTTP/2. Port 80 serves HTTP-01 challenges and redirects configured hosts to HTTPS.

Certificates and the ACME account key are stored in the autocert-compatible `storage/thruster` directory. Existing valid PEM files can be reused. Renewal retains the current usable certificate on failure; issuance failures use bounded backoff. A valid manually installed PEM is read when the current certificate is absent or expired, including during backoff. Protect the certificate directory and include it in persistent storage. Local synthetic-CA validation is separate from real public-CA deployment validation.

## Configuration

Front settings check `THRUSTER_`-prefixed names first, then the unprefixed name. An empty prefixed value wins. Application-specific `CAMPFIRE_` settings have no such alias.

| Setting | Default | Purpose |
|---|---|---|
| `HTTP_PORT` / `HTTPS_PORT` | 80 / 443 | Public listeners; TLS requires configured domains |
| `TARGET_PORT` | 3000 | Direct plain-HTTP listener; keep it private |
| `TLS_DOMAIN` | empty | Comma-separated certificate names and canonical origin |
| `CAMPFIRE_PUBLIC_ORIGIN` | unset | Explicit HTTP or HTTPS origin for shared URLs, including custom ports. It overrides the TLS-derived origin without changing listeners or certificate settings. |
| `CAMPFIRE_STORAGE_PATH` | `storage` under the content root (`/rails/storage` in Docker) | Database, uploaded files and Data Protection keys |
| `SECRET_KEY_BASE` | unset | Original Rails secret for imported avatar URLs only. Native sessions and other protected URLs use the persisted Data Protection keys. |
| `STORAGE_PATH` | `./storage/thruster` | Certificate storage, separate from `CAMPFIRE_STORAGE_PATH` |
| `ACME_DIRECTORY` | Let's Encrypt production | ACME directory URL |
| `EAB_KID` / `EAB_HMAC_KEY` | empty | External account binding; key is base64url |
| `CACHE_SIZE` / `MAX_CACHE_ITEM_SIZE` | 67108864 / 1048576 (64 MiB / 1 MiB) | Public response-cache and item limits, in bytes |
| `CAMPFIRE_RESPONSE_CACHE_MB` | 64 | Authenticated page-cache budget in MiB; `0` disables it |
| `MAX_REQUEST_BODY` | 0 | Wire-body limit in bytes; 0 disables this additional bound |
| `HTTP_IDLE_TIMEOUT` | 60 seconds | Idle connection bound; 0 disables it |
| `HTTP_READ_TIMEOUT` | 300 seconds in Docker, 30 seconds otherwise | Header and body read limit. A value of 0 disables it. |
| `HTTP_WRITE_TIMEOUT` | 300 seconds in Docker, 30 seconds otherwise | Deadline for an ordinary HTTP response. A value of 0 disables it. |
| `LOG_REQUESTS` | true | Method/status request logging |
| `H2C_ENABLED` | false | HTTP/2 prior-knowledge listener without TLS |
| `CAMPFIRE_TRUSTED_PROXIES` | empty | Trusted IP addresses or CIDR networks |
| `CAMPFIRE_MESSAGE_INDEX` | true | Idempotent creation of the message room/time index |
| `CAMPFIRE_MESSAGE_UPDATED_INDEX` | false | Optional non-unique index on `messages (room_id, updated_at)` for reconnect queries. Boot validates its shape. Disabling creation never drops an existing index. |
| `DISABLE_SSL` | unset | Any value that is not blank disables HTTPS enforcement, also `0` and `false` (as in Rails). To enforce HTTPS, leave it unset or blank |

Without TLS, enabling H2C makes the public HTTP listener HTTP/2-only; it does not support an HTTP/1 upgrade to h2c. The target listener remains HTTP/1. With TLS, port 80 remains HTTP/1 for redirects/challenges even when H2C is enabled. Cable supports HTTP/1 Upgrade and HTTP/2 extended CONNECT; accepted tunnels are never gzip-compressed.

The public response cache retains explicitly public responses with positive cache lifetimes, within its byte limits. Private/no-store responses, ranges and upgrades bypass retention. Application fragment caching remains separate.

The authenticated page cache covers room, messages, sidebar and search reads. Hits validate the protected browser ticket, current session authority and room access before serving an entry. Misses, rejected credentials, flash and due session activity continue through the normal authentication and endpoint pipeline. Entries are isolated by viewer, session and render inputs, invalidated by observed SQLite commits, and expire within 15 seconds or sooner for expiring content. Flash, changed credentials and non-successful responses bypass storage. HEAD can reuse an entry without writing a body; matching validators return 304. The budget charges independently owned encoding variants and their metadata. Restart clears all caches. Cache counters and peak charged bytes are available through the `Campfire.ResponseCache` meter and a shutdown summary.

With Native AOT and Server GC, filling the page cache can raise peak process memory by about 2–2.5 times its charged bytes. The budget bounds live cache-owned data; the collector also retains committed memory after replaced entries are collected. Allow for that process-memory overhead when setting container limits.

For idle Cable connections, enabling WebSocket compression adds about 1.6 KB of live managed data per connection and about 4–8 KB of process memory per connection. The process-memory cost includes retained transport pools and committed GC memory; it is not all live connection-owned data. Compression reduces fan-out CPU and wire traffic, so allow for this idle overhead when sizing a deployment.

## Reverse proxies

An unlisted TCP peer cannot choose the client IP using forwarded headers. For a configured trusted peer, the platform consumes the forwarded chain using the transport peer as its trust anchor; application rate limits and bans use the resulting client address. Configure every trusted hop, restrict access to those proxies and require each proxy to append its actual peer.

Forwarded Host/Proto use the same trusted-peer boundary, including the direct target listener. The empty default trusts no peer. Keep the direct target listener private. Restrict valid proxy hosts and require proxies to discard spoofed incoming metadata.

A TLS-terminating proxy must appear in `CAMPFIRE_TRUSTED_PROXIES`. Otherwise its forwarded HTTPS scheme is discarded and HTTPS enforcement repeatedly redirects the plain-HTTP backend request. `ASPNETCORE_FORWARDEDHEADERS_ENABLED` cannot enable automatic trust: the app disables that platform startup filter and uses its explicit trust boundary.

## Storage and limitations

The optional non-unique `messages (room_id, created_at)` index is created idempotently at boot, with Information logs during creation. First boot on a large database can take time. `CAMPFIRE_MESSAGE_INDEX=false` prevents creation on an unindexed database; it never drops an existing index. Match the index state explicitly in comparisons; disabling creation does not drop an existing index.

- The SDK and runtime are pinned to .NET 11 RC2. [global.json](../global.json) and the [Dockerfile](../Dockerfile) give the exact versions. Make sure that the deployment works in your environment.
- Coordinate shutdown or a consistent SQLite backup with the database, WAL, blob files and key ring. No ONCE backup/restore hook is provided.
- Jobs and sign-in limits are local process state. Queued work is best-effort and can be lost on restart; there is no durable/distributed job or rate-limit coordination.
- Sentry configuration is accepted, but telemetry integration is not implemented.
- HTTP multi-range requests return the complete resource instead of multipart ranges.

See [compatibility](compatibility.md) for intentional differences and [testing](development.md) for artifact-specific verification.
