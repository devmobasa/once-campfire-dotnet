// Approved native-rewrite representation contracts. Raw captures remain immutable; each
// canonicalization has a named receipt, and missing raw evidence stays an explicit requirement.
export interface CookieContractReceipt {
  name: string
  cleared: boolean
  attributes: Record<string, string>
  malformed: boolean
}

export interface NetworkContractReceipt {
  request: string
  status: number
  protocol: "http:" | "https:"
  headers: Record<string, string>
  cookies: CookieContractReceipt[]
  bodyHash?: string
  originalBlock?: string // exact masked rendered block before any pair-contract rules
}

export interface DocumentOwner {
  request: string // exact normalized request, including (navigation)
  status: number
  semanticEqual: boolean // independently compared server/live document owners
  expectedBodyHash: string
  actualBodyHash: string
}

export interface NativeNetworkProof {
  expectedReceipts?: NetworkContractReceipt[]
  actualReceipts?: NetworkContractReceipt[]
  documentOwners?: DocumentOwner[]
  flashOwnerVerified?: boolean // final server/live/ARIA flash content independently matches
  accountPostOwner?: { expectedRequest: string; actualRequest: string; location: string } // raw account form plus final HTML/live/ARIA independently checked
  nativeCaptureSecurityVerified?: boolean // retained collector enforced its three required headers
  signedOutRedirectOwner?: { state: "auth/signed_out_redirect"; protectedPath: string; expectedUrl: string; actualUrl: string; semanticEqual: boolean; expectedBodyHash: string; actualBodyHash: string }
}

export interface NetworkRuleReceipt { side: "expected" | "actual"; request: string; rule: string }
export interface NativeNetworkPair { expected: string; actual: string; applied: NetworkRuleReceipt[]; unproven: NetworkRuleReceipt[] }

const REQUIRED_SECURITY = { "x-content-type-options": "nosniff", "x-frame-options": "SAMEORIGIN", "referrer-policy": "strict-origin-when-cross-origin" }
const ISSUED_SESSION_NAMES = new Set(["session_token", "_campfire_session"])
const NATIVE_NAMES: Readonly<Record<string,string>> = { session_token: "session_token", _campfire_session: ".Campfire.Csrf" }
const COOKIE_ALIASES: Readonly<Record<string,string>> = { ".Campfire.Csrf": "_campfire_session" }

interface Block { request: string; status: number; headers: Set<string>; values: Map<string,string>; cookies: string[]; body: string; other: string[]; original: string; legacyJoinAllowed: boolean }

export function cookieContractReceipt(value: string): CookieContractReceipt {
  const [pair, ...parts] = value.split(";").map(part => part.trim())
  const separator = pair.indexOf("=")
  const attributes: Record<string,string> = {}
  let malformed = separator < 1
  for (const part of parts) {
    const split = part.indexOf("=")
    const name = (split < 0 ? part : part.slice(0, split)).toLowerCase()
    if (!name || Object.hasOwn(attributes, name)) malformed = true
    attributes[name] = split < 0 ? "" : part.slice(split + 1)
  }
  const cleared = pair.slice(separator + 1) === "" || attributes["max-age"] === "0" || Date.parse(attributes.expires ?? "") <= 0
  return { name: separator < 0 ? pair : pair.slice(0, separator), cleared, attributes, malformed }
}

export function cookieShape(value: string, nativeComparison?: boolean): string {
  return receiptCookieShape(cookieContractReceipt(value), nativeComparison)
}

function receiptCookieShape(cookie: CookieContractReceipt, nativeComparison?: boolean): string {
  const attrs = Object.entries(cookie.attributes).filter(([name]) => !["expires", "max-age"].includes(name)).map(([name,value]) => name + (value ? "=" + (name === "samesite" ? value.toLowerCase() : value) : "")).sort()
  const name = nativeComparison ? COOKIE_ALIASES[cookie.name] ?? cookie.name : cookie.name
  return `${name}${cookie.cleared ? " (cleared)" : ""}${attrs.length ? "; " + attrs.join("; ") : ""}`
}

