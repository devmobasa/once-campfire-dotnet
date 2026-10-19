// parity/capture entry point. Normally run through parity/bin/capture or parity/bin/compare,
// which put it inside the pinned Playwright image.
//
//   capture      --target URL [--name NAME] [--out DIR]           capture one server
//   compare      --expected URL --actual URL [--out DIR]          capture both and compare
//   recompare    --out DIR [--expected-name A --actual-name B]    compare existing captures again
//   breakpoints                                                   list swept widths per engine
//   list                                                          list the expanded jobs
//   seeds                                                         seeds the selected states use
//
// Common options:
//   --seed NAME            only states for this seed (default: default)
//   --time ISO             the instant both servers' clocks are frozen at (default: the seed's)
//   --only GLOB[,GLOB]     state id globs      --engines chromium,firefox,webkit
//   --viewports desktop,…  --schemes light,dark
//   --matrix lean|full     lean (default): Chromium desktop+phone, light+dark for every state, a smoke
//                          set on Firefox and WebKit, the breakpoint sweep on Chromium; full: the
//                          whole support matrix (release checks). See inventory.ts LEAN_*.
//   --breakpoints include|only|exclude (default include)
//   --breakpoint-states GLOBS   states swept across breakpoints (default DEFAULT_BREAKPOINT_STATES)
//   --workers N            --timeout MS        --inventory FILE
//   --allowlist FILE | --no-allowlist           --report NAME (report NAME.html, NAME.json)
//   --origin URL           the origin the browser sees for every server (default http://localhost:3999)
//   --reset CMD            start a fresh server from the seed at {port} ({url} {port} {target} {seed}); with it,
//                          each capture of a `mutates: true` state gets fresh servers of its own
//   --isolated N           how many such captures run at once (default 3); slot k of a server on
//                          port P listens on P + OFFSET * (k + 1)
//   --isolated-port-offset OFFSET  (default 1000)
//
// The self-parity gate (reference vs reference) is orchestrated by parity/bin/compare --self-parity,
// because it starts and stops servers on the host.
import fs from "node:fs"
import path from "node:path"
import { parseArgs } from "node:util"
import { execFile } from "node:child_process"
import { Allowlist } from "./allowlist.ts"
import { DEFAULT_ORIGIN } from "./proxy.ts"
import { BrowserPool } from "./browsers.ts"
import { scanWidthConditions } from "./breakpoints.ts"
import type { Target } from "./capture.ts"
import { compareJob } from "./compare.ts"
import { ALLOWLIST_FILE, DEFAULT_TIMEOUT_MS, ENGINES, PARITY_DIR, SCHEMES, SCREENS_FILE, SEED_DIR, VIEWPORT_NAMES } from "./config.ts"
import type { Engine, Scheme } from "./config.ts"
import { expandJobs, globToRegExp, jobId, loadInventory, seedTime } from "./inventory.ts"
import type { MatrixFilter } from "./inventory.ts"
import { summarize, writeReport } from "./report.ts"
import { breakpointWidths, defaultWorkers, run, summaryLine } from "./run.ts"
import type { RunResult } from "./run.ts"

const { values: opts, positionals } = parseArgs({
  allowPositionals: true,
  options: {
    target: { type: "string" },
    name: { type: "string" },
    expected: { type: "string" },
    actual: { type: "string" },
    "expected-name": { type: "string", default: "expected" },
    "actual-name": { type: "string", default: "actual" },
    out: { type: "string" },
    seed: { type: "string", default: "default" },
    time: { type: "string" },
    only: { type: "string" },
    engines: { type: "string" },
    viewports: { type: "string" },
    schemes: { type: "string" },
    breakpoints: { type: "string", default: "include" },
    matrix: { type: "string", default: "lean" },
    "breakpoint-states": { type: "string" },
    workers: { type: "string" },
    timeout: { type: "string" },
    inventory: { type: "string", default: SCREENS_FILE },
    allowlist: { type: "string", default: ALLOWLIST_FILE },
    "seed-dir": { type: "string", default: SEED_DIR },
    reset: { type: "string" },
    isolated: { type: "string" },
    "isolated-port-offset": { type: "string" },
    origin: { type: "string", default: DEFAULT_ORIGIN },
    "auth-cookie": { type: "string", default: "session_token" },
    "expected-auth-cookie": { type: "string", default: "session_token" },
    "actual-auth-cookie": { type: "string", default: "session_token" },
    "native-target": { type: "boolean", default: false },
    "expected-native-target": { type: "boolean", default: false },
    "actual-native-target": { type: "boolean", default: false },
    "no-allowlist": { type: "boolean", default: false },
    report: { type: "string", default: "report" },
    diagnostics: { type: "boolean", default: false },
    "diagnostic-no-har": { type: "boolean", default: false },
    direct: { type: "boolean", default: false },
    "native-token-resolver": { type: "string" },
    "native-contract": { type: "boolean", default: false },
  },
})

