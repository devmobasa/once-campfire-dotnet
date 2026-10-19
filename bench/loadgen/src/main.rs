//! Load generator for the Campfire benchmark (bench/run). Talks plain HTTP/1.1 and Action Cable
//! (WebSocket) to one server and prints one JSON object per command on stdout.
//!
//!   loadgen login  --base URL --email E --password P            -> {"cookie": "..."}
//!   loadgen scrape --base URL --cookie C --room ID              -> csrf token, stream names, assets
//!   loadgen http   --base URL --cookie C --path P --conc N --duration S
//!                  [--post-room ID --csrf T]                     -> latency/throughput
//!   loadgen cable  --base URL --cookie C --room ID --csrf T --clients N [--streams a,b,c]
//!                  [--sources 127.0.0.2,127.0.0.3 --hold-secs 60 --deflate 1]
//!                  [--latency-msgs 30 --interval-ms 200 --tput-secs 15 --posters 4]
//!   loadgen upload --base URL --cookie C --room ID --csrf T --file PATH [--reps 5]
//!   loadgen fetch  --base URL --cookie C --path P --out FILE     -> saves an uncompressed body
//!   loadgen gzip   --file F [--iters 200]                         -> CPU per compression, by backend/level
//!
//! `http` also takes `--gzip 0` (`Accept-Encoding: identity`), `--requests N` (stop after N requests,
//! for allocation counting) and `--trace FILE` (each request's start, latency and status). `cable` prints `PHASE <name> <unix ms>` lines on stderr so memory
//! samples can be attributed to its phases.
//!
//! Responses are validated during benchmarks and
//! mismatches counted, never silently dropped.
//!   http    --expect-status 200[,304] --expect-type text/html --expect-body 'needle|needle' --expect-echo 1
//!           --validate-every N: every response's status, Content-Type and Content-Encoding are checked; the body
//!           of every Nth response per connection (default every one) is gunzipped when compressed and searched
//!           for each needle (and, with --expect-echo, for the posted message text). -> "validation"
//!   cable   every delivery is checked: each confirmed client must get each successfully posted message exactly
//!           once, on one of the page's Turbo streams (--streams). -> "verification"
//!   upload  the POST must answer 2xx with an <img>, and the thumbnail 200 with an image/* body. -> "failures"
//! Every command's JSON also carries "loadgen": its own CPU time and utilization of the CPUs it may run on, so
//! a saturated load generator (a lower-bound result) shows in the report.

use std::collections::{HashMap, HashSet};
use std::io::Read;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

mod server_cpu;
mod wire_ws;
mod debug_logging;
mod frame_memo;
mod golden_control;
static BENCH_USER_AGENT: std::sync::OnceLock<Option<hyper::header::HeaderValue>> = std::sync::OnceLock::new();
mod upload_response;

use bytes::Bytes;
use hdrhistogram::Histogram;
use http_body_util::{BodyExt, Full};
use hyper::client::conn::http1::SendRequest;
use hyper_util::rt::TokioIo;
use serde_json::{Value, json};
use tokio::net::TcpStream;

type Res<T> = Result<T, Box<dyn std::error::Error + Send + Sync>>;

struct Args(HashMap<String, String>);

impl Args {
    fn parse(raw: &[String]) -> Self {
        let mut map = HashMap::new();
        let mut it = raw.iter();
        while let Some(k) = it.next() {
            if let Some(k) = k.strip_prefix("--") {
                map.insert(k.to_string(), it.next().cloned().unwrap_or_default());
            }
        }
        Args(map)
    }
    fn get(&self, k: &str) -> String {
        self.0.get(k).cloned().unwrap_or_else(|| panic!("missing --{k}"))
    }
    fn opt(&self, k: &str) -> Option<String> {
        self.0.get(k).cloned()
    }
    fn num<T: std::str::FromStr>(&self, k: &str, default: T) -> T {
        self.0.get(k).and_then(|v| v.parse().ok()).unwrap_or(default)
    }
}

fn host_port(base: &str) -> String {
    base.trim_start_matches("http://").trim_end_matches('/').to_string()
}

async fn connect(addr: &str) -> Res<SendRequest<Full<Bytes>>> {
    let stream = TcpStream::connect(addr).await?;
    stream.set_nodelay(true)?;
    let (sender, conn) = hyper::client::conn::http1::handshake(TokioIo::new(stream)).await?;
    tokio::spawn(async move {
        let _ = conn.await;
    });
    Ok(sender)
}

struct Resp {
    status: u16,
    headers: hyper::HeaderMap,
    body: Bytes,
}

async fn send(
    sender: &mut SendRequest<Full<Bytes>>,
    addr: &str,
    method: &str,
    path: &str,
    headers: &[(&str, String)],
    body: Bytes,
) -> Res<Resp> {
    let mut req = hyper::Request::builder().method(method).uri(path).header("host", addr);
    if let Some(Some(agent)) = BENCH_USER_AGENT.get() { req = req.header(hyper::header::USER_AGENT, agent.clone()); }
    for (k, v) in headers {
        req = req.header(*k, v.as_str());
    }
    let req = req.body(Full::new(body))?;
    // A request that takes longer than this counts as an error (and drops the connection).
    tokio::time::timeout(Duration::from_secs(30), async {
        sender.ready().await?;
        let resp = sender.send_request(req).await?;
        let status = resp.status().as_u16();
        let headers = resp.headers().clone();
        let body = resp.into_body().collect().await?.to_bytes();
        Ok(Resp { status, headers, body })
    })
    .await
    .map_err(|_| "request timed out after 30s")?
}

async fn one_shot(addr: &str, method: &str, path: &str, headers: &[(&str, String)], body: Bytes) -> Res<Resp> {
    let mut s = connect(addr).await?;
    send(&mut s, addr, method, path, headers, body).await
}

fn urlencode(s: &str) -> String {
    let mut out = String::new();
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => out.push(b as char),
            _ => out.push_str(&format!("%{b:02X}")),
        }
    }
    out
}

fn form(pairs: &[(&str, &str)]) -> Bytes {
    Bytes::from(pairs.iter().map(|(k, v)| format!("{}={}", urlencode(k), urlencode(v))).collect::<Vec<_>>().join("&"))
}

fn merge_cookies(jar: &mut Vec<(String, String)>, headers: &hyper::HeaderMap) {
    for v in headers.get_all("set-cookie") {
        let s = v.to_str().unwrap_or("");
        let pair = s.split(';').next().unwrap_or("");
        if let Some((k, v)) = pair.split_once('=') {
            jar.retain(|(n, _)| n != k);
            jar.push((k.to_string(), v.to_string()));
        }
    }
}

fn cookie_header(jar: &[(String, String)]) -> String {
    jar.iter().map(|(k, v)| format!("{k}={v}")).collect::<Vec<_>>().join("; ")
}

