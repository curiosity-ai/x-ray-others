---
name: test-documents
description: How the packages in x-ray-others find, use and add test fixtures from the shared curiosity-ai/test_documents corpus. Use when a test needs a real PSD, PostScript/EPS, XPS, OneNote, EML or MSG file, when a test is skipped with "test_documents not found", when adding a regression fixture, or when bumping the test_documents submodule.
---

# The `test_documents` corpus

[curiosity-ai/test_documents](https://github.com/curiosity-ai/test_documents) is the fixture corpus
shared by the whole X-Ray family. It is a fork of `xberg-io/test_documents` (the corpus
X-Ray.Content's Rust upstream tests against) with the X-Ray additions on top, so it is a superset
of upstream and can stand in for it anywhere.

- One folder per format: `docx/`, `pdf/`, `email/`, ... The folders this repository reads are:

  | Folder | Package | Where the files came from |
  |---|---|---|
  | `psd/` | X-Ray.Psd | Patchy's Photoshop fixtures and reference renders, psd-tools samples |
  | `ps/`, `xps/` | X-Ray.PS | the test data of the Python PostScript/XPS library the port came from |
  | `onenote/` | X-Ray.OneNote | the test files of the Python OneNote reader the port came from |
  | `email/` (and `email/xray-email/`, per-source subfolders) | X-Ray.Email | upstream's email fixtures plus the generated and collected EML/MSG set |

- Binaries are committed directly in git; there is no LFS and no bucket fetch for these folders.
- Third-party sets carry their licence: a `LICENSE` (or `NOTICE`) and a `README.md` next to the
  files saying where they came from. Keep that true for anything you add.

## How tests find it

This repository carries the corpus as a git submodule at `test_documents/`:

```sh
git submodule update --init --depth 1 test_documents
```

Every test project resolves the corpus root the same way:

1. `XRAY_TEST_DOCUMENTS`, if set, is the root.
2. Otherwise walk up from `AppContext.BaseDirectory` to the first directory named
   `test_documents`. From a build under `x-ray-others/<Package>/tests/.../bin/` that is the
   submodule; on a machine where the corpus is cloned next to `x-ray-others`, the walk finds that
   clone instead.

If no root is found, corpus tests are **skipped** (`Assert.Skip`) with a message naming the
submodule command above. If the root exists but a fixture is missing, the test **fails**: a
missing file under a present corpus means the submodule pin is behind the test, not that the
machine lacks the corpus.

## Adding a fixture

1. Put the file in the folder for its format in a clone of `curiosity-ai/test_documents`. Name it
   for what it exercises (`psd/text-warp-arc.psd`, not `psd/test3.psd`).
2. If it is third-party, check the licence allows redistribution, then add or update the
   `LICENSE`/`README.md` next to it with the source URL and licence. Never add a file whose
   licence you cannot state.
3. Keep files small. A regression case should be the smallest file that still shows the bug.
4. Commit and push to `curiosity-ai/test_documents`, then bump the pin here:

   ```sh
   git -C test_documents fetch origin main && git -C test_documents checkout origin/main
   git add test_documents && git commit -m "Bump test_documents for <reason>"
   ```

5. Reference the file from the test through the resolver above, never by an absolute path.

Expected outputs (goldens, manifests of expected fields) belong with the test that reads them
unless they are large; a package that regenerates them from a reference implementation documents
the script in its own `CLAUDE.md`.