// Shared by fresh pairs and retired-browser controls. Every raw member remains validated,
// even when masking collapsed several responses to one rendered block.
export function verifyNativeContractReceipts(text: string, receipts: NetworkContractReceipt[]): string[] {
  const blocks = parseBlocks(text)
  if (!blocks) return ["unrecognized network artifact"]
  const issues: string[] = []
  const joined = new Set<NetworkContractReceipt>()
  for (const raw of receipts) {
    for (const [name,value] of Object.entries(REQUIRED_SECURITY)) if (raw.headers[name] !== value) issues.push(`${raw.request}: ${name} must be ${value}`)
    for (const cookie of raw.cookies) {
      if (cookie.malformed) issues.push(`${raw.request}: malformed cookie ${cookie.name}`)
      else if (["session_token", ".Campfire.Csrf", ".Campfire.Flash", "last_room"].includes(cookie.name) && !validRawCookie(cookie, raw, cookie.name)) issues.push(`${raw.request}: ${cookie.name} attributes or lifetime invalid`)
      else if (cookie.name === "session_token" && !cookie.cleared && (!authIssuanceRequest(raw.request) || ![302,303].includes(raw.status))) issues.push(`${raw.request}: unexpected authentication ticket reissuance`)
    }
  }
  for (const block of blocks) {
    const members = matchingReceipts(block, receipts)
    if (!members.length) { issues.push(`${block.request}: exact per-response native security-header receipt coverage`); continue }
    for (const raw of members) {
      joined.add(raw)
      const shapes = raw.cookies.map(cookie => receiptCookieShape(cookie, true)).sort()
      if (JSON.stringify(shapes) !== JSON.stringify([...block.cookies].sort())) issues.push(`${block.request}: cookie receipts do not match recorded cookie shapes`)
    }
    if (!matchingReceipt(block, receipts)) issues.push(`${block.request}: inconsistent per-response contract policy receipts`)
  }
  for (const raw of receipts) if (!joined.has(raw)) issues.push(`${raw.request}: extra response receipt`)
  return issues
}

export function normalizeNativeNetworkPair(expected: string, actual: string, proof: NativeNetworkProof): NativeNetworkPair {
  const result: NativeNetworkPair = { expected, actual, applied: [], unproven: [] }
  const left = parseBlocks(expected), right = parseBlocks(actual)
  if (!left || !right) return result
  if (proof.actualReceipts !== undefined) {
    for (const issue of verifyNativeContractReceipts(actual, proof.actualReceipts)) {
      if (/per-response|extra response receipt/.test(issue)) result.unproven.push({ side: "actual", request: right.find(block => issue.startsWith(`${block.request}:`))?.request ?? "(capture)", rule: issue })
      else throw new Error(`Native network contract ${issue}`)
    }
  } else if (!proof.nativeCaptureSecurityVerified) {
    result.unproven.push({ side: "actual", request: "(capture)", rule: "native required security-header verification" })
    return result
  }
  for (const [side, blocks, receipts] of [["expected", left, proof.expectedReceipts], ["actual", right, proof.actualReceipts]] as const) {
    for (const block of blocks) {
      const raw = matchingReceipt(block, receipts)
      const apply = (rule: string) => result.applied.push({ side, request: block.request, rule })
      const unproven = (rule: string) => result.unproven.push({ side, request: block.request, rule })
      charset(block, apply)
      vary(block, apply)
      avatar(block, side, apply)
      cookies(block, side, raw, proof, apply, unproven)
      if (side === "actual") cacheHeaders(block, raw, apply, unproven)
      const owner = proof.documentOwners?.find(owner => owner.request === block.request && owner.status === block.status && owner.semanticEqual)
      if (owner && block.request.endsWith(" (navigation)") && /^text\/html(?:;|$)/i.test(block.values.get("content-type") ?? "")) {
        const hash = side === "expected" ? owner.expectedBodyHash : owner.actualBodyHash
        if (block.body === `sha256:${hash}` && /^[0-9a-f]{16}$/.test(hash)) {
          block.body = "independently compared document"
          apply("navigation body independently checked by semantic document owners")
        }
      }
    }
  }
  approvedEndpointPairs(left, right, proof, result)
  signedOutRedirectPair(left, right, proof, result)
  result.expected = serialize(left)
  result.actual = serialize(right)
  return result
}

