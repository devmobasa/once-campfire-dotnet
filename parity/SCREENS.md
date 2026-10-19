# Screen inventory format (`parity/screens.yml`)

The inventory lists *states*. The capture engine (`parity/capture`) renders every state against
both servers across the support matrix, and compares the results.

```yaml
- id: rooms/show/busy                 # unique, path-like; used for artifact names
  as: david                           # fixture user label to sign in as; omit for signed out
  path: /rooms/{{rooms.designers}}    # {{table.label}} interpolates fixture IDs
  seed: default                       # named seed variant from parity/seeds/ (default: default)
  matrix:                             # optional narrowing of the support matrix
    viewports: [desktop, phone]       # desktop 1440x900, laptop 1280x800, tablet 834x1194, phone 390x844
    schemes: [light, dark]
    engines: [chromium, firefox, webkit]
  steps:                              # optional interactions after the page is ready
    - click: ".message__actions-btn"
    - fill: { selector: "#message_body", text: "Hello" }
    - hover: ".boost"
    - press: "Enter"
    - wait_for: ".autocomplete__list"
    - pause_animations_at: 500        # ms; pauses all document.getAnimations() at this time
  actors:                             # multi-user states; steps may then carry `actor:`
    a: david
    b: kevin
  capture: b                          # which actor's page is captured (multi-user only)
  covers: [rooms/show, rooms/show/_composer]   # templates this state is meant to exercise
  masks:                              # optional; see "Masks"
    values:
      join_code: { selector: "#invite_url", attribute: value, match: "/join/([^/?#]+)$" }
    pixels: ["#invite_url"]
```

Steps run in order. Any step may take `actor: <name>` in multi-user states. The engine waits for
readiness (Stimulus controllers connected, cable subscriptions confirmed, fonts, images and network
idle) after navigation and after every step.

