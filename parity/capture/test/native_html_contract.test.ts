import { test } from "node:test"
import assert from "node:assert/strict"
import fs from "node:fs"
import os from "node:os"
import path from "node:path"
import { PNG } from "pngjs"
import { Allowlist } from "../allowlist.ts"
import { compareJob } from "../compare.ts"
import { normalizeDocument } from "../normalize.ts"
import type { Job } from "../inventory.ts"

const cell = { engine: "chromium", viewport: { name: "desktop", width: 1280, height: 800, deviceScaleFactor: 1, touch: false, mobile: false }, scheme: "light" } as const
const controlState = "auth/sign_in"
const login = '<!DOCTYPE html><html><body><form action="/session" method="post"><input name="email_address"><input name="password" type="password"><button>Go</button></form></body></html>'
const retired = '<!DOCTYPE html><html><body><h1>Upgrade to a supported web browser</h1></body></html>'
const network = "GET /session/new (navigation) → 200\n  headers: content-type referrer-policy x-content-type-options x-frame-options\n  content-type: text/html; charset=utf-8\n  body: sha256:1111111111111111\n"
const receipt = { actor: "main", request: "GET /session/new (navigation)", status: 200, protocol: "http:", cookies: [], headers: { "content-type": "text/html; charset=utf-8", "x-content-type-options": "nosniff", "x-frame-options": "SAMEORIGIN", "referrer-policy": "strict-origin-when-cross-origin" } }
const native = { nativeComparison: true }

function capture(dir: string, target: string, state: string, html: string, color: number) {
  const base = path.join(dir, target, state, "chromium-desktop-light")
  fs.mkdirSync(path.dirname(base), { recursive: true })
  fs.writeFileSync(base + ".json", JSON.stringify({ state, target, kind: "page", status: 200, pageErrors: [], finalUrl: "http://capture.test/session/new" }))
  fs.writeFileSync(base + ".server.norm.html", normalizeDocument(html))
  fs.writeFileSync(base + ".live.norm.html", normalizeDocument(html))
  fs.writeFileSync(base + ".aria.yml", html === login ? '- button "Go"\n' : '- heading "Upgrade to a supported web browser"\n')
  fs.writeFileSync(base + ".network.txt", network)
  fs.writeFileSync(base + ".network.contracts.json", JSON.stringify([receipt]))
  fs.writeFileSync(base + ".cable.txt", "(no frames)\n")
  const png = new PNG({ width: 1, height: 1 })
  png.data.set([color, 0, 0, 255])
  fs.writeFileSync(base + ".png", PNG.sync.write(png))
  return base
}

function fixture(state = "auth/incompatible_browser") {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "campfire-native-html-"))
  const expected = capture(dir, "reference", state, retired, 0)
  const actual = capture(dir, "candidate", state, login, 255)
  const control = capture(dir, "candidate", controlState, login, 255)
  const job: Job = { state: { id: state, path: "/session/new", seed: "default", steps: [] }, cell }
  if (["auth/incompatible_browser", "auth/incompatible_browser/apple_messages"].includes(state)) {
    for (const base of [actual, control]) {
      fs.writeFileSync(base + ".network.txt", realNativeLoginNetwork)
      fs.writeFileSync(base + ".network.contracts.json", JSON.stringify([realNativeLoginReceipt]))
    }
  }
  return { dir, expected, actual, control, job, compare: (profile = native) => compareJob(job, dir, "reference", "candidate", new Allowlist([]), profile) }
}



// C6 regression: compareJob is the production consumer of independently captured files.
// These literal raw inputs model native sign-in, rather than asking a normalizer to
// generate its own expected receipt. No production export or testing flag is added.
const realNativeLoginNetwork = "GET /session/new (navigation) → 200\n  headers: cache-control content-encoding content-type etag link pragma set-cookie vary x-cache\n  cache-control: max-age=0, must-revalidate, private\n  content-encoding: gzip\n  content-type: text/html; charset=utf-8\n  pragma: no-cache\n  vary: accept-encoding\n  set-cookie: _campfire_session; httponly; path=/; samesite=lax\n  body: sha256:ba6a563cd26d0dc5\n"
const realNativeLoginReceipt = {
  actor: "main", request: "GET /session/new (navigation)", status: 200, protocol: "http:",
  originalBlock: realNativeLoginNetwork.trimEnd(), bodyHash: "ba6a563cd26d0dc5",
  headers: { date: "Tue, 06 Oct 2026 09:53:06 GMT", pragma: "no-cache", "cache-control": "max-age=0, private, must-revalidate", vary: "Accept-Encoding", "content-type": "text/html; charset=utf-8", "content-encoding": "gzip", "x-content-type-options": "nosniff", "x-frame-options": "SAMEORIGIN", "referrer-policy": "strict-origin-when-cross-origin" },
  cookies: [{ name: ".Campfire.Csrf", cleared: false, malformed: false, attributes: { path: "/", httponly: "", samesite: "lax" } as Record<string,string> }],
}

function realNativeRetiredFixture(state = "auth/incompatible_browser") {
  const f = fixture(state)
  for (const base of [f.actual, f.control]) {
    fs.writeFileSync(base + ".network.txt", realNativeLoginNetwork)
    fs.writeFileSync(base + ".network.contracts.json", JSON.stringify([realNativeLoginReceipt]))
  }
  return f
}