/// The page's CSRF token: the Rails app renders one; the Rust app doesn't (it checks `Sec-Fetch-Site`).
fn csrf_from(html: &str) -> Option<String> {
    regex::Regex::new(r#"<meta name="csrf-token" content="([^"]*)""#).unwrap().captures(html).map(|c| c[1].to_string())
}

/// What a browser sends with requests its pages make; the Rust app's forgery protection needs it.
fn same_origin() -> (&'static str, String) {
    ("sec-fetch-site", "same-origin".into())
}

fn unescape(s: &str) -> String {
    s.replace("&amp;", "&").replace("&quot;", "\"").replace("&#39;", "'")
}

async fn login(a: &Args) -> Res<Value> {
    let addr = host_port(&a.get("base"));
    let mut jar = Vec::new();
    let r = one_shot(&addr, "GET", "/session/new", &[], Bytes::new()).await?;
    merge_cookies(&mut jar, &r.headers);
    let token = csrf_from(&String::from_utf8_lossy(&r.body)).unwrap_or_default();
    let body = form(&[("email_address", &a.get("email")), ("password", &a.get("password")), ("authenticity_token", &token)]);
    let r = one_shot(
        &addr,
        "POST",
        "/session",
        &[("cookie", cookie_header(&jar)), ("content-type", "application/x-www-form-urlencoded".into()), same_origin()],
        body,
    )
    .await?;
    merge_cookies(&mut jar, &r.headers);
    let cookie_name = a.opt("auth-cookie-name").unwrap_or_else(|| "session_token".into());
    if r.status != 302 || !jar.iter().any(|(k, _)| k == &cookie_name) {
        return Err(format!("login failed: {}", r.status).into());
    }
    let probe = one_shot(&addr, "GET", "/users/me/profile", &[("cookie", cookie_header(&jar))], Bytes::new()).await?;
    if probe.status != 200 || !String::from_utf8_lossy(&probe.body).contains("name=\"user[name]\"") {
        return Err(format!("authenticated profile probe failed: {}", probe.status).into());
    }
    Ok(json!({ "cookie": cookie_header(&jar) }))
}

async fn scrape(a: &Args) -> Res<Value> {
    let addr = host_port(&a.get("base"));
    let path = format!("/rooms/{}", a.get("room"));
    let r = one_shot(&addr, "GET", &path, &[("cookie", a.get("cookie"))], Bytes::new()).await?;
    let mut html = String::from_utf8_lossy(&r.body).to_string();
    // The sidebar frame (loaded lazily by the page) carries the rooms/user-rooms stream sources.
    let sidebar = one_shot(&addr, "GET", "/users/me/sidebar", &[("cookie", a.get("cookie"))], Bytes::new()).await?;
    if r.status != 200 || sidebar.status != 200 || !html.contains("<turbo-cable-stream-source") {
        return Err(format!("authenticated room/sidebar scrape failed: {} / {}", r.status, sidebar.status).into());
    }
    html.push_str(&String::from_utf8_lossy(&sidebar.body));
    let streams: Vec<String> = {
        // "Channel|signed name" for each <turbo-cable-stream-source>.
        let re = regex::Regex::new(r#"<turbo-cable-stream-source channel="([^"]+)" signed-stream-name="([^"]+)""#).unwrap();
        let mut seen = HashSet::new();
        re.captures_iter(&html).map(|c| format!("{}|{}", &c[1], unescape(&c[2]))).filter(|s| seen.insert(s.clone())).collect()
    };
    let css = regex::Regex::new(r#"href="(/assets/[^"]+\.css)""#).unwrap().captures(&html).map(|c| c[1].to_string());
    let js = regex::Regex::new(r#"(/assets/[^"]+\.js)""#).unwrap().captures(&html).map(|c| c[1].to_string());
    Ok(json!({
        "status": r.status,
        "bytes": r.body.len(),
        "csrf": csrf_from(&html),
        "streams": streams,
        "css": css,
        "js": js,
    }))
}

fn hist() -> Histogram<u64> {
    Histogram::new_with_bounds(1, 120_000_000, 3).unwrap()
}

fn ms(us: u64) -> f64 {
    (us as f64 / 1000.0 * 1000.0).round() / 1000.0
}

fn summary(h: &Histogram<u64>) -> Value {
    if h.is_empty() {
        return json!({"n": 0});
    }
    json!({
        "n": h.len(),
        "p50_ms": ms(h.value_at_quantile(0.5)),
        "p90_ms": ms(h.value_at_quantile(0.9)),
        "p95_ms": ms(h.value_at_quantile(0.95)),
        "p99_ms": ms(h.value_at_quantile(0.99)),
        "max_ms": ms(h.max()),
        "mean_ms": (h.mean() / 10.0).round() / 100.0,
    })
}

static SEQ: AtomicU64 = AtomicU64::new(0);

fn nonce() -> String {
    let t = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos();
    format!("{:x}{:x}", t, SEQ.fetch_add(1, Ordering::Relaxed))
}

// PATCH: response validation (see the header). One `Expect` per command; one `Validation` per connection task,
// merged at the end.
struct Expect {
    /// Empty: any status below 400 passes (the harness's old notion of ok).
    statuses: Vec<u16>,
    /// A prefix of the Content-Type, compared case-insensitively.
    content_type: Option<String>,
    /// Literal needles, each of which must appear in the decoded body.
    needles: Vec<regex::bytes::Regex>,
    needle_text: Vec<String>,
    /// POST: the posted message text must come back in the response.
    echo: bool,
    /// Decode and search the body of every Nth response of a connection task.
    every: u64,
    /// `Accept-Encoding: identity` was sent, so a compressed response is a mismatch.
    identity: bool,
}

impl Expect {
    fn from_args(a: &Args, identity: bool) -> Res<Self> {
        let needle_text: Vec<String> = a.opt("expect-body").unwrap_or_default().split('|').filter(|s| !s.is_empty()).map(str::to_string).collect();
        let needles = needle_text.iter().map(|n| regex::bytes::Regex::new(&regex::escape(n))).collect::<Result<_, _>>()?;
        Ok(Expect {
            statuses: a.opt("expect-status").unwrap_or_default().split(',').filter_map(|s| s.trim().parse().ok()).collect(),
            content_type: a.opt("expect-type").filter(|s| !s.is_empty()).map(|s| s.to_ascii_lowercase()),
            needles,
            needle_text,
            echo: a.num("expect-echo", 0u8) != 0,
            every: a.num("validate-every", 1u64).max(1),
            identity,
        })
    }

    fn checks_bodies(&self) -> bool {
        !self.needles.is_empty() || self.echo
    }
}

#[derive(Default)]
struct Validation {
    /// Responses whose status and headers were checked (all of them).
    checked: u64,
    /// Responses whose body was decoded and searched.
    bodies: u64,
    kinds: HashMap<&'static str, u64>,
    samples: Vec<String>,
    encodings: HashMap<String, u64>,
    /// Transport failures (connect, send, timeout): counted in `errors`, described here.
    error_samples: Vec<String>,
}

impl Validation {
    fn fail(&mut self, kind: &'static str, detail: String) {
        *self.kinds.entry(kind).or_default() += 1;
        if self.samples.len() < 5 {
            self.samples.push(format!("{kind}: {detail}"));
        }
    }

    fn merge(&mut self, other: Validation) {
        self.checked += other.checked;
        self.bodies += other.bodies;
        for (k, v) in other.kinds {
            *self.kinds.entry(k).or_default() += v;
        }
        for s in other.samples {
            if self.samples.len() < 5 {
                self.samples.push(s);
            }
        }
        for (k, v) in other.encodings {
            *self.encodings.entry(k).or_default() += v;
        }
        for s in other.error_samples {
            if self.error_samples.len() < 5 {
                self.error_samples.push(s);
            }
        }
    }

    fn transport_error(&mut self, detail: String) {
        if self.error_samples.len() < 5 {
            self.error_samples.push(detail);
        }
    }

    fn mismatches(&self) -> u64 {
        self.kinds.values().sum()
    }

    fn json(&self) -> Value {
        json!({
            "checked": self.checked,
            "bodies_checked": self.bodies,
            "mismatches": self.mismatches(),
            "kinds": self.kinds.iter().map(|(k, v)| (k.to_string(), json!(v))).collect::<serde_json::Map<_, _>>(),
            "samples": self.samples,
            "error_samples": self.error_samples,
            "content_encodings": self.encodings.iter().map(|(k, v)| (k.clone(), json!(v))).collect::<serde_json::Map<_, _>>(),
        })
    }
}

fn header_str<'a>(headers: &'a hyper::HeaderMap, name: &str) -> &'a str {
    headers.get(name).and_then(|v| v.to_str().ok()).unwrap_or("")
}

/// `needle` in `haystack`, not followed by another digit ("bench write 1" must not match "bench write 12").
fn contains_token(haystack: &[u8], needle: &str) -> bool {
    let n = needle.as_bytes();
    let mut from = 0;
    while let Some(i) = haystack[from..].windows(n.len()).position(|w| w == n) {
        let end = from + i + n.len();
        if !haystack.get(end).is_some_and(u8::is_ascii_digit) {
            return true;
        }
        from = from + i + 1;
    }
    false
}

/// Checks one response. `body_check` says whether this one's body is decoded and searched; `echo` is the
/// message text a POST sent.
fn validate(e: &Expect, v: &mut Validation, r: &Resp, echo: Option<&str>, body_check: bool) {
    v.checked += 1;
    let encoding = match header_str(&r.headers, "content-encoding").to_ascii_lowercase() {
        s if s.is_empty() => "identity".to_string(),
        s => s,
    };
    *v.encodings.entry(encoding.clone()).or_default() += 1;
    let status_ok = if e.statuses.is_empty() { r.status < 400 } else { e.statuses.contains(&r.status) };
    if !status_ok {
        v.fail("status", r.status.to_string());
        return;
    }
    if let Some(t) = &e.content_type {
        let ct = header_str(&r.headers, "content-type");
        if !ct.to_ascii_lowercase().starts_with(t.as_str()) {
            v.fail("type", ct.to_string());
        }
    }
    if e.identity && encoding != "identity" {
        v.fail("encoding", format!("{encoding} although identity was asked for"));
    }
    if !body_check || !e.checks_bodies() || r.status == 304 {
        return;
    }
    v.bodies += 1;
    let decoded;
    let body: &[u8] = match encoding.as_str() {
        "identity" => &r.body,
        "gzip" => {
            let mut out = Vec::with_capacity(r.body.len() * 8);
            if let Err(err) = flate2::read::MultiGzDecoder::new(&r.body[..]).read_to_end(&mut out) {
                v.fail("gzip", err.to_string());
                return;
            }
            decoded = out;
            &decoded
        }
        other => {
            v.fail("encoding", format!("cannot decode {other}"));
            return;
        }
    };
    for (re, text) in e.needles.iter().zip(&e.needle_text) {
        if !re.is_match(body) {
            v.fail("body", format!("missing {text:?} in {} bytes", body.len()));
        }
    }
    if let Some(text) = echo.filter(|_| e.echo) {
        if !contains_token(body, text) {
            v.fail("echo", format!("missing {text:?}"));
        }
    }
}

/// This process's CPU time (user + system, all threads) in seconds, from /proc/self/stat (USER_HZ is 100 on
/// Linux).
fn cpu_secs() -> f64 {
    std::fs::read_to_string("/proc/self/stat")
        .ok()
        .and_then(|s| {
            let fields: Vec<&str> = s.rsplit_once(')')?.1.split_whitespace().collect();
            Some((fields.get(11)?.parse::<f64>().ok()? + fields.get(12)?.parse::<f64>().ok()?) / 100.0)
        })
        .unwrap_or(0.0)
}

fn allowed_cpus() -> usize {
    std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1)
}

fn message_request(cookie: &str, csrf: &str, body_text: &str) -> (Vec<(&'static str, String)>, Bytes) {
    let body = form(&[
        ("message[body]", body_text),
        ("message[client_message_id]", &nonce()),
        ("authenticity_token", csrf),
    ]);
    (
        vec![
            ("cookie", cookie.to_string()),
            ("content-type", "application/x-www-form-urlencoded".into()),
            ("accept", "text/vnd.turbo-stream.html, text/html, application/xhtml+xml".into()),
            ("x-csrf-token", csrf.to_string()),
            same_origin(),
        ],
        body,
    )
}

async fn http_load(a: &Args) -> Res<Value> {
    let addr = host_port(&a.get("base"));
    let cookie = a.opt("cookie").unwrap_or_default();
    let path = a.opt("path").unwrap_or_else(|| "/".into());
    let conc: usize = a.num("conc", 1);
    let duration = Duration::from_secs_f64(a.num("duration", 10.0));
    let post_room = a.opt("post-room");
    let csrf = a.opt("csrf").unwrap_or_default();
    let gzip = a.num("gzip", 1u8) != 0;
    let accept_encoding = if gzip { "gzip" } else { "identity" };
    let limit: u64 = a.num("requests", u64::MAX);
    let trace_path = a.opt("trace");
    let trace = Arc::new(Mutex::new(Vec::<(u128, u64, u16)>::new()));
    let issued = Arc::new(AtomicU64::new(0));
    let expect = Arc::new(Expect::from_args(a, !gzip)?); // PATCH: response validation
    let validation = Arc::new(Mutex::new(Validation::default()));

    let hist_all = Arc::new(Mutex::new(hist()));
    let statuses = Arc::new(Mutex::new(HashMap::<u16, u64>::new()));
    let errors = Arc::new(AtomicU64::new(0));
    let bytes_total = Arc::new(AtomicU64::new(0));
    // PATCH: snapshot the owned container outside the measured workload interval.
    let server_cpu = a.opt("server-cgroup").map(|path| server_cpu::Snapshot::read(&path)).transpose()?;
    let started_unix_us = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH)?.as_micros();
    let start = Instant::now();
    let deadline = start + duration;
    let mut tasks = Vec::new();
    for _ in 0..conc {
        let (addr, cookie, path, post_room, csrf) = (addr.clone(), cookie.clone(), path.clone(), post_room.clone(), csrf.clone());
        let (hist_all, statuses, errors, bytes_total, issued, trace) =
            (hist_all.clone(), statuses.clone(), errors.clone(), bytes_total.clone(), issued.clone(), trace.clone());
        let (expect, validation) = (expect.clone(), validation.clone());
        let tracing = trace_path.is_some();
        tasks.push(tokio::spawn(async move {
            let mut h = hist();
            let mut local = HashMap::<u16, u64>::new();
            let mut checks = Validation::default();
            let mut responses = 0u64;
            let mut conn: Option<SendRequest<Full<Bytes>>> = None;
            let mut i = 0u64;
            let mut local_trace = Vec::new();
            while Instant::now() < deadline && issued.fetch_add(1, Ordering::Relaxed) < limit {
                if conn.is_none() {
                    match connect(&addr).await {
                        Ok(c) => conn = Some(c),
                        Err(e) => {
                            checks.transport_error(format!("connect: {e}"));
                            errors.fetch_add(1, Ordering::Relaxed);
                            tokio::time::sleep(Duration::from_millis(10)).await;
                            continue;
                        }
                    }
                }
                let mut echo = None;
                let (method, p, headers, body) = match &post_room {
                    Some(room) => {
                        i += 1;
                        let text = format!("bench write {i}");
                        let (mut h, b) = message_request(&cookie, &csrf, &text);
                        h.push(("accept-encoding", accept_encoding.into()));
                        echo = Some(text);
                        ("POST", format!("/rooms/{room}/messages"), h, b)
                    }
                    None => {
                        let mut h = vec![("cookie", cookie.clone())];
                        h.push(("accept-encoding", accept_encoding.into()));
                        ("GET", path.clone(), h, Bytes::new())
                    }
                };
                let t0 = Instant::now();
                let wall0 = if tracing { std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_micros() } else { 0 };
                match send(conn.as_mut().unwrap(), &addr, method, &p, &headers, body).await {
                    Ok(r) => {
                        h.record(t0.elapsed().as_micros() as u64).ok();
                        if tracing {
                            local_trace.push((wall0, t0.elapsed().as_micros() as u64, r.status));
                        }
                        *local.entry(r.status).or_default() += 1;
                        bytes_total.fetch_add(r.body.len() as u64, Ordering::Relaxed);
                        // PATCH: validate after the latency is recorded, so checking costs the load generator's
                        // CPU but not the measured time.
                        validate(&expect, &mut checks, &r, echo.as_deref(), responses % expect.every == 0);
                        responses += 1;
                        if r.headers.get("connection").map(|v| v == "close").unwrap_or(false) {
                            conn = None;
                        }
                    }
                    Err(e) => {
                        checks.transport_error(format!("request {} after {} ms: {e:?}", responses + 1, t0.elapsed().as_millis()));
                        errors.fetch_add(1, Ordering::Relaxed);
                        conn = None;
                    }
                }
            }
            hist_all.lock().unwrap().add(&h).unwrap();
            trace.lock().unwrap().extend(local_trace);
            validation.lock().unwrap().merge(checks);
            let mut s = statuses.lock().unwrap();
            for (k, v) in local {
                *s.entry(k).or_default() += v;
            }
        }));
    }
    for t in tasks {
        t.await?;
    }
    let elapsed = start.elapsed().as_secs_f64();
    let ended_unix_us = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH)?.as_micros();
    let server_cpu = server_cpu.map(|snapshot| snapshot.finish()).transpose()?;
    if let Some(path) = &trace_path {
        // One line per request: start (unix µs), latency (µs), status.
        let mut t = trace.lock().unwrap();
        t.sort();
        let lines: String = t.iter().map(|(s, l, st)| format!("{s} {l} {st}\n")).collect();
        std::fs::write(path, lines)?;
    }
    let h = hist_all.lock().unwrap();
    let st = statuses.lock().unwrap();
    let ok: u64 = st.iter().filter(|(k, _)| **k < 400).map(|(_, v)| v).sum();
    let validation = validation.lock().unwrap();
    Ok(json!({
        "mismatches": validation.mismatches(),
        "validation": validation.json(),
        "path": if let Some(r) = &post_room { format!("POST /rooms/{r}/messages") } else { path },
        "conc": conc,
        "gzip": gzip,
        "secs": (elapsed * 100.0).round() / 100.0,
        "started_unix_us": started_unix_us,
        "ended_unix_us": ended_unix_us,
        "rps": ((h.len() as f64 / elapsed) * 10.0).round() / 10.0,
        "ok": ok,
        "statuses": st.iter().map(|(k, v)| (k.to_string(), json!(v))).collect::<serde_json::Map<_, _>>(),
        "errors": errors.load(Ordering::Relaxed),
        "avg_bytes": if h.len() > 0 { bytes_total.load(Ordering::Relaxed) / h.len() } else { 0 },
        "latency": summary(&h),
        "server_cpu": server_cpu.map(|sample| sample.json(h.len())),
    }))
}