Fake time moves only in fixed ticks (50ms of the browser's paused clock), and only while the page
has settled in real time: network idle, nothing left that real time resolves (cable confirmations,
images, zero-delay timeouts), and the DOM unchanged since the previous tick. A page is ready after
8 ticks in a row that changed nothing, and a `wait_for` ticks until its element shows. So the fake
time a capture spends depends only on the app's own timers, never on the machine's speed
(`parity/capture/readiness.ts`).

Every document's fake clock starts at the same point: Playwright replays the harness's
`install` and `pauseAt` in each new document and would advance the clock by the real milliseconds
between the two calls, which moved every `requestAnimationFrame` by up to a frame (a room's
composer got its focus ring whenever Lexxy's rAF mount came before composer_controller.js's
zero-delay `focus()`). `capture.ts` (`freezeClock`) dates the pause at the install. Before the
screenshot the mouse is moved to where it already is, so `:hover` reflects the settled page in
every engine rather than whenever the engine next synthesizes a mouse move (Firefox showed the
actions button of the message that moved under the pointer in `interactions/message_deleted` in
some captures and not in others).

`mutates: true` states (see the top of `screens.yml` for what counts) never share a server: with a
reset hook (`--reset`, which `parity/bin/compare --self-parity` sets up), each of their captures
gets freshly started servers of its own, `--isolated N` (default 3) at a time, alongside the other
states. Slot k of a server on port P listens on P + 1000 × (k + 1), and the reset command is run
with that `{port}`.

## Matrix

`parity/bin/compare` (and `capture`) take `--matrix lean|full`; lean is the default
([upstream capture design](https://github.com/basecamp/once-campfire-rust/blob/3fe9f61dcd6930b597170e1ea2c7b7604c3570ce/plans/rust-conversion.md), decision 4). The frontend is byte-identical and the browsers are pinned,
so a port can only change pixels by sending different bytes: the gate is server output, and pixels
back it up.

Every page capture records five text layers besides the screenshot, and every one is compared:

| Layer | File | What |
|---|---|---|
| server | `.server.norm.html` | the main document's response, normalized (typed placeholders for tokens and times) |
| live | `.live.norm.html` | `body.outerHTML` at capture time, normalized |
| aria | `.aria.yml` | the accessibility tree |
| network | `.network.txt` | every response from the server to the page (and navigation): method, path, status, header shape (names, plus the values of `content-type`, `location`, `cache-control`, `content-disposition`, `vary`, and each cookie's name and attributes), and the sha256 of the normalized body (media ranges: the total size). Native comparisons also compare retained `content-encoding` values. Sorted, since requests run in parallel. |
| cable | `.cable.txt` | every Action Cable frame except pings, per subscription in order, payloads normalized |

The **lean** matrix (`parity/capture/inventory.ts`, `LEAN_*`):

- every state on Chromium, desktop and phone, light and dark (a state narrowed to other viewports
  keeps its first one);
- the smoke states (`realtime/**`, `auth/sign_in`, `rooms/show/designers`,
  `interactions/composer/with_text`, `interactions/lightbox`,
  `interactions/mention_autocomplete/results`) also on Firefox and WebKit, desktop and phone, light;
- the breakpoint sweep on Chromium;
- fragments once, as always.

The **full** matrix is every engine × every viewport × both schemes plus the sweep on every
engine: keep it for release checks. `--self-parity` runs once with the lean matrix (twice, plus a
run-1-vs-run-2 comparison, with the full one), all seeds in parallel.

## Masks

A few values are made up at random by the server while a state runs, so two servers never agree on
them: today only the join code that `Account::Joinable` generates when `auth/first_run/completed`
creates the account (every other random value is either seeded or has a typed placeholder in
`normalize.ts`). A state declares these in `masks:`, and nothing is masked anywhere else:

- `values: { name: { selector, attribute?, match? } }`: after the steps, the harness reads the value
  from that element on *this* server's page (the attribute, or the text; `match` is a regular
  expression whose one group is the value) and replaces that exact string with `«name»` in every
  text layer: server HTML, live DOM, accessibility tree, the network layer (before bodies are
  hashed) and the cable layer. The value is never guessed by a pattern over arbitrary text, so the
  same code rendered for the wrong account, or a different code anywhere else, still differs. A
  missing element fails the capture.
- `pixels: [selector, …]`: those elements' boxes are painted over (magenta) in the screenshot on
  both sides. Each selector must match something.

What each capture found is in its `.json` (`masks`), and the report lists every masked state.

When a comparison includes the native .NET target, four explicit representation rules apply to
both captures. They do not turn a changed status, body, authorization result or cookie security
attribute into a match:

- Opaque Data Protection tokens are replaced only after an optional fixture-owned decoder has
  authenticated their purpose and record. Unknown and tampered tokens stay literal. A different
  purpose or record remains different. The decoder uses fixture key rings, never production keys.
- The native target must send `X-Content-Type-Options: nosniff`, `X-Frame-Options: SAMEORIGIN` and
  `Referrer-Policy: strict-origin-when-cross-origin`. Capture checks their exact values before
  omitting the documented security-header differences from the comparison. Missing headers fail.
- For images and non-2xx responses, gzip versus identity and the corresponding `Accept-Encoding`
  member of `Vary` differ across targets. The decoded body, content type, status and other `Vary` members stay
  compared. Unknown encodings stay visible, as do encoding differences on successful HTML.
- Both apps name the authentication cookie `session_token`. Only `.Campfire.Csrf` maps to `_campfire_session` as a cookie-name alias.
  Clearing, path, domain, HttpOnly, Secure and SameSite still differ when their contracts
  differ. As in ordinary captures, the comparer omits ticking Expires/Max-Age values. These captures do
  not prove cookie lifetime behavior. Flash and unknown cookie names have no alias.

Retained normalized artifacts cannot prove raw headers that were already omitted or recover
previously omitted encoding values. Capture again when a changed rule needs those raw values;
keep the earlier report rather than relabeling it as validation of the new rule.

| State | Values | Pixels |
|---|---|---|
| `auth/first_run/completed` | `«join_code»` from `#invite_url[value]`, `/join/([^/?#]+)$` | `#invite_url`, `a[href^='/qr_code/']` |

## Pixel flakes

The pixel layer backs up the server-output layers, and a few rasterization effects aren't the
server's doing. So when a cell's server output (server HTML, live DOM, accessibility tree, network
and cable) is identical on both sides and only the pixels differ, the cell is captured again on
both sides (on fresh servers for `mutates: true` states), up to 2 more times. If a later attempt
matches, the cell passes and is marked **flaky** in `report.json` (`flaky: true`, `attempts`) and
the HTML report, which keeps each failed attempt's screenshots and diff
(`<cell>.attempt-N.png`). A cell whose server output differs is never retried, and one whose pixels
still differ after 3 attempts fails. The summary line counts flaky cells.

The sidebar toggle's arc on phone after the sign-up redirects (`auth/join/completed`,
`auth/first_run/completed`) used to come out one gray level apart along its top arc. It was stale
raster, not the server: the room's sidebar frame loads once or twice depending on whether
UnreadRoomsChannel's confirmation (which reloads it) lands before or after the first load, and with
one load Chromium kept the toggle's tiles from an earlier raster. The winner followed each server's
speed, so a retry wasn't an independent sample. Before the screenshot, Chromium captures now
promote the root to its own layer and back, giving each change 150ms to be drawn (`rasterAfresh`
in `capture.ts`). That throws every tile away, so the pixels are a fresh raster of the final page.
