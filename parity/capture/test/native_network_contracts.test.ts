import { test } from "node:test"
import assert from "node:assert/strict"
import { cookieContractReceipt, normalizeNativeNetworkPair, verifyNativeContractReceipts } from "../native_network_contracts.ts"
import type { NativeNetworkProof, NetworkContractReceipt } from "../native_network_contracts.ts"

const security = { "x-content-type-options": "nosniff", "x-frame-options": "SAMEORIGIN", "referrer-policy": "strict-origin-when-cross-origin" }
const date = "Tue, 06 Oct 2026 12:00:00 GMT"
const expires = "Thu, 05 Nov 2026 12:00:00 GMT"
const request = "GET /rooms/5 (navigation)"
const cookieAttrs = "httponly; path=/; samesite=lax"
function text(options: { request?: string; status?: number; values?: Record<string,string>; cookies?: string[]; body?: string; extraHeaders?: string[] } = {}): string {
  const values = options.values ?? { "content-type": "text/html; charset=utf-8" }
  const cookies = options.cookies ?? []
  return [`${options.request ?? request} → ${options.status ?? 200}`, `  headers: ${[...new Set([...Object.keys(values), ...(options.extraHeaders ?? []), ...(cookies.length ? ["set-cookie"] : [])])].sort().join(" ")}`,
    ...Object.entries(values).map(([name,value]) => `  ${name}: ${value}`), ...cookies.map(value => `  set-cookie: ${value}`), `  body: ${options.body ?? "sha256:1111111111111111"}`].join("\n") + "\n"
}
function raw(options: { request?: string; status?: number; headers?: Record<string,string>; cookies?: string[] } = {}): NetworkContractReceipt {
  return { request: options.request ?? request, status: options.status ?? 200, protocol: "http:", headers: { ...security, date, ...(options.headers ?? {}) }, cookies: (options.cookies ?? []).map(cookieContractReceipt) }
}
function pair(expected: string, actual: string, proof: NativeNetworkProof = { nativeCaptureSecurityVerified: true }) {
  return normalizeNativeNetworkPair(expected, actual, proof)
}
function equal(result: ReturnType<typeof pair>) { assert.equal(result.expected, result.actual); assert.equal(result.unproven.length, 0) }
function different(expected: string, actual: string, proof?: NativeNetworkProof) { const result = pair(expected, actual, proof); assert.notEqual(result.expected, result.actual) }

test("retained Vary blocks compare field-name sets while preserving missing gzip text variation", () => {
  const values = { "content-type": "audio/mpeg", "content-encoding": "gzip", vary: "accept-encoding" }
  const expected = text({ status: 206, values })
  equal(pair(expected, text({ status: 206, values: { ...values, vary: "Accept-Encoding, accept-encoding" } })))
  for (const vary of ["origin", "accept-encoding, origin", "*"]) different(expected, text({ status: 206, values: { ...values, vary } }))
  const { vary: _vary, ...absent } = values
  different(expected, text({ status: 206, values: absent }))
  different(text({ values: { ...values, "content-type": "text/html" } }), text({ values: { ...absent, "content-type": "text/html" } }))
})

test("approved Rails session reissuance is omitted only for ordinary issued secure-shape cookies", () => {
  for (const name of ["session_token", "_campfire_session"]) {
    equal(pair(text({ cookies: [`${name}; ${cookieAttrs}`] }), text()))
    for (const cookie of [`${name}; path=/; samesite=lax`, `${name}; httponly; path=/other; samesite=lax`, `${name}; httponly; path=/; samesite=none`, `${name} (cleared); ${cookieAttrs}`, `unknown; ${cookieAttrs}`]) different(text({ cookies: [cookie] }), text())
  }
  different(text({ cookies: [`session_token; ${cookieAttrs}`], request: "POST /session", status: 302 }), text({ request: "POST /session", status: 302 }))
  different(text({ cookies: [`session_token; ${cookieAttrs}`] }), text({ status: 404 }))
  different(text({ cookies: [`session_token; ${cookieAttrs}`] }), text({ body: "sha256:2222222222222222" }))
})

