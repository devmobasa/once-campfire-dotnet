"""Shared pieces for bench/attrib and bench/profile: start the Rust app in one of several serving
configurations, drive bench/loadgen against it, and account CPU and memory per process.

Configurations (see `start`):

  proxy-thruster  docker -p 127.0.0.1:PORT:80, bin/boot (the front server, Thruster's job since 6b0d797)
  proxy-direct    docker -p 127.0.0.1:PORT:3000, the bare app on TARGET_PORT (no front server)
  host-thruster   docker --network host, the front server on PORT (no docker-proxy): what bench/run measures
  host-direct     docker --network host, the bare app on PORT (neither)
  native-<label>  the host binary NATIVE_<LABEL>_BIN, front server on PORT (optionally LD_PRELOAD=NATIVE_<LABEL>_PRELOAD),
                  on the host, pinned to SERVER_CPUS: for build variants and in-process profilers

Every configuration gets its own copy of the seed, with outbound Web Push and webhook endpoints
rewritten to 127.0.0.1:9 so deliveries fail fast and locally in every configuration (bench/run gets
the same effect from `--dns 127.0.0.1`, which host networking can't use).
"""
import json, os, re, resource, shutil, signal, sqlite3, subprocess, threading, time, urllib.request

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
BENCH = os.path.join(ROOT, "bench")
# PATCH: PARITY_SEED_DIR, as parity/bin/seed writes it.
SEED = os.path.join(os.environ.get("PARITY_SEED_DIR") or os.path.join(ROOT, "parity", ".seed"), "default")
ENV_FILE = os.path.join(ROOT, "parity", ".env.reference")
LOADGEN = os.path.join(ROOT, "target", "bench", "release", "loadgen")

SERVER_CPUS = os.environ.get("SERVER_CPUS", "8-11")
LOADGEN_CPUS = os.environ.get("LOADGEN_CPUS", "12-15")
PORT = int(os.environ.get("PORT", "4390"))
IMAGE = os.environ.get("RUST_IMAGE", "campfire-rust:app")
CONTAINER = f"bench-attrib-{PORT}"
# BENCH_WORK_DIR puts the app's storage elsewhere (e.g. on tmpfs, to take fsync out of the picture).
WORK = os.path.join(os.environ.get("BENCH_WORK_DIR", os.path.join(BENCH, ".work")), f"attrib-{PORT}")
CLK_TCK = os.sysconf("SC_CLK_TCK")


def log(*a):
    print(time.strftime("[%H:%M:%S]"), *a, flush=True, file=__import__("sys").stderr)


def loadavg():
    return open("/proc/loadavg").read().split()[:3]


def build_loadgen():
    subprocess.run(["cargo", "build", "--release", "-q"], cwd=os.path.join(BENCH, "loadgen"), check=True,
                   env={**os.environ, "CARGO_TARGET_DIR": os.path.join(ROOT, "target", "bench")})


# --- Seed ------------------------------------------------------------------------------------

def snapshot_seed(dest):
    """Copies the seed once per run (the parity tools regenerate it) and neuters outbound
    deliveries."""
    shutil.rmtree(dest, ignore_errors=True)
    shutil.copytree(SEED, dest)
    db = sqlite3.connect(os.path.join(dest, "db", "production.sqlite3"))
    db.execute("UPDATE push_subscriptions SET endpoint = 'https://127.0.0.1:9/push/' || id")
    db.execute("UPDATE webhooks SET url = 'http://127.0.0.1:9/hook/' || id")
    db.commit()
    db.close()
    return json.load(open(os.path.join(dest, "labels.json")))


def fresh_storage(seed):
    shutil.rmtree(WORK, ignore_errors=True)
    os.makedirs(os.path.join(WORK, "storage"))
    for sub in ("db", "storage"):
        dst = os.path.join(WORK, "storage", "db" if sub == "db" else "files")
        shutil.copytree(os.path.join(seed, sub), dst)
    return os.path.join(WORK, "storage")


# --- Environment -------------------------------------------------------------------------------

