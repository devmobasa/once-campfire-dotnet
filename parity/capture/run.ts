// Orchestrates a run: expand jobs, capture each against every target with a bounded pool of
// workers, compare, and write the report.
import os from "node:os"
import fs from "node:fs"
import path from "node:path"
import { execSync } from "node:child_process"
import { Allowlist } from "./allowlist.ts"
import { BrowserPool } from "./browsers.ts"
import { resolveBreakpointWidths, scanWidthConditions } from "./breakpoints.ts"
import { artifactBase, captureCell } from "./capture.ts"
import type { CaptureEnv, CellMeta, Target } from "./capture.ts"
import { compareJob, pixelOnlyFailure } from "./compare.ts"
import type { Attempt, CellComparison } from "./compare.ts"
import { ENGINES } from "./config.ts"
import type { Engine } from "./config.ts"
import { cellId, expandJobs, jobId } from "./inventory.ts"
import type { Job, MatrixFilter, State } from "./inventory.ts"
import { summarize, writeReport } from "./report.ts"
import { SessionCache } from "./session.ts"
import { startProxy } from "./proxy.ts"

// Each worker drives one browser context, roughly a core of renderer work, and the servers under
// test need room too. The box is shared, so the default is modest; PARITY_WORKERS or --workers
// raise it.
export function defaultWorkers(): number {
  const fromEnv = Number(process.env.PARITY_WORKERS)
  if (fromEnv > 0) return fromEnv
  return Math.max(2, Math.min(8, Math.floor(os.availableParallelism() / 4)))
}

export interface RunOptions {
  states: State[]
  filter: MatrixFilter
  targets: Target[]
  outDir: string
  time: string
  workers: number
  timeoutMs: number
  seedDir?: string
  allowlist?: Allowlist
  // Restores a fresh copy of the seed on a server; with it, every `mutates: true` job runs on
  // servers of its own (isolatedTargets), started fresh for each capture.
  reset?: (target: Target) => Promise<void>
  // Where isolated jobs run: slot k's server for a target (see isolatedUrl). Slots run in parallel
  // with the shared servers' jobs, on the same pool of workers.
  isolatedSlots?: number
  isolatedPortOffset?: number
  quiet?: boolean
  reportName?: string
  diagnostics?: boolean // trace/HAR/netlog: diagnostic output only
  diagnosticNoHar?: boolean // instrumentation A/B; acceptance assertions remain enabled
  direct?: boolean // diagnostic A/B only; requires the pinned container to reach the target
  nativeTokenResolver?: string // loopback diagnostic service with fixture-owned key rings
}

export interface RunResult {
  jobs: Job[]
  metas: CellMeta[]
  comparisons: CellComparison[]
  reportFile?: string
  durationMs: number
}

export async function breakpointWidths(pool: BrowserPool, engines: readonly Engine[]): Promise<Record<Engine, number[]>> {
  const conditions = scanWidthConditions()
  const widths = {} as Record<Engine, number[]>
  for (const engine of engines) {
    const { browser, release } = await pool.acquire(engine)
    try {
      widths[engine] = await resolveBreakpointWidths(browser, conditions)
    } finally {
      await release()
    }
  }
  return widths
}

