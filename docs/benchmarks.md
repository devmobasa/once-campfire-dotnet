# Benchmark results

Results cover October 18 and 19, 2026. The Rails rows are an exception and come from October 15, 2026. We fetched and pinned the other default branches on October 18. C uses `master`. We used unmodified upstream application sources. Elixir uses archived Debian packages with unchanged application code, dependencies and runtime configuration.

We ran three interleaved repetitions in one session on a **shared desktop with a 16-core processor**. Applications use four CPU threads. Generators use eight separate threads, including their SMT siblings. We monitor application siblings. Starts require one-minute load average below 1.5, with up to 900 seconds of waiting. Timing has no runtime tracing or debug logging. Desktop activity and loopback copying remain sources of noise.

Rust uses four Tokio worker threads through `TOKIO_WORKER_THREADS=4`.

Pages use gzip at 16 and 64 connections, a five-second warm-up and 20-second windows. Posting uses 16 connections, a 120-second warm-up and a 120-second window. Cable uses 10,000 clients and 1,000 paced posts with a requested rate of 10 posts/s. Four posters then drive 120 seconds of saturation, followed by a bounded drain. Slower targets achieve less than 10 posts/s. Rust main has paced-only results. This session measures no identity pages, 1k Cable or c64 posting.

The generator validates every HTTP body. It fully checks each distinct body and recognizes repeats through exact bytes. A separate database check confirms that the app stores each acknowledged post exactly once. Each Cable delivery retains missing, duplicate, off-stream and independent decoded-text checks. Invalid samples contribute no performance numbers. No valid result was generator-limited. Maximum generator utilization was 0.374, against the 0.85 limit. The tables show sample counts below three.

The tools for these validation and persistence checks are not in this repository. The tools that ran Go, Express and Elixir are also not in this repository. The checked-in `bench/run` script runs only the Rails, Rust, C and C# targets.

C# page hits use a generation-keyed response cache for each identity, with a 64 MiB budget. Changes to sessions and permissions stop the reuse. Upstream cache policies remain their defaults, including Express's separate cache per worker. Each implementation runs these workloads with its own caching, rendering, authentication, security and compression policies. As a result, the tables do not show equal feature coverage. They also do not isolate the cost of a language or a runtime. At c64, both C# builds use less server CPU per page than every included reference on all four routes. Rust main, Go and C use less CPU per post, and Rust main uses less CPU and memory for paced Cable delivery.

We sum process PSS across child workers and required Redis within the application's cgroup. HTTP body bytes are encoded bodies. Cable bytes include received RFC 6455 frames and companion streams, excluding the HTTP handshake, per logical message delivery. Deliveries/s is the generator's `frames_per_sec`: one message delivered to one subscriber. Fully delivered broadcasts/s is a different metric. Sampled peaks can miss brief spikes.

The Rails rows use `edbc779`, the Rails default branch on October 15. The default branch on October 18, `11d3fb9`, served less than half as many pages per second. At 16 connections, it served 1,561 room pages and 355 posts per second, with about twice the CPU per page. A separate room-page test on October 18 and 19 ran both commits at 16 connections. Rails `edbc779` served 3,568–3,622 pages per second, and `11d3fb9` served 1,581–1,586.

Three extra C# runs gave 0.5–3.7% higher page rates than the main runs, with similar CPU per page. The tables use only the main runs.

## Pages

Medians cover three valid repetitions. Ranges show min–max. CPU is application user plus system CPU per response.

