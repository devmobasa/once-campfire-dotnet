// Captures one state in one matrix cell against one server: screenshot, server HTML, live DOM and
// accessibility tree, all taken after real readiness. Fragment states capture the response only.
import fs from "node:fs"
import path from "node:path"
import YAML from "yaml"
import type { BrowserContext, BrowserContextOptions, Frame, Locator, Page, Request, Response } from "playwright"
import { freezeAnimatedImages } from "./animated_images.ts"
import { contextOptions } from "./browsers.ts"
import type { BrowserPool } from "./browsers.ts"
import { PARITY_DIR, REPO_DIR, SEED_DIR } from "./config.ts"
import { cellId, interpolate, interpolateStep, isFragment, loadLabels } from "./inventory.ts"
import type { Job, Labels, Masks, State } from "./inventory.ts"
import { maskText, normalizeResponse, normalizeDocument } from "./normalize.ts"
import { CableLog, NetworkLog } from "./network.ts"
import { DETERMINISM_SCRIPT, PageTracker, READINESS_SCRIPT, waitForReady } from "./readiness.ts"
import type { SessionCache } from "./session.ts"
import { runStep } from "./steps.ts"
import { protectedTokenResolver } from "./protected_tokens.ts"
import { RequestLifecycleRecorder } from "./lifecycle.ts"
import { installErrorBodyDrain } from "./fetch_drain.ts"
import type { NormalizeOptions } from "./normalize.ts"
import type { StepContext } from "./steps.ts"
import type { WireReceipts } from "./wire_receipts.ts"

export interface Target {
  name: string // "expected" | "actual" | ...
  url: string // the real server
  origin: string // what the browser sees (shared by all targets; see proxy.ts)
  proxy?: string // this target's forward proxy, set by run()
  wireHeaders?: WireReceipts
  authCookieName?: string // explicit authentication contract for this target
  nativeTarget?: boolean // native credentials, protected tokens and security contracts
}

export interface CaptureEnv {
  pool: BrowserPool
  sessions: SessionCache
  outDir: string // run directory; artifacts go under <outDir>/<target.name>/
  time: string // ISO instant the browser clock is frozen at (the seed's clock.now)
  timeoutMs: number
  seedDir?: string
  diagnostics?: boolean
  diagnosticNoHar?: boolean
  nativeComparison?: boolean
  nativeTokenResolver?: string
}

export interface CellMeta {
  state: string
  cell: string
  target: string
  url: string
  kind: "page" | "fragment"
  status?: number
  contentType?: string
  finalUrl?: string
  error?: string
  readiness?: unknown
  animations?: string[]
  focus?: string
  trace?: string[] // after each step: the step, then scroll positions and the focused element
  retriedAfter?: string
  masks?: { values: Record<string, string>; pixels: Record<string, number> } // what masks.* found
  cable?: Record<string, string[]>
  imageRouteErrors?: ReturnType<Awaited<ReturnType<typeof freezeAnimatedImages>>["snapshot"]>
  pageErrors: string[]
  console: string[]
  durationMs: number
  browserVersion?: string
}

export const ARTIFACTS = [".png", ".server.html", ".server.norm.html", ".live.norm.html", ".aria.yml", ".network.txt", ".network.contracts.json", ".cable.txt", ".json"]

// Seeded opengraph embeds point at external images; the harness serves them (no network).
const EXTERNAL_FIXTURES: [string, string][] = [
  ["https://example.com/og/**", "reference/test/fixtures/files/moon.jpg"],
  ["https://pbs.twimg.com/profile_images/**", "reference/test/fixtures/files/moon.jpg"],
]

export function artifactBase(outDir: string, target: string, job: Job): string {
  return path.join(outDir, target, job.state.id, cellId(job.cell))
}