const command = positionals[0]
const list = (value?: string) => value?.split(",").map((s) => s.trim()).filter(Boolean)

function filter(): MatrixFilter {
  const engines = list(opts.engines) as Engine[] | undefined
  const viewports = list(opts.viewports)
  const schemes = list(opts.schemes) as Scheme[] | undefined
  for (const e of engines ?? []) if (!ENGINES.includes(e)) fail(`unknown engine ${e}`)
  for (const v of viewports ?? []) if (!VIEWPORT_NAMES.includes(v)) fail(`unknown viewport ${v}`)
  for (const s of schemes ?? []) if (!SCHEMES.includes(s)) fail(`unknown scheme ${s}`)
  if (!["include", "only", "exclude"].includes(opts.breakpoints!)) fail(`--breakpoints must be include, only or exclude`)
  if (!["lean", "full"].includes(opts.matrix!)) fail(`--matrix must be lean or full`)
  return { engines, viewports, schemes, only: list(opts.only), breakpoints: opts.breakpoints as MatrixFilter["breakpoints"], breakpointStates: list(opts["breakpoint-states"]), matrix: opts.matrix as MatrixFilter["matrix"] }
}

function states() {
  if (!fs.existsSync(opts.inventory!)) fail(`no inventory at ${opts.inventory}`)
  return loadInventory(opts.inventory!).filter((s) => s.seed === opts.seed)
}

function time(): string {
  const value = opts.time ?? seedTime(opts.seed!, opts["seed-dir"])
  if (!value || Number.isNaN(Date.parse(value))) fail(`pass --time ISO8601 (seed ${opts.seed} records no clock)`)
  return new Date(value!).toISOString()
}

function outDir(kind: string): string {
  const stamp = new Date().toISOString().replace(/[:.]/g, "-")
  // PARITY PATCH: PARITY_OUT_DIR (mounted by capture/sandbox/run.sh) replaces parity/out.
  return path.resolve(opts.out ?? path.join(process.env.PARITY_OUT_DIR || path.join(PARITY_DIR, "out"), `${kind}-${stamp}`))
}

function common() {
  return {
    states: states(),
    filter: filter(),
    time: time(),
    workers: opts.workers ? Number(opts.workers) : defaultWorkers(),
    timeoutMs: opts.timeout ? Number(opts.timeout) : DEFAULT_TIMEOUT_MS,
    seedDir: opts["seed-dir"],
    isolatedSlots: opts.isolated ? Number(opts.isolated) : undefined,
    isolatedPortOffset: opts["isolated-port-offset"] ? Number(opts["isolated-port-offset"]) : undefined,
    diagnostics: opts.diagnostics,
    diagnosticNoHar: opts["diagnostic-no-har"],
    direct: opts.direct,
    nativeTokenResolver: opts["native-token-resolver"],
  }
}

// --reset "CMD" starts a fresh server from the seed, with {url} {port} {target} {seed}. It runs
// asynchronously: other captures carry on while a server boots.
function resetHook() {
  const template = opts.reset
  if (!template) return undefined
  return (target: Target) => {
    const url = new URL(target.url)
    const command = template.replaceAll("{url}", target.url).replaceAll("{port}", url.port).replaceAll("{target}", target.name).replaceAll("{seed}", opts.seed!)
    return new Promise<void>((resolve, reject) => {
      const child = execFile("sh", ["-c", command], (error) => (error ? reject(new Error(`reset failed: ${command}: ${error.message}`)) : resolve()))
      child.stdout?.pipe(process.stdout)
      child.stderr?.pipe(process.stderr)
    })
  }
}

function loadAllowlist(): Allowlist {
  return opts["no-allowlist"] ? new Allowlist([]) : Allowlist.load(opts.allowlist!)
}

function fail(message: string): never {
  console.error(`parity: ${message}`)
  process.exit(2)
}

function finish(result: RunResult) {
  console.log(summaryLine(result))
  if (result.reportFile) console.log(`report: ${result.reportFile}`)
  const c = summarize(result.comparisons)
  const errors = result.metas.filter((m) => m.error).length
  process.exitCode = c.fail || c.error || errors ? 1 : 0
}