| Target | Route | c | Responses/s [range] | CPU µs [range] | p50 / p95 / p99 ms | Idle / peak PSS MiB | Body bytes | Max generator util |
|---|---|---:|---|---|---|---|---:|---:|
| C# AOT | room | 16 | 83,774 [79,745–85,287] | 36.59 [35.86–36.62] | 0.162 / 0.384 / 0.705 | 120.3 / 130.6 | 23,447 | 0.268 |
| C# AOT | messages | 16 | 89,577 [88,755–91,259] | 34.15 [33.58–34.20] | 0.151 / 0.342 / 0.641 | 120.3 / 142.6 | 14,024 | 0.269 |
| C# AOT | sidebar | 16 | 96,055 [94,853–96,944] | 31.88 [31.80–32.22] | 0.142 / 0.330 / 0.568 | 120.3 / 140.9 | 6,680 | 0.285 |
| C# AOT | search | 16 | 92,454 [91,245–92,687] | 32.91 [32.75–33.41] | 0.147 / 0.355 / 0.609 | 120.3 / 142.1 | 9,753 | 0.278 |
| C# AOT | room | 64 | 101,573 [95,032–102,350] | 32.86 [32.84–34.98] | 0.544 / 1.244 / 1.894 | 120.3 / 141.1 | 23,985 | 0.289 |
| C# AOT | messages | 64 | 109,901 [105,525–110,750] | 30.34 [30.22–31.77] | 0.513 / 1.137 / 1.663 | 120.3 / 144.7 | 14,698 | 0.293 |
| C# AOT | sidebar | 64 | 115,861 [113,665–116,537] | 28.68 [28.62–29.31] | 0.490 / 1.075 / 1.577 | 120.3 / 144.7 | 6,733 | 0.295 |
| C# AOT | search | 64 | 111,730 [110,413–113,448] | 29.84 [29.49–30.24] | 0.509 / 1.104 / 1.597 | 120.3 / 148.3 | 9,797 | 0.295 |
| C# JIT | room | 16 | 109,018 [108,089–111,349] | 25.49 [24.92–25.51] | 0.118 / 0.327 / 0.560 | 135.2 / 177.7 | 23,463 | 0.326 |
| C# JIT | messages | 16 | 118,403 [115,283–118,454] | 23.84 [23.39–24.81] | 0.110 / 0.301 / 0.507 | 135.2 / 193.9 | 14,049 | 0.334 |
| C# JIT | sidebar | 16 | 125,664 [124,200–127,379] | 22.09 [21.65–22.37] | 0.103 / 0.281 / 0.462 | 135.2 / 192.3 | 6,687 | 0.343 |
| C# JIT | search | 16 | 121,478 [118,790–121,797] | 22.59 [22.53–23.68] | 0.106 / 0.292 / 0.520 | 135.2 / 192.5 | 9,759 | 0.339 |
| C# JIT | room | 64 | 131,248 [130,379–133,723] | 23.45 [22.95–23.46] | 0.425 / 0.964 / 1.427 | 135.2 / 198.3 | 23,988 | 0.370 |
| C# JIT | messages | 64 | 140,404 [139,789–142,943] | 22.03 [21.30–22.11] | 0.403 / 0.891 / 1.301 | 135.2 / 193.5 | 14,708 | 0.356 |
| C# JIT | sidebar | 64 | 146,490 [145,830–149,040] | 20.49 [20.29–20.57] | 0.383 / 0.874 / 1.274 | 135.2 / 192.5 | 6,741 | 0.356 |
| C# JIT | search | 64 | 142,390 [139,176–143,555] | 21.29 [20.76–21.41] | 0.396 / 0.909 / 1.307 | 135.2 / 192.1 | 9,802 | 0.356 |
| Rails main (October 15) | room | 16 | 3,525 [3,254–3,624] | 937.91 [913.71–1,055.09] | 3.870 / 9.508 / 13.083 | 441.6 / 779.9 | 20,754 | 0.163 |
| Rails main (October 15) | messages | 16 | 3,577 [3,543–3,615] | 923.00 [918.66–924.54] | 3.929 / 9.177 / 13.001 | 441.6 / 813.6 | 11,785 | 0.029 |
| Rails main (October 15) | sidebar | 16 | 3,782 [3,699–3,968] | 854.88 [843.03–860.96] | 3.727 / 8.598 / 11.872 | 441.6 / 847.2 | 5,880 | 0.027 |
| Rails main (October 15) | search | 16 | 3,805 [3,742–3,917] | 864.53 [853.76–866.40] | 3.686 / 8.558 / 11.909 | 441.6 / 866.8 | 9,374 | 0.030 |
| Rails main (October 15) | room | 64 | 3,583 [3,425–3,603] | 945.13 [933.98–1,024.84] | 15.061 / 39.774 / 60.037 | 441.6 / 811.0 | 20,754 | 0.140 |
| Rails main (October 15) | messages | 64 | 3,702 [3,639–3,769] | 917.74 [906.92–921.56] | 15.056 / 35.419 / 51.234 | 441.6 / 840.4 | 11,785 | 0.032 |
| Rails main (October 15) | sidebar | 64 | 3,955 [3,859–4,132] | 851.56 [837.51–858.56] | 11.650 / 49.123 / 67.431 | 441.6 / 859.4 | 5,880 | 0.030 |
| Rails main (October 15) | search | 64 | 3,962 [3,927–4,078] | 862.90 [847.90–863.87] | 12.320 / 45.049 / 65.717 | 441.6 / 879.9 | 9,374 | 0.034 |
| Rust main | room | 16 | 62,927 [62,733–63,239] | 54.96 [54.71–55.11] | 0.243 / 0.405 / 0.492 | 39.8 / 46.7 | 24,286 | 0.218 |
| Rust main | messages | 16 | 59,084 [58,559–59,270] | 58.58 [58.27–59.01] | 0.260 / 0.428 / 0.514 | 39.8 / 47.8 | 16,158 | 0.198 |
| Rust main | sidebar | 16 | 71,880 [71,311–72,545] | 47.89 [47.37–48.28] | 0.208 / 0.374 / 0.464 | 39.8 / 48.2 | 5,911 | 0.225 |
| Rust main | search | 16 | 71,771 [71,319–72,396] | 47.82 [47.36–48.08] | 0.208 / 0.375 / 0.466 | 39.8 / 49.5 | 9,755 | 0.229 |
| Rust main | room | 64 | 67,173 [66,721–67,755] | 51.10 [50.59–51.29] | 0.911 / 1.533 / 1.855 | 39.8 / 52.5 | 24,286 | 0.225 |
| Rust main | messages | 64 | 62,937 [62,397–63,282] | 54.45 [54.02–54.86] | 0.975 / 1.628 / 1.957 | 39.8 / 53.5 | 16,158 | 0.209 |
| Rust main | sidebar | 64 | 77,703 [76,833–78,646] | 44.11 [43.59–44.54] | 0.782 / 1.393 / 1.720 | 39.8 / 53.2 | 5,911 | 0.236 |
| Rust main | search | 64 | 77,603 [76,755–78,429] | 44.02 [43.61–44.47] | 0.783 / 1.395 / 1.720 | 39.8 / 55.0 | 9,755 | 0.241 |
| C master | room | 16 | 76,005 [75,836–76,274] | 45.52 [45.42–45.64] | 0.203 / 0.330 / 0.427 | 40.6 / 43.1 | 21,303 | 0.240 |
| C master | messages | 16 | 75,009 [74,718–75,548] | 45.74 [45.65–46.03] | 0.209 / 0.328 / 0.421 | 40.6 / 45.6 | 12,311 | 0.230 |
| C master | sidebar | 16 | 82,315 [81,699–82,399] | 41.88 [41.86–42.23] | 0.187 / 0.306 / 0.391 | 40.6 / 45.8 | 5,900 | 0.246 |
| C master | search | 16 | 80,307 [79,816–80,423] | 43.01 [42.99–43.33] | 0.191 / 0.312 / 0.405 | 40.6 / 46.2 | 9,525 | 0.244 |
| C master | room | 64 | 93,574 [93,483–93,757] | 37.39 [37.27–37.46] | 0.652 / 1.171 / 1.685 | 40.6 / 43.3 | 21,303 | 0.269 |
| C master | messages | 64 | 94,263 [94,210–94,749] | 36.93 [36.73–36.98] | 0.649 / 1.150 / 1.712 | 40.6 / 45.7 | 12,311 | 0.263 |
| C master | sidebar | 64 | 98,665 [98,617–98,832] | 35.25 [35.23–35.30] | 0.638 / 1.068 / 1.550 | 40.6 / 45.8 | 5,900 | 0.273 |
| C master | search | 64 | 95,650 [95,137–96,414] | 36.44 [36.16–36.60] | 0.666 / 1.066 / 1.534 | 40.6 / 46.2 | 9,525 | 0.268 |
| Go main | room | 16 | 38,837 [38,706–39,452] | 92.45 [90.98–92.89] | 0.302 / 1.085 / 1.663 | 39.3 / 50.4 | 21,333 | 0.183 |
| Go main | messages | 16 | 39,431 [39,325–40,069] | 91.52 [90.10–91.57] | 0.298 / 1.062 / 1.632 | 39.3 / 52.2 | 12,940 | 0.184 |
| Go main | sidebar | 16 | 44,093 [43,489–44,489] | 80.74 [80.07–81.66] | 0.265 / 0.959 / 1.513 | 39.3 / 53.1 | 5,816 | 0.194 |
| Go main | search | 16 | 43,854 [43,622–44,466] | 81.78 [80.71–82.44] | 0.269 / 0.947 / 1.484 | 39.3 / 54.5 | 9,432 | 0.195 |
| Go main | room | 64 | 40,680 [40,642–41,554] | 88.85 [87.05–88.98] | 1.372 / 3.397 / 4.797 | 39.3 / 52.8 | 21,333 | 0.195 |
| Go main | messages | 64 | 41,390 [41,383–41,844] | 87.67 [86.68–87.90] | 1.351 / 3.326 / 4.712 | 39.3 / 56.1 | 12,940 | 0.188 |
| Go main | sidebar | 64 | 46,499 [45,968–46,689] | 77.36 [77.01–78.29] | 1.079 / 3.598 / 5.203 | 39.3 / 55.4 | 5,816 | 0.203 |
| Go main | search | 64 | 46,213 [45,714–46,748] | 78.33 [77.47–79.26] | 1.095 / 3.563 / 5.181 | 39.3 / 56.8 | 9,432 | 0.205 |
| Express main | room | 16 | 28,111 [28,091–28,259] | 136.24 [135.70–136.57] | 0.544 / 1.119 / 1.502 | 308.3 / 1,000.8 | 19,522 | 0.109 |
| Express main | messages | 16 | 40,671 [40,038–41,197] | 92.47 [92.19–93.82] | 0.371 / 0.772 / 1.111 | 308.3 / 1,011.4 | 11,006 | 0.147 |
| Express main | sidebar | 16 | 55,247 [53,246–55,420] | 68.09 [67.93–69.06] | 0.278 / 0.640 / 0.741 | 308.3 / 1,002.7 | 5,424 | 0.188 |
| Express main | search | 16 | 47,741 [47,150–48,975] | 78.03 [77.01–79.06] | 0.311 / 0.651 / 1.128 | 308.3 / 1,002.6 | 8,990 | 0.170 |
| Express main | room | 64 | 28,313 [28,269–28,477] | 135.30 [134.46–135.48] | 2.038 / 3.478 / 5.025 | 308.3 / 1,006.7 | 19,522 | 0.112 |
| Express main | messages | 64 | 41,399 [41,367–41,495] | 91.50 [91.40–91.63] | 1.329 / 2.183 / 3.492 | 308.3 / 1,012.6 | 11,006 | 0.152 |
| Express main | sidebar | 64 | 55,988 [55,740–56,550] | 67.27 [66.54–67.54] | 1.167 / 1.781 / 2.618 | 308.3 / 1,002.8 | 5,424 | 0.197 |
| Express main | search | 64 | 49,357 [48,826–49,703] | 76.46 [75.93–77.26] | 1.180 / 1.930 / 3.164 | 308.3 / 1,002.8 | 8,990 | 0.177 |
| Elixir (archived Debian) | room | 16 | 3,996 [3,984–4,000] | 935.74 [931.21–938.53] | 3.656 / 7.149 / 9.524 | 236.9 / 290.2 | 20,676 | 0.037 |
| Elixir (archived Debian) | messages | 16 | 4,107 [4,096–4,124] | 909.15 [905.53–910.43] | 3.557 / 6.947 / 9.204 | 236.9 / 297.2 | 11,908 | 0.036 |
| Elixir (archived Debian) | sidebar | 16 | 4,205 [4,193–4,214] | 887.85 [886.48–891.44] | 3.474 / 6.734 / 9.033 | 236.9 / 301.2 | 5,888 | 0.031 |
| Elixir (archived Debian) | search | 16 | 4,171 [4,156–4,178] | 894.55 [892.67–898.58] | 3.492 / 6.781 / 8.943 | 236.9 / 298.6 | 9,444 | 0.036 |
| Elixir (archived Debian) | room | 64 | 4,191 [4,188–4,198] | 895.59 [895.45–896.22] | 14.674 / 24.499 / 30.040 | 236.9 / 359.1 | 20,676 | 0.042 |
| Elixir (archived Debian) | messages | 64 | 4,322 [4,314–4,323] | 867.82 [867.22–869.87] | 14.192 / 23.828 / 29.284 | 236.9 / 345.9 | 11,908 | 0.042 |
| Elixir (archived Debian) | sidebar | 64 | 4,337 [4,337–4,346] | 865.74 [862.57–865.91] | 14.193 / 23.454 / 28.339 | 236.9 / 331.7 | 5,888 | 0.031 |
| Elixir (archived Debian) | search | 64 | 4,286 [4,275–4,298] | 875.13 [872.29–875.56] | 14.380 / 23.728 / 29.212 | 236.9 / 329.6 | 9,444 | 0.040 |