export async function captureCell(job: Job, target: Target, env: CaptureEnv): Promise<CellMeta> {
  const started = Date.now()
  const { state, cell } = job
  const base = artifactBase(env.outDir, target.name, job)
  fs.mkdirSync(path.dirname(base), { recursive: true })
  for (const ext of ARTIFACTS) fs.rmSync(base + ext, { force: true })
  for (const file of fs.readdirSync(path.dirname(base))) {
    if (file.startsWith(path.basename(base) + ".") && file.endsWith(".private-lifecycle.json")) fs.rmSync(path.join(path.dirname(base), file), { force: true })
  }

  const meta: CellMeta = {
    state: state.id, cell: cellId(cell), target: target.name, url: target.url, kind: isFragment(state) ? "fragment" : "page",
    pageErrors: [], console: [], durationMs: 0,
  }
  const { browser, release } = await env.pool.acquire(cell.engine)
  meta.browserVersion = browser.version()
  const contexts: BrowserContext[] = []
  const imageOwners: Awaited<ReturnType<typeof freezeAnimatedImages>>[] = []
  const beforeClose: (() => Promise<void>)[] = []
  const newContext = async (user: string | undefined, labels: Labels) => {
    const options = contextOptions(cell, browser.version())
    if (state.user_agent) options.userAgent = userAgent(state.user_agent)
    if (state.headers) options.extraHTTPHeaders = interpolateStep(state.headers as any, labels, state.id) as any
    options.proxy = proxyOptions(target, cell.engine)
    if (user) options.storageState = await env.sessions.get(browser, target, options.proxy, user, labels)
    if (env.diagnostics && !env.diagnosticNoHar) options.recordHar = { path: `${base}.actor-${contexts.length}.har`, mode: "full", content: "attach" }
    const context = await browser.newContext(options)
    contexts.push(context)
    context.setDefaultTimeout(env.timeoutMs)
    await isolateNetwork(context, target.origin)
    await scriptsInOrder(context, target.origin, meta)
    imageOwners.push(await freezeAnimatedImages(context, target.origin, env.timeoutMs))
    return context
  }
  try {
    const sharedLabels = loadLabels(state.seed, env.seedDir)
    // A native candidate's tokens belong to its own persistent key ring. Read its freshly
    // prepared sidecar for every capture/reset and never mutate the shared label cache.
    const labelsFile = path.join(env.seedDir ?? SEED_DIR, ".candidates", new URL(target.url).port, "db", "native-fixture.json")
    let labels = sharedLabels
    if (target.nativeTarget) {
      const nativeLabels = JSON.parse(fs.readFileSync(labelsFile, "utf8")).labels
      if (!nativeLabels || typeof nativeLabels !== "object" || Array.isArray(nativeLabels)) {
        throw new Error(`Native capture labels are missing or invalid: ${labelsFile}`)
      }
      labels = { ...sharedLabels, ...nativeLabels }
    }
    if (isFragment(state)) await captureFragment(state, target, env, base, meta, labels, await newContext(state.as, labels))
    else await capturePage(state, target, env, base, meta, labels, newContext, cell.viewport.touch, beforeClose)
  } catch (error: any) {
    meta.error = String(error?.message ?? error).split("\n").slice(0, 6).join("\n")
  } finally {
    await Promise.all(beforeClose.map((f) => f().catch(() => {})))
    // Route callbacks are scheduled outside capturePage's awaited error boundary. Retire
    // only this context's image routes and drain them while its request context is alive.
    await Promise.all(imageOwners.map(owner => owner.retire()))
    await Promise.all(contexts.map((c) => c.close().catch(() => {})))
    // A retirement timeout is already a failure. Context closure cancels its remaining
    // operations; retain their original errors before final cell metadata is written.
    await Promise.all(imageOwners.map(owner => owner.finish()))
    const imageErrors = imageOwners.flatMap(owner => owner.snapshot())
    if (imageErrors.length) {
      meta.imageRouteErrors = imageErrors
      const failure = imageErrors.map(error => `image route ${error.stage} ${error.url ?? "cleanup"}: ${error.message}`).join("\n")
      meta.error = meta.error ? `${meta.error}\n${failure}` : failure
    }
    await release()
    meta.durationMs = Date.now() - started
    fs.writeFileSync(base + ".json", JSON.stringify(meta, null, 2) + "\n")
  }
  return meta
}

