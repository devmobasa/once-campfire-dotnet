# Campfire in C#

A C# port of [ONCE Campfire](https://github.com/basecamp/once-campfire), the self-hosted team chat app from Basecamp. The port uses ASP.NET Core Minimal APIs and native platform security. Rails is the feature reference, and the port uses the same browser frontend as Rails.

The port runs on .NET 11 RC2. The default Docker image uses JIT on Linux amd64 and arm64. The `aot` target builds Native AOT from the same source. Kestrel serves public HTTP and TLS in the application process. Redis and Resque are not necessary.

## Performance

We measured each app with 16 concurrent connections on a shared desktop with a 16-core processor. Each app had four CPU threads. Each value is the median of three interleaved runs on October 18 and 19, 2026. The Rails values come from October 15, 2026. Each app ran its default branch, with gzip and its default caches.

The C# column shows the JIT build, which was the faster of the two C# builds. The detailed results also give the values for Native AOT. Each page result is a repeated read of one page by one signed-in user. As a result, C# serves these pages from its warm response cache.

| HTTP workload (requests/sec) | [Rails](https://github.com/basecamp/once-campfire) | [Elixir](https://github.com/basecamp/once-campfire-elixir) | [Express](https://github.com/basecamp/once-campfire-express) | [Go](https://github.com/basecamp/once-campfire-go) | [Rust](https://github.com/basecamp/once-campfire-rust) | [C](https://github.com/basecamp/once-campfire-c) | C# |
|---|---:|---:|---:|---:|---:|---:|---:|
| Room page | 3,525 | 3,996 | 28,111 | 38,837 | 62,927 | 76,005 | 109,018 |
| Messages page | 3,577 | 4,107 | 40,671 | 39,431 | 59,084 | 75,009 | 118,403 |
| Sidebar | 3,782 | 4,205 | 55,247 | 44,093 | 71,880 | 82,315 | 125,664 |
| Search | 3,805 | 4,171 | 47,741 | 43,854 | 71,771 | 80,307 | 121,478 |
| Post a message | 406 | 761 | 870 | 1,760 | 2,599 | 1,420 | 2,562 |

The port passes all checks of the [official verification harness](https://github.com/basecamp/once-campfire-verification) at `ec02deb`. This table uses our own load generator.

The load generator validates every response and every post. Elixir uses archived Debian packages.

The Rails column shows `edbc779`, the Rails default branch on October 15. The default branch on October 18, `11d3fb9`, served less than half as many pages per second. A separate test on October 18 and 19 gave a similar rate for `edbc779`: 3,568–3,622 room pages per second. The detailed results give more values for `11d3fb9`.

Other Campfire READMEs show different absolute values, because they use different hardware and a different load generator. For example, the Rust README gives 106,494 requests/sec for the Rust room page, and this table gives 62,927. Values from different tables are not comparable. For the page workloads, the order of the apps is the same in both tables.

**Where C# falls behind:**

- **Memory:** C# uses 135 MiB when idle. This is more than three times the memory of Rust, C and Go (39–41 MiB). But for this load, it is still a small amount of memory. In the page runs at 16 and 64 connections, the sampled peak for C# is about 200 MiB at more than 100,000 pages per second. This value includes the response cache, which can use up to 64 MiB. For the same pages, Rails, Express and Elixir have peaks of 290–1,013 MiB.
- **Posting:** Rust uses about 29% less CPU per post, Go uses about 38% less and C uses about 11% less. Rust, Go and C also have a lower median latency for a post. Rust has the highest median posting rate, but its range across runs overlaps the range of C#. C# has the lowest p95 and p99 latency.
- **Page tail latency:** At 16 connections, C has a lower p99 latency than C# on all four pages (0.39–0.43 ms, against 0.46–0.56 ms for C#). Rust has a lower p99 latency for the room page and for search.
- **Real-time updates to 10,000 clients:** At a steady pace, Rust uses about 30% less CPU per delivery and has a lower latency. When idle, Rust uses about a quarter of the memory of the C# Native AOT build and a fifth of the JIT build. At full load, the sampled peak for C# is about 1 GiB. This is similar to Go, less than Express, and much less than Rails and Elixir (5.5–7 GiB). Go has only one valid full-load run. C did not reach 10,000 connected clients. We have no valid full-load result for Rust. Rust closes the connection of a client that falls 256 messages behind, and our check requires that every client gets every message.

**[Detailed results](docs/benchmarks.md):** 64 connections, CPU per request, latency, memory, real-time delivery, ranges, revisions and exclusions.

## Build options

JIT is the default image because it serves the measured page and posting workloads faster. AOT uses less memory, starts faster and needs no .NET runtime. The AOT target uses `OptimizationPreference=Speed` and the standard CPU baseline. Both builds set `System.Threading.ThreadPool.UnfairSemaphoreSpinLimit=256`, restoring RC1's default after RC2 raised it to 1,024. Both use the same application source. The builds use neither static PGO nor an AVX2 minimum CPU baseline.

## Build and run

Initialize the pinned Rails reference, which supplies the browser assets:

```sh
git submodule update --init reference
docker build -t campfire-dotnet:app .
docker build --target aot -t campfire-dotnet:aot .
```

For local HTTP, mount persistent storage, including its Data Protection keys:

```sh
docker run --rm -p 8080:80 -e DISABLE_SSL=1 \
  -v campfire-storage:/rails/storage campfire-dotnet:app
```

Open http://localhost:8080 to create the first account. Preserve the storage volume and its `keys/` directory across restarts. Rails password digests support sign-in. Rails avatar URLs work when `SECRET_KEY_BASE` matches the original installation. Other Rails cookies and signed URLs do not work.

For HTTPS, set `TLS_DOMAIN`, expose ports 80 and 443 and retain the storage volume. Read [deployment and limitations](docs/deployment.md) before deployment, especially the backup and trusted-proxy settings.

## Development

Use the SDK pinned in [global.json](global.json). Install the repository's portable formatting preset locally before running the development gate:

```sh
cp eng/style.editorconfig .editorconfig
dotnet eng/check
```

See [development and testing](docs/development.md), [compatibility](docs/compatibility.md) and [contribution guidance](CONTRIBUTING.md). Imported sources, licenses and adaptations are recorded in [PROVENANCE.md](PROVENANCE.md).