## Posting

Gzip, c16. Each app stored every acknowledged post exactly once.

| Target | n | Posts/s [range] | CPU µs/post [range] | p50 / p95 / p99 ms | Idle / peak PSS MiB | Body bytes | Max generator util |
|---|---:|---|---|---|---|---:|---:|
| C# AOT | 3 | 2,101 [2,078–2,168] | 966.99 [959.04–972.83] | 5.422 / 15.690 / 37.066 | 120.0 / 155.1 | 2,077 | 0.031 |
| C# JIT | 3 | 2,562 [2,228–2,573] | 900.83 [892.88–902.51] | 5.237 / 8.087 / 31.501 | 136.5 / 220.5 | 2,074 | 0.037 |
| Rails main (October 15) | 3 | 406 [406–413] | 9,176.34 [9,049.56–9,195.79] | 35.193 / 77.447 / 98.565 | 453.7 / 978.9 | 2,029 | 0.010 |
| Rust main | 3 | 2,599 [2,384–2,667] | 642.19 [639.56–643.34] | 3.633 / 12.034 / 45.445 | 40.2 / 120.2 | 2,009 | 0.037 |
| C master | 3 | 1,420 [1,341–1,457] | 804.82 [803.39–806.09] | 3.718 / 34.906 / 59.943 | 40.7 / 44.4 | 9,586 | 0.019 |
| Go main | 3 | 1,760 [1,736–1,792] | 562.35 [560.67–567.15] | 3.094 / 29.788 / 49.979 | 39.9 / 138.2 | 1,948 | 0.026 |
| Express main | 3 | 870 [827–888] | 1,271.66 [1,250.27–1,304.94] | 9.914 / 63.625 / 142.458 | 312.1 / 7,445.7 | 1,903 | 0.018 |
| Elixir (archived Debian) | 3 | 761 [760–766] | 4,755.65 [4,727.27–4,763.02] | 18.882 / 33.867 / 42.956 | 246.6 / 362.9 | 2,022 | 0.015 |