async function capturePage(
  state: State, target: Target, env: CaptureEnv, base: string, meta: CellMeta, labels: Labels,
  newContext: (user: string | undefined, labels: Labels) => Promise<BrowserContext>, touch: boolean,
  beforeClose: (() => Promise<void>)[],
) {
  const actors: Record<string, string | undefined> = state.actors ?? { main: state.as }
  // Where Date goes back to after each tick of fake time; undefined lets it advance with the ticks.
  const clockTime = state.clock === "advancing" ? undefined : Date.parse(env.time)
  const captureActor = state.actors ? state.capture! : "main"
  const pages: Record<string, Page> = {}
  const trackers: Record<string, PageTracker> = {}
  const networks: Record<string, NetworkLog> = {}
  const normalizeOptions: NormalizeOptions = { seedTime: Date.parse(env.time), nativeComparison: env.nativeComparison, nativeTarget: target.nativeTarget, protectedTokens: new Map() }
  if (normalizeOptions.nativeTarget && env.nativeTokenResolver) normalizeOptions.resolveProtectedTokens = protectedTokenResolver(env.nativeTokenResolver, target.url, normalizeOptions.protectedTokens!)
  let serverResponse: Response | null = null

  // Contexts are set up one actor at a time so multi-user states connect in a fixed order.
  for (const [actor, user] of Object.entries(actors)) {
    const context = await newContext(user, labels)
    await freezeClock(context, env.time)
    await context.addInitScript({ path: DETERMINISM_SCRIPT })
    if (state.notifications === "granted") await context.addInitScript(NOTIFICATIONS_GRANTED)
    await context.addInitScript({ path: READINESS_SCRIPT })
    // PARITY PATCH: settle unconsumed error bodies without altering application responses.
    await context.addInitScript(installErrorBodyDrain)
    const page = await context.newPage()
    const lifecycle = new RequestLifecycleRecorder(page)
    beforeClose.push(async () => {
      try {
        fs.writeFileSync(`${base}.${actor}.private-lifecycle.json`, JSON.stringify(lifecycle.snapshot(), null, 2) + "\n")
      } catch (error) {
        const failure = `Private lifecycle receipt write failed: ${String(error)}`
        meta.error = meta.error ? `${meta.error}\n${failure}` : failure
      } finally {
        lifecycle.stop()
      }
    })
    if (env.diagnostics) {
      const events: unknown[] = []
      const elapsed = () => Date.now()
      page.on("request", (request) => events.push({ at: elapsed(), type: "request", method: request.method(), url: request.url() }))
      page.on("response", (response) => events.push({ at: elapsed(), type: "response", status: response.status(), url: response.url() }))
      page.on("requestfinished", (request) => events.push({ at: elapsed(), type: "finished", url: request.url(), timing: request.timing() }))
      page.on("requestfailed", (request) => events.push({ at: elapsed(), type: "failed", url: request.url(), failure: request.failure(), timing: request.timing() }))
      page.on("console", (message) => events.push({ at: elapsed(), type: "console", level: message.type(), text: message.text() }))
      beforeClose.push(async () => {
        fs.writeFileSync(`${base}.${actor}.timing.json`, JSON.stringify(events, null, 2) + "\n")
        const tracker = trackers[actor]
        fs.writeFileSync(`${base}.${actor}.cable-diagnostic.json`, JSON.stringify({ cable: tracker.cableLog, errors: tracker.errors, console: tracker.console, networkErrors: tracker.networkErrors, inflight: [...tracker.inflight].map((request) => ({ url: request.url(), timing: request.timing() })) }, null, 2) + "\n")
      })
    }
    page.on("dialog", (dialog) => (state.accept_dialogs ? dialog.accept() : dialog.dismiss()).catch(() => {}))
    pages[actor] = page
    trackers[actor] = new PageTracker(page)
    trackers[actor].cable = new CableLog(normalizeOptions, !!state.mutates)
    networks[actor] = new NetworkLog(page, new URL(target.origin).origin, normalizeOptions, target.wireHeaders?.verifier())
    beforeClose.push(() => trackers[actor].abortHeld())
    const route = typeof state.path === "string" ? state.path : state.path[actor] ?? state.path[captureActor]
    const url = new URL(interpolate(route, labels, state.id), target.origin).href
    // PARITY PATCH: preserve navigation diagnostics before context cleanup; acceptance stays unchanged.
    const response = await page.goto(url, { waitUntil: "load", timeout: env.timeoutMs }).catch((error) => {
      const tracker = trackers[actor]
      meta.pageErrors.push(...tracker.errors)
      meta.console.push(...tracker.console, ...tracker.networkErrors)
      meta.console.push(...[...tracker.inflight].slice(0, 32).map((request) => `pending ${request.resourceType()} ${request.method()} ${request.url()}`))
      throw error
    })
    if (actor === captureActor) serverResponse = response
    await waitForReady([trackers[actor]], env.timeoutMs, clockTime)
    if (env.diagnostics) await page.evaluate(async () => {
      const moduleName = "@rails/actioncable"
      const cable = await import(moduleName)
      cable.logger.enabled = true
    }).catch((error) => meta.console.push(`ActionCable diagnostic logger: ${error}`))
    if (env.diagnostics) {
      if (context.browser()?.browserType().name() === "chromium") {
        const session = await context.newCDPSession(page)
        const events: unknown[] = []
        await session.send("Network.enable")
        for (const event of ["Network.requestWillBeSent", "Network.responseReceived", "Network.loadingFinished", "Network.loadingFailed"] as const) session.on(event, (payload) => events.push({ at: Date.now(), event, payload }))
        beforeClose.push(async () => {
          fs.writeFileSync(`${base}.${actor}.cdp-network.json`, JSON.stringify(events, null, 2) + "\n")
          await session.detach().catch(() => {})
        })
      }
      await context.tracing.start({ screenshots: true, snapshots: true, sources: true })
      beforeClose.push(() => context.tracing.stop({ path: `${base}.${actor}.trace.zip` }))
    }
  }

  const capturePage = pages[captureActor]
  capturePage.on("response", (response) => {
    if (response.request().isNavigationRequest() && response.frame() === capturePage.mainFrame()) serverResponse = response
  })
  const stepContext: StepContext = { pages, trackers, defaultActor: captureActor, touch, baseUrl: target.origin, timeoutMs: env.timeoutMs, time: clockTime }
  meta.trace = []
  for (const step of state.steps) {
    await runStep(interpolateStep(step, labels, state.id), stepContext)
    meta.readiness = await waitForReady(Object.values(trackers), env.timeoutMs, clockTime)
    meta.trace.push(`${JSON.stringify(step)} -> ${await pageTrace(capturePage)}`)
  }
  if (!state.steps.length) meta.readiness = await waitForReady([trackers[captureActor]], env.timeoutMs, clockTime)

  const response = serverResponse as Response | null
  meta.status = response?.status()
  meta.finalUrl = capturePage.url()
  const seedTime = Date.parse(env.time)
  const mask = await valueMasks(capturePage, state.masks, meta)
  if (response) {
    const html = await response.text().catch(() => "")
    await normalizeOptions.resolveProtectedTokens?.(html)
    fs.writeFileSync(base + ".server.html", html)
    fs.writeFileSync(base + ".server.norm.html", mask(normalizeDocument(html, normalizeOptions)))
  }

  if (Object.keys(pages).length > 1) {
    // The last actor to act holds window focus; give it back to the captured page.
    await capturePage.bringToFront()
    await waitForReady([trackers[captureActor]], env.timeoutMs, clockTime)
  }
  if (!touch && (await rehover(capturePage))) await waitForReady([trackers[captureActor]], env.timeoutMs, clockTime)
  meta.animations = await capturePage.evaluate((at) => (window as any).__parity.pauseAnimations(at), stepContext.pauseAnimationsAt ?? null)
  meta.trace.push(`capture -> ${await pageTrace(capturePage)}`)
  meta.trace.push(...(await capturePage.evaluate(() => (window as any).__parity.focusLog()).catch(() => [])).map((l: string) => `focus: ${l}`))
  meta.focus = await capturePage.evaluate(() => {
    const el = document.activeElement
    return `${document.hasFocus() ? "window focused" : "window blurred"}; active ${el ? el.tagName.toLowerCase() + (el.id ? "#" + el.id : "") + (el.className && typeof el.className === "string" ? "." + el.className.trim().split(/\s+/).join(".") : "") : "none"}`
  })
  await rasterAfresh(capturePage)
  const shot = await stableScreenshot(capturePage, env.timeoutMs, await pixelMasks(capturePage, state.masks, meta))
  fs.writeFileSync(base + ".png", shot.png)
  if (!shot.stable) meta.console.push("screenshot never stabilized (two consecutive frames always differed)")
  const live = await capturePage.evaluate(() => document.body?.outerHTML ?? "")
  await normalizeOptions.resolveProtectedTokens?.(live)
  fs.writeFileSync(base + ".live.norm.html", mask(normalizeDocument(`<!DOCTYPE html><html><head></head>${live}</html>`, normalizeOptions)))
  const aria = await capturePage.locator("body").ariaSnapshot({ timeout: env.timeoutMs })
  fs.writeFileSync(base + ".aria.yml", mask(maskText(aria, normalizeOptions)) + "\n")

  meta.cable = Object.fromEntries(Object.entries(trackers).map(([actor, t]) => [actor, t.cableLog]))
  // Server output beyond the document: every response and every cable frame, per actor.
  const multi = Object.keys(pages).length > 1
  const perActor = async (text: (actor: string) => Promise<string> | string) =>
    (await Promise.all(Object.keys(pages).map(async (actor) => (multi ? `## ${actor}\n` : "") + (await text(actor))))).join("\n")
  fs.writeFileSync(base + ".network.txt", await perActor((actor) => networks[actor].text(mask)))
  fs.writeFileSync(base + ".network.contracts.json", JSON.stringify(Object.entries(networks).flatMap(([actor, network]) => network.contractReceipts().map((receipt) => ({ ...receipt, actor }))), null, 2) + "\n")
  await Promise.all(Object.values(trackers).map((tracker) => tracker.cable!.resolveProtectedTokens()))
  fs.writeFileSync(base + ".cable.txt", await perActor((actor) => trackers[actor].cable!.text(mask)))
  meta.pageErrors = Object.values(trackers).flatMap((t) => t.errors)
  meta.console.push(...Object.values(trackers).flatMap((t) => t.console))
  checkStatus(state, meta)
}