export async function run(options: RunOptions): Promise<RunResult> {
  const started = Date.now()
  if (options.direct && !options.diagnostics) throw new Error("--direct is diagnostic-only and requires --diagnostics")
  const pool = new BrowserPool(options.diagnostics ? path.join(options.outDir, "diagnostics") : undefined)
  const sessions = new SessionCache()
  const env: CaptureEnv = { pool, sessions, outDir: options.outDir, time: options.time, timeoutMs: options.timeoutMs, seedDir: options.seedDir, diagnostics: options.diagnostics, diagnosticNoHar: options.diagnosticNoHar, nativeComparison: options.targets.some((target) => target.nativeTarget), nativeTokenResolver: options.nativeTokenResolver }
  fs.mkdirSync(options.outDir, { recursive: true })
  const slots = options.reset ? Math.max(1, options.isolatedSlots ?? DEFAULT_ISOLATED_SLOTS) : 0
  // isolated[k][i]: slot k's server standing in for options.targets[i]
  const isolated: Target[][] = Array.from({ length: slots }, (_, k) =>
    options.targets.map((t) => ({ ...t, url: isolatedUrl(t.url, k, options.isolatedPortOffset), proxy: undefined })),
  )
  const all = [...options.targets, ...isolated.flat()]
  const proxies = options.direct ? [] : await Promise.all(all.map((t) => startProxy(t.url)))
  all.forEach((t, i) => { if (options.direct) t.origin = t.url; else { t.proxy = proxies[i].server; t.wireHeaders = proxies[i].wireHeaders } })
  try {
    const wantsBreakpoints = options.filter.breakpoints !== "exclude"
    const engines = (options.filter.engines ?? ENGINES).filter((e) => ENGINES.includes(e))
    const widths = wantsBreakpoints ? await breakpointWidths(pool, engines) : ({} as Record<Engine, number[]>)
    if (wantsBreakpoints && !options.quiet) console.log(`breakpoint widths: ${JSON.stringify(widths)}`)
    const jobs = expandJobs(options.states, options.filter, widths)
    const metas: CellMeta[] = []
    const comparisons: CellComparison[] = []
    let done = 0

    // A mutating state starts from the seed on every capture: its slot's server is reset first.
    // Without a reset hook, mutating jobs run last, on the shared servers, one at a time.
    const reset = async (target: Target) => {
      await options.reset!(target)
      sessions.forget(target)
    }

    // A capture on a fresh server: a failed reset fails the capture (and its one retry).
    const captureOn = async (job: Job, target: Target, fresh: boolean): Promise<CellMeta> => {
      if (fresh) {
        try {
          await reset(target)
        } catch (error: any) {
          const meta = { state: job.state.id, cell: cellId(job.cell), target: target.name, url: target.url, kind: "page", error: String(error?.message ?? error), pageErrors: [], console: [], durationMs: 0 } as CellMeta
          const base = artifactBase(options.outDir, target.name, job)
          fs.mkdirSync(path.dirname(base), { recursive: true })
          fs.writeFileSync(base + ".json", JSON.stringify(meta, null, 2) + "\n")
          return meta
        }
      }
      return captureCell(job, target, env)
    }

    const captureAll = async (job: Job, slot?: number): Promise<CellMeta[]> => {
      const fresh = slot !== undefined
      const targets = fresh ? isolated[slot] : options.targets
      const captured: CellMeta[] = []
      for (const target of targets) {
        let meta = await captureOn(job, target, fresh)
        if (meta.error && !options.diagnostics) {
          // One retry for infrastructure flakes (a crashed renderer, a page whose modules never
          // ran); the retry is recorded, and a deterministic failure fails again.
          const first = meta.error
          meta = await captureOn(job, target, fresh)
          meta.retriedAfter = first
          fs.writeFileSync(artifactBase(options.outDir, target.name, job) + ".json", JSON.stringify(meta, null, 2) + "\n")
        }
        captured.push(meta)
      }
      return captured
    }

    const runJob = async (job: Job, slot?: number) => {
      let captured = await captureAll(job, slot)
      let error = captured.find((m) => m.error)?.error
      let status = error ? "error" : "captured"
      if (options.targets.length === 2 && options.allowlist) {
        const compare = () => compareJob(job, options.outDir, options.targets[0].name, options.targets[1].name, options.allowlist!, { nativeComparison: env.nativeComparison })
        let comparison = compare()
        // Pixel flake policy (parity/SCREENS.md, "Pixel flakes"): when every server-output layer
        // is identical and only the pixels differ, the cell is captured again on both sides, up to
        // PIXEL_RETRIES more times. A later match passes the cell, marked flaky with its attempts.
        const attempts: Attempt[] = []
        while (pixelOnlyFailure(comparison) && attempts.length < PIXEL_RETRIES) {
          attempts.push(keepAttempt(options.outDir, options.targets, job, comparison, attempts.length + 1))
          captured = await captureAll(job, slot)
          error = captured.find((m) => m.error)?.error
          comparison = compare()
        }
        if (attempts.length) {
          comparison.attempts = [...attempts, attemptOf(comparison, attempts.length + 1)]
          comparison.flaky = comparison.status === "pass"
        }
        comparisons.push(comparison)
        status = comparison.flaky ? "pass (flaky)" : comparison.status
      }
      metas.push(...captured)
      done++
      if (!options.quiet && (status !== "pass" || done % 25 === 0 || done === jobs.length)) {
        console.log(`[${done}/${jobs.length}] ${jobId(job)} ${status}${error ? `: ${error.split("\n")[0]}` : ""}`)
      }
    }

    const shared = interleave(jobs.filter((j) => !j.state.mutates))
    const mutating = interleave(jobs.filter((j) => j.state.mutates))
    if (!options.quiet) {
      console.log(`${jobs.length} cells (${shared.length} on shared servers, ${mutating.length} ${slots ? `on fresh servers in ${slots} slots` : "serial"}) × ${options.targets.length} targets, ${options.workers} workers`)
    }
    if (slots) {
      await schedule(shared, mutating, options.workers, slots, runJob)
    } else {
      await schedule(shared, [], options.workers, 0, runJob)
      for (const job of mutating) await runJob(job)
    }

    const result: RunResult = { jobs, metas, comparisons, durationMs: Date.now() - started }
    if (options.targets.length === 2 && options.allowlist) {
      result.reportFile = writeReport(options.outDir, options.reportName ?? "report", comparisons, {
        title: "Parity report",
        expected: `${options.targets[0].name} ${options.targets[0].url}`,
        actual: `${options.targets[1].name} ${options.targets[1].url}`,
        startedAt: new Date(started).toISOString(),
        durationMs: result.durationMs,
        unusedAllowlist: options.allowlist.unused(),
      })
    }
    return result
  } finally {
    await pool.close()
    await Promise.all(proxies.map((p) => p.close()))
  }
}