Rust main, Go and C use less CPU per post than either C# build. Express reaches peak PSS of 7,319–7,519 MiB across three repetitions.

## Cable

10,000 clients. Latency is post-to-received-by-all, rather than one client. We record negotiated modes from upgrades. Plain-mode loopback copying can constrain delivery rates even below the generator CPU ceiling. Paced-only rows have no saturation estimate.

| Target | Mode | n | Saturated deliveries/s [range] | Saturated CPU µs/delivery | Paced CPU µs/delivery | Paced p50 / p95 / p99 ms | Saturated p99 ms | Idle / fan-out / drained PSS MiB | Connect s | Wire bytes/delivery |
|---|---|---:|---|---:|---:|---|---:|---|---:|---:|
| C# AOT | deflate | 3 | 737,656 [735,120–749,482] | 4.59 | 10.02 | 29.583 / 40.831 / 47.007 | 376.32 | 714.6 / 997.9 / 710.4 | 1.41 | 2,376 |
| C# JIT | deflate | 3 | 759,171 [757,360–775,658] | 4.44 | 10.02 | 28.655 / 41.439 / 49.567 | 113.09 | 920.0 / 1,059.7 / 810.4 | 2.57 | 2,371 |
| Rust main | deflate, paced only | 3 | — | — | 6.99 | 23.599 / 28.655 / 33.407 | — | 181.9 / — / 190.1 | 1.25 | 2,333 |
| Go main | deflate | 1 | 250,271 [250,271–250,271] | 10.39 | 13.35 | 35.903 / 64.319 / 77.631 | 886.78 | 721.9 / 1,046.0 / 1,027.2 | 1.23 | 2,657 |
| Rails main (October 15) | plain | 2 | 28,562 [28,506–28,618] | 110.78 | 118.25 | 695.04 / 2220.03 / 2859.52 | 8140.80 | 2,468.3 / 6,994.7 / 6,154.4 | 13.88 | 12,002 |
| Express main | plain | 2 | 77,540 [77,308–77,772] | 46.04 | 52.80 | 301.695 / 379.263 / 414.591 | 2385.92 | 578.7 / 1,271.2 / 1,270.3 | 2.28 | 9,197 |
| Elixir (archived Debian) | plain | 2 | 70,947 [70,284–71,610] | 43.98 | 39.27 | 86.943 / 131.615 / 139.135 | 49594.37 | 2,188.9 / 5,608.2 / 4,077.6 | 3.65 | 11,982 |