test("native auth retains sign-in issuance and checks its absolute thirty-day lifetime and attributes", () => {
  const options = { request: "POST /session", status: 302, cookies: [`session_token; ${cookieAttrs}`] }
  const receipt = raw({ request: options.request, status: 302, cookies: [`session_token=opaque; path=/; httponly; samesite=lax; expires=${expires}`] })
  equal(pair(text(options), text(options), { actualReceipts: [receipt] }))
  for (const mutation of ["Wed, 04 Nov 2026 12:00:00 GMT", "Fri, 06 Nov 2026 12:00:00 GMT", "not-a-date"]) {
    const bad = structuredClone(receipt); bad.cookies[0].attributes.expires = mutation
    assert.throws(() => pair(text(options), text(options), { actualReceipts: [bad] }), /lifetime invalid/)
  }
  const reissued = structuredClone(receipt); reissued.request = request; reissued.status = 200
  assert.throws(() => pair(text({ cookies: options.cookies }), text({ cookies: options.cookies }), { actualReceipts: [reissued] }), /unexpected authentication ticket reissuance/)
  const rejected = structuredClone(receipt); rejected.status = 401
  assert.throws(() => pair(text({ ...options, status: 401 }), text({ ...options, status: 401 }), { actualReceipts: [rejected] }), /unexpected authentication ticket reissuance/)
  for (const key of Object.keys(security)) {
    const bad = structuredClone(receipt); delete bad.headers[key]
    assert.throws(() => pair(text(options), text(options), { actualReceipts: [bad] }), /must be/)
  }
  assert.ok(pair(text(options), text(options)).unproven.some(value => value.rule.includes("lifetime")))
})

test("native antiforgery cookie lifecycle checks session-only attrs and unknown-cookie names remain visible", () => {
  const shape = `_campfire_session; ${cookieAttrs}`
  const receipt = raw({ cookies: [".Campfire.Csrf=opaque; httponly; path=/; samesite=lax"] })
  equal(pair(text({ cookies: [shape] }), text({ cookies: [shape] }), { actualReceipts: [receipt] }))
  for (const [name, value] of [["domain", "other.example"], ["max-age", "300"], ["expires", expires], ["secure", ""]]) {
    const bad = structuredClone(receipt); bad.cookies[0].attributes[name] = value
    assert.throws(() => pair(text({ cookies: [shape] }), text({ cookies: [shape] }), { actualReceipts: [bad] }), /lifetime invalid/)
  }
  different(text(), text({ cookies: [`unexpected; ${cookieAttrs}`] }), { actualReceipts: [raw({ cookies: ["unexpected=value; httponly; path=/; samesite=lax"] })] })
})

test("last_room allows only native HttpOnly with the unchanged root/Lax shape and one-year policy", () => {
  const expected = text({ cookies: ["last_room; path=/; samesite=lax"] })
  const actual = text({ cookies: [`last_room; ${cookieAttrs}`] })
  const receipt = raw({ cookies: ["last_room=5; httponly; path=/; samesite=lax; max-age=31536000"] })
  equal(pair(expected, actual, { actualReceipts: [receipt] }))
  for (const value of ["0", "300", "63072000"]) { const bad = structuredClone(receipt); bad.cookies[0].attributes["max-age"] = value; assert.throws(() => pair(expected, actual, { actualReceipts: [bad] }), /lifetime invalid/) }
  for (const shape of ["last_room; httponly; path=/other; samesite=lax", "last_room; httponly; path=/; samesite=none", "last_room; path=/; samesite=none"]) assert.throws(() => pair(expected, text({ cookies: [shape] }), { actualReceipts: [receipt] }), /cookie receipts do not match/)
  assert.ok(pair(expected, actual).unproven.some(value => value.rule.includes("last_room")))
})

