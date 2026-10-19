import assert from "node:assert/strict"
import fs from "node:fs"
import os from "node:os"
import path from "node:path"
import test from "node:test"
import type { BrowserPool } from "../browsers.ts"
import { captureCell } from "../capture.ts"
import { VIEWPORTS } from "../config.ts"
import type { Labels } from "../inventory.ts"
import type { SessionCache } from "../session.ts"

test("native fixture credentials use the explicit target profile when Rails shares session_token", async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "campfire-native-profile-"))
  const seed = path.basename(directory)
  const shared = { users: { david: 1 }, avatar_tokens: { david: "rails-token" } }
  const native = { users: { david: 2 }, avatar_tokens: { david: "native-token" } }
  fs.mkdirSync(path.join(directory, seed), { recursive: true })
  fs.writeFileSync(path.join(directory, seed, "labels.json"), JSON.stringify(shared))
  const sidecar = path.join(directory, ".candidates", "3001", "db")
  fs.mkdirSync(sidecar, { recursive: true })
  fs.writeFileSync(path.join(sidecar, "native-fixture.json"), JSON.stringify({ labels: native }))
  try {
    for (const [nativeTarget, authCookieName, expected] of [[true, "session_token", native], [false, "session_token", shared], [false, ".Campfire.Auth", shared]] as const) {
      let credentials: Labels | undefined
      const pool = { acquire: async () => ({ browser: { version: () => "140.0.0.0" }, release: async () => {} }) } as unknown as BrowserPool
      const sessions = { get: async (_browser: unknown, _target: unknown, _proxy: unknown, _user: unknown, labels: Labels) => { credentials = labels; throw new Error("Reached sign-in boundary") } } as unknown as SessionCache
      const target = { name: "candidate", url: "http://127.0.0.1:3001", origin: "http://capture.test", authCookieName, nativeTarget }
      const meta = await captureCell(
        { state: { id: "auth/profile", path: "/", seed, as: "david", steps: [] }, cell: { engine: "chromium", viewport: VIEWPORTS.desktop, scheme: "light" } },
        target, { pool, sessions, outDir: path.join(directory, "out"), seedDir: directory, time: "2026-10-18T00:00:00Z", timeoutMs: 1000 },
      )
      assert.equal(meta.error, "Reached sign-in boundary")
      assert.deepEqual(credentials, expected, `${nativeTarget} ${authCookieName}`)
    }
  } finally { fs.rmSync(directory, { recursive: true, force: true }) }
})