Rust main uses 6.99 µs per paced delivery, against 10.02 µs for both C# builds. Idle PSS is 181.9 MiB for Rust, 714.6 MiB for AOT and 920.0 MiB for JIT. The limits section explains why Rust has no saturation result.

## Idle and loaded HTTP memory

Median fresh-process idle PSS and median of each repetition's maximum loaded PSS. Page peaks cover all eight route/concurrency windows.

| Target | Pages idle / peak PSS MiB | Posting idle / peak PSS MiB |
|---|---|---|
| C# AOT | 120.3 / 152.3 | 120.0 / 155.1 |
| C# JIT | 135.2 / 198.3 | 136.5 / 220.5 |
| Rails main (October 15) | 441.6 / 879.9 | 453.7 / 978.9 |
| Rust main | 39.8 / 55.0 | 40.2 / 120.2 |
| C master | 40.6 / 46.2 | 40.7 / 44.4 |
| Go main | 39.3 / 57.1 | 39.9 / 138.2 |
| Express main | 308.3 / 1,012.6 | 312.1 / 7,445.7 |
| Elixir (archived Debian) | 236.9 / 359.5 | 246.6 / 362.9 |

The .NET response cache keeps a 64 MiB budget. A full cache can raise peak AOT process memory by about 2–2.5 times its charge because Server GC retains committed memory. Compressed idle Cable adds about 1.6 KiB live memory and 4–8 KiB process memory per connection. These are separate diagnostic observations. PSS does not identify individual live objects.