test("protected flash lifecycle requires raw five-minute policy and independent semantic flash owners", () => {
  const shape = `.Campfire.Flash; ${cookieAttrs}`
  const actual = text({ cookies: [shape] })
  const receipt = raw({ cookies: [".Campfire.Flash=opaque; httponly; path=/; samesite=lax; max-age=300"] })
  const proof = { actualReceipts: [receipt], flashOwnerVerified: true }
  equal(pair(text(), actual, proof))
  different(text(), actual, { actualReceipts: [receipt] })
  const bad = structuredClone(receipt); bad.cookies[0].attributes["max-age"] = "301"
  assert.throws(() => pair(text(), actual, { actualReceipts: [bad], flashOwnerVerified: true }), /lifetime invalid/)
  different(text(), text({ cookies: [shape], body: "sha256:2222222222222222" }), proof)
  const clear = raw({ cookies: [".Campfire.Flash=; httponly; path=/; samesite=lax; expires=Thu, 01 Jan 1970 00:00:00 GMT"] })
  equal(pair(text(), text({ cookies: [`.Campfire.Flash (cleared); ${cookieAttrs}`] }), { actualReceipts: [clear], flashOwnerVerified: true }))
})

test("Pragma is canonicalized only for exact no-cache raw issuance policy", () => {
  const values = { "content-type": "text/html; charset=utf-8", "cache-control": "max-age=0, must-revalidate, private" }
  const expected = text({ values }), actual = text({ values, extraHeaders: ["pragma"] })
  equal(pair(expected, actual, { actualReceipts: [raw({ headers: { pragma: "no-cache" } })] }))
  different(expected, actual, { actualReceipts: [raw({ headers: { pragma: "public" } })] })
  assert.throws(() => pair(expected, text({ values: { ...values, pragma: "no-cache" } }), { actualReceipts: [raw({ headers: { pragma: "public" } })] }), /receipt does not match/)
  assert.ok(pair(expected, actual).unproven.some(value => value.rule.includes("Pragma")))
  different(expected, text({ values: { ...values, "cache-control": "public" }, extraHeaders: ["pragma"] }), { actualReceipts: [raw({ headers: { pragma: "no-cache" } })] })
})

test("404/500 exception-cache differences retain exact status/type/body and require no-store plus past expiry", () => {
  for (const status of [404, 500]) {
    const values = { "content-type": "text/html; charset=utf-8" }
    const expected = text({ status, values }), actual = text({ status, values: { ...values, "cache-control": "no-cache, no-store" }, extraHeaders: ["pragma", "expires"] })
    const proof = { actualReceipts: [raw({ status, headers: { pragma: "no-cache", expires: "-1" } })] }
    equal(pair(expected, actual, proof))
    different(expected, actual, { actualReceipts: [raw({ status, headers: { pragma: "no-cache", expires: expires } })] })
    different(expected, text({ status, values: { ...values, "cache-control": "public, max-age=1800" }, extraHeaders: ["pragma", "expires"] }), proof)
    different(expected, text({ status: 200, values: { ...values, "cache-control": "no-cache, no-store" }, extraHeaders: ["pragma", "expires"] }), proof)
    different(expected, text({ status, values: { ...values, "cache-control": "no-cache, no-store" }, extraHeaders: ["pragma", "expires"], body: "sha256:2222222222222222" }), proof)
    assert.ok(pair(expected, actual).unproven.length)
  }
})