async function main() {
  switch (command) {
    case "capture": {
      if (!opts.target) fail("capture needs --target URL")
      const dir = outDir("capture")
      const result = await run({ ...common(), targets: [{ name: opts.name ?? "capture", url: opts.target!, origin: opts.origin!, authCookieName: opts["auth-cookie"], nativeTarget: opts["native-target"] }], outDir: dir, reset: resetHook() })
      console.log(`artifacts: ${path.join(dir, opts.name ?? "capture")}`)
      return finish(result)
    }
    case "compare": {
      if (!opts.expected || !opts.actual) fail("compare needs --expected URL --actual URL")
      const result = await run({
        ...common(),
        targets: [
          { name: opts["expected-name"]!, url: opts.expected!, origin: opts.origin!, authCookieName: opts["expected-auth-cookie"], nativeTarget: opts["expected-native-target"] },
          { name: opts["actual-name"]!, url: opts.actual!, origin: opts.origin!, authCookieName: opts["actual-auth-cookie"], nativeTarget: opts["actual-native-target"] },
        ],
        outDir: outDir("compare"),
        allowlist: loadAllowlist(),
        reset: resetHook(),
        reportName: opts.report,
      })
      return finish(result)
    }
    case "recompare": {
      if (!opts.out) fail("recompare needs --out DIR of an earlier run")
      return finish(recompare(path.resolve(opts.out!), opts["expected-name"]!, opts["actual-name"]!))
    }
    case "breakpoints": {
      for (const c of scanWidthConditions()) console.log(`(${c.feature}: ${c.length})  ${c.sources.join(" ")}`)
      const pool = new BrowserPool()
      try {
        const widths = await breakpointWidths(pool, filter().engines ?? ENGINES)
        for (const [engine, ws] of Object.entries(widths)) console.log(`${engine}: ${ws.join(", ")}`)
      } finally {
        await pool.close()
      }
      return
    }
    case "seeds": {
      // The seeds the selected states need, one per line (for parity/bin/compare --self-parity).
      const f = filter()
      const onlyRes = f.only?.map(globToRegExp)
      const all = loadInventory(opts.inventory!).filter((s) => !onlyRes || onlyRes.some((re) => re.test(s.id)))
      for (const seed of [...new Set(all.map((s) => s.seed))].sort()) console.log(seed)
      return
    }
    case "list": {
      const pool = new BrowserPool()
      try {
        const f = filter()
        const all = states()
        const widths = f.breakpoints !== "exclude" ? await breakpointWidths(pool, f.engines ?? ENGINES) : ({} as Record<Engine, number[]>)
        const jobs = expandJobs(all, f, widths)
        for (const job of jobs) console.log(jobId(job))
        console.log(`${jobs.length} cells in ${new Set(jobs.map((j) => j.state.id)).size} states`)
      } finally {
        await pool.close()
      }
      return
    }
    default:
      fail(`unknown command ${command ?? ""}; see parity/capture/cli.ts`)
  }
}

// Compare two capture directories under one run dir (names may contain slashes, e.g. run-1/expected).
function recompare(runDir: string, expectedName: string, actualName: string, quiet = false): RunResult {
  const started = Date.now()
  const allowlist = loadAllowlist()
  const jobs = expandJobs(states(), filter(), readWidths(path.join(runDir, expectedName)))
  const comparisons = jobs.map((job) => compareJob(job, runDir, expectedName, actualName, allowlist, { nativeComparison: opts["native-contract"] }))
  const result: RunResult = { jobs, metas: [], comparisons, durationMs: Date.now() - started }
  result.reportFile = writeReport(runDir, opts.report!, comparisons, {
    title: `Parity report: ${expectedName} vs ${actualName}`,
    expected: expectedName,
    actual: actualName,
    startedAt: new Date(started).toISOString(),
    durationMs: result.durationMs,
    unusedAllowlist: allowlist.unused(),
  })
  if (!quiet) for (const c of comparisons.filter((c) => c.status !== "pass")) console.log(`${c.state} @ ${c.cell} ${c.status}: ${c.layers.filter((l) => !l.equal).map((l) => l.layer).join(", ")}${c.error ? ` ${c.error.split("\n")[0]}` : ""}`)
  return result
}

// Breakpoint cells present in a capture directory, recovered from their names (bp<width>).
function readWidths(dir: string): Record<Engine, number[]> {
  const widths = Object.fromEntries(ENGINES.map((e) => [e, new Set<number>()])) as Record<Engine, Set<number>>
  const walk = (d: string) => {
    if (!fs.existsSync(d)) return
    for (const entry of fs.readdirSync(d, { withFileTypes: true })) {
      if (entry.isDirectory()) walk(path.join(d, entry.name))
      const m = /^(chromium|firefox|webkit)-bp(\d+)-/.exec(entry.name)
      if (m) widths[m[1] as Engine].add(Number(m[2]))
    }
  }
  walk(dir)
  return Object.fromEntries(Object.entries(widths).map(([e, s]) => [e, [...s].sort((a, b) => a - b)])) as Record<Engine, number[]>
}

main().catch((error) => {
  console.error(error?.stack ?? error)
  process.exit(1)
})