// ---------------------------------------------------------------------------------------------
// Action Cable fan-out

struct Delivery {
    expected_clients: usize,
    sent: Mutex<HashMap<u64, Instant>>,
    got: Mutex<HashMap<u64, (usize, Instant)>>, // seq -> (clients received, last receipt)
    per_client: Mutex<Histogram<u64>>,
    receipts: AtomicU64,
    /// Common actual RFC6455 reader counters in both plain/offered lanes.
    wire: Arc<wire_ws::Counters>,
    memo: Arc<frame_memo::Memo>,
    // PATCH: delivery verification. A delivery must arrive on one of the page's Turbo streams (its signed name
    // is in the frame's identifier), once per client, and carry a message this run posted.
    duplicates: AtomicU64,
    off_stream: AtomicU64,
    unexpected: AtomicU64,
    post_failures: AtomicU64,
    disconnected: AtomicU64,
    samples: Mutex<Vec<String>>,
}

impl Delivery {
    fn sample(&self, what: String) {
        let mut s = self.samples.lock().unwrap();
        if s.len() < 5 {
            s.push(what);
        }
    }
}

fn markers(text: &str) -> Vec<u64> {
    let mut out = Vec::new();
    let mut rest = text;
    while let Some(i) = rest.find("bmk") {
        rest = &rest[i + 3..];
        let digits: String = rest.chars().take_while(|c| c.is_ascii_digit()).collect();
        if !digits.is_empty() && rest[digits.len()..].starts_with('z') {
            if let Ok(n) = digits.parse() {
                out.push(n);
            }
        }
    }
    out
}