test("HTML Accept Vary and UTF-8 spelling do not hide other variation or media types", () => {
  const actual = text({ values: { "content-type": "text/html; charset=utf-8", vary: "accept-encoding" } })
  equal(pair(text({ values: { "content-type": "text/html; charset=UTF-8", vary: "accept, accept-encoding" } }), actual))
  for (const vary of ["*", "accept, origin", "accept, accept-encoding, cookie"]) different(text({ values: { "content-type": "text/html; charset=UTF-8", vary } }), actual)
  different(text({ values: { "content-type": "text/html; charset=latin1", vary: "accept, accept-encoding" } }), actual)
  different(text({ values: { "content-type": "application/json; charset=UTF-8", vary: "accept, accept-encoding" } }), text({ values: { "content-type": "application/json; charset=utf-8", vary: "accept-encoding" } }))
})

test("avatar SWR rule is limited to nonexpiring authenticated avatar purpose and exact public policy", () => {
  const request = "GET /users/«signed_id:user/avatar:5»/avatar?v=20260102160000"
  const expected = text({ request, values: { "content-type": "image/webp", "cache-control": "max-age=1800, public" } })
  const actual = text({ request, values: { "content-type": "image/webp", "cache-control": "max-age=1800, public, stale-while-revalidate=604800" } })
  equal(pair(expected, actual))
  for (const request of ["GET /users/CfDJ8Unknown/avatars", "GET /users/«signed_id:user/avatar:5:expires»/avatar", "GET /users/«signed_id:user/transfer:5»/avatar"]) different(text({ request, values: { "content-type": "image/webp", "cache-control": "max-age=1800, public" } }), text({ request, values: { "content-type": "image/webp", "cache-control": "max-age=1800, public, stale-while-revalidate=604800" } }))
  different(expected, text({ request, values: { "content-type": "image/webp", "cache-control": "max-age=1800, private, stale-while-revalidate=604800" } }))
  different(expected, text({ request, values: { "content-type": "image/webp", "cache-control": "max-age=1800, public, stale-while-revalidate=604801" } }))
})

test("navigation body duplication is removed only by the independently matching exact document owners", () => {
  const expected = text(), actual = text({ body: "sha256:2222222222222222" })
  const owner = { request, status: 200, semanticEqual: true, expectedBodyHash: "1111111111111111", actualBodyHash: "2222222222222222" }
  equal(pair(expected, actual, { nativeCaptureSecurityVerified: true, documentOwners: [owner] }))
  for (const mutation of [{ semanticEqual: false }, { request: "GET /rooms/6 (navigation)" }, { status: 404 }, { actualBodyHash: "3333333333333333" }]) different(expected, actual, { nativeCaptureSecurityVerified: true, documentOwners: [{ ...owner, ...mutation }] })
  different(text({ request: "GET /rooms/5" }), text({ request: "GET /rooms/5", body: "sha256:2222222222222222" }), { nativeCaptureSecurityVerified: true, documentOwners: [owner] })
  different(expected, text({ status: 404, body: "sha256:2222222222222222" }), { nativeCaptureSecurityVerified: true, documentOwners: [owner] })
  different(expected, text({ body: "sha256:3333333333333333" }), { nativeCaptureSecurityVerified: true, documentOwners: [owner] })
})

test("raw cookie attribute receipts exclude values and reject malformed duplicates", () => {
  const receipt = cookieContractReceipt("session_token=secret; Path=/; HttpOnly; SameSite=Lax")
  assert.equal(JSON.stringify(receipt).includes("secret"), false)
  assert.equal(receipt.malformed, false)
  assert.equal(cookieContractReceipt("session_token=secret; Path=/; Path=/other").malformed, true)
})

test("every native response needs its own exact unique security receipt; an unrelated receipt proves nothing", () => {
  const expected = text(), actual = text()
  equal(pair(expected, actual, { actualReceipts: [raw()] }))
  for (const actualReceipts of [[], [raw({ request: "GET /other" })], [raw(), raw()]]) {
    const result = pair(expected, actual, { actualReceipts, nativeCaptureSecurityVerified: true })
    assert.ok(result.unproven.some(value => value.rule.includes("per-response")))
  }
  const additional = text({ request: "GET /other" })
  const result = pair(expected + additional, actual + additional, { actualReceipts: [raw()] })
  assert.ok(result.unproven.some(value => value.request === "GET /other"))
})

