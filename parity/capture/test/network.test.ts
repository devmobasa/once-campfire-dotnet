// PARITY PATCH: response-description failures must remain capture errors without crashing unrelated workers.
import { test } from "node:test"
import assert from "node:assert/strict"
import { setImmediate } from "node:timers/promises"
import type { Page, Response } from "playwright"
import { NetworkLog } from "../network.ts"
import { normalizeNativeNetworkPair, verifyNativeContractReceipts } from "../native_network_contracts.ts"
import { WireReceipts } from "../wire_receipts.ts"
import type { WireRequest } from "../wire_receipts.ts"

function pageWithResponse(fallback: () => Promise<unknown>, headers: Record<string,string> = { "content-type": "text/plain" }, status = 200, cookies: string[] = [], options: { method?: string; browser?: string } = {}) {
  let record!: (response: Response) => void
  const page = {
    on: (_event: string, listener: typeof record) => { record = listener },
    context: () => ({ request: { get: fallback }, ...(options.browser ? { browser: () => ({ browserType: () => ({ name: () => options.browser }) }) } : {}) }),
  } as unknown as Page
  const response = (url: string, body: () => Promise<Buffer>) => ({
    url: () => url,
    request: () => ({ method: () => options.method ?? "GET", resourceType: () => "document", headers: () => ({}), url: () => url, isNavigationRequest: () => false }),
    allHeaders: async () => headers, headers: () => headers, headersArray: async () => cookies.map((value) => ({ name: "set-cookie", value })), status: () => status, body,
  }) as unknown as Response
  return { page, emit: (url: string, body: () => Promise<Buffer>) => record(response(url, body)) }
}

test("a response body failing after context closure is handled immediately and still fails the network layer", async () => {
  const closed = new Error("Target page, context or browser has been closed")
  const fixture = pageWithResponse(async () => { throw closed })
  const log = new NetworkLog(fixture.page, "http://localhost:3999", {})
  fixture.emit("http://localhost:3999/rooms/1", async () => { throw new Error("document body discarded") })

  // Navigation cleanup may finish before text() is reached. An unhandled rejection fails this test runner.
  await setImmediate()
  await assert.rejects(log.text(), (error: unknown) => error instanceof Error && error.cause === closed)
})

const securityHeaders = { "x-content-type-options": "nosniff", "x-frame-options": "SAMEORIGIN", "referrer-policy": "strict-origin-when-cross-origin" }

test("future network receipts retain the actual redirect Location for the challenge owner", async () => {
  const location = "http://localhost:3999/session/new?returnUrl=%2Frooms%2F5"
  const fixture = pageWithResponse(async () => assert.fail("redirect body must not be fetched"), { location, ...securityHeaders }, 302, [], { browser: "chromium" })
  const log = new NetworkLog(fixture.page, "http://localhost:3999", { nativeComparison: true, nativeTarget: true })
  fixture.emit("http://localhost:3999/rooms/5", async () => assert.fail("redirect body must not be fetched"))
  const artifact = await log.text()
  assert.equal(log.contractReceipts()[0].headers.location, location)
  assert.match(artifact, /location: http:\/\/localhost:3999\/session\/new\?returnUrl=%2Frooms%2F5/)
})

test("Vary compares deduplicated field-name sets, including partial audio, without losing missing or extra variation", async () => {
  const record = async (vary: string | undefined, contentType = "audio/mpeg", status = 206) => {
    const headers: Record<string,string> = { "content-type": contentType, "content-encoding": "gzip" }
    if (vary !== undefined) headers.vary = vary
    const fixture = pageWithResponse(async () => assert.fail("unexpected body refetch"), headers, status)
    const log = new NetworkLog(fixture.page, "http://localhost:3999", { nativeComparison: true })
    fixture.emit("http://localhost:3999/audio.mp3", async () => Buffer.from("actual decoded audio"))
    return log.text()
  }
  const expected = await record("Accept-Encoding")
  assert.equal(expected, await record("accept-encoding, Accept-Encoding"))
  for (const vary of [undefined, "Origin", "Accept-Encoding, Origin", "*"]) assert.notEqual(expected, await record(vary), String(vary))
  assert.notEqual(await record("Accept-Encoding", "text/html", 200), await record(undefined, "text/html", 200))
})