function signedOutFixture() {
  const f = fixture("auth/signed_out_redirect")
  f.job.state.path = "/rooms/5"
  const nativeLogin = login.replace('<input name="email_address">', '<input id="returnUrl" name="returnUrl" type="hidden" value="/rooms/5"><input name="email_address">')
  for (const [base, html, finalUrl] of [[f.expected, login, "http://capture.test/session/new"], [f.actual, nativeLogin, "http://capture.test/session/new?returnUrl=%2Frooms%2F5"]]) {
    fs.writeFileSync(base + ".json", JSON.stringify({ kind: "page", status: 200, pageErrors: [], finalUrl }))
    for (const ext of [".server.norm.html", ".live.norm.html"]) fs.writeFileSync(base + ext, normalizeDocument(html))
    fs.writeFileSync(base + ".aria.yml", '- button "Go"\n')
  }
  fs.copyFileSync(f.expected + ".png", f.actual + ".png")
  const write = (base: string, actual: boolean) => {
    const location = actual ? "http://capture.test/session/new?returnUrl=%2Frooms%2F5" : "http://capture.test/session/new"
    const finalRequest = actual ? "GET /session/new?returnUrl=%2Frooms%2F5 (navigation)" : "GET /session/new (navigation)"
    const bodyHash = actual ? "2222222222222222" : "1111111111111111"
    const challengeHeaders: Record<string,string> = actual ? { location } : { "cache-control": "no-cache", "content-type": "text/html; charset=utf-8", location }
    const finalHeaders = { "cache-control": "max-age=0, must-revalidate, private", "content-type": "text/html; charset=utf-8", "content-encoding": "gzip", vary: "accept-encoding", ...(actual ? { pragma: "no-cache" } : {}) }
    const block = (request: string, status: number, headers: Record<string,string>, body: string) => [request + ` → ${status}`, "  headers: " + Object.keys(headers).sort().join(" "), ...Object.entries(headers).map(([name,value]) => `  ${name}: ${value}`), "  body: " + body].join("\n")
    const challenge = block("GET /rooms/5 (navigation)", 302, challengeHeaders, "no body (redirect)")
    const final = block(finalRequest, 200, finalHeaders, "sha256:" + bodyHash)
    fs.writeFileSync(base + ".network.txt", challenge + "\n" + final + "\n")
    fs.writeFileSync(base + ".network.contracts.json", JSON.stringify([
      { request: "GET /rooms/5 (navigation)", status: 302, protocol: "http:", headers: { ...challengeHeaders, ...receipt.headers }, cookies: [], originalBlock: challenge },
      { request: finalRequest, status: 200, protocol: "http:", headers: { ...receipt.headers, ...finalHeaders }, cookies: [], originalBlock: final, bodyHash },
    ].map((value, index) => { delete (value.headers as Record<string,string>).location; if (actual && index === 0) delete (value.headers as Record<string,string>)["content-type"]; return value })))
  }
  write(f.expected, false); write(f.actual, true)
  return f
}

test("signed-out returnUrl compares only the exact room challenge and independently equal sign-in documents", () => {
  const f = signedOutFixture()
  try {
    const result = f.compare()
    assert.equal(result.status, "pass")
    assert.ok(result.networkContract?.applied.some(rule => rule.rule.includes("challenge contract")))
    assert.equal(f.compare({ nativeComparison: false }).status, "fail")
  } finally { fs.rmSync(f.dir, { recursive: true, force: true }) }
})

test("signed-out redirect allowances reject adjacent route, document, raw-header and status mutations", () => {
  type F = ReturnType<typeof signedOutFixture>
  const raw = (f: F, change: (value: any[]) => void) => { const values = JSON.parse(fs.readFileSync(f.actual + ".network.contracts.json", "utf8")); change(values); fs.writeFileSync(f.actual + ".network.contracts.json", JSON.stringify(values)) }
  const cases: [string, (f: F) => void][] = [
    ["other state", f => { f.job.state.id = "auth/sign_in" }],
    ["wrong original path", f => { f.job.state.path = "/rooms/6" }],
    ["wrong return room", f => { for (const ext of [".server.norm.html", ".live.norm.html"]) fs.writeFileSync(f.actual + ext, fs.readFileSync(f.actual + ext, "utf8").replace('/rooms/5', '/rooms/6')) }],
    ["visible return field", f => { for (const ext of [".server.norm.html", ".live.norm.html"]) fs.writeFileSync(f.actual + ext, fs.readFileSync(f.actual + ext, "utf8").replace('type="hidden"', 'type="text"')) }],
    ["duplicate return field", f => { for (const ext of [".server.norm.html", ".live.norm.html"]) fs.writeFileSync(f.actual + ext, fs.readFileSync(f.actual + ext, "utf8").replace(/(^.*name="returnUrl".*$)/m, '$1\n$1')) }],
    ["wrong form method", f => { for (const ext of [".server.norm.html", ".live.norm.html"]) fs.writeFileSync(f.actual + ext, fs.readFileSync(f.actual + ext, "utf8").replace('method="post"', 'method="get"')) }],
    ["malformed form URL", f => { for (const ext of [".server.norm.html", ".live.norm.html"]) fs.writeFileSync(f.actual + ext, fs.readFileSync(f.actual + ext, "utf8").replace('action="/session"', 'action="http://[invalid"')) }],
    ["wrong document", f => fs.appendFileSync(f.actual + ".server.norm.html", '<p>Wrong document</p>\n')],
    ["wrong ARIA", f => fs.appendFileSync(f.actual + ".aria.yml", '- alert "Wrong"\n')],
    ["page error", f => { const meta = JSON.parse(fs.readFileSync(f.actual + ".json", "utf8")); meta.pageErrors = ["TypeError"]; fs.writeFileSync(f.actual + ".json", JSON.stringify(meta)) }],
    ["extra query", f => { const meta = JSON.parse(fs.readFileSync(f.actual + ".json", "utf8")); meta.finalUrl += '&other=1'; fs.writeFileSync(f.actual + ".json", JSON.stringify(meta)) }],
    ["cross origin", f => { const meta = JSON.parse(fs.readFileSync(f.actual + ".json", "utf8")); meta.finalUrl = meta.finalUrl.replace('capture.test', 'evil.test'); fs.writeFileSync(f.actual + ".json", JSON.stringify(meta)) }],
    ["missing security", f => raw(f, values => { delete values[0].headers['x-frame-options'] })],
    ["wrong raw location", f => raw(f, values => { values[0].headers.location = 'http://capture.test/session/new?returnUrl=%2Frooms%2F6' })],
    ["missing captured Location", f => raw(f, values => { values[0].originalBlock = values[0].originalBlock.replace(/^  location: .*\n/m, '') })],
    ["mismatched captured Location", f => raw(f, values => { values[0].originalBlock = values[0].originalBlock.replace('returnUrl=%2Frooms%2F5', 'returnUrl=%2Frooms%2F6') })],
    ["duplicate captured Location", f => raw(f, values => { values[0].originalBlock += '\n  location: http://capture.test/session/new?returnUrl=%2Frooms%2F5' })],
    ["wrong raw status", f => raw(f, values => { values[0].status = 301 })],
    ["raw body differs", f => raw(f, values => { values[1].bodyHash = '3333333333333333' })],
    ["missing body hash", f => raw(f, values => { delete values[1].bodyHash })],
    ["unapproved challenge cache header", f => raw(f, values => { values[0].headers['cache-control'] = 'public' })],
    ["unapproved raw content type", f => raw(f, values => { values[0].headers['content-type'] = 'application/json' })],
    ["duplicate raw response", f => raw(f, values => { values.push(values[0]) })],
    ["extra request", f => fs.appendFileSync(f.actual + '.network.txt', 'GET /other → 200\n  headers: content-type\n  content-type: text/plain\n  body: sha256:3333333333333333\n')],
  ]
  for (const [name, change] of cases) {
    const f = signedOutFixture()
    try { change(f); assert.notEqual(f.compare().status, "pass", name) }
    finally { fs.rmSync(f.dir, { recursive: true, force: true }) }
  }
})