test("the shared raw verifier rejects identical malformed native/control cookies and incomplete coverage", () => {
  const options = { cookies: [`last_room; ${cookieAttrs}`] }
  const valid = raw({ cookies: ["last_room=5; httponly; path=/; samesite=lax; max-age=31536000"] })
  assert.deepEqual(verifyNativeContractReceipts(text(options), [valid]), [])
  for (const mutate of [(raw: NetworkContractReceipt) => { raw.cookies[0].malformed = true }, (raw: NetworkContractReceipt) => { raw.cookies[0].attributes.path = "/other" }, (raw: NetworkContractReceipt) => { raw.cookies[0].attributes["max-age"] = "300" }, (raw: NetworkContractReceipt) => { delete raw.headers["x-frame-options"] }, (raw: NetworkContractReceipt) => { raw.request = "GET /other" }, (raw: NetworkContractReceipt) => { raw.status = 404 }]) {
    const invalid = structuredClone(valid); mutate(invalid)
    assert.ok(verifyNativeContractReceipts(text(options), [invalid]).length)
  }
  assert.ok(verifyNativeContractReceipts(text(options), []).length)
  assert.ok(verifyNativeContractReceipts(text(options), [valid, valid]).length)
  const cleared = raw({ cookies: ["session_token=; httponly; path=/; samesite=lax; expires=Thu, 01 Jan 1970 00:00:00 GMT"] })
  assert.deepEqual(verifyNativeContractReceipts(text({ cookies: [`session_token (cleared); ${cookieAttrs}`] }), [cleared]), [])
})

test("fresh pairs validate cleared native cookies before matching identical cleared shapes", () => {
  for (const [name, alias] of [["session_token", "session_token"], [".Campfire.Csrf", "_campfire_session"], ["last_room", "last_room"], [".Campfire.Flash", ".Campfire.Flash"]]) {
    const actual = text({ cookies: [`${alias} (cleared); ${cookieAttrs}`] })
    const clear = raw({ cookies: [`${name}=; httponly; path=/; samesite=lax; expires=Thu, 01 Jan 1970 00:00:00 GMT; max-age=0`] })
    equal(pair(actual, actual, { actualReceipts: [clear] }))
    for (const [attribute,value] of [["expires", expires], ["max-age", "300"], ["httponly", "foo"]]) {
      const invalid = structuredClone(clear); invalid.cookies[0].attributes[attribute] = value
      const sameBadShape = attribute === "httponly" ? actual.replace("httponly", "httponly=foo") : actual
      assert.throws(() => pair(sameBadShape, sameBadShape, { actualReceipts: [invalid] }), /attributes or lifetime invalid/)
      assert.ok(verifyNativeContractReceipts(sameBadShape, [invalid]).some(issue => issue.includes("attributes or lifetime invalid")))
    }
    const malformed = structuredClone(clear); malformed.cookies[0].malformed = true
    assert.throws(() => pair(actual, actual, { actualReceipts: [malformed] }), /malformed cookie/)
    const missingSecurity = structuredClone(clear); delete missingSecurity.headers["x-frame-options"]
    assert.throws(() => pair(actual, actual, { actualReceipts: [missingSecurity] }), /must be SAMEORIGIN/)
    const secureShape = actual.replace("samesite=lax", "samesite=lax; secure=foo")
    const invalidSecure = structuredClone(clear); invalidSecure.protocol = "https:"; invalidSecure.cookies[0].attributes.secure = "foo"
    assert.throws(() => pair(secureShape, secureShape, { actualReceipts: [invalidSecure] }), /attributes or lifetime invalid/)
    assert.ok(verifyNativeContractReceipts(secureShape, [invalidSecure]).some(issue => issue.includes("attributes or lifetime invalid")))
    const validSecure = structuredClone(clear); validSecure.protocol = "https:"; validSecure.cookies[0].attributes.secure = ""
    const httpsShape = actual.replace("samesite=lax", "samesite=lax; secure")
    equal(pair(httpsShape, httpsShape, { actualReceipts: [validSecure] }))
  }
})