test("native security headers are verified even when their presence is normalized", async () => {
  const fixture = pageWithResponse(async () => assert.fail("unexpected refetch"), { "content-type": "text/plain", ...securityHeaders })
  const log = new NetworkLog(fixture.page, "http://localhost:3999", { nativeComparison: true, nativeTarget: true })
  fixture.emit("http://localhost:3999/up", async () => Buffer.from("OK"))
  assert.doesNotMatch(await log.text(), /x-frame-options|referrer-policy/)
  for (const name of Object.keys(securityHeaders)) {
    for (const value of [undefined, "wrong-value"]) {
      const headers: Record<string,string> = { "content-type": "text/plain", ...securityHeaders }
      if (value === undefined) delete headers[name]
      else headers[name] = value
      const bad = pageWithResponse(async () => assert.fail("unexpected refetch"), headers)
      const badLog = new NetworkLog(bad.page, "http://localhost:3999", { nativeComparison: true, nativeTarget: true })
      bad.emit("http://localhost:3999/up", async () => Buffer.from("OK"))
      await assert.rejects(badLog.text(), (error: any) => error.cause.message.includes(name))
    }
  }
})

test("compression shape is ignored for images and error responses but retained for successful HTML", async () => {
  for (const [contentType, status, ignored] of [["image/webp",200,true], ["text/html",404,true], ["text/html",200,false]] as const) {
    const fixture = pageWithResponse(async () => assert.fail("unexpected refetch"), { "content-type": contentType, "content-encoding": "gzip", vary: "Accept-Encoding" }, status)
    const log = new NetworkLog(fixture.page, "http://localhost:3999", { nativeComparison: true })
    fixture.emit("http://localhost:3999/resource", async () => Buffer.from("decoded body"))
    const result = await log.text()
    assert.equal(result.includes("content-encoding"), !ignored)
    assert.equal(result.includes("vary:"), !ignored)
    assert.match(result, /body: sha256:/)
  }
  const record = async (contentType: string, status: number, body = "decoded body", encoding?: string) => {
    const headers: Record<string,string> = { "content-type": contentType }
    if (encoding) { headers["content-encoding"] = encoding; headers.vary = "Accept-Encoding" }
    const fixture = pageWithResponse(async () => assert.fail("unexpected refetch"), headers, status)
    const log = new NetworkLog(fixture.page, "http://localhost:3999", { nativeComparison: true })
    fixture.emit("http://localhost:3999/resource", async () => Buffer.from(body))
    return log.text()
  }
  for (const [contentType, status] of [["image/webp",200], ["text/html",404]] as const) {
    const expected = await record(contentType, status, "decoded body", "gzip")
    assert.equal(expected, await record(contentType, status))
    assert.notEqual(expected, await record(contentType, status, "different body"))
    assert.notEqual(expected, await record(contentType, status === 200 ? 404 : 500))
    assert.notEqual(expected, await record("text/plain", status))
    assert.notEqual(expected, await record(contentType, status, "decoded body", "br"))
  }
  assert.notEqual(await record("text/html", 200, "decoded body", "gzip"), await record("text/html", 200, "decoded body", "br"))
})

test("a zero-byte 200 image is retried and still fails if empty or status changes", async () => {
  for (const [status, body, accepted] of [[200,"image bytes",true], [200,"",false], [404,"missing",false]] as const) {
    let retries = 0
    const fixture = pageWithResponse(async () => { retries++; return { status: () => status, body: async () => Buffer.from(body) } }, { "content-type": "image/webp" })
    const log = new NetworkLog(fixture.page, "http://localhost:3999", {})
    fixture.emit("http://localhost:3999/avatar.webp", async () => Buffer.alloc(0))
    if (accepted) assert.doesNotMatch(await log.text(), /body: empty/)
    else await assert.rejects(log.text())
    assert.equal(retries, 1)
  }
})

test("cookie aliases preserve clearing and security attributes and retain unknown cookie names", async () => {
  const record = async (cookie: string) => {
    const fixture = pageWithResponse(async () => assert.fail("unexpected refetch"), { "content-type": "text/plain", "set-cookie": cookie }, 200, [cookie])
    const log = new NetworkLog(fixture.page, "http://localhost:3999", { nativeComparison: true })
    fixture.emit("http://localhost:3999/session", async () => Buffer.from("body"))
    return log.text()
  }
  assert.equal(await record("session_token=a; Path=/; HttpOnly; SameSite=Lax"), await record("session_token=b; Path=/; HttpOnly; SameSite=Lax"))
  assert.notEqual(await record(".Campfire.Auth=a; Path=/; HttpOnly; SameSite=Lax"), await record("session_token=b; Path=/; HttpOnly; SameSite=Lax"))
  assert.equal(await record(".Campfire.Csrf=a; Path=/; HttpOnly; SameSite=Lax"), await record("_campfire_session=b; Path=/; HttpOnly; SameSite=Lax"))
  assert.notEqual(await record("session_token=a; Path=/"), await record("session_token=b; Path=/; HttpOnly"))
  assert.notEqual(await record("session_token=a; Path=/; SameSite=None"), await record("session_token=b; Path=/; SameSite=Lax"))
  assert.notEqual(await record("session_token=a; Path=/private"), await record("session_token=b; Path=/"))
  assert.notEqual(await record("session_token=a; Path=/; Secure"), await record("session_token=b; Path=/"))
  assert.notEqual(await record("session_token=a; Path=/; Domain=localhost"), await record("session_token=b; Path=/; Domain=other.example"))
  assert.notEqual(await record("session_token=; Path=/"), await record("session_token=b; Path=/"))
  assert.notEqual(await record("unexpected=a; Path=/"), await record("session_token=b; Path=/"))
})