test("real native retired-browser control accepts sign-in Pragma and a session-only CSRF cookie", () => {
  for (const state of ["auth/incompatible_browser", "auth/incompatible_browser/apple_messages"]) {
    const f = realNativeRetiredFixture(state)
    try {
      const result = f.compare()
      assert.equal(result.status, "allowed", "valid actual native/control pair must not receive asymmetric Rails Pragma normalization")
      assert.deepEqual(result.layers.filter(layer => layer.nativeContract).map(layer => layer.layer).sort(), ["aria", "live", "network", "pixels", "server"])
      assert.equal(f.compare({ nativeComparison: false }).status, "fail")
    } finally { fs.rmSync(f.dir, { recursive: true, force: true }) }
  }
})

test("real native retired-browser control rejects nearby raw-policy and semantic mutations", () => {
  type F = ReturnType<typeof realNativeRetiredFixture>
  const bothRaw = (f: F, mutate: (value: typeof realNativeLoginReceipt) => void) => {
    for (const base of [f.actual, f.control]) {
      const value = structuredClone(realNativeLoginReceipt)
      mutate(value)
      fs.writeFileSync(base + ".network.contracts.json", JSON.stringify([value]))
    }
  }
  const cases: [string, (f: F) => void][] = [
    ["identically wrong Pragma in raw and text", f => { for (const base of [f.actual, f.control]) fs.writeFileSync(base + ".network.txt", realNativeLoginNetwork.replace("pragma: no-cache", "pragma: private")); bothRaw(f, value => { value.headers.pragma = "private"; value.originalBlock = realNativeLoginNetwork.replace("pragma: no-cache", "pragma: private").trimEnd() }) }],
    ["identically wrong Cache-Control in raw and text", f => { for (const base of [f.actual, f.control]) fs.writeFileSync(base + ".network.txt", realNativeLoginNetwork.replace("max-age=0, must-revalidate, private", "public, max-age=3600")); bothRaw(f, value => { value.headers["cache-control"] = "public, max-age=3600"; value.originalBlock = realNativeLoginNetwork.replace("max-age=0, must-revalidate, private", "public, max-age=3600").trimEnd() }) }],
    ["identical raw Pragma disagrees with text", f => bothRaw(f, value => { value.headers.pragma = "private" })],
    ["identical raw Cache-Control disagrees with text", f => bothRaw(f, value => { value.headers["cache-control"] = "public, max-age=3600" })],
    ["identical duplicate sign-in receipt", f => { for (const base of [f.actual, f.control]) fs.writeFileSync(base + ".network.contracts.json", JSON.stringify([realNativeLoginReceipt, realNativeLoginReceipt])) }],
    ["identically missing security", f => bothRaw(f, value => { delete (value.headers as Record<string,string>)["x-content-type-options"] })],
    ["identically wrong security", f => bothRaw(f, value => { value.headers["x-frame-options"] = "ALLOWALL" })],
    ["identically malformed cookie", f => bothRaw(f, value => { value.cookies[0].malformed = true })],
    ["identically wrong HttpOnly", f => bothRaw(f, value => { value.cookies[0].attributes.httponly = "foo" })],
    ["identically wrong Secure", f => bothRaw(f, value => { value.cookies[0].attributes.secure = "foo" })],
    ["identically wrong cookie path", f => bothRaw(f, value => { value.cookies[0].attributes.path = "/other" })],
    ["identically wrong SameSite", f => bothRaw(f, value => { value.cookies[0].attributes.samesite = "none" })],
    ["identically persistent CSRF expiry", f => bothRaw(f, value => { value.cookies[0].attributes.expires = "Wed, 07 Oct 2026 09:53:06 GMT" })],
    ["identically persistent CSRF Max-Age", f => bothRaw(f, value => { value.cookies[0].attributes["max-age"] = "300" })],
    ["missing actual receipts", f => fs.rmSync(f.actual + ".network.contracts.json")],
    ["missing control receipts", f => fs.rmSync(f.control + ".network.contracts.json")],
    ["changed actual body", f => fs.writeFileSync(f.actual + ".network.txt", realNativeLoginNetwork.replace("ba6a563cd26d0dc5", "2222222222222222"))],
    ["changed actual status", f => fs.writeFileSync(f.actual + ".json", JSON.stringify({ kind: "page", status: 201, pageErrors: [] }))],
    ["changed actual Pragma value", f => fs.writeFileSync(f.actual + ".network.txt", realNativeLoginNetwork.replace("pragma: no-cache", "pragma: private"))],
    ["changed actual Pragma shape", f => fs.writeFileSync(f.actual + ".network.txt", realNativeLoginNetwork.replace(" link pragma set-cookie", " link set-cookie").replace("  pragma: no-cache\n", ""))],
    ["unreceipted identical extra request", f => { for (const base of [f.actual, f.control]) fs.appendFileSync(base + ".network.txt", realNativeLoginNetwork.replace("/session/new", "/unexpected")) }],
    ["identical extra receipt", f => { for (const base of [f.actual, f.control]) fs.writeFileSync(base + ".network.contracts.json", JSON.stringify([realNativeLoginReceipt, { ...realNativeLoginReceipt, request: "GET /unexpected", originalBlock: realNativeLoginNetwork.replace("/session/new", "/unexpected").trimEnd() }])) }],
    ["actual document changed", f => fs.appendFileSync(f.actual + ".live.norm.html", "<p>wrong</p>\n")],
    ["control page error", f => fs.writeFileSync(f.control + ".json", JSON.stringify({ kind: "page", status: 200, pageErrors: ["AbortError"] }))],
    ["control capture error", f => fs.writeFileSync(f.control + ".json", JSON.stringify({ kind: "page", status: 200, pageErrors: [], error: "not ready" }))],
  ]
  for (const [name, mutate] of cases) {
    const f = realNativeRetiredFixture()
    try {
      mutate(f)
      const result = f.compare()
      assert.equal(result.status, "fail", name)
      assert.equal(result.layers.some(layer => layer.nativeContract), false, name)
    } finally { fs.rmSync(f.dir, { recursive: true, force: true }) }
  }
})

