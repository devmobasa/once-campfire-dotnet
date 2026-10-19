# JSON vectors

These files contain the Rails reference's JSON output bytes for the .NET JSON writer and its endpoints.
Each `<name>.json` holds the bytes that Rails produced.
`index.json` records each source, request, response status, headers, size, SHA-256 and generated record.

## Generator

[`reference-tools/json/generate.rb`](../../reference-tools/json/generate.rb), run in the reference image in production mode with `parity/.env.reference`, on a throwaway copy of the default seed:

```sh
PATH="$PWD/eng/docker-shim:$PATH" PARITY_SEED_DIR=... \
  parity/bin/reference runner --seed default reference-tools/json/generate.rb
```

It writes into `vectors/json/` (override with `-e JSON_VECTORS_OUT=/work/<dir>`). Two runs give byte-identical files.

What it does, at a clock frozen at 2026-03-02 16:00:00 UTC (the seed clock) with `travel_to`:

1. Renames the account to `Signal & <Noise> "HQ" ’ U+2028 😀` and exercises `accounts.settings`.
2. Creates a user whose name and bio are the hostile string `HOSTILE` (below, without its tab and newline in the name), a bot `Böt <"&">` with the fixed token `JsonVectorBot1`, and a closed room `Edge <cases> & “quotes”` with David, Kevin, both of them, and 38 older filler messages (so the bot index spans more than one 40-message page).
3. Posts three messages (plain, `HOSTILE` escaped as HTML, and rich text with a Kevin mention, bold, italic and a link), an image (`moon.jpg` as `moon ☾ <1>.jpg`), and a boost `🔥 <3 & "x"`.
4. Sends requests in process through the whole Rails middleware stack (no `config.ru` `Rack::Deflater`, no Thruster), host `campfire.test`, Chrome user agent. Browser requests sign in as David through the real form.

`HOSTILE` is `<b>Zoë</b> & "Ünïcødé" 'quotes' ` + U+2028 + `line` + U+2029 + `para 😀 tab` + TAB + `newline` + LF + ` bs\ slash/ </script>`.

The unfurl fetch is stubbed inside the process: the private-network guard resolves every host to a public address, `Opengraph::Fetch#fetch_document` returns a fixed page with `og:` tags built from `HOSTILE`, and `fetch_content_type` answers `image/png`. Everything after the fetch (parsing, sanitizing, validation, `render json:`) is the app's own code.

## Vectors