// The mouse stays where the last step left it, and when the page changes under it (the message it
// deleted is gone, and the next one moved up) an engine updates :hover whenever it next
// synthesizes a mouse move, which Firefox does on a later refresh tick: the next message's actions
// button showed in some captures and not in others. Moving the mouse to where it already is makes
// every engine hit-test the settled page now.
async function rehover(page: Page): Promise<boolean> {
  const at = await page.evaluate(() => (window as any).__parity?.pointer() ?? null).catch(() => null)
  if (!at) return false
  await page.mouse.move(at[0], at[1])
  return true
}

// masks.values (parity/SCREENS.md, "Masks"): a value the server made up at random, such as the
// join code Account::Joinable generates when the first run creates the account, is read from this
// server's page and replaced by «name» wherever it appears in a text layer. The value comes from
// the element that shows it, never from a pattern over arbitrary text, so a wrong value elsewhere
// still differs. A mask whose element is missing fails the capture rather than masking nothing.
async function valueMasks(page: Page, masks: Masks | undefined, meta: CellMeta): Promise<(text: string) => string> {
  const entries = Object.entries(masks?.values ?? {})
  if (!entries.length) return (text) => text
  const found: [string, string][] = []
  for (const [name, spec] of entries) {
    const raw = await page.evaluate(({ selector, attribute }) => {
      const el = document.querySelector(selector)
      if (!el) return null
      return attribute ? el.getAttribute(attribute) : el.textContent
    }, { selector: spec.selector, attribute: spec.attribute ?? null })
    if (raw === null) throw new Error(`masks.values.${name}: nothing matches ${spec.selector}${spec.attribute ? ` [${spec.attribute}]` : ""}`)
    const value = spec.match ? new RegExp(spec.match).exec(raw)?.[1] : raw.trim()
    if (!value) throw new Error(`masks.values.${name}: ${JSON.stringify(raw)} doesn't match ${spec.match}`)
    found.push([name, value])
  }
  meta.masks = { values: Object.fromEntries(found), pixels: meta.masks?.pixels ?? {} }
  found.sort(([, a], [, b]) => b.length - a.length)
  return (text) => found.reduce((out, [name, value]) => out.replaceAll(value, `«${name}»`), text)
}