struct CableClientRequest { addr: String, source: Option<std::net::IpAddr>, cookie: String, subs: Vec<String>, requested_deflate: bool }
async fn cable_client(
    request: CableClientRequest, confirmed: Arc<AtomicUsize>, connected: Arc<AtomicUsize>, stop: Arc<AtomicBool>, delivery: Arc<Delivery>,
) -> Res<()> {
    let CableClientRequest { addr, source, cookie, subs, requested_deflate } = request;
    let socket = tokio::net::TcpSocket::new_v4()?;
    if let Some(source) = source { socket.bind(std::net::SocketAddr::new(source, 0))?; }
    let stream = socket.connect(tokio::net::lookup_host(&addr).await?.next().ok_or("no address")?).await?;
    stream.set_nodelay(true)?;
    let agent = BENCH_USER_AGENT.get().and_then(|value| value.as_ref()).map(|value| value.to_str().expect("validated User-Agent"));
    let mut ws = tokio::time::timeout(Duration::from_secs(30), wire_ws::WireWs::handshake(stream, &addr, &cookie, requested_deflate, agent, delivery.wire.clone())).await.map_err(|_| "WebSocket handshake timed out")??;
    connected.fetch_add(1, Ordering::Relaxed);
    for ident in &subs { ws.send_text(&json!({"command": "subscribe", "identifier": ident}).to_string()).await?; }
    let mut seen = HashSet::new();
    let mut confirms = 0;
    while !stop.load(Ordering::Relaxed) {
        let Some((facts, received)) = ws.next_validated(&delivery.memo).await? else { break; };
        on_text(&facts, received, subs.len(), &mut confirms, &mut seen, &confirmed, &delivery);
    }
    Ok(())
}