| File | Source |
|---|---|
| `by_bots_index.json` | `GET /rooms/:room_id/:bot_key/messages`: `messages/by_bots/index.json.jbuilder` → `messages/_message` → `users/_user`, with `X-Total-Count` and the `Link` header |
| `by_bots_index_before.json` | The same with `?before=`: the previous page, no `Link` |
| `by_bots_update.json` | `PUT …/messages/:id` with a raw `HOSTILE` body: `MessagesController#update` → `format.json { render :show }` → `messages/by_bots/show.json.jbuilder` |
| `boosts_by_bots_create.json` | `POST …/messages/:id/boosts` (201): `messages/boosts/by_bots/show.json.jbuilder` → `messages/boosts/_boost` → `users/_user` |
| `autocompletable_users.json`, `…_query.json`, `…_room.json` | `GET /autocompletable/users.json`, unfiltered, `?query=Zo`, and `?room_id=`: `autocompletable/users/index.json.jbuilder` → `_user` |
| `unfurl_link.json` | `POST /unfurl_link`: `render json: opengraph` in `UnfurlLinksController` |
| `webmanifest.json` | `GET /webmanifest`: `pwa/manifest.json.erb` (ERB, not a JSON encoder) |
| `column_accounts_settings.json` | The raw `accounts.settings` text as seeded, after `settings.restrict_room_creation_to_administrators = true`, after `update!(settings: { …: "0" })`, and as `Account.create!` writes it untouched (rolled back) |
| `column_blob_metadata_*.json` | The raw `active_storage_blobs.metadata` of the first seeded blob of each content type (`moon.jpg`, `moon.webp`, `alpha-centuri.mov`, `launch-notes.txt`, `pixel.bmp`), and of the image uploaded here after analysis |
| `webhook_payload.json` | `Webhook#payload` for the mention message (`Hash#to_json`, so ActiveSupport escaping) |
| `web_push_payload.json` | `WebPush::Notification#encoded_message` for the `HOSTILE` message (`JSON.generate`, the json gem: **no** HTML escaping) |
| `cable_broadcasts.json` | Every string Action Cable published for `broadcast_create` of the image message (the Turbo stream append and four `{roomId}` unread notifications), a typing `start` from Zoë, and a `{room_id}` read notification, recorded at the pubsub adapter, with the frame a subscriber receives: `ActiveSupport::JSON.encode({ "identifier" => <the subscription identifier string>, "message" => ActiveSupport::JSON.decode(payload) })`, as `ActionCable::Channel::Streams` and the connection do |
| `activesupport_encode.json` | `ActiveSupport::JSON.encode` of HTML specials, the separators, non-ASCII, astral characters, all C0 controls and DEL, backslash and slash, a 2^64 integer, floats (including -0.0, 1e15, 1e16, 1e20, 1.0e-5, 1.5e-7, 2^70), NaN and the infinities, BigDecimals, times (UTC, microseconds, offsets, `TimeWithZone` in UTC and Berlin, `Date`, `DateTime`, a `Duration`), symbols, `nil` and booleans, and nesting. The keys name the inputs; `generate.rb` has their exact Ruby values |
| `to_json_vs_json_generate.json` | `HOSTILE` through `Object#to_json`, `Hash#to_json`, `JSON.generate` and `JSON.dump`, side by side |

## What the bytes show

- ActiveSupport escaping (`escape_html_entities_in_json: true`): `<`, `>` and `&` become `<`, `>` and `&`. U+2028 and U+2029 stay raw (`escape_js_separators_in_json: false`). `"`, `\`, and C0 controls use the short escapes (`\b \t \n \f \r`) or `\u00XX`; DEL, `/`, `'` and all non-ASCII stay raw. This applies to the Jbuilder views, `Hash#to_json` (webhooks), and Action Cable payloads and frames.
- `render json:` does not escape HTML (`escape_json_responses: false`): `unfurl_link.json` has a raw `&amp;` and raw separators.
- `unfurl_link.json` also contains `"context_for_validation":{"context":null},"errors":{}`: `Opengraph::Metadata` has no `as_json`, so `Object#as_json` serializes every instance variable, including the ones `valid?` created.
- `JSON.generate` (Web Push) escapes neither HTML nor the separators.
- `webmanifest.json` is ERB: the account name is HTML-escaped (`&amp;`, `&lt;`, `&quot;`) inside a JSON string, and the icon URL's `&` is `&amp;`.
- The autocomplete `name` is `h(user.name)`, so it is HTML-escaped and then JSON-escaped (`&lt;b&gt;`).
- Times are ISO 8601 with 3 fractional digits and `Z` for UTC (`2026-03-02T16:00:00.000Z`), or the offset for other zones. `Float` uses Ruby's shortest round-trip form, but ActiveSupport prints `1.5e-7` as `0.00000015` and `1.0e-5` as `0.00001`, while `1e15` and larger keep the exponent (`1e+15`). BigDecimal is a string. NaN and infinities are `null`. A `Duration` is its seconds.
- Stored columns: `accounts.settings` is `{"restrict_room_creation_to_administrators":false}` from creation on (Rails 8.2 `has_json`; unknown keys are rejected and `"0"` casts to `false`). Blob metadata keeps Ruby's key order and types, so a video's `width` is `320.0` and `display_aspect_ratio` an array.