## Build options

| Build | Settings |
|---|---|
| Native AOT | `OptimizationPreference=Speed`, standard CPU baseline, ThreadPool spin limit 256 |
| JIT, default image | Same source, ReadyToRun, ThreadPool spin limit 256 |

The spin limit restores .NET 11 RC1's default, raised to 1,024 in RC2. The tables above include this setting, but they do not isolate its effect. We use neither static PGO nor an AVX2 minimum CPU baseline. JIT is the default image because it serves the measured page and posting workloads faster. AOT uses less memory, starts faster and needs no .NET runtime. Both builds use the same application source.

## Revisions and artifacts

Image IDs identify immutable local timing artifacts rather than published release images. Both C# images use the application source of the commit that added these results. Both builds set the ThreadPool spin limit to 256.

| Target | Revision | Image ID | Manifest digest |
|---|---|---|---|
| C# AOT | `current source, .NET 11 RC2, Speed, spin 256` | `ad9f7c1c5e2b757beb21043abc2ead322ebd60f1a36a69963646ba88444d6b48` | `sha256:093b0a12f9a75c67743351a97b7fe31aebd0c703085f69f53382d75750d5d062` |
| C# JIT | `current source, .NET 11 RC2, ReadyToRun, spin 256` | `a4f9eb80c35fafd5021993c0ba59c0d15b6564752c028888e9d0b2805cc5aa29` | `sha256:87176087f8de8a3655ba0c78ff43c653d255afbe240508120ec3287b2d6b08c6` |
| Rails main (October 15) | `edbc779f4dfc9b26c36310881711ddc976a9dfc8` | `d92403621d2e33a2d936752f70a5f6b750cd7c010fa37cd8f154c8764d614c99` | `sha256:a4cfb982de97792be4bff8a52e47f2addb74f6ba872a4f535394be14a0717563` |
| Rails main (October 18) | `11d3fb9ce01faad3384807563053ba86126f747e` | `375e67549fd797076142dde0ca166daa7884d2fdaa7c53d1915f8be46b3ffac9` | `sha256:b3dda811a94ce5435067d817fddcb012988b34bb195c328b14acecb1d5530e22` |
| Rust main | `6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad` | `6b8f7b9346e5ae999a75b723c735dddef0c96d72a42e4946ac612b669442e366` | `sha256:8f44d7f5af5e15d1065c754186336e99562c55605ec402135cb574b0575b523a` |
| C master | `873a51f523de5a0cc235884e5725cfb5521d2027` | `db598c3ab4b4c2bf0fb0ecd63049d75de5f6e7717ea7c54314339e663030128f` | `sha256:f781aed710ccc00f96ed8fe394c15199664ff1df82ef40f37a955a4441d968fc` |
| Go main | `c4ab53d00b0a19a55962e03d18057ca6c3da9437` | `1b86a74051a22c22ad5986cd6dc0c40c2ce6a33ee6d92a8d317f15ec0f17a1e1` | `sha256:4ed4082304fc367d8da147847b43ff3d377d0317a77cd2a327e2396a35385058` |
| Express main | `bb70a7d478160c016393d981e3538987247d3b3d` | `7b25478bfa0ca97eb2457d5c3a1f9d5b500e9799136fef19b2552c73eba1fe49` | `sha256:2133b511a99e501124a24b7349d7fb86e98ca17fe3c7a0ef41c8acb56be11e2c` |
| Elixir (archived Debian) | `58964cf8a8788b2a20c7bde16dae65b025812593` | `64759b59e5aa34568d798571a59e1c2e3547bea701176571d4ea0a5f1aba532a` | `sha256:c276e621e4a4e00b132610154d6802959b160389fe0b2633b4751c83e4a820ea` |

