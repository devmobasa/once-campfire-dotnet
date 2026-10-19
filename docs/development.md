# Development and testing

Use the SDK pinned in [global.json](../global.json). Initialize the browser assets and install the portable formatting preset locally:

```sh
git submodule update --init reference
cp eng/style.editorconfig .editorconfig
dotnet eng/check
```

The gate needs the pinned SDK, clang, the zlib development headers (for Native AOT) and a container engine. The gate runs these steps:

- locked restore
- Release build
- all tests except the media tests
- format verification
- Native AOT publish
- native HTTP, TLS and HTTP/2 smoke tests
- the media tests, inside the pinned Debian toolchain container

The media step uses Podman by default. If you use Docker, run `PODMAN=docker dotnet eng/check`. The options `--no-media`, `--no-aot` and `--no-format` skip steps. If you skip a step, report it together with the counts of discovered, passed, failed and skipped tests.

## Source layout

- `src/Campfire.Web`: direct Minimal API modules, native authentication/header-based forgery protection, HTTP/Cable transport, hosting and RazorSlices views.
- `src/Campfire.Application`: explicit SQL, SQLite leases, operations, rich text, storage and ordered after-commit work.
- `tests/`: application, web and full-pipeline functional/security tests.
- `reference/`, `vendor/assets/`: pinned original browser assets and their dependencies.
- `vectors/`, `reference-tools/`, `parity/`: synthetic/reference fixtures and browser comparison tooling.
- `eng/`, `bench/`: build, native smoke, media and validated benchmark tools.

`Program.cs` calls `MapCampfireEndpoints`. Each feature registers `MapGet`, `MapPost`, `MapDelete` or explicit GET/HEAD mappings in its own `*Endpoints.Routes.cs`. Numeric route/query values use native binding. JSON bodies use DTOs with source-generated metadata. Resource checks run explicitly in the owning handler; there is no controller/action registry or callback interpreter.

Form parsing belongs to ASP.NET Core. The small bracket-field adapter supports only the selected frontend's objects and flat arrays, retains separate form/query collections and overlays form values on query values for frontend operation inputs. Routing uses typed handler arguments and never enters that adapter. Arbitrary duplicate/conflicting fields return 400; the retained role checkbox's exact hidden/checked pair is accepted. `_method` is accepted only in POST forms, before routing, for DELETE/PATCH/PUT. Browser forgery protection checks the effective unsafe method centrally using Fetch Metadata and Origin. Frontend token tags remain empty; cached messages contain no hidden tokens. Real HTTPS tests cover missing/hostile metadata and authenticated writes.

Keep native AOT registration explicit. Dapper.AOT generates SQL binders/readers. Owned JSON uses source-generated metadata or explicit writers. Database connections and transactions belong to leases; release them before network/media work. Trim/AOT warnings are errors. The application uses ordinary awaits and one-line automatic property accessors.

Slice base classes escape owned values with Rails-compatible helpers and pass escaped content to RazorSlices as literal output. All three inheritance roots cover the dependency's write overloads. `HtmlEncoder.Default` protects fallback paths. Arbitrary custom `IHtmlContent` can overescape through that fallback. A contract test detects new application content implementations. The web project requires no unsafe code.

SQLite memory mapping is disabled (`mmap_size=0`); connections use SQLite's bounded page cache.

Rendered HTML records immutable UTF-8 fragment ranges. Gzip piece keys use once-computed fragment digests and ordered predecessor identities/glue within the current fragment run's 32 KiB history. Text before the run is used only by its first fragment; that stored piece retains and compares the exact dictionary suffix, excluding glue, and charges it to the fragment store. A fixed, approximately 1 MiB doorkeeper hashes complete keys and admits pieces or whole members on their second sighting. Text and member lookups use XxHash128 with an exact plaintext check. One-off pieces use the actual response history, continuing contiguous compression or priming at most 8 KiB, and are not retained. If one-off bytes plus their priming exceed half the page, the response compresses whole and stores no payloads; its keys still enter the doorkeeper so reusable pages can warm. Admitted pieces always use exact keyed history.

Fragment, text and repeated-body gzip stores have charged limits of 32, 16 and 16 MiB, including index reserves; active responses, encoder workspace and runtime heap retention contribute separately to process memory. The `Campfire.Gzip` meter exposes store/doorkeeper charges and turnover, primed/compressed bytes, and hit/admit/one-off counts labeled by bounded route patterns. Member one-offs are reported separately; guards count only spliced-page whole-compression fallbacks. HEAD/304 handling precedes compression, and identity clients receive the plain pooled buffer.

## Publishing

The Docker build defaults to JIT. Select `--target aot` for Native AOT. Native AOT selects `OptimizationPreference=Speed` and keeps the default instruction-set baseline. The native linker requires clang and zlib development headers. Both variants use the pinned media libraries, ICU and the same browser assets:

```sh
dotnet restore src/Campfire.Web --locked-mode -p:PublishReadyToRun=true
dotnet publish src/Campfire.Web -c Release -r linux-x64 --no-restore -o artifacts/aot
dotnet publish src/Campfire.Web -c Release -r linux-x64 --no-restore --self-contained false \
  -p:PublishAot=false -p:PublishReadyToRun=true -p:UseAppHost=false -o artifacts/jit
```

## Browser and functional comparison

Use disposable synthetic databases. The reference checks application behavior and the frontend. Imported bcrypt passwords and configured Rails avatar URLs work without rewriting those credentials. Other Rails cookies and signed URLs remain unsupported. The native comparison helper rewrites synthetic passwords and protected identifiers in its own seed copy. It leaves the reference copy unchanged. Match logical rows, membership, messages, blobs, timestamps and explicit index settings.

These commands use `docker`. If the machine has only rootless Podman, first put the shim on `PATH` with `export PATH="$PWD/eng/docker-shim:$PATH"`. The shim sends each command to Podman.

```sh
export PARITY_SEED_DIR="$HOME/.cache/campfire-parity/seeds"
export PARITY_OUT_DIR="$HOME/.cache/campfire-parity/results"
parity/bin/reference build
parity/bin/seed build
PARITY_CANDIDATE_APP_IMAGE=campfire-dotnet:app \
  PARITY_CANDIDATE_IMAGE=campfire-candidate-dotnet parity/bin/candidate build
PARITY_CANDIDATE_IMAGE=campfire-candidate-dotnet parity/bin/candidate compare \
  --seed default --out "$PARITY_OUT_DIR/dotnet-default"
```

Run the five seeds (`default`, `first_run`, `custom_styles`, `restricted`, `crowd`) for the selected builds, browsers and viewports. Mutating cases require fresh isolated instances. Inspect actual DOM, styles, console/page errors, network requests and Cable deliveries. Native cookie/token/error differences are expected reports, not permission to suppress unrelated failures or declare strict byte parity. Keep raw reports. Security tests must independently verify tampering, expiry, origin, revocation, permissions, method overrides and bot credential separation.

The readiness lifecycle regression uses real browser clocks, custom-element registration and a pending fetch in all three engines. Run it in the same pinned Playwright image used by capture; it is separate from the host-only `npm test` suite:

```sh
playwright_image="campfire-parity-playwright:$(cat parity/Dockerfile.playwright parity/package-lock.json | sha256sum | cut -c1-12)"
docker build -f parity/Dockerfile.playwright -t "$playwright_image" parity
docker run --rm --init --network none --ipc host --user "$(id -u):$(id -g)" \
  -e HOME=/tmp -e TZ=UTC -v "$PWD:$PWD:ro" --tmpfs "$PWD/parity/node_modules" \
  -w "$PWD/parity" "$playwright_image" node --test capture/test/readiness.browser.ts
```

Run readiness lifecycle and application browser gates against the same capture inputs. Clock ticks can release deferred registration only after network and DOM settling; missing definitions remain a readiness timeout.

Native comparisons use an explicit contract profile, independent of the shared `session_token` cookie name. Capture uses `--native-target`. Paired runs use `--expected-native-target` or `--actual-native-target`. The candidate wrapper selects the actual native target by default. Retained comparisons use `--native-contract`. Copy retained captures into a new output directory before recompare to preserve original reports and diff images:

```sh
node parity/capture/cli.ts recompare --out /path/to/new-capture-copy \
  --expected-name reference --actual-name candidate --native-contract
```

The HTML rules cover cached boost-form hidden CSRF omission and the fixed synthetic account form's `/account` action. Other fields, routes, methods and body differences remain visible. The two retired incompatible-browser screens require the native page to match an independent same-cell native sign-in capture, including status, HTML, live DOM, ARIA, pixels, network and Cable. Their named retirement allowance never establishes document-pair semantic equality. Network rules require raw `.network.contracts.json` receipts where header values or cookie lifetimes matter; missing required evidence remains a failed comparison even if the text is equal. Navigation body hashes can differ only when the final document's server HTML and live DOM independently compare equal. Intermediate documents and nonnavigation bodies remain checked.

The candidate helper defaults to native security preparation. Set `PARITY_CANDIDATE_NATIVE_SECURITY=0` when wrapping Rust or an older non-native-security image. Keys are mounted with the disposable candidate's database/files. `prepare-fixture` requires explicit synthetic-fixture opt-in and a fixture password; it is not a migration command. No public upload-creation or raw-upload PUT endpoint is available. Existing download paths and multipart message/profile uploads remain.

See [frontend contracts](compatibility.md), [deployment](deployment.md), [benchmark controls](benchmarks.md) and [provenance](../PROVENANCE.md).