// masks.pixels: the listed elements' boxes are painted over in the screenshot (Playwright's
// screenshot mask), on both servers alike. Each selector must match something.
async function pixelMasks(page: Page, masks: Masks | undefined, meta: CellMeta) {
  const selectors = masks?.pixels ?? []
  const counts: Record<string, number> = {}
  for (const selector of selectors) {
    counts[selector] = await page.locator(selector).count()
    if (!counts[selector]) throw new Error(`masks.pixels: nothing matches ${selector}`)
  }
  if (selectors.length) meta.masks = { values: meta.masks?.values ?? {}, pixels: counts }
  return selectors.map((selector) => page.locator(selector))
}

// Where the captured page is scrolled and what has focus, for diagnosing differences.
function pageTrace(page: Page): Promise<string> {
  return page.evaluate(() => {
    const describe = (el: Element | null) => (el ? el.tagName.toLowerCase() + (el.id ? "#" + el.id : "") + (typeof el.className === "string" && el.className.trim() ? "." + el.className.trim().split(/\s+/).join(".") : "") : "none")
    const scrolled = [...document.querySelectorAll("*")].filter((el) => el.scrollTop || el.scrollLeft).map((el) => `${describe(el)}@${el.scrollLeft},${el.scrollTop}`)
    return `window@${scrollX},${scrollY} ${scrolled.join(" ")}; focus ${document.hasFocus() ? "" : "(blurred) "}${describe(document.activeElement)}`
  }).catch((error) => `trace failed: ${String(error).split("\n")[0]}`)
}