test("native retired-browser rendering needs an independent exact same-cell sign-in control", () => {
  for (const state of ["auth/incompatible_browser", "auth/incompatible_browser/apple_messages"]) {
    const f = fixture(state)
    try {
      const result = f.compare()
      assert.equal(result.status, "allowed")
      assert.deepEqual(result.layers.filter((layer) => layer.nativeContract).map((layer) => layer.layer).sort(), ["aria", "live", "network", "pixels", "server"])
      assert.equal(f.compare({ nativeComparison: false }).status, "fail", "generic comparisons do not retire a page")
    } finally { fs.rmSync(f.dir, { recursive: true, force: true }) }
  }
})

test("retired-browser contract retains changed status, security, body, DOM, ARIA, pixels, Cable and missing controls", () => {
  const mutations: [string, (f: ReturnType<typeof fixture>) => void][] = [
    ["status", (f) => fs.writeFileSync(f.actual + ".json", JSON.stringify({ kind: "page", status: 201, pageErrors: [] }))],
    ["security header", (f) => fs.writeFileSync(f.actual + ".network.contracts.json", JSON.stringify([{ ...receipt, headers: { ...receipt.headers, "x-content-type-options": "wrong" } }]))],
    ["extra request", (f) => fs.appendFileSync(f.actual + ".network.txt", network.replace("/session/new", "/unexpected"))],
    ["network body", (f) => fs.writeFileSync(f.actual + ".network.txt", network.replace("1111111111111111", "2222222222222222"))],
    ["missing native raw receipt", (f) => fs.rmSync(f.actual + ".network.contracts.json")],
    ["missing control raw receipt", (f) => fs.rmSync(f.control + ".network.contracts.json")],
    ["identically invalid native/control cookies", (f) => {
      const bad = { ...receipt, cookies: [{ name: "session_token", cleared: false, malformed: false, attributes: { path: "/", httponly: "", samesite: "none" } }] }
      for (const base of [f.actual, f.control]) {
        fs.writeFileSync(base + ".network.contracts.json", JSON.stringify([bad]))
        fs.writeFileSync(base + ".network.txt", network.replace("  body:", "  set-cookie: session_token; path=/; httponly; samesite=none\n  body:"))
      }
    }],
    ["server body", (f) => fs.appendFileSync(f.actual + ".server.norm.html", "<p>wrong</p>\n")],
    ["live DOM", (f) => fs.appendFileSync(f.actual + ".live.norm.html", "<p>wrong</p>\n")],
    ["ARIA", (f) => fs.appendFileSync(f.actual + ".aria.yml", '- alert "Wrong"\n')],
    ["pixels", (f) => { const png = new PNG({ width: 1, height: 1 }); png.data.set([128, 0, 0, 255]); fs.writeFileSync(f.actual + ".png", PNG.sync.write(png)) }],
    ["Cable", (f) => fs.appendFileSync(f.actual + ".cable.txt", "unexpected frame\n")],
    ["page error", (f) => fs.writeFileSync(f.actual + ".json", JSON.stringify({ kind: "page", status: 200, pageErrors: ["AbortError"] }))],
    ["missing control", (f) => fs.rmSync(f.control + ".json")],
    ["missing control body", (f) => fs.rmSync(f.control + ".server.norm.html")],
    ["control failed", (f) => fs.writeFileSync(f.control + ".json", JSON.stringify({ kind: "page", status: 200, pageErrors: [], error: "not ready" }))],
  ]
  for (const [name, mutate] of mutations) {
    const f = fixture()
    try {
      mutate(f)
      assert.equal(f.compare().status, name === "page error" ? "error" : "fail", name)
      assert.equal(f.compare().layers.some((layer) => layer.nativeContract), false, name)
    } finally { fs.rmSync(f.dir, { recursive: true, force: true }) }
  }
  const other = fixture("auth/incompatible_browser/other")
  try { assert.equal(other.compare().status, "fail") } finally { fs.rmSync(other.dir, { recursive: true, force: true }) }
})