function signedOutRedirectPair(left: Block[], right: Block[], proof: NativeNetworkProof, result: NativeNetworkPair): void {
  const owner = proof.signedOutRedirectOwner
  if (!owner || owner.state !== "auth/signed_out_redirect" || !owner.semanticEqual || !/^\/rooms\/[1-9][0-9]*$/.test(owner.protectedPath)) return
  const expectedUrl = new URL(owner.expectedUrl), actualUrl = new URL(owner.actualUrl)
  if (expectedUrl.origin !== actualUrl.origin || expectedUrl.pathname !== "/session/new" || expectedUrl.search || expectedUrl.hash
    || actualUrl.pathname !== expectedUrl.pathname || actualUrl.hash || [...actualUrl.searchParams].length !== 1 || actualUrl.searchParams.get("returnUrl") !== owner.protectedPath) return
  const one = (blocks: Block[], request: string) => { const found = blocks.filter(block => block.request === request); return found.length === 1 ? found[0] : undefined }
  const request = `GET ${owner.protectedPath} (navigation)`
  const e = one(left, request), a = one(right, request)
  const ef = one(left, "GET /session/new (navigation)"), af = one(right, `GET ${actualUrl.pathname + actualUrl.search} (navigation)`)
  if (!e || !a || !ef || !af || e.status !== 302 || a.status !== 302 || ef.status !== 200 || af.status !== 200
    || e.body !== "no body (redirect)" || a.body !== e.body || e.values.get("location") !== owner.expectedUrl || a.values.get("location") !== owner.actualUrl) return
  const er = matchingReceipt(e, proof.expectedReceipts), ar = matchingReceipt(a, proof.actualReceipts)
  const efr = matchingReceipt(ef, proof.expectedReceipts), afr = matchingReceipt(af, proof.actualReceipts)
  if (!er || !ar || !efr || !afr || [er, ar, efr, afr].some(raw => raw.originalBlock === undefined)
    || capturedLocation(er) !== owner.expectedUrl || capturedLocation(ar) !== owner.actualUrl
    || ef.body !== `sha256:${owner.expectedBodyHash}` || af.body !== `sha256:${owner.actualBodyHash}`
    || efr.bodyHash !== owner.expectedBodyHash || afr.bodyHash !== owner.actualBodyHash
    || !/^[0-9a-f]{16}$/.test(owner.expectedBodyHash) || !/^[0-9a-f]{16}$/.test(owner.actualBodyHash)) return
  // These are the exact documented challenge-header differences, not general header masks.
  if (e.values.get("cache-control") !== "no-cache" || er.headers["cache-control"] !== "no-cache"
    || e.values.get("content-type") !== "text/html; charset=utf-8" || er.headers["content-type"] !== "text/html; charset=utf-8"
    || a.headers.has("cache-control") || a.headers.has("content-type") || a.values.has("cache-control") || a.values.has("content-type")
    || Object.hasOwn(ar.headers, "cache-control") || Object.hasOwn(ar.headers, "content-type")) return
  for (const name of ["cache-control", "content-type"]) { e.headers.delete(name); e.values.delete(name) }
  a.values.set("location", owner.expectedUrl)
  af.request = ef.request
  ef.body = af.body = "independently compared signed-out sign-in document"
  result.applied.push({ side: "expected", request, rule: "signed-out challenge contract: native 302 omits Rails no-cache and HTML Content-Type" },
    { side: "actual", request, rule: "signed-out challenge exact original room returnUrl with independently compared sign-in document/ARIA" },
    { side: "actual", request: af.request, rule: "signed-out sign-in query and body independently checked; raw body hashes retained" })
  for (const [side, raw] of [["expected", er], ["actual", ar]] as const) if (raw.headers.location === undefined) result.applied.push({ side, request, rule: "retained challenge Location proved by exact captured originalBlock; raw Location field absent" })
}