test("deduplicated original blocks retain and validate every raw policy variant", () => {
  const actual = text({ cookies: [`_campfire_session; ${cookieAttrs}`] })
  const first = { ...raw({ cookies: [".Campfire.Csrf=one; httponly; path=/; samesite=lax"] }), originalBlock: actual.trimEnd() }
  const second = structuredClone(first); second.headers.date = "Tue, 06 Oct 2026 12:00:01 GMT"
  equal(pair(text(), actual, { actualReceipts: [first, second] }))
  assert.deepEqual(verifyNativeContractReceipts(actual, [first, second]), [])
  for (const mutate of [(receipt: NetworkContractReceipt) => { delete receipt.headers["x-frame-options"] }, (receipt: NetworkContractReceipt) => { receipt.cookies[0].attributes["max-age"] = "300" }, (receipt: NetworkContractReceipt) => { receipt.cookies[0].attributes.httponly = "foo" }]) {
    const bad = structuredClone(second); mutate(bad)
    assert.throws(() => pair(text(), actual, { actualReceipts: [first, bad] }), /must be|attributes or lifetime invalid/)
  }
  const divergent = structuredClone(second); divergent.headers.pragma = "public"
  assert.ok(pair(text(), actual, { actualReceipts: [first, divergent] }).unproven.some(value => value.rule.includes("inconsistent")))
  const differentBody = text({ cookies: [`_campfire_session; ${cookieAttrs}`], body: "sha256:2222222222222222" })
  assert.ok(pair(actual, differentBody, { actualReceipts: [first, second] }).unproven.some(value => value.rule.includes("per-response")))
  const differentStatus = text({ cookies: [`_campfire_session; ${cookieAttrs}`], status: 404 })
  assert.ok(pair(actual, differentStatus, { actualReceipts: [first, second] }).unproven.some(value => value.rule.includes("per-response")))

  const authText = text({ request: "POST /session", status: 302, cookies: [`session_token; ${cookieAttrs}`] })
  const auth = { ...raw({ request: "POST /session", status: 302, cookies: [`session_token=one; httponly; path=/; samesite=lax; expires=${expires}`] }), originalBlock: authText.trimEnd() }
  const later = structuredClone(auth); later.headers.date = "Tue, 06 Oct 2026 12:00:01 GMT"; later.cookies[0].attributes.expires = "Thu, 05 Nov 2026 12:00:01 GMT"
  equal(pair(authText, authText, { actualReceipts: [auth, later] }))
  later.cookies[0].attributes.expires = "Fri, 06 Nov 2026 12:00:01 GMT"
  assert.throws(() => pair(authText, authText, { actualReceipts: [auth, later] }), /attributes or lifetime invalid/)
})

test("original receipt identity joins distinct blocks before mutations and legacy ambiguity stays unproven", () => {
  const firstText = text({ values: { "content-type": "text/html; charset=UTF-8" } })
  const secondText = text({ values: { "content-type": "text/html; charset=utf-8" }, body: "sha256:2222222222222222" })
  const first = { ...raw(), originalBlock: firstText.trimEnd() }, second = { ...raw(), originalBlock: secondText.trimEnd() }
  equal(pair(firstText + secondText, firstText + secondText, { actualReceipts: [first, second] }))
  assert.ok(pair(firstText + secondText, firstText + secondText, { actualReceipts: [raw()] }).unproven.some(value => value.rule.includes("per-response")))
  const wrong = { ...second, originalBlock: first.originalBlock }
  assert.ok(pair(firstText + secondText, firstText + secondText, { actualReceipts: [first, wrong] }).unproven.some(value => value.rule.includes("per-response")))
})