def app_env():
    """parity/.env.reference with bench/run's process model for SERVER_CPUS."""
    ncpu = int(subprocess.check_output(["taskset", "-c", SERVER_CPUS, "nproc"]).strip())
    workers = (ncpu * 666 + 999) // 1000
    env = {}
    for line in open(ENV_FILE):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        k, v = line.split("=", 1)
        env[k] = v
    env.update(WEB_CONCURRENCY=str(workers), JOB_CONCURRENCY=str(workers), RAILS_MAX_THREADS="5", RAILS_LOG_LEVEL="warn")
    return env


# --- Processes -----------------------------------------------------------------------------------

def children(pid):
    out = []
    for p in os.listdir("/proc"):
        if p.isdigit():
            try:
                stat = open(f"/proc/{p}/stat").read()
            except OSError:
                continue
            ppid = int(stat.rsplit(")", 1)[1].split()[1])
            if ppid == pid:
                out.append(int(p))
    return out


def comm(pid):
    try:
        return open(f"/proc/{pid}/comm").read().strip()
    except OSError:
        return ""


def cpu_secs(pid):
    """utime + stime of every thread of `pid`, in seconds."""
    try:
        f = open(f"/proc/{pid}/stat").read().rsplit(")", 1)[1].split()
        return (int(f[11]) + int(f[12])) / CLK_TCK
    except OSError:
        return 0.0


def rss(pid):
    """RssAnon and VmRSS in MB."""
    out = {}
    try:
        for line in open(f"/proc/{pid}/status"):
            k, _, v = line.partition(":")
            if k in ("RssAnon", "VmRSS", "VmHWM", "Threads"):
                out[k] = int(v.split()[0]) / (1024 if k != "Threads" else 1)
    except OSError:
        pass
    return out


def docker_proxy_pids(port):
    pids = []
    for p in os.listdir("/proc"):
        if p.isdigit():
            try:
                cmd = open(f"/proc/{p}/cmdline", "rb").read().split(b"\0")
            except OSError:
                continue
            if cmd and cmd[0].endswith(b"docker-proxy") and str(port).encode() in cmd:
                pids.append(int(p))
    return pids