function capturedLocation(raw: NetworkContractReceipt): string | undefined {
  const values = raw.originalBlock?.split("\n").filter(line => line.startsWith("  location: ")).map(line => line.slice(12))
  if (values?.length !== 1 || raw.headers.location !== undefined && raw.headers.location !== values[0]) return
  return values[0]
}

function approvedEndpointPairs(left: Block[], right: Block[], proof: NativeNetworkProof, result: NativeNetworkPair): void {
  const apply = (side: "expected" | "actual", block: Block, rule: string) => result.applied.push({ side, request: block.request, rule })
  const exactHeader = (block: Block, raw: NetworkContractReceipt | undefined, name: string, value: string) => raw?.headers[name] === value && block.values.get(name) === value
  const one = (blocks: Block[], request: string) => { const found = blocks.filter(block => block.request === request); return found.length === 1 ? found[0] : undefined }
  const rawHeader = (block: Block, raw: NetworkContractReceipt | undefined, name: string) => {
    const value = block.values.get(name)
    return value !== undefined && raw?.headers[name]?.toLowerCase().split(",").map(value => value.trim()).join(", ") === value
  }
  const cacheShape = (value?: string) => value?.split(",").map(value => value.trim()).sort().join(", ")
  const expectedManifest = one(left, "GET /webmanifest.json"), actualManifest = one(right, "GET /webmanifest.json")
  if (expectedManifest && actualManifest) {
    const a = actualManifest, e = expectedManifest, raw = matchingReceipt(a, proof.actualReceipts)
    if (a.status === 200 && e.status === 200 && a.body === e.body && /^sha256:[0-9a-f]{16}$/.test(a.body)
      && a.values.get("content-type") === "application/json; charset=utf-8" && e.values.get("content-type") === a.values.get("content-type")
      && a.values.get("content-encoding") === "gzip" && e.values.get("content-encoding") === "gzip" && a.values.get("cache-control") === e.values.get("cache-control")
      && a.values.get("vary") === "accept, accept-encoding" && e.values.get("vary") === "accept-encoding" && rawHeader(a, raw, "vary")
      && raw?.headers["content-type"] === a.values.get("content-type") && raw?.headers["content-encoding"] === a.values.get("content-encoding")
      && cacheShape(raw?.headers["cache-control"]) === "max-age=0, must-revalidate, private" && cacheShape(a.values.get("cache-control")) === "max-age=0, must-revalidate, private"
      && raw?.bodyHash === a.body.slice(7)) {
      a.values.set("vary", "accept-encoding"); apply("actual", a, "single-format webmanifest Accept Vary with equal JSON body")
    }
  }
  const expectedRejection = one(left, "POST /session"), actualRejection = one(right, "POST /session")
  if (proof.flashOwnerVerified && expectedRejection && actualRejection) {
    const a = actualRejection, e = expectedRejection, raw = matchingReceipt(a, proof.actualReceipts)
    if (a.status === 401 && e.status === 401 && a.body === e.body && a.body === "not compared (not a GET)"
      && a.values.get("content-type") === "text/html; charset=utf-8" && e.values.get("content-type") === a.values.get("content-type")
      && exactHeader(a, raw, "cache-control", "no-cache") && e.values.get("cache-control") === "no-cache"
      && exactHeader(a, raw, "pragma", "no-cache") && !e.headers.has("pragma") && !e.values.has("pragma")) {
      a.headers.delete("pragma"); a.values.delete("pragma"); apply("actual", a, "401 sign-in antiforgery Pragma with independently equal final rejection DOM and ARIA")
    }
  }
  const owner = proof.accountPostOwner
  if (owner && /^POST \/account\.[1-9][0-9]*$/.test(owner.expectedRequest) && owner.actualRequest === "POST /account") {
    const e = one(left, owner.expectedRequest), a = one(right, owner.actualRequest)
    if (e && a) {
      const er = matchingReceipt(e, proof.expectedReceipts), ar = matchingReceipt(a, proof.actualReceipts)
      if (e.status === 302 && a.status === 302 && e.body === "no body (redirect)" && a.body === e.body
        && e.values.get("location") === owner.location && a.values.get("location") === owner.location
        && exactHeader(e, er, "cache-control", "no-cache") && exactHeader(a, ar, "cache-control", "no-cache")
        && exactHeader(e, er, "content-type", "text/html; charset=utf-8") && exactHeader(a, ar, "content-type", "text/html; charset=utf-8")
        && er?.originalBlock === e.original && ar?.originalBlock === a.original) {
        e.request = owner.actualRequest; apply("expected", e, "exact raw account form owner POST route with equal redirect and final flash/document")
      }
    }
  }
}