test("navigation hashes require both final-document semantic owners and real raw security evidence", () => {
  const f = fixture("auth/sign_in/error")
  try {
    for (const ext of [".server.norm.html", ".live.norm.html", ".aria.yml", ".png"]) fs.copyFileSync(f.actual + ext, f.expected + ext)
    fs.writeFileSync(f.actual + ".network.txt", network.replace("1111111111111111", "2222222222222222"))
    const networkLayer = () => f.compare().layers.find((layer) => layer.layer === "network")!
    assert.equal(networkLayer().equal, true)
    fs.appendFileSync(f.actual + ".live.norm.html", "changed live message\n")
    assert.equal(networkLayer().equal, false)
    fs.copyFileSync(f.expected + ".live.norm.html", f.actual + ".live.norm.html")
    fs.appendFileSync(f.actual + ".network.txt", network.replace("/session/new", "/intermediate").replace("1111111111111111", "3333333333333333"))
    assert.equal(networkLayer().equal, false, "unowned intermediate document remains a difference")
    fs.writeFileSync(f.actual + ".network.txt", network.replace("1111111111111111", "2222222222222222"))
    fs.rmSync(f.actual + ".network.contracts.json")
    assert.equal(networkLayer().equal, false, "missing raw security evidence fails closed")
    fs.writeFileSync(f.actual + ".network.txt", network)
    assert.equal(networkLayer().equal, false, "equal text cannot satisfy missing required evidence")
  } finally { fs.rmSync(f.dir, { recursive: true, force: true }) }
})

test("native HTML comparer applies retained boost/account contracts without hiding adjacent body changes", () => {
  const f = fixture("account/edit/admin")
  const token = '<input name="authenticity_token" type="hidden" value="random">'
  const account = '<form action="/account.873240054" data-controller="form" method="post"><input name="_method" type="hidden" value="patch"><input name="account[name]" value="Campfire"></form>'
  const boost = '<form action="/messages/5/boosts/6" class="button_to" method="post"><input name="_method" type="hidden" value="delete"><button data-action="boost-delete#perform">Delete</button>TOKEN</form>'
  try {
    const layers = () => f.compare().layers.filter((layer) => layer.layer === "server" || layer.layer === "live")
    for (const [expected, actual] of [[account, account.replace("/account.873240054", "/account")], [boost.replace("TOKEN", token), boost.replace("TOKEN", "")]]) {
      for (const ext of [".server.norm.html", ".live.norm.html"]) {
        fs.writeFileSync(f.expected + ext, normalizeDocument(expected))
        fs.writeFileSync(f.actual + ext, normalizeDocument(actual))
      }
      assert.equal(layers().every((layer) => layer.equal), true)
      assert.equal(f.compare({ nativeComparison: false }).layers.find((layer) => layer.layer === "server")!.equal, false)
    }
    fs.appendFileSync(f.actual + ".live.norm.html", "wrong message\n")
    assert.equal(layers().find((layer) => layer.layer === "live")!.equal, false)
    fs.writeFileSync(f.expected + ".server.norm.html", normalizeDocument(boost.replace("TOKEN", token + token), native))
    assert.equal(layers().find((layer) => layer.layer === "server")!.equal, false, "reapplying the profile to retained captures must preserve duplicate token fields")
  } finally { fs.rmSync(f.dir, { recursive: true, force: true }) }
})


// A real file-boundary consumer: literal raw response policies plus separately captured
// server/live/ARIA owners, not expected values generated by the contract normalizer.
function narrowEndpointFixture(kind: "401" | "manifest" | "account") {
  const f = fixture(kind === "account" ? "account/edit/saved" : kind === "401" ? "auth/sign_in/error" : "auth/sign_in")
  const write = (base: string, request: string, status: number, values: Record<string,string>, body: string, rawValues: Record<string,string> = values) => {
    const text = [`${request} → ${status}`, `  headers: ${Object.keys(values).sort().join(" ")}`, ...Object.entries(values).map(([name,value]) => `  ${name}: ${value}`), `  body: ${body}`].join("\n") + "\n"
    fs.writeFileSync(base + ".network.txt", text)
    fs.writeFileSync(base + ".network.contracts.json", JSON.stringify([{ actor: "main", request, status, protocol: "http:", originalBlock: text.trimEnd(), headers: { ...realNativeLoginReceipt.headers, ...rawValues }, cookies: [], bodyHash: body.startsWith("sha256:") ? body.slice(7) : undefined }]))
  }
  for (const base of [f.actual, f.expected]) {
    const actual = base === f.actual
    const action = actual ? "/account" : "/account.873240054"
    const html = kind === "account" ?
      '<form class="txt--medium" data-controller="form" enctype="multipart/form-data" action="' + action + '" method="post"><input name="_method" type="hidden" value="patch"><input type="file" name="account[logo]"></form>' +
      '<form data-controller="form" enctype="multipart/form-data" action="' + action + '" method="post"><input name="_method" type="hidden" value="patch"><input type="file" name="account[logo]"></form>' +
      '<form class="flex flex-column gap" data-controller="form" action="' + action + '" method="post"><input name="_method" type="hidden" value="patch"><input type="text" id="account_name" name="account[name]" value="37signals HQ"></form>' +
      '<form class="flex align-center gap center" data-controller="form" action="' + action + '" method="post"><input name="_method" type="hidden" value="put"><input type="hidden" name="account[settings][restrict_room_creation_to_administrators]" value="true"></form><p>Saved</p>' :
      '<form action="/session" method="post"><p>Incorrect email or password</p></form>'
    fs.writeFileSync(base + ".server.html", html)
    for (const ext of [".server.norm.html", ".live.norm.html"]) fs.writeFileSync(base + ext, normalizeDocument(html, native))
    fs.writeFileSync(base + ".aria.yml", kind === "account" ? '- textbox "Account name": 37signals HQ\n- status "Saved"\n' : '- alert "Incorrect email or password"\n')
    if (base !== f.actual) fs.copyFileSync(f.actual + ".png", base + ".png")
    fs.writeFileSync(base + ".json", JSON.stringify({ kind: "page", status: 200, pageErrors: [], finalUrl: kind === "account" ? "http://capture.test/account/edit" : "http://capture.test/session/new" }))
    if (kind === "401") write(base, "POST /session", 401, { "content-type": "text/html; charset=utf-8", "cache-control": "no-cache", ...(actual ? { pragma: "no-cache" } : {}) }, "not compared (not a GET)")
    if (kind === "manifest") write(base, "GET /webmanifest.json", 200, { "content-type": "application/json; charset=utf-8", "cache-control": "max-age=0, must-revalidate, private", "content-encoding": "gzip", vary: actual ? "accept, accept-encoding" : "accept-encoding" }, "sha256:2c2aaf5a651296e1", { "content-type": "application/json; charset=utf-8", "cache-control": "max-age=0, private, must-revalidate", "content-encoding": "gzip", vary: actual ? "Accept, Accept-Encoding" : "Accept-Encoding" })
    if (kind === "account") write(base, actual ? "POST /account" : "POST /account.873240054", 302, { "content-type": "text/html; charset=utf-8", "cache-control": "no-cache", location: "http://capture.test/account/edit" }, "no body (redirect)")
  }
  const change = (base: string, old: string, replacement: string, raw?: (receipt: any) => void) => {
    const text = fs.readFileSync(base + ".network.txt", "utf8").replace(old, replacement)
    fs.writeFileSync(base + ".network.txt", text)
    const receipts = JSON.parse(fs.readFileSync(base + ".network.contracts.json", "utf8"))
    receipts[0].originalBlock = text.trimEnd()
    if (raw) raw(receipts[0])
    fs.writeFileSync(base + ".network.contracts.json", JSON.stringify(receipts))
  }
  return { ...f, change }
}