async function captureFragment(state: State, target: Target, env: CaptureEnv, base: string, meta: CellMeta, labels: Labels, context: BrowserContext) {
  const request = state.request ? (interpolateStep(state.request as any, labels, state.id) as any) : {}
  const url = new URL(interpolate(state.path as string, labels, state.id), target.origin).href
  const response = await context.request.fetch(url, {
    method: request.method ?? "GET",
    headers: request.headers,
    data: request.body,
    maxRedirects: 0,
    timeout: env.timeoutMs,
  })
  const body = await response.body()
  const normalizeOptions: NormalizeOptions = { seedTime: Date.parse(env.time), nativeComparison: env.nativeComparison, nativeTarget: target.nativeTarget, protectedTokens: new Map() }
  if (normalizeOptions.nativeTarget && env.nativeTokenResolver) {
    normalizeOptions.resolveProtectedTokens = protectedTokenResolver(env.nativeTokenResolver, target.url, normalizeOptions.protectedTokens!)
    await normalizeOptions.resolveProtectedTokens(body.toString("utf8") + "\n" + url)
  }
  meta.status = response.status()
  meta.contentType = response.headers()["content-type"]
  fs.writeFileSync(base + ".server.html", body)
  const location = response.headers()["location"]
  const head = [`HTTP ${meta.status}`, `content-type: ${meta.contentType ?? ""}`, ...(location ? [`location: ${location}`] : [])]
  fs.writeFileSync(base + ".server.norm.html", `${maskText(head.join("\n"), normalizeOptions)}\n\n${normalizeResponse(body, meta.contentType ?? "", normalizeOptions)}`)
  checkStatus(state, meta)
}

// `notifications: granted`: the browser reports permission granted and an existing push
// subscription, which is what notifications_controller.js#isEnabled checks before it dispatches
// notifications:ready (the bell's involvement frame loads on it). Headless browsers have no push
// service, so the subscription is a stand-in; nothing here posts it to the server.
// WebKit's iPhone emulation has no Notification at all (iOS Safari only has it in an installed
// PWA); granted means a browser that has it, so a stand-in is defined there.
const NOTIFICATIONS_GRANTED = `(() => {
  if (typeof Notification === "undefined") window.Notification = class Notification { constructor() {} }
  Object.defineProperty(Notification, "permission", { configurable: true, get: () => "granted" })
  Notification.requestPermission = () => Promise.resolve("granted")
  if (!navigator.serviceWorker) return
  const subscription = {
    endpoint: "https://push.parity.invalid/subscription",
    toJSON: () => ({ endpoint: "https://push.parity.invalid/subscription", keys: { p256dh: "parity", auth: "parity" } }),
    unsubscribe: () => Promise.resolve(true),
  }
  const registration = { pushManager: { getSubscription: () => Promise.resolve(subscription), subscribe: () => Promise.resolve(subscription) } }
  navigator.serviceWorker.getRegistration = () => Promise.resolve(registration)
})()`