function matchingReceipt(block: Block, receipts?: NetworkContractReceipt[]): NetworkContractReceipt | undefined {
  const matches = matchingReceipts(block, receipts)
  // Date/expiry may vary, but every member must prove the same cache rule. Cookie lifetime
  // and security are checked independently for every member by the shared verifier.
  return matches.length && matches.every(receipt => ["pragma", "expires"].every(name => receipt.headers[name] === matches[0].headers[name])) ? matches[0] : undefined
}

function matchingReceipts(block: Block, receipts?: NetworkContractReceipt[]): NetworkContractReceipt[] {
  const candidates = receipts?.filter(receipt => receipt.request === block.request && receipt.status === block.status) ?? []
  if (candidates.some(receipt => receipt.originalBlock === undefined)) return block.legacyJoinAllowed && candidates.length === 1 && candidates[0].originalBlock === undefined ? candidates : []
  return candidates.filter(receipt => receipt.originalBlock === block.original)
}

function charset(block: Block, apply: (rule: string) => void): void {
  const value = block.values.get("content-type")
  if (value?.endsWith("; charset=UTF-8")) {
    block.values.set("content-type", value.slice(0, -5) + "utf-8")
    apply("UTF-8 charset spelling")
  }
}

function vary(block: Block, apply: (rule: string) => void): void {
  let value = block.values.get("vary")
  if (value !== undefined) {
    const set = [...new Set(value.split(",").map(name => name.trim().toLowerCase()))].sort().join(", ")
    if (set !== value) { block.values.set("vary", set); value = set; apply("Vary deduplicated field-name set") }
  }
  if (value === "") { block.headers.delete("vary"); block.values.delete("vary"); apply("empty Vary equivalent to absent"); return }
  if (block.status === 200 && block.request.startsWith("GET ") && /^text\/html(?:;|$)/.test(block.values.get("content-type") ?? "") && (value === "accept" || value === "accept, accept-encoding")) {
    if (value === "accept") { block.headers.delete("vary"); block.values.delete("vary") }
    else block.values.set("vary", "accept-encoding")
    apply("HTML Accept negotiation Vary")
  }
}