export const PIXEL_RETRIES = 2

function attemptOf(comparison: CellComparison, attempt: number): Attempt {
  const pixels = comparison.layers.find((l) => l.layer === "pixels")?.pixels
  return { attempt, status: comparison.status, differentPixels: pixels?.differentPixels, sizeMismatch: pixels?.sizeMismatch }
}

// Keeps a failed attempt's screenshots and diff beside the cell's artifacts (<cell>.attempt-N.png),
// since the next capture overwrites them.
function keepAttempt(outDir: string, targets: Target[], job: Job, comparison: CellComparison, attempt: number): Attempt {
  const record = attemptOf(comparison, attempt)
  const keep = (file: string) => {
    const kept = file.replace(/\.png$/, `.attempt-${attempt}.png`)
    fs.copyFileSync(file, kept)
    return kept
  }
  const [expected, actual] = targets.map((t) => artifactBase(outDir, t.name, job) + ".png")
  record.images = { expected: keep(expected), actual: keep(actual), diff: comparison.diffImage && fs.existsSync(comparison.diffImage) ? keep(comparison.diffImage) : undefined }
  return record
}

export const DEFAULT_ISOLATED_SLOTS = 3
export const DEFAULT_ISOLATED_PORT_OFFSET = 1000

// Slot k of a server on port P listens on P + offset * (k + 1): 4101 -> 5101, 6101, ...
export function isolatedUrl(url: string, slot: number, offset = DEFAULT_ISOLATED_PORT_OFFSET): string {
  const u = new URL(url)
  u.port = String(Number(u.port || 80) + offset * (slot + 1))
  return u.href.replace(/\/$/, "")
}

// Runs every job on `workers` workers. Isolated jobs take a free slot, and are preferred while one
// is free (they are the long ones); otherwise a worker takes the next shared job.
async function schedule(shared: Job[], isolated: Job[], workers: number, slots: number, fn: (job: Job, slot?: number) => Promise<void>) {
  const free = Array.from({ length: slots }, (_, k) => k)
  let wake: (() => void)[] = []
  const notify = () => {
    const waiting = wake
    wake = []
    waiting.forEach((resolve) => resolve())
  }
  const worker = async () => {
    while (shared.length || isolated.length) {
      if (isolated.length && free.length) {
        const slot = free.shift()!
        try {
          await fn(isolated.shift()!, slot)
        } finally {
          free.push(slot)
          notify()
        }
      } else if (shared.length) {
        await fn(shared.shift()!)
      } else {
        await new Promise<void>((resolve) => wake.push(resolve))
      }
    }
    notify()
  }
  await Promise.all(Array.from({ length: Math.max(1, workers) }, worker))
}

// Round-robin across engines so concurrent workers spread over all browsers.
export function interleave(jobs: Job[]): Job[] {
  const groups = ENGINES.map((engine) => jobs.filter((j) => j.cell.engine === engine))
  const out: Job[] = []
  for (let k = 0; out.length < jobs.length; k++) for (const group of groups) if (group[k]) out.push(group[k])
  return out
}

export function summaryLine(result: RunResult): string {
  if (!result.comparisons.length) {
    const errors = result.metas.filter((m) => m.error).length
    return `${result.metas.length} captures, ${errors} errors in ${(result.durationMs / 1000).toFixed(1)}s`
  }
  const c = summarize(result.comparisons)
  const flaky = result.comparisons.filter((r) => r.flaky).length
  return `${result.comparisons.length} cells: ${c.pass} pass (${flaky} flaky), ${c.fail} fail, ${c.allowed} allowed, ${c.error} error in ${(result.durationMs / 1000).toFixed(1)}s`
}

export function shell(command: string) {
  execSync(command, { stdio: ["ignore", "inherit", "inherit"] })
}

export function relativeToCwd(file: string) {
  return path.relative(process.cwd(), file) || file
}