/// A frame's text: counts subscription confirmations and records each marked message's arrival.
fn on_text(facts: &frame_memo::Facts, now: Instant, subscriptions: usize, confirms: &mut usize, seen: &mut HashSet<u64>, confirmed: &AtomicUsize, delivery: &Delivery) {
    let text = facts.text.as_ref();
    if debug_logging::enabled() {
        eprintln!("<< {}", &text[..text.len().min(300)]);
    }
    if facts.confirmation {
        *confirms += 1;
        if *confirms == subscriptions {
            confirmed.fetch_add(1, Ordering::Relaxed);
        }
        return;
    }
    // PATCH: a marker repeated inside one frame is one delivery; the same marker in a second frame is a duplicate.
    let on_stream = facts.on_stream;
    for &seq in &facts.markers {
        if !seen.insert(seq) {
            delivery.duplicates.fetch_add(1, Ordering::Relaxed);
            delivery.sample(format!("duplicate: message {seq} arrived twice on one client"));
            continue;
        }
        if !on_stream {
            delivery.off_stream.fetch_add(1, Ordering::Relaxed);
            delivery.sample(format!("off stream: message {seq} in {}", text.chars().take(160).collect::<String>()));
        }
        delivery.receipts.fetch_add(1, Ordering::Relaxed);
        let sent = delivery.sent.lock().unwrap().get(&seq).copied();
        if let Some(t0) = sent {
            delivery.per_client.lock().unwrap().record(now.duration_since(t0).as_micros() as u64).ok();
        } else {
            delivery.unexpected.fetch_add(1, Ordering::Relaxed);
            delivery.sample(format!("unexpected: message {seq} was never posted"));
        }
        let mut got = delivery.got.lock().unwrap();
        let e = got.entry(seq).or_insert((0, now));
        e.0 += 1;
        e.1 = now;
        let complete=e.0==delivery.expected_clients;
        drop(got);
        if complete {delivery.memo.retire(seq);}
    }
}

async fn post_marked(sender: &mut Option<SendRequest<Full<Bytes>>>, addr: &str, room: &str, cookie: &str, csrf: &str, seq: u64, delivery: &Delivery) -> Option<u64> {
    if sender.is_none() {
        *sender = connect(addr).await.ok();
    }
    let (h, b) = message_request(cookie, csrf, &format!("fanout bmk{seq}z"));
    let t0 = Instant::now();
    delivery.sent.lock().unwrap().insert(seq, t0);
    // PATCH: a failed post is counted (it used to vanish from the post histogram and nothing else).
    let Some(conn) = sender.as_mut() else {
        delivery.post_failures.fetch_add(1, Ordering::Relaxed);
        delivery.sample(format!("post {seq}: could not connect"));
        return None;
    };
    match send(conn, addr, "POST", &format!("/rooms/{room}/messages"), &h, b).await {
        Ok(r) if r.status < 400 => Some(t0.elapsed().as_micros() as u64),
        other => {
            delivery.post_failures.fetch_add(1, Ordering::Relaxed);
            delivery.sample(match other {
                Ok(r) => format!("post {seq}: status {}", r.status),
                Err(e) => format!("post {seq}: {e}"),
            });
            *sender = None;
            None
        }
    }
}