## Limits and exclusions

- C has page and posting results but no Cable result. In the Cable test, only 909 of 10,000 clients connected to C before the time limit.
- The tables do not measure the older Rails commit that this repository pins as its reference.
- Go Cable has one valid repetition out of three. In the other two saturation runs, 8,942 and 6,094 client connections ended with EOF without a close frame. We exclude both runs.
- Express Cable has two valid repetitions. One POST connection error invalidated the other repetition. Express uses four workers with separate response caches.
- Rails Cable has two valid repetitions. One post timed out after 30 seconds in the excluded repetition. This capacity result does not establish actual message loss.
- Elixir Cable has two valid repetitions. The other run left 1,200,000 deliveries unobserved after the 60-second drain. This does not establish permanent loss. Elixir declines compression and uses plain frames.
- We built Elixir with exact archived Debian packages from snapshot `20260918T000000Z`: libvips-dev/libvips42t64 `8.16.1-1+deb13u1`, ffmpeg `7:7.1.5-0+deb13u1`. We changed only build package-source configuration. Application code, dependencies and runtime configuration remain unchanged.
- Rails and Express decline Cable compression and use plain frames.
- Rust main has no valid saturation result. In the main run, the Rust saturation check exceeded the 128 MiB memory budget of our validator. Two extra runs with a 512 MiB budget also failed. In these runs, the Rust server closed 1 and 3,588 client connections. In the second run, it also closed our validation connection. We publish no rate from failed runs, but the three paced Rust runs are valid.
- Rust main closes the connection of a client that falls 256 messages behind, and it sends `reconnect: true`. The closed connections match this policy. This conclusion comes from code reading and counts, not from a separate experiment. A larger validator budget did not help. Our rule requires every message at every client, so a Rust saturation run cannot pass at this load.
- We keep the records of every failed run. Medians with n=1 or n=2 use only the valid runs and do not describe reliability across all three runs. We do not combine failed runs with valid runs. We do not rerun a reference app to replace a failed run.