class App:
    def __init__(self, config, proc=None):
        self.config = config
        self.proc = proc
        self.pids = {}
        self.base = f"http://127.0.0.1:{PORT}"

    def discover(self):
        if self.proc is not None:
            self.pids["campfire"] = self.proc.pid
        else:
            root = int(subprocess.check_output(["docker", "inspect", "-f", "{{.State.Pid}}", CONTAINER]).strip())
            if comm(root) == "campfire":
                self.pids["campfire"] = root
            else:
                self.pids["thrust"] = root
                for c in children(root):
                    if comm(c) == "campfire":
                        self.pids["campfire"] = c
            for i, p in enumerate(docker_proxy_pids(PORT)):
                self.pids[f"docker-proxy{i or ''}"] = p

    def cpu(self):
        return {k: cpu_secs(p) for k, p in self.pids.items()}

    def stop(self):
        if self.proc is not None:
            self.proc.send_signal(signal.SIGTERM)
            try:
                self.proc.wait(15)
            except subprocess.TimeoutExpired:
                self.proc.kill()
        subprocess.run(["docker", "rm", "-f", CONTAINER], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def wait_up(base, timeout=60):
    t0 = time.time()
    while time.time() - t0 < timeout:
        try:
            if urllib.request.urlopen(base + "/up", timeout=1).status == 200:
                return time.time() - t0
        except Exception:
            time.sleep(0.02)
    raise RuntimeError("app did not come up")


def start(config, seed, extra_env=None):
    """Starts `config` on a fresh copy of the seed and waits for /up."""
    subprocess.run(["docker", "rm", "-f", CONTAINER], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    storage = fresh_storage(seed)
    env = app_env()
    env.update(extra_env or {})
    if config.startswith("native-"):
        label = config[len("native-"):].upper().replace("-", "_")
        binary = os.environ[f"NATIVE_{label}_BIN"]
        preload = os.environ.get(f"NATIVE_{label}_PRELOAD", "")
        penv = {k: v for k, v in os.environ.items() if k in ("PATH", "HOME", "USER", "LANG")}
        penv.update(env)
        # The front server (crates/kit/src/front, what Thruster did) on PORT, the bare app beside it.
        penv.update(HTTP_PORT=str(PORT), TARGET_PORT=str(PORT + 1), CAMPFIRE_STORAGE_PATH=storage)
        if preload:
            penv["LD_PRELOAD"] = preload
        logf = open(os.path.join(WORK, "app.log"), "w")
        proc = subprocess.Popen(["taskset", "-c", SERVER_CPUS, binary, "server"], cwd=WORK, env=penv, stdout=logf, stderr=logf)
        app = App(config, proc)
    else:
        net, entry = config.split("-")
        args = ["docker", "run", "-d", "--name", CONTAINER, "--cpuset-cpus", SERVER_CPUS, "--user", f"{os.getuid()}:{os.getgid()}",
                "-v", f"{storage}/db:/rails/storage/db", "-v", f"{storage}/files:/rails/storage/files"]
        if net == "proxy":
            args += ["--dns", "127.0.0.1", "-p", f"127.0.0.1:{PORT}:{80 if entry == 'thruster' else 3000}"]
        else:
            args += ["--network", "host"]
            # "thruster" is the in-process front server since 6b0d797; "direct" the bare app on TARGET_PORT.
            if entry == "thruster":
                env.update(HTTP_PORT=str(PORT), TARGET_PORT=str(PORT + 1))
            else:
                env.update(HTTP_PORT=str(PORT + 1), TARGET_PORT=str(PORT))
        for k, v in env.items():
            args += ["-e", f"{k}={v}"]
        args.append(IMAGE)
        if entry == "direct":
            args += ["campfire", "server"]
        subprocess.run(args, check=True, stdout=subprocess.DEVNULL)
        app = App(config)
    try:
        app.cold_start_s = wait_up(app.base)
    except RuntimeError:
        if app.proc is None:
            subprocess.run(["docker", "logs", "--tail", "30", CONTAINER])
        else:
            print(open(os.path.join(WORK, "app.log")).read()[-3000:])
        app.stop()
        raise
    app.discover()
    return app


# --- Load generator ------------------------------------------------------------------------------

def lg(*args, stderr=False):
    """Runs loadgen pinned to LOADGEN_CPUS; returns (json, loadgen cpu secs[, stderr])."""
    r0 = resource.getrusage(resource.RUSAGE_CHILDREN)
    p = subprocess.run(["taskset", "-c", LOADGEN_CPUS, LOADGEN, *map(str, args)], capture_output=True, text=True)
    r1 = resource.getrusage(resource.RUSAGE_CHILDREN)
    if p.returncode != 0:
        raise RuntimeError(f"loadgen {args[0]} failed: {p.stderr[-2000:]}")
    cpu = (r1.ru_utime - r0.ru_utime) + (r1.ru_stime - r0.ru_stime)
    out = json.loads(p.stdout)
    return (out, cpu, p.stderr) if stderr else (out, cpu)


def session(app, labels):
    cookie = lg("login", "--base", app.base, "--email", labels["emails.david"], "--password", labels["passwords.all"])[0]["cookie"]
    room = labels["rooms.watercooler"]
    s = lg("scrape", "--base", app.base, "--cookie", cookie, "--room", room)[0]
    return {
        "cookie": cookie, "csrf": s["csrf"] or "", "streams": ",".join(s["streams"]), "room": room,
        "write_room": labels["rooms.hq"], "before": labels["messages.busy_060"], "css": s["css"],
    }


def routes(sess):
    r, w = sess["room"], sess["write_room"]
    return {
        "up": ["--path", "/up"],
        "room_show": ["--path", f"/rooms/{r}"],
        "room_show_identity": ["--path", f"/rooms/{r}", "--gzip", "0"],
        "messages_page": ["--path", f"/rooms/{r}/messages?before={sess['before']}"],
        "sidebar": ["--path", "/users/me/sidebar"],
        "search": ["--path", "/searches?q=coffee"],
        "post_message": ["--post-room", w, "--csrf", sess["csrf"]],
    }


def http(app, sess, route_args, conc, secs, extra=()):
    """One closed-loop run, with CPU seconds per request for each server-side process."""
    c0 = app.cpu()
    res, lg_cpu = lg("http", "--base", app.base, "--cookie", sess["cookie"], *route_args, "--conc", conc, "--duration", secs, *extra)
    c1 = app.cpu()
    n = max(res["latency"].get("n", 0), 1)
    res["cpu_ms_per_req"] = {k: round((c1[k] - c0[k]) * 1000 / n, 4) for k in c0}
    res["cpu_ms_per_req"]["loadgen"] = round(lg_cpu * 1000 / n, 4)
    res["cpu_cores"] = {k: round((c1[k] - c0[k]) / res["secs"], 2) for k in c0}
    res["cpu_cores"]["loadgen"] = round(lg_cpu / res["secs"], 2)
    return res


class MemSampler(threading.Thread):
    """Samples RssAnon of the app (and of thrust, when present) every 100 ms, with wall-clock ms,
    plus the container's cgroup memory.current for docker configurations."""

    def __init__(self, app):
        super().__init__(daemon=True)
        self.app, self.samples, self.running = app, [], True
        self.cgroup = None
        if app.proc is None:
            cid = subprocess.run(["docker", "inspect", "-f", "{{.Id}}", CONTAINER], capture_output=True, text=True).stdout.strip()
            path = f"/sys/fs/cgroup/system.slice/docker-{cid}.scope/memory.current"
            if not os.path.exists(path):
                # PATCH: rootless Podman (eng/docker-shim) reports its cgroup path itself.
                rel = subprocess.run(["docker", "inspect", "-f", "{{.State.CgroupPath}}", CONTAINER],
                                     capture_output=True, text=True).stdout.strip()
                path = f"/sys/fs/cgroup{rel}/memory.current" if rel.startswith("/") else path
            self.cgroup = path if os.path.exists(path) else None

    def run(self):
        while self.running:
            m = rss(self.app.pids["campfire"])
            if m:
                thrust = rss(self.app.pids["thrust"]).get("RssAnon", 0) if "thrust" in self.app.pids else 0
                cg = int(open(self.cgroup).read()) / 1048576 if self.cgroup else 0
                self.samples.append((int(time.time() * 1000), m.get("RssAnon", 0), m.get("VmRSS", 0), m.get("Threads", 0), thrust, cg))
            time.sleep(0.1)

    def stop(self):
        self.running = False
        self.join()


def cable(app, sess, clients, tput_secs=15, posters=4, hold=0):
    sampler = MemSampler(app)
    sampler.start()
    c0 = app.cpu()
    res, lg_cpu, err = lg("cable", "--base", app.base, "--cookie", sess["cookie"], "--room", sess["room"], "--csrf", sess["csrf"],
                          "--streams", sess["streams"], "--clients", clients, "--tput-secs", tput_secs, "--posters", posters,
                          "--hold-secs", hold, stderr=True)
    c1 = app.cpu()
    time.sleep(1)
    sampler.stop()
    phases = [(m.group(1), int(m.group(2))) for m in re.finditer(r"PHASE (\w+) (\d+)", err)]
    res["memory_by_phase"] = phase_memory(sampler.samples, phases)
    res["cpu_secs"] = {k: round(c1[k] - c0[k], 2) for k in c0}
    res["cpu_secs"]["loadgen"] = round(lg_cpu, 2)
    deliveries = res["throughput"]["complete"] * res["ready"] + res["latency"]["complete"] * res["ready"]
    res["cpu_us_per_delivery"] = {k: round(v * 1e6 / max(deliveries, 1), 2) for k, v in res["cpu_secs"].items()}
    return res


def phase_memory(samples, phases):
    """Peak and end RssAnon (MB) within each phase window."""
    out = {}
    bounds = phases + [("after", 10**15)]
    if samples:
        before = [s for s in samples if s[0] < (phases[0][1] if phases else 0)]
        out["before"] = {"peak_anon_mb": round(max((s[1] for s in before), default=0), 1)}
    for (name, t0), (_, t1) in zip(bounds, bounds[1:]):
        # The window plus the last sample before it, so short phases still get a reading.
        prior = [s for s in samples if s[0] < t0][-1:]
        window = prior + [s for s in samples if t0 <= s[0] < t1]
        if window:
            out[name] = {"peak_anon_mb": round(max(s[1] for s in window), 1), "end_anon_mb": round(window[-1][1], 1),
                         "peak_threads": int(max(s[3] for s in window))}
            if len(window[0]) > 4:
                out[name]["thrust_peak_anon_mb"] = round(max(s[4] for s in window), 1)
                out[name]["cgroup_peak_mb"] = round(max(s[5] for s in window), 1)
    return out
