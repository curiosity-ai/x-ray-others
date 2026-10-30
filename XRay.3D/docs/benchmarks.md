# Timing harness

`tests/XRay.ThreeD.Tests/BenchmarkTests.cs` times load plus GLB conversion of the largest dataset files without a benchmarking
library. It is skipped unless `XRAY_3D_BENCH` is set, so the normal test run has no timing in it.

```sh
XRAY_3D_BENCH=1 XRAY_3D_BENCH_ROUNDS=5 XRAY_3D_BENCH_OUT=bench.txt \
  dotnet test --project XRay.3D/tests/XRay.ThreeD.Tests -c Release -- --filter-class "*BenchmarkTests"
```

Each line: best and median wall time of the rounds after one warm-up, bytes allocated per round on the test thread, GC counts.
Add a case to `Cases()` for another file. Numbers depend on the machine; the allocation column is the stable one.

Reference run (one cloud VM core, .NET 10, Release, 3 rounds), after the allocation pass of this change:

| File | Size | Best | Allocated per round |
|---|---|---|---|
| `sldasm/nist-mtc-assembly.sldasm` | 2.1 MB | 480 ms | 49 MB |
| `step/feather-esp8266.step` | 3.1 MB | 800 ms | 411 MB |
| `step/adafruit-motor-hat.step` | 2.0 MB | 690 ms | 249 MB |
| `glb/watering-can.glb` | 3.4 MB | 13 ms | 8.5 MB |
| `ply/gsplat-measuring-tape.ply` | 1.3 MB | 15 ms | 5 MB |
| `dxf/godown-warehouse-...dxf` | 0.16 MB | 3 ms | 1.9 MB |
| `synthetic/laz-f3.laz` | 31 KB | 7 ms | 2.7 MB |

STEP is the outlier: about 130 bytes allocated per input byte, mostly the surface tessellators (point lists and vertex records per
face), which still allocate per face rather than renting scratch. That is the next place to look.

## What the allocation pass changed

- Part 21: entity type names are interned (one string per distinct name, through a span lookup), scalar arguments no longer allocate
  an empty list, `StepArg.Refs` is an iterator instead of two LINQ stages.
- STEP tessellator: reference lists are walked in place instead of `ToList` and `Select`.
- DXF: the text is indexed by line start instead of being split into a string per line; group codes are parsed from spans; short value
  strings (entity, layer and section names) are interned; numeric parsing trims spans instead of strings.
- Meshopt decoder and encoder: vertex blocks use pooled scratch and stack-allocated baseline vertices, the per-lane bit tables are
  built once; every loop checks the cancellation token (blocks, triangles, indices, filters).
- SolidWorks display lists: the face order by material uses one key sort instead of a LINQ `OrderBy`.