// Date is frozen at the seed's instant (https://github.com/basecamp/once-campfire-rust/blob/3fe9f61dcd6930b597170e1ea2c7b7604c3570ce/plans/rust-conversion.md, "Determinism"), and timers and
// requestAnimationFrame run on Playwright's fake clock, paused there. Zero-delay timeouts still
// fire at once; everything else waits for the readiness loop to advance the clock, which it only
// does with the network idle and every module and controller loaded (readiness.ts), and it puts
// Date back to the seed instant after each step. Pending callbacks then fire in due-time order
// however fast modules and responses arrived. With real timers, composer_controller.js's
// setTimeout(0) focus() raced Lexxy's requestAnimationFrame mount of the editor root, and the
// composer had its focus ring in some captures and not in others.
//
// Every document starts its fake monotonic clock (performance.now, the base of every timer) at 0.
// Playwright replays a context's clock calls in each new document, and between the install and
// the pauseAt it advances that clock by the *real* milliseconds that passed between the two calls
// in this process: 0 to 30 of them, depending on load. requestAnimationFrame is due at the next
// multiple of 16 of that clock, so the offset decided whether Lexxy's rAF mount of the editor root
// (lexxy.js connectedCallback) ran before composer_controller.js's onNextEventLoopTick focus(): at
// an offset of 15 or 31 the frame was 1ms away, due together with the timeout (which the fake
// clock delays by 1ms when it's set from inside a timer), and ran first because it was created
// first, so a room's composer had its focus ring in about one capture in 120. CLOCK_EPOCH_SCRIPT
// dates the pauseAt at the install's instant before the replay, so no real time passes between
// them. (Installing is what zeroes the clock; a bare pauseAt lets it follow real time until the
// replay.)
async function freezeClock(context: BrowserContext, time: string) {
  const instant = new Date(time)
  await context.clock.install({ time: new Date(instant.getTime() - 1000) })
  await context.clock.pauseAt(instant)
  await context.addInitScript(CLOCK_EPOCH_SCRIPT)
}

const CLOCK_EPOCH_SCRIPT = `(() => {
  const log = globalThis.__pwClock && globalThis.__pwClock.controller && globalThis.__pwClock.controller._log
  if (!log || !log.length) return
  const install = log.find((entry) => entry.type === "install")
  const pause = log.find((entry) => entry.type === "pauseAt")
  if (install && pause) pause.time = install.time
})()`

// Chromium keeps a layer's rastered tiles until something invalidates them, so an unchanged region
// shows whatever it looked like when it was last rastered, and that isn't always what rastering the
// final page gives. After auth/join/completed's redirect, the room's sidebar frame loads once or
// twice (rooms_list_controller.js reloads it when UnreadRoomsChannel confirms, which can land
// before or after the first load: a race between the server's two answers). The DOM ends up the
// same, but with one load the phone sidebar toggle's circle kept tiles rastered earlier, a gray
// level off along its top arc from a fresh raster; with two it was rastered again. Which one won
// followed each server's speed, so it looked sticky within a run. Promoting the root to a layer of
// its own and back throws every layer's tiles away, so the screenshot is a fresh raster of the
// final page. Each change gets real time to reach a drawn frame: without the waits, the revert
// sometimes landed in the same frame, and errors/500's illustration came out either way. Without
// this, the arc followed the number of loads in 20 of 20 captures; with it, 42 of 42 captures
// (light and dark, one load or two) were identical, and errors/500 was 20 of 20.
const RASTER_SETTLE_MS = 150

async function rasterAfresh(page: Page) {
  if (page.context().browser()?.browserType().name() !== "chromium") return
  const style = await page.evaluate(() => {
    const root = document.documentElement
    const style = root.getAttribute("style")
    root.style.setProperty("will-change", "transform")
    return style
  })
  await page.waitForTimeout(RASTER_SETTLE_MS)
  await page.screenshot({ animations: "allow", caret: "hide" })
  await page.evaluate((style) => {
    const root = document.documentElement
    if (style === null) root.removeAttribute("style")
    else root.setAttribute("style", style)
  }, style)
  await page.waitForTimeout(RASTER_SETTLE_MS)
}

// What the Web Animations API can't pause (UA shadow DOM like Chromium's media-controls loading
// spinner, Chromium re-rasterizing a large downscaled image a few hundred ms after it appears in
// the lightbox) settles on its own: take frames until they have been identical for a full second.
const STABLE_FOR_MS = 1000
const FRAME_INTERVAL_MS = 250

async function stableScreenshot(page: Page, timeoutMs: number, mask: Locator[] = []): Promise<{ png: Buffer; stable: boolean }> {
  const shoot = () => page.screenshot({ animations: "allow", caret: "hide", scale: "device", timeout: timeoutMs, mask, maskColor: "#FF00FF" })
  const deadline = Date.now() + Math.min(timeoutMs, 15_000)
  let previous = await shoot()
  let since = Date.now()
  while (Date.now() < deadline) {
    await page.waitForTimeout(FRAME_INTERVAL_MS)
    const next = await shoot()
    if (!next.equals(previous)) {
      previous = next
      since = Date.now()
    } else if (Date.now() - since >= STABLE_FOR_MS) {
      return { png: next, stable: true }
    }
  }
  return { png: previous, stable: false }
}