async fn wait_drain(delivery: &Delivery, seqs: &[u64], clients: usize, timeout: Duration) {
    let until = Instant::now() + timeout;
    while Instant::now() < until {
        let got = delivery.got.lock().unwrap();
        if seqs.iter().all(|s| got.get(s).map(|g| g.0 >= clients).unwrap_or(false)) {
            return;
        }
        drop(got);
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
}

fn phase(name: &str) {
    let ms = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_millis();
    eprintln!("PHASE {name} {ms}");
}

fn fanout_stats(delivery: &Delivery, seqs: &[u64], clients: usize) -> (Histogram<u64>, usize, Option<Instant>) {
    let sent = delivery.sent.lock().unwrap();
    let got = delivery.got.lock().unwrap();
    let mut all = hist();
    let mut complete = 0;
    let mut last = None::<Instant>;
    for s in seqs {
        if let Some((n, t)) = got.get(s) {
            last = Some(last.map_or(*t, |l| l.max(*t)));
            if *n >= clients {
                complete += 1;
                all.record(t.duration_since(sent[s]).as_micros() as u64).ok();
            }
        }
    }
    (all, complete, last)
}

async fn cable(a: &Args) -> Res<Value> {
    let addr = host_port(&a.get("base"));
    let cookie = a.get("cookie");
    let room = a.get("room");
    let csrf = a.opt("csrf").unwrap_or_default();
    let clients: usize = a.num("clients", 100);
    let latency_msgs: u64 = a.num("latency-msgs", 30);
    let interval = Duration::from_millis(a.num("interval-ms", 200));
    let tput_secs: f64 = a.num("tput-secs", 15.0);
    let posters: usize = a.num("posters", 4);
    let hold_secs: u64 = a.num("hold-secs", 0);
    let deflate = match a.opt("deflate").as_deref() { None | Some("0") => false, Some("1") => true, _ => return Err("--deflate must be exactly 0 or 1".into()) };
    let sources: Vec<std::net::IpAddr> =
        a.opt("sources").unwrap_or_default().split(',').filter(|s| !s.is_empty()).map(|s| s.parse()).collect::<Result<_, _>>()?;

    // The chatter.js load shape: presence for the room, unread rooms, heartbeat, and the page's
    // turbo stream sources (rooms list, the room's messages, the user's rooms).
    let mut subs = vec![
        json!({"channel": "PresenceChannel", "room_id": room.parse::<u64>()?}).to_string(),
        json!({"channel": "UnreadRoomsChannel"}).to_string(),
        json!({"channel": "HeartbeatChannel"}).to_string(),
    ];
    let mut stream_names = Vec::new();
    for s in a.get("streams").split(',').filter(|s| !s.is_empty()) {
        let (channel, name) = s.split_once('|').unwrap_or(("Turbo::StreamsChannel", s));
        subs.push(json!({"channel": channel, "signed_stream_name": name}).to_string());
        stream_names.push(name.to_string());
    }

    let memo = Arc::new(frame_memo::Memo::new(stream_names.clone(), subs.clone()));
    let delivery = Arc::new(Delivery {
        expected_clients: clients,
        sent: Mutex::new(HashMap::new()),
        got: Mutex::new(HashMap::new()),
        per_client: Mutex::new(hist()),
        receipts: AtomicU64::new(0),
        wire: Arc::new(wire_ws::Counters::default()),
        memo: memo.clone(),
        duplicates: AtomicU64::new(0),
        off_stream: AtomicU64::new(0),
        unexpected: AtomicU64::new(0),
        post_failures: AtomicU64::new(0),
        disconnected: AtomicU64::new(0),
        samples: Mutex::new(Vec::new()),
    });
    let confirmed = Arc::new(AtomicUsize::new(0));
    let connected = Arc::new(AtomicUsize::new(0));
    let stop = Arc::new(AtomicBool::new(false));
    let failed = Arc::new(AtomicUsize::new(0));

    // One independent, uncompressed control subscription supplies exact golden text.
    // It is present in every arm, excluded from delivery and wire counters, and
    // registered separately. It cannot validate itself via the memo.
    let control_errors = Arc::new(AtomicU64::new(0));
    let (control_memo,control_stop,control_failures,control_delivery)=(memo.clone(),stop.clone(),control_errors.clone(),delivery.clone());
    let (control_addr,control_cookie,control_subs)=(addr.clone(),cookie.clone(),subs.clone());
    let (admitted,admission)=tokio::sync::oneshot::channel();
    let control_task=golden_control::Control::start(async move {
        let admission_result=tokio::time::timeout(Duration::from_secs(30),async {
            let mut control=wire_ws::WireWs::handshake(tokio::net::TcpStream::connect(&control_addr).await?,&control_addr,&control_cookie,false,None,Arc::new(wire_ws::Counters::default())).await?;
            for identifier in &control_subs {control.send_text(&json!({"command":"subscribe","identifier":identifier}).to_string()).await?;}
            let mut confirms=0;
            while confirms<control_subs.len(){
                let text=control.next_text().await?.ok_or("control closed before admission")?;
                let value:Value=serde_json::from_str(&text)?;
                control_memo.add_golden(text)?;
                if value["type"]=="confirm_subscription" {confirms+=1;}
            }
            Ok::<_,Box<dyn std::error::Error+Send+Sync>>(control)
        }).await;
        let mut control=match admission_result {
            Ok(Ok(control))=>{let _=admitted.send(Ok::<_,String>(()));control},
            Ok(Err(error))=>{let _=admitted.send(Err(error.to_string()));return;},
            Err(error)=>{let _=admitted.send(Err(error.to_string()));return;}
        };
        while !control_stop.load(Ordering::Relaxed) {
            match control.next_text().await {
                Ok(Some(text))=>{if let Err(error)=control_memo.add_golden(text){control_delivery.sample(format!("golden control: {error}"));control_failures.fetch_add(1,Ordering::Relaxed);break;}},
                Ok(None)=>{control_delivery.sample("golden control: server closed the plain control connection".into());control_failures.fetch_add(1,Ordering::Relaxed);break;},
                Err(error)=>{control_delivery.sample(format!("golden control transport: {error}"));control_failures.fetch_add(1,Ordering::Relaxed);break;}
            }
        }
    })?;
    match admission.await? {Ok(())=>{},Err(error)=>{control_task.stop().await?;return Err(error.into());}}

    // Connect with bounded parallelism (50 handshakes in flight).
    phase("connect");
    let connect_start = Instant::now();
    let gate = Arc::new(tokio::sync::Semaphore::new(50));
    let mut handles = Vec::new();
    for n in 0..clients {
        let permit = gate.clone().acquire_owned().await?;
        let source = (!sources.is_empty()).then(|| sources[n % sources.len()]);
        let (addr, cookie, subs, confirmed, connected, stop, delivery, failed) =
            (addr.clone(), cookie.clone(), subs.clone(), confirmed.clone(), connected.clone(), stop.clone(), delivery.clone(), failed.clone());
        let before = connected.load(Ordering::Relaxed);
        handles.push(tokio::spawn(async move {
            let c2 = connected.clone();
            let (stop2, delivery2) = (stop.clone(), delivery.clone());
            let task = tokio::spawn(cable_client(CableClientRequest { addr, source, cookie, subs, requested_deflate: deflate }, confirmed, connected, stop, delivery));
            // Release the permit once this client has connected (or failed).
            let until = Instant::now() + Duration::from_secs(30);
            while c2.load(Ordering::Relaxed) <= before && !task.is_finished() && Instant::now() < until {
                tokio::time::sleep(Duration::from_millis(5)).await;
            }
            drop(permit);
            // PATCH: a client whose socket the server closed before the run ended missed whatever came after;
            // one that failed (to connect, or mid-stream) is counted in `failed`, with a sample of why.
            match task.await {
                Ok(Ok(())) if !stop2.load(Ordering::Relaxed) => {
                    delivery2.disconnected.fetch_add(1, Ordering::Relaxed);
                }
                Ok(Err(e)) => {
                    if !stop2.load(Ordering::Relaxed) {
                        delivery2.sample(format!("client: {e}"));
                    }
                    failed.fetch_add(1, Ordering::Relaxed);
                }
                _ => {}
            }
        }));
    }
    let until = Instant::now() + Duration::from_secs(120.max(clients as u64 / 200));
    while confirmed.load(Ordering::Relaxed) + failed.load(Ordering::Relaxed) < clients && Instant::now() < until {
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    let connect_secs = connect_start.elapsed().as_secs_f64();
    let ready = confirmed.load(Ordering::Relaxed);
    phase("connected");
    tokio::time::sleep(Duration::from_secs(1)).await;
    if hold_secs > 0 {
        // Everyone connected and idle: what the server spends on heartbeats and holding sockets.
        phase("idle");
        tokio::time::sleep(Duration::from_secs(hold_secs)).await;
        phase("idle_done");
    }
    let paced_wire_before = delivery.wire.snapshot();
    phase("paced");

    // Phase 1: paced messages, one at a time (open loop at `interval`), for delivery latency.
    let mut poster = None;
    let mut post_h = hist();
    let mut seqs = Vec::new();
    let mut posted_ok = Vec::new(); // PATCH: the messages every client must receive
    let mut seq = 0u64;
    for _ in 0..latency_msgs {
        seq += 1;
        let tick = Instant::now();
        if let Some(us) = post_marked(&mut poster, &addr, &room, &cookie, &csrf, seq, &delivery).await {
            post_h.record(us).ok();
            posted_ok.push(seq);
        }
        seqs.push(seq);
        let spent = tick.elapsed();
        if spent < interval {
            tokio::time::sleep(interval - spent).await;
        }
    }
    wait_drain(&delivery, &seqs, ready, Duration::from_secs(20)).await;
    let paced_wire_after = delivery.wire.snapshot();
    phase("paced_drained");
    let (all_h, complete, _) = fanout_stats(&delivery, &seqs, ready);
    let client_h = std::mem::replace(&mut *delivery.per_client.lock().unwrap(), hist());
    let latency = json!({
        "messages": latency_msgs,
        "wire": paced_wire_after.delta_json(paced_wire_before),
        "complete": complete,
        "post": summary(&post_h),
        "per_client": summary(&client_h),
        "all_clients": summary(&all_h),
    });

    // Phase 2: closed-loop posters for tput_secs, then drain; delivered messages/sec.
    let receipts_before = delivery.receipts.load(Ordering::Relaxed);
    let wire_before = delivery.wire.snapshot();
    let next = Arc::new(AtomicU64::new(seq + 1));
    phase("saturated");
    let tput_cpu0 = cpu_secs();
    let tput_start = Instant::now();
    let tput_deadline = tput_start + Duration::from_secs_f64(tput_secs);
    let mut ptasks = Vec::new();
    for _ in 0..posters {
        let (addr, room, cookie, csrf, delivery, next) = (addr.clone(), room.clone(), cookie.clone(), csrf.clone(), delivery.clone(), next.clone());
        ptasks.push(tokio::spawn(async move {
            let mut conn = None;
            let mut mine = Vec::new();
            let mut h = hist();
            while Instant::now() < tput_deadline {
                let s = next.fetch_add(1, Ordering::Relaxed);
                if let Some(us) = post_marked(&mut conn, &addr, &room, &cookie, &csrf, s, &delivery).await {
                    h.record(us).ok();
                    mine.push(s);
                }
            }
            (mine, h)
        }));
    }
    let mut tseqs = Vec::new();
    let mut tpost_h = hist();
    for t in ptasks {
        let (m, h) = t.await?;
        tseqs.extend(m);
        tpost_h.add(&h).ok();
    }
    let posting_secs = tput_start.elapsed().as_secs_f64();
    phase("saturated_posted");
    wait_drain(&delivery, &tseqs, ready, Duration::from_secs(60)).await;
    let wire_after = delivery.wire.snapshot();
    phase("saturated_drained");
    let tput_util = (cpu_secs() - tput_cpu0) / (tput_start.elapsed().as_secs_f64() * allowed_cpus() as f64);
    let (tall_h, tcomplete, last) = fanout_stats(&delivery, &tseqs, ready);
    let span = last.map(|l| l.duration_since(tput_start).as_secs_f64()).unwrap_or(posting_secs).max(posting_secs);
    let tclient_h = delivery.per_client.lock().unwrap().clone();
    let receipts = delivery.receipts.load(Ordering::Relaxed) - receipts_before;
    let wire_bytes = wire_after.byte_delta(wire_before);
    let throughput = json!({
        "posters": posters,
        "posted": tseqs.len(),
        "posts_per_sec": ((tseqs.len() as f64 / posting_secs) * 10.0).round() / 10.0,
        "complete": tcomplete,
        "delivered_msgs_per_sec": ((tcomplete as f64 / span) * 10.0).round() / 10.0,
        "frames_per_sec": (receipts as f64 / span).round(),
        "wire": wire_after.delta_json(wire_before),
        "wire_mb_per_sec": (wire_bytes as f64 / span / 1e6 * 10.0).round() / 10.0,
        "drain_secs": ((span - posting_secs) * 100.0).round() / 100.0,
        "post": summary(&tpost_h),
        "per_client": summary(&tclient_h),
        "all_clients": summary(&tall_h),
        "loadgen_util": (tput_util * 1000.0).round() / 1000.0,
    });

    // PATCH: every message posted successfully must have reached every confirmed client, once, by the end of its
    // phase's drain window (20 s paced, 60 s saturated).
    posted_ok.extend(&tseqs);
    let (expected, delivered) = {
        let got = delivery.got.lock().unwrap();
        let delivered: usize = posted_ok.iter().map(|s| got.get(s).map_or(0, |g| g.0.min(ready))).sum();
        (posted_ok.len() * ready, delivered)
    };
    let counter = |c: &AtomicU64| c.load(Ordering::Relaxed);
    let missing = (expected - delivered) as u64;
    let verification = json!({
        "posted": posted_ok.len(),
        "post_failures": counter(&delivery.post_failures),
        "expected_deliveries": expected,
        "delivered": delivered,
        "missing": missing,
        "duplicates": counter(&delivery.duplicates),
        "off_stream": counter(&delivery.off_stream),
        "unexpected": counter(&delivery.unexpected),
        "disconnected": counter(&delivery.disconnected),
        "unconfirmed": clients - ready,
        "mismatches": missing + counter(&delivery.duplicates) + counter(&delivery.off_stream) + counter(&delivery.unexpected),
        "samples": delivery.samples.lock().unwrap().clone(),
        "golden_control_errors":control_errors.load(Ordering::Relaxed),
    });

    let hold: f64 = a.num("hold-secs", 0.0);
    if hold > 0.0 {
        tokio::time::sleep(Duration::from_secs_f64(hold)).await;
    }
    phase("done");
    stop.store(true, Ordering::Relaxed);
    control_task.stop().await?;
    for h in handles {
        h.abort();
    }
    Ok(json!({
        "clients": clients,
        "wire_negotiation": delivery.wire.negotiation_json(deflate),
        "frame_validation_memo":memo.report(),
        "golden_control_scheduler":"dedicated reader thread, same generator CPU set",
        "ready": ready,
        "failed": failed.load(Ordering::Relaxed),
        "connect_secs": (connect_secs * 100.0).round() / 100.0,
        "subscriptions_per_client": subs.len(),
        "latency": latency,
        "throughput": throughput,
        "verification": verification,
    }))
}

#[cfg(test)]
mod cable_delivery_tests {
    use super::*;
    #[tokio::test]
    async fn memo_hits_still_reject_duplicate_and_stale_deliveries() {
        debug_logging::initialize();
        let memo=Arc::new(frame_memo::Memo::new(vec!["good".into()],vec![]));
        let body=json!({"identifier":json!({"signed_stream_name":"good"}).to_string(),"message":"bmk1z"}).to_string();
        memo.add_golden(body.clone()).unwrap();
        let delivery=Delivery { expected_clients:1,sent:Mutex::new(HashMap::from([(1,Instant::now())])),got:Mutex::new(HashMap::new()),per_client:Mutex::new(hist()),receipts:AtomicU64::new(0),wire:Arc::new(wire_ws::Counters::default()),memo:memo.clone(),duplicates:AtomicU64::new(0),off_stream:AtomicU64::new(0),unexpected:AtomicU64::new(0),post_failures:AtomicU64::new(0),disconnected:AtomicU64::new(0),samples:Mutex::new(Vec::new()) };
        let mut seen=HashSet::new();let confirmed=AtomicUsize::new(0);let mut confirms=0;
        for _ in 0..2 { let facts=memo.decode(false,body.clone().into_bytes()).await.unwrap();on_text(&facts,Instant::now(),1,&mut confirms,&mut seen,&confirmed,&delivery); }
        assert_eq!(delivery.duplicates.load(Ordering::Relaxed),1);assert_eq!(delivery.receipts.load(Ordering::Relaxed),1);
        // The same fully validated bytes are stale on a client whose run did not post them.
        delivery.sent.lock().unwrap().clear();seen.clear();let facts=memo.decode(false,body.into_bytes()).await.unwrap();on_text(&facts,Instant::now(),1,&mut confirms,&mut seen,&confirmed,&delivery);
        assert_eq!(delivery.unexpected.load(Ordering::Relaxed),1);assert_eq!(memo.report()["full_validations"],1);
    }
}

// ---------------------------------------------------------------------------------------------
// Upload + thumbnail

async fn upload(a: &Args) -> Res<Value> {
    let addr = host_port(&a.get("base"));
    let cookie = a.get("cookie");
    let room = a.get("room");
    let csrf = a.opt("csrf").unwrap_or_default();
    let file = a.get("file");
    let reps: usize = a.num("reps", 5);
    let data = std::fs::read(&file)?;
    let name = std::path::Path::new(&file).file_name().unwrap().to_string_lossy().to_string();
    let ctype = if name.ends_with(".png") { "image/png" } else { "image/jpeg" };
    let img_re = upload_response::AttachmentImages::new(); // PATCH: the attachment follows the author's avatar.

    let mut runs = Vec::new();
    for _ in 0..reps {
        // PATCH: a failed rep is recorded and counted instead of aborting the suite.
        match upload_once(&addr, &cookie, &room, &csrf, &name, ctype, &data, &img_re).await {
            Ok(run) => runs.push(run),
            Err(e) => runs.push(json!({"error": e.to_string()})),
        }
    }
    let mut totals: Vec<f64> = runs.iter().filter(|r| r.get("error").is_none()).filter_map(|r| r["total_ms"].as_f64()).collect();
    totals.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let failures: Vec<String> = runs.iter().filter_map(|r| r.get("error").and_then(Value::as_str).map(str::to_string)).collect();
    Ok(json!({
        "file": name, "bytes": data.len(),
        "median_total_ms": totals.get(totals.len() / 2),
        "failures": failures.len(),
        "failure_samples": failures.iter().take(5).collect::<Vec<_>>(),
        "runs": runs,
    }))
}

/// One upload: select the attachment image, then require a real raster thumbnail, not the author's avatar.
#[allow(clippy::too_many_arguments)]
async fn upload_once(addr: &str, cookie: &str, room: &str, csrf: &str, name: &str, ctype: &str, data: &[u8], img_re: &upload_response::AttachmentImages) -> Res<Value> {
    let addr = addr.to_string();
    let (cookie, csrf) = (cookie.to_string(), csrf.to_string());
    {
        let boundary = format!("----bench{}", nonce());
        let mut body = Vec::new();
        for (k, v) in [("authenticity_token", csrf.as_str()), ("message[client_message_id]", &nonce())] {
            body.extend(format!("--{boundary}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n{v}\r\n").as_bytes());
        }
        body.extend(
            format!("--{boundary}\r\nContent-Disposition: form-data; name=\"message[attachment]\"; filename=\"{name}\"\r\nContent-Type: {ctype}\r\n\r\n")
                .as_bytes(),
        );
        body.extend(data);
        body.extend(format!("\r\n--{boundary}--\r\n").as_bytes());
        let t0 = Instant::now();
        let r = one_shot(
            &addr,
            "POST",
            &format!("/rooms/{room}/messages"),
            &[
                ("cookie", cookie.clone()),
                ("content-type", format!("multipart/form-data; boundary={boundary}")),
                ("accept", "text/vnd.turbo-stream.html, text/html".into()),
                ("x-csrf-token", csrf.clone()),
                same_origin(),
            ],
            Bytes::from(body),
        )
        .await?;
        let post_ms = t0.elapsed().as_secs_f64() * 1000.0;
        let html = String::from_utf8_lossy(&r.body).to_string();
        if !(200..300).contains(&r.status) {
            return Ok(json!({"post_status": r.status, "post_ms": post_ms, "error": format!("POST answered {}", r.status)}));
        }
        let Some(src) = img_re.source(&html).map(unescape) else {
            return Ok(json!({"post_status": r.status, "post_ms": post_ms, "error": "no attachment <img> in response"}));
        };
        // Follow redirects to the bytes (representations/redirect -> disk service).
        let mut url = src.clone();
        let mut status = 0;
        let mut size = 0;
        let mut thumb_type = String::new();
        let mut thumb_raster = false;
        for _ in 0..5 {
            let path = url.trim_start_matches(&format!("http://{addr}")).to_string();
            let g = one_shot(&addr, "GET", &path, &[("cookie", cookie.clone())], Bytes::new()).await?;
            status = g.status;
            size = g.body.len();
            thumb_type = header_str(&g.headers, "content-type").to_string();
            thumb_raster = upload_response::is_raster(&g.body, &thumb_type);
            match g.headers.get("location") {
                Some(loc) if (300..400).contains(&g.status) => url = loc.to_str()?.to_string(),
                _ => break,
            }
        }
        let total_ms = t0.elapsed().as_secs_f64() * 1000.0;
        let mut run = json!({
            "post_status": r.status, "post_ms": (post_ms * 10.0).round() / 10.0,
            "thumb_status": status, "thumb_bytes": size, "thumb_type": thumb_type,
            "thumb_url": src, "final_thumb_url": url, "thumb_raster": thumb_raster,
            "thumb_ms": ((total_ms - post_ms) * 10.0).round() / 10.0,
            "total_ms": (total_ms * 10.0).round() / 10.0,
        });
        if status != 200 || !thumb_raster || size == 0 {
            run["error"] = json!(format!("thumbnail answered {status} {thumb_type:?} with {size} bytes"));
        }
        Ok(run)
    }
}

// ---------------------------------------------------------------------------------------------
// Compression cost

async fn fetch(a: &Args) -> Res<Value> {
    let addr = host_port(&a.get("base"));
    let path = a.get("path");
    let r = one_shot(&addr, "GET", &path, &[("cookie", a.opt("cookie").unwrap_or_default())], Bytes::new()).await?;
    std::fs::write(a.get("out"), &r.body)?;
    Ok(json!({"status": r.status, "bytes": r.body.len(), "content_encoding": r.headers.get("content-encoding").map(|v| v.to_str().unwrap_or("").to_string())}))
}

/// CPU per compression of one body. `miniz_oxide` is the app's backend (flate2's default
/// `rust_backend`); the app's `Rack::Deflater` port writes the body as one chunk with a sync flush
/// and then finishes, at `Compression::default()` (6). zlib-rs is flate2's optional backend.
fn gzip_cost(a: &Args) -> Res<Value> {
    use std::io::Write;
    let data = std::fs::read(a.get("file"))?;
    let iters: u32 = a.num("iters", 200);
    let time = |f: &dyn Fn() -> usize| {
        let mut out = 0;
        for _ in 0..3 {
            out = f();
        }
        let mut samples: Vec<f64> = (0..iters)
            .map(|_| {
                let t0 = Instant::now();
                out = f();
                t0.elapsed().as_secs_f64() * 1e6
            })
            .collect();
        samples.sort_by(|a, b| a.partial_cmp(b).unwrap());
        json!({"median_us": samples[samples.len() / 2].round(), "min_us": samples[0].round(), "out_bytes": out})
    };
    let mut results = serde_json::Map::new();
    for level in [1u8, 4, 6, 9] {
        results.insert(format!("miniz_oxide_l{level}"), time(&|| miniz_oxide::deflate::compress_to_vec(&data, level).len()));
        results.insert(
            format!("zlib_rs_l{level}"),
            time(&|| {
                // The app's exact call sequence: one write, sync flush, finish.
                let mut e = flate2::GzBuilder::new().mtime(0).operating_system(3).write(Vec::new(), flate2::Compression::new(level as u32));
                e.write_all(&data).unwrap();
                e.flush().unwrap();
                e.finish().unwrap().len()
            }),
        );
    }
    Ok(json!({"file": a.get("file"), "bytes": data.len(), "iters": iters, "results": results}))
}

#[tokio::main]
async fn main() {
    debug_logging::initialize();
    let raw: Vec<String> = std::env::args().collect();
    let cmd = raw.get(1).cloned().unwrap_or_default();
    let a = Args::parse(&raw[2.min(raw.len())..]);
    let agent = match a.opt("user-agent") {
        None => None,
        Some(value) => match hyper::header::HeaderValue::from_str(&value) {
            Ok(header) if !value.is_empty() && !value.contains('\r') && !value.contains('\n') && header.to_str().is_ok() => Some(header),
            _ => { eprintln!("invalid --user-agent"); std::process::exit(2); }
        },
    };
    BENCH_USER_AGENT.set(agent).expect("User-Agent initialized once per command");
    let (wall0, cpu0) = (Instant::now(), cpu_secs());
    let out = match cmd.as_str() {
        "login" => login(&a).await,
        "scrape" => scrape(&a).await,
        "http" => http_load(&a).await,
        "cable" => cable(&a).await,
        "upload" => upload(&a).await,
        "fetch" => fetch(&a).await,
        "gzip" => gzip_cost(&a),
        _ => Err("usage: loadgen login|scrape|http|cable|upload|fetch|gzip --base URL ...".into()),
    };
    match out {
        Ok(mut v) => {
            // PATCH: the load generator's own CPU use; near 1.0 it is the bottleneck and the result a lower bound.
            if let Some(o) = v.as_object_mut() {
                let (wall, cpu, cpus) = (wall0.elapsed().as_secs_f64(), cpu_secs() - cpu0, allowed_cpus());
                o.insert("user_agent".into(), json!(BENCH_USER_AGENT.get().and_then(|value| value.as_ref()).map(|value| value.to_str().expect("validated header"))));
                o.insert(
                    "loadgen".into(),
                    json!({"debug_logging": debug_logging::enabled(), "cpu_secs": (cpu * 100.0).round() / 100.0, "wall_secs": (wall * 100.0).round() / 100.0, "cpus": cpus,
                           "util": (cpu / (wall * cpus as f64) * 1000.0).round() / 1000.0}),
                );
            }
            println!("{v}")
        }
        Err(e) => {
            eprintln!("loadgen {cmd}: {e}");
            std::process::exit(1);
        }
    }
}