for (const kind of ["401", "manifest", "account"] as const) {
  test(`C6 narrow endpoint policies accept the approved ${kind} difference`, () => {
    const f = narrowEndpointFixture(kind)
    try {
      assert.equal(f.compare().status, "pass", kind)
      assert.ok(f.compare().networkContract?.applied.some(receipt => receipt.rule.includes(kind === "401" ? "401 sign-in" : kind === "manifest" ? "webmanifest" : "account form owner")), kind)
      assert.equal(f.compare({ nativeComparison: false }).status, "fail", "no native profile must keep the raw difference")
    } finally { fs.rmSync(f.dir, { recursive: true, force: true }) }
  })
}

test("C6 narrow endpoint policies reject nearby body, status, raw policy, ownership and unknown changes", () => {
  type F = ReturnType<typeof narrowEndpointFixture>
  const cases: ["401" | "manifest" | "account", string, (f: F) => void][] = [
    ["401", "wrong raw Pragma", f => f.change(f.actual, "pragma: no-cache", "pragma: private", raw => { raw.headers.pragma = "private" })],
    ["401", "raw Pragma disagrees", f => f.change(f.actual, "nothing", "nothing", raw => { raw.headers.pragma = "private" })],
    ["401", "wrong status", f => f.change(f.actual, "→ 401", "→ 403", raw => { raw.status = 403 })],
    ["401", "wrong rejection DOM", f => fs.appendFileSync(f.actual + ".live.norm.html", '<p>Different rejection</p>\n')],
    ["401", "wrong rejection ARIA", f => fs.appendFileSync(f.actual + ".aria.yml", '- alert "Wrong"\n')],
    ["401", "changed raw body marker", f => f.change(f.actual, "not compared (not a GET)", "sha256:1234567890abcdef")],
    ["401", "wrong cache policy", f => f.change(f.actual, "cache-control: no-cache", "cache-control: public", raw => { raw.headers["cache-control"] = "public" })],
    ["401", "other path", f => f.change(f.actual, "POST /session", "POST /other", raw => { raw.request = "POST /other" })],
    ["manifest", "wrong JSON body", f => f.change(f.actual, "2c2aaf5a651296e1", "1234567890abcdef", raw => { raw.bodyHash = "1234567890abcdef" })],
    ["manifest", "wrong status", f => f.change(f.actual, "→ 200", "→ 201", raw => { raw.status = 201 })],
    ["manifest", "unknown Vary", f => f.change(f.actual, "vary: accept, accept-encoding", "vary: accept, authorization", raw => { raw.headers.vary = "Accept, Authorization" })],
    ["manifest", "wrong type", f => f.change(f.actual, "application/json", "text/plain", raw => { raw.headers["content-type"] = "text/plain; charset=utf-8" })],
    ["manifest", "wrong encoding", f => f.change(f.actual, "content-encoding: gzip", "content-encoding: br", raw => { raw.headers["content-encoding"] = "br" })],
    ["manifest", "wrong raw cache policy", f => f.change(f.actual, "nothing", "nothing", raw => { raw.headers["cache-control"] = "public, max-age=3600" })],
    ["manifest", "raw encoding disagrees", f => f.change(f.actual, "nothing", "nothing", raw => { raw.headers["content-encoding"] = "br" })],
    ["manifest", "missing raw body hash", f => f.change(f.actual, "nothing", "nothing", raw => { delete raw.bodyHash })],
    ["account", "wrong owner id", f => fs.writeFileSync(f.expected + ".server.html", '<form action="/account.99" method="post" data-controller="form"></form>')],
    ["account", "missing independent form owner", f => fs.rmSync(f.expected + ".server.html")],
    ["account", "wrong form method", f => fs.writeFileSync(f.actual + ".server.html", '<form action="/account" method="get" data-controller="form"></form>')],
    ["account", "identical DELETE hidden method", f => { for (const base of [f.actual, f.expected]) fs.writeFileSync(base + ".server.html", fs.readFileSync(base + ".server.html", "utf8").replace('value="patch"', 'value="delete"')) }],
    ["account", "shared arbitrary sibling method", f => { for (const base of [f.actual, f.expected]) fs.writeFileSync(base + ".server.html", fs.readFileSync(base + ".server.html", "utf8").replace('value="put"', 'value="trace"')) }],
    ["account", "one-sided sibling method", f => fs.writeFileSync(f.actual + ".server.html", fs.readFileSync(f.actual + ".server.html", "utf8").replace('value="put"', 'value="patch"'))],
    ["account", "one-sided form count", f => fs.writeFileSync(f.actual + ".server.html", fs.readFileSync(f.actual + ".server.html", "utf8").replace(/<form\b[^>]*>[\s\S]*?<\/form>/, ""))],
    ["account", "one-sided form order", f => { const file = f.actual + ".server.html", text = fs.readFileSync(file, "utf8"), forms = text.match(/<form\b[^>]*>[\s\S]*?<\/form>/g)!; fs.writeFileSync(file, forms[3] + forms[0] + forms[1] + forms[2] + '<p>Saved</p>') }],
    ["account", "sibling duplicate override", f => fs.writeFileSync(f.actual + ".server.html", fs.readFileSync(f.actual + ".server.html", "utf8").replace('value="put">', 'value="put"><input name="_method" type="hidden" value="put">'))],
    ["account", "different sibling account owner", f => fs.writeFileSync(f.expected + ".server.html", fs.readFileSync(f.expected + ".server.html", "utf8").replace('action="/account.873240054"', 'action="/account.99"'))],
    ["account", "missing name form anchor", f => { for (const base of [f.actual, f.expected]) fs.writeFileSync(base + ".server.html", fs.readFileSync(base + ".server.html", "utf8").replace('id="account_name"', 'id="other_name"')) }],
    ["account", "duplicate hidden method", f => fs.writeFileSync(f.actual + ".server.html", fs.readFileSync(f.actual + ".server.html", "utf8").replace('</form>', '<input name="_method" type="hidden" value="patch"></form>'))],
    ["account", "wrong redirected body", f => f.change(f.actual, "no body (redirect)", "sha256:1234567890abcdef")],
    ["account", "wrong redirect status", f => f.change(f.actual, "→ 302", "→ 307", raw => { raw.status = 307 })],
    ["account", "wrong redirect destination", f => f.change(f.actual, "location: http://capture.test/account/edit", "location: http://capture.test/other", raw => { raw.headers.location = "http://capture.test/other" })],
    ["account", "wrong final flash", f => fs.appendFileSync(f.actual + ".live.norm.html", '<p>Not saved</p>\n')],
    ["account", "wrong final URL", f => fs.writeFileSync(f.actual + ".json", JSON.stringify({ kind: "page", status: 200, pageErrors: [], finalUrl: "http://capture.test/other" }))],
    ["account", "different state cannot grant alias", f => { for (const base of [f.actual, f.expected]) fs.renameSync(path.dirname(base), path.join(path.dirname(path.dirname(base)), "admin")); f.job.state.id = "account/edit/admin" }],
  ]
  for (const [kind, name, mutate] of cases) {
    const f = narrowEndpointFixture(kind)
    try { mutate(f); assert.equal(f.compare().status, "fail", `${kind}: ${name}`) }
    finally { fs.rmSync(f.dir, { recursive: true, force: true }) }
  }
  for (const kind of ["401", "manifest", "account"] as const) {
    const policies: [string, (f: F) => void][] = [
      ["missing raw security", f => f.change(f.actual, "nothing", "nothing", raw => { delete raw.headers["x-frame-options"] })],
      ["wrong raw security", f => f.change(f.actual, "nothing", "nothing", raw => { raw.headers["x-frame-options"] = "ALLOWALL" })],
      ["duplicate response with different raw policy", f => { const file = f.actual + ".network.contracts.json"; const values = JSON.parse(fs.readFileSync(file, "utf8")); values.push({ ...structuredClone(values[0]), headers: { ...values[0].headers, pragma: "private" } }); fs.writeFileSync(file, JSON.stringify(values)) }],
      ["unknown extra response", f => { const extra = "GET /unknown → 200\n  headers: content-type\n  content-type: text/plain\n  body: sha256:1234567890abcdef\n"; fs.appendFileSync(f.actual + ".network.txt", extra); const file = f.actual + ".network.contracts.json"; const values = JSON.parse(fs.readFileSync(file, "utf8")); values.push({ ...structuredClone(values[0]), request: "GET /unknown", status: 200, originalBlock: extra.trimEnd(), cookies: [], headers: { ...values[0].headers, "content-type": "text/plain" }, bodyHash: "1234567890abcdef" }); fs.writeFileSync(file, JSON.stringify(values)) }],
    ]
    for (const [name, mutate] of policies) {
      const f = narrowEndpointFixture(kind)
      try { mutate(f); assert.equal(f.compare().status, "fail", `${kind}: ${name}`) }
      finally { fs.rmSync(f.dir, { recursive: true, force: true }) }
    }
  }
  for (const encoding of ["br", "identity", "absent"]) {
    const f = narrowEndpointFixture("manifest")
    try {
      for (const base of [f.actual, f.expected]) {
        f.change(base, "content-encoding: gzip", encoding === "absent" ? "content-encoding: " : `content-encoding: ${encoding}`, raw => { if (encoding === "absent") delete raw.headers["content-encoding"]; else raw.headers["content-encoding"] = encoding })
        if (encoding === "absent") {
          const file = base + ".network.txt", text = fs.readFileSync(file, "utf8").replace(" content-encoding", "").replace("  content-encoding: \n", "")
          fs.writeFileSync(file, text)
          const receipts = JSON.parse(fs.readFileSync(base + ".network.contracts.json", "utf8")); receipts[0].originalBlock = text.trimEnd(); fs.writeFileSync(base + ".network.contracts.json", JSON.stringify(receipts))
        }
      }
      assert.equal(f.compare().status, "fail", `manifest: identical ${encoding} is outside the approved gzip profile`)
    } finally { fs.rmSync(f.dir, { recursive: true, force: true }) }
  }
})