test("network request and redirect tokens use only resolved purpose and record identities", async () => {
  const protectedToken = "CfDJ8" + "A".repeat(60)
  const signed = (id: number, purpose: string) => Buffer.from(JSON.stringify({ _rails: { data: id, pur: purpose } })).toString("base64url") + "--" + "f".repeat(64)
  const record = async (token: string, description?: string) => {
    const verified = new Map<string,string>()
    const fixture = pageWithResponse(async () => assert.fail("redirect body must not be fetched"), { "content-type": "text/html", location: `/users/${token}/avatar` }, 302)
    const log = new NetworkLog(fixture.page, "http://localhost:3999", { nativeComparison: true, protectedTokens: verified, resolveProtectedTokens: async (text) => {
      if (description && text.includes(protectedToken)) verified.set(protectedToken, description)
    } })
    fixture.emit(`http://localhost:3999/users/${token}/avatar`, async () => assert.fail("redirect body must not be fetched"))
    return log.text()
  }
  const expected = await record(signed(5, "user/avatar"))
  assert.equal(expected, await record(protectedToken, "«signed_id:user/avatar:5»"))
  assert.notEqual(expected, await record(protectedToken, "«signed_id:user/avatar:6»"))
  assert.notEqual(expected, await record(protectedToken, "«signed_id:user/transfer:5»"))
  assert.notEqual(expected, await record(protectedToken))
  assert.notEqual(expected, await record(protectedToken.slice(0, -1) + "B", "«signed_id:user/avatar:5»"))
})

test("a recorded response keeps its headers and normalized body in the network comparison", async () => {
  const fixture = pageWithResponse(async () => { throw new Error("unexpected fallback") })
  const log = new NetworkLog(fixture.page, "http://localhost:3999", {})
  fixture.emit("http://localhost:3999/rooms/1", async () => Buffer.from("hello"))

  assert.equal(await log.text(), "GET /rooms/1 → 200\n  headers: content-type\n  content-type: text/plain\n  body: sha256:2cf24dba5fb0a30e\n")
})

test("contract receipts join the exact bounded network output and exclude cookie values", async () => {
  const fixture = pageWithResponse(async () => assert.fail("unexpected refetch"), { "content-type": "text/html", pragma: "no-cache", expires: "-1", date: "Tue, 06 Oct 2026 12:00:00 GMT", ...securityHeaders }, 200, [".Campfire.Csrf=secret; Path=/; HttpOnly; SameSite=Lax"])
  const log = new NetworkLog(fixture.page, "http://localhost:3999", { nativeComparison: true, nativeTarget: true })
  assert.throws(() => log.contractReceipts(), /completed text snapshot/)
  fixture.emit("http://localhost:3999/rooms/1", async () => Buffer.from("hello"))
  const text = await log.text()
  const receipts = log.contractReceipts()
  assert.match(text, /pragma: no-cache/)
  assert.match(text, /expires: -1/)
  assert.equal(receipts[0].request, "GET /rooms/1")
  assert.equal(receipts[0].originalBlock, text.trimEnd())
  assert.match(text, new RegExp(`body: sha256:${receipts[0].bodyHash}`))
  assert.equal(receipts[0].headers["x-frame-options"], "SAMEORIGIN")
  assert.equal(JSON.stringify(receipts).includes("secret"), false)
  receipts[0].headers["x-frame-options"] = "changed"
  assert.equal(log.contractReceipts()[0].headers["x-frame-options"], "SAMEORIGIN")
})