function checkStatus(state: State, meta: CellMeta) {
  const expected = state.expect_status ?? 200
  if (meta.status !== expected) throw new Error(`expected HTTP ${expected}, got ${meta.status}`)
}

// Only the server under test is reachable; seeded external images are served from fixtures, and
// anything else external is refused, so no capture depends on the internet.
export function proxyOptions(target: Target, engine: string): BrowserContextOptions["proxy"] {
  if (!target.proxy) return undefined
  // Chromium never proxies loopback hosts unless told to; Firefox is told with a launch pref.
  return { server: target.proxy, bypass: engine === "chromium" ? "<-loopback>" : undefined }
}

async function isolateNetwork(context: BrowserContext, originUrl: string) {
  const origin = new URL(originUrl).origin
  await context.route((url) => url.origin !== origin && /^https?:$/.test(url.protocol), (route) => route.abort("blockedbyclient"))
  for (const [glob, file] of EXTERNAL_FIXTURES) {
    await context.route(glob, (route) => route.fulfill({ path: path.join(REPO_DIR, file) }))
  }
}

// A document's scripts arrive one at a time, in the order it asked for them, and only after it
// has been rendered once.
//
// Order: which module finishes loading first decides which controllers connect first, and so the
// order the page subscribes to cable channels in. When ReadRoomsChannel's subscribe went out
// before PresenceChannel's, the page received its own read broadcast; otherwise it didn't (the
// Redis subscription wasn't there yet). Serializing the responses makes that order the page's own.
//
// First rendering: autofocus happens early in a rendering update, and controllers focus things
// when they connect: rooms/opens/_form's filter_controller.js focuses its (scrollable) user menu,
// and in Firefox the menu kept focus in most captures and #room_name's autofocus in the rest. In a
// real browser the modules practically always arrive after the first frame; here they always do.
const FIRST_PAINT_WAIT_MS = 10_000

// Whether the document has been through a rendering update: a ResizeObserver's first callback runs
// in one (after autofocus), and nothing fakes it (paint timing entries are missing in Firefox and
// WebKit at times). If one had already run before it was installed, the next one also qualifies.
function renderedOnce(): boolean {
  const w = window as any
  if (!document.documentElement) return false
  if (!w.__parityRenderObserver) {
    w.__parityRenderObserver = new ResizeObserver(() => (w.__parityRendered = true))
    w.__parityRenderObserver.observe(document.documentElement)
  }
  return !!w.__parityRendered
}

async function waitForFirstRendering(frame: Frame, url: string, meta: CellMeta) {
  const deadline = Date.now() + FIRST_PAINT_WAIT_MS
  while (Date.now() < deadline) {
    if (frame.isDetached() || (await frame.evaluate(renderedOnce).catch(() => false))) return
    await new Promise((resolve) => setTimeout(resolve, 20))
  }
  meta.console.push(`harness: ${url} released without a rendering update after ${FIRST_PAINT_WAIT_MS}ms`)
}

async function scriptsInOrder(context: BrowserContext, originUrl: string, meta: CellMeta) {
  const origin = new URL(originUrl).origin
  const queues = new WeakMap<Frame, Promise<void>>() // per frame: the previous script, loaded
  const finished = new Map<Request, () => void>()
  const done = (request: Request) => {
    finished.get(request)?.()
    finished.delete(request)
  }
  context.on("requestfinished", done)
  context.on("requestfailed", done)
  await context.route((url) => url.origin === origin, async (route, request) => {
    if (request.resourceType() !== "script") return route.fallback()
    let frame: Frame
    try {
      frame = request.frame()
    } catch {
      return route.fallback() // a worker's script
    }
    const previous = queues.get(frame) ?? Promise.resolve()
    const loaded = new Promise<void>((resolve) => finished.set(request, resolve))
    const timeout = () => new Promise<void>((resolve) => setTimeout(resolve, FIRST_PAINT_WAIT_MS))
    queues.set(frame, previous.then(() => Promise.race([loaded, timeout()])))
    try {
      await previous
      await waitForFirstRendering(frame, request.url(), meta)
      await route.fallback()
    } catch {
      // the page went away meanwhile
    }
  })
}

let userAgents: Record<string, string> | undefined
function userAgent(name: string): string {
  userAgents ??= YAML.parse(fs.readFileSync(path.join(PARITY_DIR, "seeds/user_agents.yml"), "utf8")) ?? {}
  return userAgents![name] ?? name
}