// Error acceptance is owned by compareJob on real saved capture files, not by an isolated
// predicate. Report JSON/HTML are distinct externally consumed evidence boundaries.
test("page-error acceptance rejects either target, empty reasons and fragment errors without hiding comparison layers", () => {
  for (const kind of ["page", "fragment"]) for (const profile of [{ nativeComparison: false }, { nativeComparison: true }]) {
    for (const [side, reasons] of [["expected", ["reference failure"]], ["actual", ["AbortError"]], ["actual", [""]], ["both", ["same failure", "same failure"]]] as const) {
      const f = fixture("auth/sign_in")
      try {
        for (const ext of [".server.norm.html", ".live.norm.html", ".aria.yml", ".png", ".network.txt", ".network.contracts.json", ".cable.txt"]) fs.copyFileSync(f.actual + ext, f.expected + ext)
        for (const target of ["expected", "actual"] as const) {
          const file = f[target] + ".json"
          const meta = JSON.parse(fs.readFileSync(file, "utf8"))
          meta.kind = kind
          meta.pageErrors = target === side || side === "both" ? [...reasons] : []
          fs.writeFileSync(file, JSON.stringify(meta))
        }
        // Layer allowances cannot forgive a runtime error, and differing layers remain visible.
        fs.appendFileSync(f.actual + ".server.norm.html", "<p>body mismatch</p>\n")
        const result = compareJob(f.job, f.dir, "reference", "candidate", new Allowlist([{ state: "**", reason: "test layer allowance", owner: "test" }]), profile)
        assert.equal(result.status, "error", `${kind} ${side} ${JSON.stringify(reasons)} ${profile.nativeComparison}`)
        assert.equal(result.layers.find(layer => layer.layer === "server")?.equal, false)
        assert.match(result.error!, /page error/)
        assert.equal(result.error!.includes(JSON.stringify(reasons[0])), true)
      } finally { fs.rmSync(f.dir, { recursive: true, force: true }) }
    }
  }
})