test("canonical token URLs share one rendered block while preserving all raw policy receipts", async () => {
  const first = "CfDJ8" + "A".repeat(60), second = "CfDJ8" + "B".repeat(60)
  const protectedTokens = new Map([[first, "«signed_id:user/avatar:5»"], [second, "«signed_id:user/avatar:5»"]])
  const fixture = pageWithResponse(async () => assert.fail("unexpected refetch"), { "content-type": "text/plain", ...securityHeaders })
  const log = new NetworkLog(fixture.page, "http://localhost:3999", { nativeComparison: true, nativeTarget: true, protectedTokens })
  fixture.emit(`http://localhost:3999/users/${first}/avatar`, async () => Buffer.from("same bytes"))
  fixture.emit(`http://localhost:3999/users/${second}/avatar`, async () => Buffer.from("same bytes"))
  const artifact = await log.text(), receipts = log.contractReceipts()
  assert.equal(artifact.match(/ → 200/g)?.length, 1)
  assert.equal(receipts.length, 2)
  assert.ok(receipts.every(receipt => receipt.originalBlock === artifact.trimEnd()))
  assert.deepEqual(verifyNativeContractReceipts(artifact, receipts), [])
  assert.equal(normalizeNativeNetworkPair(artifact, artifact, { actualReceipts: receipts }).unproven.length, 0)
  receipts[1].headers["x-frame-options"] = "bad"
  assert.throws(() => normalizeNativeNetworkPair(artifact, artifact, { actualReceipts: receipts }), /must be SAMEORIGIN/)
})

test("WebKit redirect wire Date fills omitted raw evidence for the real auth lifetime consumer", async () => {
  const url = "http://localhost:3999/session", date = "Tue, 06 Oct 2026 12:00:00 GMT"
  const cookie = "session_token=secret; Path=/; HttpOnly; SameSite=Lax; Expires=Thu, 05 Nov 2026 12:00:00 GMT"
  const capture = async (options: { browserHeaders?: Record<string,string>; wireHeaders?: Record<string,string>; wireRequest?: Partial<WireRequest>; cookie?: string } = {}) => {
    const issued = options.cookie ?? cookie
    const browserHeaders = { "content-type": "text/html", location: "/rooms", "set-cookie": issued, ...options.browserHeaders }
    const fixture = pageWithResponse(async () => assert.fail("redirect must not refetch"), browserHeaders, 302, [issued], { method: "POST", browser: "webkit" })
    const wire = new WireReceipts(), verify = wire.verifier()
    wire.record({ method: "POST", url, status: 302, location: "/rooms", ...options.wireRequest }, { ...securityHeaders, date, pragma: "no-cache", expires: "Thu, 01 Jan 1970 00:00:00 GMT", ...options.wireHeaders })
    const log = new NetworkLog(fixture.page, "http://localhost:3999", { nativeComparison: true, nativeTarget: true }, verify)
    fixture.emit(url, async () => assert.fail("redirect body must not be fetched"))
    const artifact = await log.text(), receipts = log.contractReceipts()
    return { artifact, receipts, compare: () => normalizeNativeNetworkPair(artifact, artifact, { actualReceipts: receipts }) }
  }
  const valid = await capture()
  assert.equal(valid.receipts[0].headers.date, date)
  assert.equal(valid.receipts[0].headers.pragma, "no-cache")
  assert.equal(valid.receipts[0].headers.expires, "Thu, 01 Jan 1970 00:00:00 GMT")
  assert.doesNotMatch(valid.artifact, /  date:|  pragma:|  expires:/) // truthful browser block; raw evidence is separate
  assert.equal(valid.compare().unproven.length, 0)
  assert.equal(valid.compare().expected, valid.compare().actual)
  assert.equal(JSON.stringify(valid.receipts).includes("secret"), false)
  for (const options of [{ wireHeaders: { date: "not-a-date" } }, { wireHeaders: { date: "Wed, 07 Oct 2026 12:00:00 GMT" } }, { cookie: cookie.replace("05 Nov", "06 Nov") }]) {
    const invalid = await capture(options)
    assert.throws(invalid.compare, /attributes or lifetime invalid/)
  }
  const browserConflict = await capture({ browserHeaders: { date: "Wed, 07 Oct 2026 12:00:00 GMT", pragma: "public" } })
  assert.equal(browserConflict.receipts[0].headers.pragma, "public")
  assert.equal(browserConflict.receipts[0].headers.date, "Wed, 07 Oct 2026 12:00:00 GMT")
  assert.throws(browserConflict.compare, /attributes or lifetime invalid/)
  for (const wireRequest of [{ method: "GET" }, { url: url + "/wrong" }, { status: 303 }, { location: "/wrong" }]) await assert.rejects(capture({ wireRequest }), (error: any) => error.cause.message.includes("found 0"))
  await assert.rejects(capture({ wireHeaders: { "x-frame-options": "bad" } }), (error: any) => error.cause.message.includes("must be SAMEORIGIN"))
  await assert.rejects(capture({ browserHeaders: { "x-frame-options": "bad" } }), (error: any) => error.cause.message.includes("must be SAMEORIGIN"))
})
