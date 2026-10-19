# Policy-chain vector

`action_chains.json` records, for every routed `controller#action` of the reference, the callbacks Rails runs around the action, in chain order. The .NET policy filter is tested against it. Generated in the reference image in production mode with `parity/.env.reference`, on a copy of the default seed:

```sh
PATH="$PWD/eng/docker-shim:$PATH" PARITY_SEED_DIR=... \
  parity/bin/reference runner --seed default reference-tools/campfire/action_chains.rb > vectors/policy/action_chains.json
```

The output is deterministic (two runs are byte-identical). Rails `8.2.0.alpha` at `1a02651`, Campfire `90b3300`.

## Layout

| Key | Content |
|---|---|
| `actions` | 147 entries keyed `controller_path#action`: the controller class, the routes (verb and path), the route defaults, `action` (`method`, or `missing` when neither a method nor a template exists), and `chain` |
| `chain[]` | `kind` (`before`, `around`, `after`), the filter (`type: method` with `name`, `owner` and `defined_at`; `type: proc` with `defined_at` and the source line in `code`), and `conditions` |
| `conditions[]` | The conditions that remain after `only:`/`except:` are resolved: `kind` (`if`/`unless`), and a method name or a proc, with its source location and line |
| `macro`, `arguments` | For procs that Rails' `rate_limit` and `allow_browser` create: the macro's arguments, read from the proc's binding (for example `to: 10`, `within: 180 s`, the `with:` proc and the store for `sessions#create`) |
| `missing_controllers` | Routes whose controller class does not exist: `rooms/settings#show` (`Rooms::SettingsController`) |
| `anonymous_probes` | Status, content type and body digest for anonymous requests to the Action Mailbox and conductor routes, the missing controller, and every routed action without a method, sent in process through the whole Rails middleware stack (without `config.ru`'s `Rack::Deflater` and Thruster) |
| `controllers` | Every concrete controller: superclass, file, included modules, action methods, routed actions, and its whole chain (`raw_chain`) with the `only`/`except` lists unresolved |

## Reading a chain

- Order is chain order. Before and around callbacks run in that order. After callbacks run in reverse order once the action returns, and not at all when a before callback rendered or redirected (the chain halts).
- `only:`/`except:` are fully resolved: a callback that does not apply to the action is absent. `skip_before_action … only:` (for example `allow_unauthenticated_access only: %i[new create]`) is resolved the same way, because Rails implements it as an `unless` ActionFilter on the kept callback.
- Other conditions depend on the request and stay symbolic: `reject_banned_ip unless: safe_request?`, and `verify_authenticity_token unless: -> { authenticated_by.bot_key? }` (the bot-key CSRF exemption). The HTTP replay checks their outcomes.
- Every chain begins with the framework's around callbacks (Action Text's `around_action` from `action_text/engine.rb:71` and Turbo's `turbo_tracking_request_id`) and Geared Pagination's `set_paginated_headers` after callback, then `ApplicationController`'s concerns in their declaration order.

## Findings worth a test

- `rooms/settings#show` is declared without a controller. An anonymous `GET /rooms/1/settings` answers **500** with `public/500.html`, not 404: `ActionDispatch::MissingController` is not in `rescue_responses`. No callback runs, so a signed-in request gets the same.
- 33 routed actions have neither a method nor a template (for example `accounts#show`, `rooms#new`, `users/profiles#destroy`; see the `missing` entries). `AbstractController::ActionNotFound` is raised before any callback, so each answers **404** with `public/404.html`, whoever asks and without a CSRF check.
- Action Mailbox ingress routes answer **404** with an empty `text/html` body: no ingress is configured (`action_mailbox_ingress` is null), so `ensure_configured` responds `head :not_found`.
- Conductor routes answer **403** (empty body) to GET, from `ensure_development_env`, and **422** with `public/422.html` to POST: `verify_authenticity_token` runs before `ensure_development_env` in their chain.