test("page-error reports retain both targets and make an empty error reason visible", async () => {
  const { writeReport } = await import("../report.ts")
  const f = fixture("auth/sign_in")
  try {
    for (const ext of [".server.norm.html", ".live.norm.html", ".aria.yml", ".png", ".network.txt", ".network.contracts.json", ".cable.txt"]) fs.copyFileSync(f.actual + ext, f.expected + ext)
    for (const [side, pageErrors] of [["expected", [""]], ["actual", ["AbortError: <unsafe>"]]] as const) {
      const file = f[side] + ".json"
      const meta = JSON.parse(fs.readFileSync(file, "utf8"))
      meta.pageErrors = [...pageErrors]
      fs.writeFileSync(file, JSON.stringify(meta))
    }
    const result = f.compare({ nativeComparison: false })
    const report = writeReport(f.dir, "errors", [result], { title: "owner error report", expected: "reference", actual: "candidate", startedAt: "2026-10-06T00:00:00Z", durationMs: 1, unusedAllowlist: [] })
    const json = JSON.parse(fs.readFileSync(path.join(f.dir, "errors.json"), "utf8"))
    assert.deepEqual(json.results[0].diagnostics.expected.pageErrors, [""])
    assert.deepEqual(json.results[0].diagnostics.actual.pageErrors, ["AbortError: <unsafe>"])
    assert.equal(json.info.counts.error, 1)
    const html = fs.readFileSync(report, "utf8")
    assert.match(html, /expected page errors/)
    assert.match(html, /actual page errors/)
    assert.match(html, /\[\s*&quot;&quot;\s*\]/)
    assert.match(html, /AbortError: &lt;unsafe&gt;/)
  } finally { fs.rmSync(f.dir, { recursive: true, force: true }) }
})

test("console-only reports preserve diagnostics while clean captures remain passing", async () => {
  const { writeReport } = await import("../report.ts")
  const f = fixture("auth/sign_in")
  try {
    for (const ext of [".server.norm.html", ".live.norm.html", ".aria.yml", ".png", ".network.txt", ".network.contracts.json", ".cable.txt"]) fs.copyFileSync(f.actual + ext, f.expected + ext)
    const file = f.actual + ".json"
    const meta = JSON.parse(fs.readFileSync(file, "utf8"))
    meta.console = ["HTTP 404 <diagnostic>"]
    fs.writeFileSync(file, JSON.stringify(meta))
    const result = f.compare({ nativeComparison: false })
    assert.equal(result.status, "pass")
    const report = writeReport(f.dir, "console", [result], { title: "owner console report", expected: "reference", actual: "candidate", startedAt: "2026-10-06T00:00:00Z", durationMs: 1, unusedAllowlist: [] })
    const json = JSON.parse(fs.readFileSync(path.join(f.dir, "console.json"), "utf8"))
    assert.deepEqual(json.results[0].diagnostics.actual.console, ["HTTP 404 <diagnostic>"])
    assert.deepEqual(json.results[0].diagnostics.actual.pageErrors, [])
    assert.match(fs.readFileSync(report, "utf8"), /HTTP 404 &lt;diagnostic&gt;/)
  } finally { fs.rmSync(f.dir, { recursive: true, force: true }) }
})