function avatar(block: Block, _side: "expected" | "actual", apply: (rule: string) => void): void {
  if (block.status !== 200 || !/^GET \/users\/«signed_id:user\/avatar:\d+»\/avatar(?:\?v=\d+)?$/.test(block.request) || !/^image\//.test(block.values.get("content-type") ?? "")) return
  if (block.values.get("cache-control") === "max-age=1800, public, stale-while-revalidate=604800") {
    block.values.set("cache-control", "max-age=1800, public")
    apply("nonexpiring avatar seven-day stale-while-revalidate")
  }
}

function validCookieAttributes(shape: string, httpOnly: boolean): boolean {
  const [name, ...attributes] = shape.split("; ")
  if (name.endsWith(" (cleared)")) return false
  const permitted = ["path=/", "samesite=lax", ...(httpOnly ? ["httponly"] : [])]
  return attributes.length === permitted.length && attributes.every(value => permitted.includes(value)) ||
    attributes.length === permitted.length + 1 && attributes.includes("secure") && attributes.every(value => value === "secure" || permitted.includes(value))
}

function validRawCookie(raw: CookieContractReceipt | undefined, receipt: NetworkContractReceipt, kind: string): boolean {
  if (!raw || raw.malformed) return false
  const attrs = raw.attributes
  const allowed = new Set(["path", "httponly", "samesite", "secure", "expires", "max-age"])
  if (Object.keys(attrs).some(key => !allowed.has(key)) || attrs.path !== "/" || attrs.samesite?.toLowerCase() !== "lax" || attrs.httponly !== "" || Object.hasOwn(attrs, "secure") !== (receipt.protocol === "https:") || (Object.hasOwn(attrs, "secure") && attrs.secure !== "")) return false
  if (raw.cleared) return (!attrs.expires || Date.parse(attrs.expires) <= 0) && (!attrs["max-age"] || attrs["max-age"] === "0")
  if (kind === "session_token") {
    const seconds = (Date.parse(attrs.expires ?? "") - Date.parse(receipt.headers.date ?? "")) / 1000
    return Number.isFinite(seconds) && Math.abs(seconds - 30 * 86400) <= 5 && (!attrs["max-age"] || attrs["max-age"] === String(30 * 86400))
  }
  if (kind === ".Campfire.Csrf") return !attrs.expires && !attrs["max-age"]
  if (kind === ".Campfire.Flash") return attrs["max-age"] === "300" && !attrs.expires
  return kind === "last_room" && attrs["max-age"] === String(365 * 86400)
}

function cookies(block: Block, side: "expected" | "actual", raw: NetworkContractReceipt | undefined, proof: NativeNetworkProof, apply: (rule: string) => void, unproven: (rule: string) => void): void {
  const retained: string[] = []
  for (const cookie of block.cookies) {
    const name = cookie.split("; ")[0]
    if (side === "expected" && ISSUED_SESSION_NAMES.has(name) && validCookieAttributes(cookie, true) && (name !== "session_token" || !authIssuanceRequest(block.request))) {
      apply("Rails credential/session reissuance on ordinary responses")
      continue
    }
    if (side === "actual" && ISSUED_SESSION_NAMES.has(name) && validCookieAttributes(cookie, true)) {
      const originalName = NATIVE_NAMES[name]
      const supplied = raw?.cookies.filter(value => value.name === originalName)
      if (!raw || supplied?.length !== 1) unproven("native cookie issuer/lifetime evidence")
      else if (!validRawCookie(supplied[0], raw, originalName)) throw new Error(`Native network contract ${block.request}: ${originalName} attributes or lifetime invalid`)
      else if (name === "session_token" && (!authIssuanceRequest(block.request) || ![302, 303].includes(block.status))) throw new Error(`Native network contract ${block.request}: unexpected authentication ticket reissuance`)
      else {
        // Sign-in issuance is retained on both sides. Other valid CSRF issuance is a lifecycle difference.
        if (name === "_campfire_session") { apply("native session-only antiforgery issuance"); continue }
      }
    }
    if (side === "actual" && name === "last_room" && validCookieAttributes(cookie, true)) {
      const supplied = raw?.cookies.filter(value => value.name === "last_room")
      if (!raw || supplied?.length !== 1) unproven("last_room fixed-lifetime evidence")
      else if (!validRawCookie(supplied[0], raw, "last_room")) throw new Error(`Native network contract ${block.request}: last_room attributes or lifetime invalid`)
      else {
        retained.push(cookie.replace("; httponly", "")); apply("last_room HttpOnly and one-year native lifetime"); continue
      }
    }
    if (side === "actual" && /^\.Campfire.Flash(?: \(cleared\))?$/.test(name) && proof.flashOwnerVerified) {
      const supplied = raw?.cookies.filter(value => value.name === ".Campfire.Flash")
      if (!raw || supplied?.length !== 1) unproven("protected flash issuer/lifetime evidence")
      else if (!validRawCookie(supplied[0], raw, ".Campfire.Flash")) throw new Error(`Native network contract ${block.request}: flash attributes or lifetime invalid`)
      else { apply("protected flash cookie with independently compared flash content"); continue }
    }
    retained.push(cookie)
  }
  block.cookies = retained
  if (!retained.length) block.headers.delete("set-cookie")
}

function authIssuanceRequest(request: string): boolean {
  return /^(?:POST \/(?:session|first_run|join\/[^ ?]+)|(?:POST|PUT|PATCH) \/session\/transfers\/[^ ?]+)(?: \(navigation\))?$/.test(request)
}

function cacheHeaders(block: Block, raw: NetworkContractReceipt | undefined, apply: (rule: string) => void, unproven: (rule: string) => void): void {
  for (const name of ["pragma", "expires"]) if (raw?.headers[name] !== undefined && block.values.has(name) && raw.headers[name] !== block.values.get(name)) throw new Error(`Native network contract ${block.request}: ${name} receipt does not match recorded header`)
  const pragma = block.values.get("pragma") ?? raw?.headers.pragma
  const expires = block.values.get("expires") ?? raw?.headers.expires
  const error = [404, 500].includes(block.status) && /^text\/html(?:;|$)/.test(block.values.get("content-type") ?? "") && block.values.get("cache-control") === "no-cache, no-store"
  if (error && block.headers.has("pragma") && block.headers.has("expires")) {
    if (pragma === undefined || expires === undefined) unproven("native exception error-cache raw values")
    else if (pragma === "no-cache" && expires === "-1") {
      for (const name of ["pragma", "expires", "cache-control"]) { block.headers.delete(name); block.values.delete(name) }
      apply("native 404/500 exception no-store policy"); return
    }
  }
  if (block.headers.has("pragma") && !error && block.status >= 200 && block.status < 400 && /^text\/html(?:;|$)/.test(block.values.get("content-type") ?? "") && block.values.get("cache-control") === "max-age=0, must-revalidate, private") {
    if (pragma === undefined) unproven("antiforgery Pragma raw value")
    else if (pragma === "no-cache") { block.headers.delete("pragma"); block.values.delete("pragma"); apply("antiforgery Pragma no-cache") }
  }
  if ([302, 303].includes(block.status) && /^text\/html(?:;|$)/.test(block.values.get("content-type") ?? "") && block.values.get("cache-control") === "no-cache" && raw?.cookies.some(cookie => ["session_token", ".Campfire.Flash"].includes(cookie.name))) {
    if ((block.headers.has("pragma") && pragma === undefined) || (block.headers.has("expires") && expires === undefined)) unproven("native credential/flash redirect cache raw values")
    else if ((!block.headers.has("pragma") || pragma === "no-cache") && (!block.headers.has("expires") || expires === "Thu, 01 Jan 1970 00:00:00 GMT")) {
      for (const name of ["pragma", "expires"]) { block.headers.delete(name); block.values.delete(name) }
      apply("native credential/flash redirect past-expiry headers")
    }
  }
}

function parseBlocks(text: string): Block[] | undefined {
  const blocks: Block[] = []
  let current: Block | undefined
  for (const line of text.trimEnd().split("\n")) {
    const head = /^([A-Z]+ .+) → (\d{3})$/.exec(line)
    if (head) { current = { request: head[1], status: Number(head[2]), headers: new Set(), values: new Map(), cookies: [], body: "", other: [], original: line, legacyJoinAllowed: false }; blocks.push(current); continue }
    if (!current) return text.trim() ? undefined : []
    current.original += "\n" + line
    if (line.startsWith("  headers: ")) current.headers = new Set(line.slice(11).split(" ").filter(Boolean))
    else if (line.startsWith("  set-cookie: ")) current.cookies.push(line.slice(14))
    else if (line.startsWith("  body: ")) current.body = line.slice(8)
    else { const value = /^  ([a-z-]+): (.*)$/.exec(line); if (value) current.values.set(value[1], value[2]); else current.other.push(line) }
  }
  for (const block of blocks) block.legacyJoinAllowed = blocks.filter(other => other.request === block.request && other.status === block.status).length === 1
  return blocks
}

function serialize(blocks: Block[]): string {
  return blocks.map(block => [ `${block.request} → ${block.status}`, `  headers: ${[...block.headers].sort().join(" ")}`,
    ...[...block.values].sort(([left],[right]) => left.localeCompare(right)).map(([key,value]) => `  ${key}: ${value}`),
    ...block.cookies.sort().map(value => `  set-cookie: ${value}`), ...block.other, `  body: ${block.body}` ].join("\n")).sort().join("\n") + "\n"
}
