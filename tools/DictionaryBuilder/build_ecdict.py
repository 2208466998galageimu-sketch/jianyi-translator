#!/usr/bin/env python3
"""Build Thanslator's compact offline English -> Simplified Chinese dictionary.

The input is ECDICT's ecdict.csv (UTF-8, 13 columns).  The output is a SQLite
database designed to be opened read-only by the Thanslator app.

Standard library only.  Typical usage:

    python -I tools/DictionaryBuilder/build_ecdict.py
    python -I tools/DictionaryBuilder/build_ecdict.py --csv C:\\temp\\ecdict.csv

With no --csv the CSV is downloaded from ECDICT into a fresh directory under
the operating system's temp directory, hashed, used, and deleted.  The CSV is
never required to live in the repository.

Schema version 1 (also written to meta.schema_version):

    CREATE TABLE entries (
        word TEXT PRIMARY KEY,
        word_lower TEXT NOT NULL,
        phonetic TEXT,
        translation TEXT NOT NULL,
        bnc INTEGER,
        frq INTEGER,
        collins INTEGER,
        oxford INTEGER,
        exchange TEXT
    );
    CREATE INDEX idx_entries_lower ON entries(word_lower);
    CREATE TABLE forms (form TEXT PRIMARY KEY, word TEXT NOT NULL);
    CREATE INDEX idx_forms_word ON forms(word);
    CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);

Data notes:
  * All CSV rows are kept (including the handful of rows with an empty
    translation); translation is copied verbatim, including the literal
    two-character "\\n" line separators used by ECDICT.
  * word_lower is Python str.lower() of word.  ECDICT's own word column is
    unique case-sensitively and, for the pinned snapshot, also unique under
    case folding.
  * Empty optional CSV fields become SQL NULL.  bnc/frq keep ECDICT's 0
    ("not ranked") values; an empty collins/oxford becomes NULL.
  * forms maps inflected surface forms to a lemma that exists in entries.
    Candidates come from exchange tokens (tags p/d/i/3/r/t/s/f) and from
    entries carrying "0:<lemma>" (an explicit form -> lemma statement).
    Conflicts are resolved deterministically: explicit 0:<lemma> candidates
    first, then form tokens on lemma entries, then form tokens on inflected
    entries; ties go to the first row in CSV order.  Tokens with an empty
    form, an unknown tag, or an unresolvable lemma are skipped.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import os
import shutil
import sqlite3
import sys
import tempfile
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

SOURCE_URL = "https://raw.githubusercontent.com/skywind3000/ECDICT/master/ecdict.csv"
SOURCE_LICENSE = (
    "MIT (ECDICT repository, Copyright (c) 2025 Linwei); upstream data "
    "includes cdict (reported GPL) and diaosi/\u5c4c\u4e1d\u5b57\u5178 "
    "(license unknown) - see assets/dictionary/README.md"
)
SCHEMA_VERSION = 1
FORM_TAGS = frozenset({"p", "d", "i", "3", "r", "t", "s", "f"})
EXPECTED_COLUMNS = (
    "word",
    "phonetic",
    "definition",
    "translation",
    "pos",
    "collins",
    "oxford",
    "tag",
    "bnc",
    "frq",
    "exchange",
    "detail",
    "audio",
)
COL_WORD = 0
COL_PHONETIC = 1
COL_TRANSLATION = 3
COL_COLLINS = 5
COL_OXFORD = 6
COL_BNC = 8
COL_FRQ = 9
COL_EXCHANGE = 10

DDL = """
CREATE TABLE entries (
    word TEXT PRIMARY KEY,
    word_lower TEXT NOT NULL,
    phonetic TEXT,
    translation TEXT NOT NULL,
    bnc INTEGER,
    frq INTEGER,
    collins INTEGER,
    oxford INTEGER,
    exchange TEXT
);
CREATE INDEX idx_entries_lower ON entries(word_lower);
CREATE TABLE forms (
    form TEXT PRIMARY KEY,
    word TEXT NOT NULL
);
CREATE INDEX idx_forms_word ON forms(word);
CREATE TABLE meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
"""

INSERT_ENTRY = (
    "INSERT INTO entries "
    "(word, word_lower, phonetic, translation, bnc, frq, collins, oxford, exchange) "
    "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)"
)
INSERT_FORM = "INSERT INTO forms (form, word) VALUES (?, ?)"
INSERT_META = "INSERT INTO meta (key, value) VALUES (?, ?)"

BATCH_SIZE = 5000


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    repo_root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(
        description="Build the Thanslator English->Chinese SQLite dictionary from ECDICT.",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    parser.add_argument(
        "--csv",
        metavar="PATH",
        help="Existing ecdict.csv to use. If omitted, download it to an OS temp directory.",
    )
    parser.add_argument(
        "--out",
        metavar="PATH",
        default=str(repo_root / "assets" / "dictionary" / "dict-en-zh.sqlite"),
        help="Output SQLite database path.",
    )
    parser.add_argument(
        "--manifest",
        metavar="PATH",
        help="Manifest JSON path (default: manifest.json next to --out).",
    )
    parser.add_argument(
        "--source-url",
        default=SOURCE_URL,
        help="Source URL recorded in meta/manifest when the CSV is downloaded.",
    )
    parser.add_argument(
        "--download-dir",
        metavar="PATH",
        help="Directory for the downloaded CSV. Default: a new OS temp directory. "
        "Must not be inside the repository.",
    )
    parser.add_argument(
        "--keep-csv",
        action="store_true",
        help="Keep a CSV downloaded into the default temporary directory.",
    )
    parser.add_argument(
        "--no-manifest",
        action="store_true",
        help="Do not write manifest.json.",
    )
    parser.add_argument(
        "--built-at",
        metavar="ISO8601",
        help="Override built_at (UTC ISO-8601, e.g. 2026-01-01T00:00:00Z). "
        "Default: SOURCE_DATE_EPOCH if set, else the current UTC time.",
    )
    return parser.parse_args(argv)


def configure_stdout() -> None:
    """Avoid UnicodeEncodeError on legacy Windows consoles."""
    try:
        sys.stdout.reconfigure(errors="backslashreplace")
        sys.stderr.reconfigure(errors="backslashreplace")
    except (AttributeError, ValueError):
        pass


def sha256_file(path: Path, chunk_size: int = 1 << 20) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while True:
            chunk = handle.read(chunk_size)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def utc_now_timestamp() -> str:
    epoch = os.environ.get("SOURCE_DATE_EPOCH")
    if epoch:
        try:
            moment = datetime.fromtimestamp(int(epoch), tz=timezone.utc)
        except (ValueError, OverflowError) as exc:
            raise SystemExit(f"Invalid SOURCE_DATE_EPOCH: {epoch!r}") from exc
    else:
        moment = datetime.now(timezone.utc)
    return moment.strftime("%Y-%m-%dT%H:%M:%SZ")


def normalize_built_at(value: str) -> str:
    cleaned = value.strip()
    if cleaned.endswith("Z"):
        cleaned = cleaned[:-1] + "+00:00"
    try:
        moment = datetime.fromisoformat(cleaned)
    except ValueError as exc:
        raise SystemExit(f"Invalid --built-at value: {value!r}") from exc
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=timezone.utc)
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def is_inside(child: Path, parent: Path) -> bool:
    try:
        child.resolve().relative_to(parent.resolve())
        return True
    except ValueError:
        return False


def download_csv(url: str, download_dir: Path | None, repo_root: Path) -> tuple[Path, Path]:
    """Download the CSV and return (csv_path, target_dir).

    A caller using the default temp directory removes target_dir afterwards.
    """
    owns_dir = download_dir is None
    if download_dir is not None:
        target_dir = download_dir.resolve()
        if is_inside(target_dir, repo_root):
            raise SystemExit(
                f"Refusing to download into the repository: {target_dir}. "
                "Choose a directory outside the repo."
            )
        target_dir.mkdir(parents=True, exist_ok=True)
    else:
        target_dir = Path(tempfile.mkdtemp(prefix="thanslator-ecdict-"))

    target = target_dir / "ecdict.csv"
    if target.exists():
        raise SystemExit(f"Refusing to overwrite existing file: {target}")

    print(f"Downloading {url}")
    try:
        request = urllib.request.Request(
            url,
            headers={"User-Agent": "Thanslator-DictionaryBuilder/1.0"},
        )
        with urllib.request.urlopen(request, timeout=120) as response, target.open(
            "wb"
        ) as out:
            expected = response.headers.get("Content-Length")
            written = 0
            while True:
                chunk = response.read(1 << 20)
                if not chunk:
                    break
                out.write(chunk)
                written += len(chunk)
                if written % (8 << 20) == 0:
                    print(f"  downloaded {written:,} bytes", flush=True)
        if expected is not None and written != int(expected):
            raise SystemExit(
                f"Download size mismatch: expected {expected}, got {written} bytes"
            )
    except BaseException:
        if owns_dir:
            shutil.rmtree(target_dir, ignore_errors=True)
        else:
            target.unlink(missing_ok=True)
        raise
    print(f"  saved {target} ({written:,} bytes)")
    return target, target_dir


def open_csv(csv_path: Path):
    """Open the CSV and validate its header. Caller closes the file."""
    csv.field_size_limit(min(sys.maxsize, 2**31 - 1))
    handle = csv_path.open("r", encoding="utf-8-sig", newline="")
    reader = csv.reader(handle)
    try:
        header = next(reader)
    except StopIteration:
        handle.close()
        raise SystemExit(f"CSV is empty: {csv_path}")
    if tuple(header) != EXPECTED_COLUMNS:
        handle.close()
        raise SystemExit(
            "Unexpected CSV header.\n"
            f"  expected: {list(EXPECTED_COLUMNS)}\n"
            f"  actual:   {header}"
        )
    return handle, reader


def int_or_none(value: str) -> int | None:
    value = value.strip()
    if not value:
        return None
    try:
        return int(value)
    except ValueError as exc:
        raise ValueError(f"Expected an integer, got {value!r}") from exc


def none_if_empty(value: str) -> str | None:
    return value if value else None


def parse_exchange(exchange: str) -> tuple[str | None, list[str]]:
    """Return (lemma_from_0_token, form_tokens) from an exchange field."""
    lemma = None
    forms: list[str] = []
    for token in exchange.split("/"):
        if ":" not in token:
            continue
        tag, value = token.split(":", 1)
        if tag == "0":
            lemma = value or lemma
        elif tag in FORM_TAGS:
            forms.append(value)
    return lemma, forms


def connect_for_build(path: Path) -> sqlite3.Connection:
    connection = sqlite3.connect(str(path), isolation_level=None)
    connection.execute("PRAGMA journal_mode = OFF")
    connection.execute("PRAGMA synchronous = OFF")
    connection.execute("PRAGMA temp_store = MEMORY")
    connection.execute("PRAGMA cache_size = -262144")
    connection.execute("PRAGMA page_size = 4096")
    return connection


def build_database(
    csv_path: Path,
    db_path: Path,
    source_url: str,
    source_sha256: str,
    built_at: str,
) -> dict[str, int]:
    """Stream the CSV into db_path. Returns row counts and skip statistics."""
    if db_path.exists():
        db_path.unlink()

    handle, reader = open_csv(csv_path)
    candidates: dict[str, list[tuple[int, int, str]]] = {}
    entries_count = 0
    bad_rows = 0
    empty_forms = 0
    self_forms = 0
    first_data_line = None

    connection = connect_for_build(db_path)
    try:
        connection.executescript(DDL)

        connection.execute("BEGIN")
        batch: list[tuple] = []
        try:
            for row in reader:
                if first_data_line is None:
                    first_data_line = reader.line_num
                if len(row) != len(EXPECTED_COLUMNS):
                    bad_rows += 1
                    raise ValueError(
                        f"CSV line {reader.line_num}: expected "
                        f"{len(EXPECTED_COLUMNS)} columns, got {len(row)}"
                    )

                word = row[COL_WORD]
                if not word:
                    raise ValueError(f"CSV line {reader.line_num}: empty word")

                batch.append(
                    (
                        word,
                        word.lower(),
                        none_if_empty(row[COL_PHONETIC]),
                        row[COL_TRANSLATION],
                        int_or_none(row[COL_BNC]),
                        int_or_none(row[COL_FRQ]),
                        int_or_none(row[COL_COLLINS]),
                        int_or_none(row[COL_OXFORD]),
                        none_if_empty(row[COL_EXCHANGE]),
                    )
                )
                entries_count += 1

                exchange = row[COL_EXCHANGE]
                if exchange:
                    lemma, forms = parse_exchange(exchange)
                    sequence = entries_count - 1
                    if lemma and lemma != word:
                        candidates.setdefault(word, []).append((0, sequence, lemma))
                    priority = 1 if (lemma is None or lemma == word) else 2
                    for form in forms:
                        if not form:
                            empty_forms += 1
                            continue
                        if form == word:
                            self_forms += 1
                            continue
                        candidates.setdefault(form, []).append(
                            (priority, sequence, word)
                        )

                if len(batch) >= BATCH_SIZE:
                    connection.executemany(INSERT_ENTRY, batch)
                    batch.clear()
            if batch:
                connection.executemany(INSERT_ENTRY, batch)
                batch.clear()
            connection.execute("COMMIT")
        except BaseException:
            connection.execute("ROLLBACK")
            raise

        if entries_count == 0:
            raise ValueError("CSV contains a header but no data rows")
        if first_data_line is None:
            raise ValueError("CSV contains no data rows")

        # Resolve explicit "0:<lemma>" targets to an actual entries.word.  A
        # handful of ECDICT rows contain truncated lemmas; skip those instead
        # of writing dangling references.
        resolve_cache: dict[str, str | None] = {}

        def resolve_lemma(raw: str) -> str | None:
            if raw in resolve_cache:
                return resolve_cache[raw]
            found = connection.execute(
                "SELECT word FROM entries WHERE word = ?", (raw,)
            ).fetchone()
            if found is None:
                found = connection.execute(
                    "SELECT word FROM entries WHERE word_lower = ? ORDER BY word LIMIT 1",
                    (raw.lower(),),
                ).fetchone()
            resolved = found[0] if found is not None else None
            resolve_cache[raw] = resolved
            return resolved

        forms_rows: list[tuple[str, str]] = []
        p0_missing = 0
        p0_self = 0
        for form in sorted(candidates):
            chosen: str | None = None
            for priority, _sequence, lemma in sorted(
                candidates[form], key=lambda item: (item[0], item[1])
            ):
                if priority == 0:
                    resolved = resolve_lemma(lemma)
                    if resolved is None:
                        p0_missing += 1
                        continue
                    if resolved == form:
                        p0_self += 1
                        continue
                    chosen = resolved
                    break
                chosen = lemma
                break
            if chosen is not None:
                forms_rows.append((form, chosen))

        connection.execute("BEGIN")
        try:
            for start in range(0, len(forms_rows), BATCH_SIZE):
                connection.executemany(
                    INSERT_FORM, forms_rows[start : start + BATCH_SIZE]
                )
            connection.execute("COMMIT")
        except BaseException:
            connection.execute("ROLLBACK")
            raise

        meta_rows = [
            ("source_url", source_url),
            ("source_license", SOURCE_LICENSE),
            ("built_at", built_at),
            ("row_count", str(entries_count)),
            ("schema_version", str(SCHEMA_VERSION)),
            ("source_sha256", source_sha256),
        ]
        connection.executemany(INSERT_META, meta_rows)
        connection.execute("VACUUM")
        connection.execute("PRAGMA journal_mode = DELETE")

        integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
        if integrity != "ok":
            raise RuntimeError(f"PRAGMA integrity_check failed: {integrity}")

        stats = {
            "entries": entries_count,
            "forms": len(forms_rows),
            "meta": len(meta_rows),
            "form_candidates": len(candidates),
            "empty_forms_skipped": empty_forms,
            "self_forms_skipped": self_forms,
            "unresolved_lemmas_skipped": p0_missing,
            "self_lemmas_skipped": p0_self,
            "bad_rows": bad_rows,
        }
    finally:
        connection.close()
        handle.close()

    return stats


def write_manifest(manifest_path: Path, manifest: dict) -> None:
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    temporary = manifest_path.with_name(manifest_path.name + ".tmp")
    temporary.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    os.replace(temporary, manifest_path)


def main(argv: list[str] | None = None) -> int:
    configure_stdout()
    args = parse_args(argv)
    repo_root = Path(__file__).resolve().parents[2]
    out_path = Path(args.out).resolve()
    out_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path = (
        Path(args.manifest).resolve()
        if args.manifest
        else out_path.parent / "manifest.json"
    )

    built_at = (
        normalize_built_at(args.built_at) if args.built_at else utc_now_timestamp()
    )

    downloaded_dir: Path | None = None
    downloaded_by_default = False
    if args.csv:
        csv_path = Path(args.csv).resolve()
        if not csv_path.is_file():
            raise SystemExit(f"CSV not found: {csv_path}")
        source_url = args.source_url
    else:
        csv_path, downloaded_dir = download_csv(
            args.source_url,
            Path(args.download_dir) if args.download_dir else None,
            repo_root,
        )
        downloaded_by_default = args.download_dir is None
        source_url = args.source_url

    try:
        csv_size = csv_path.stat().st_size
        csv_sha256 = sha256_file(csv_path)
        print(f"CSV: {csv_path}")
        print(f"  bytes:  {csv_size:,}")
        print(f"  sha256: {csv_sha256}")

        temporary_db = out_path.with_name(out_path.name + ".tmp")
        print(f"Building SQLite database: {temporary_db}")
        try:
            stats = build_database(
                csv_path=csv_path,
                db_path=temporary_db,
                source_url=source_url,
                source_sha256=csv_sha256,
                built_at=built_at,
            )
            os.replace(temporary_db, out_path)
        except BaseException:
            if temporary_db.exists():
                temporary_db.unlink()
            raise

        db_size = out_path.stat().st_size
        db_sha256 = sha256_file(out_path)

        print("Built:")
        print(f"  database:      {out_path}")
        print(f"  bytes:         {db_size:,}")
        print(f"  sha256:        {db_sha256}")
        print(f"  entries:       {stats['entries']:,}")
        print(f"  forms:         {stats['forms']:,}")
        print(f"  meta rows:     {stats['meta']:,}")
        print(
            "  skipped:       "
            f"{stats['empty_forms_skipped']:,} empty form tokens, "
            f"{stats['self_forms_skipped']:,} self form tokens, "
            f"{stats['unresolved_lemmas_skipped']:,} unresolved lemmas, "
            f"{stats['self_lemmas_skipped']:,} self lemmas"
        )

        if not args.no_manifest:
            manifest = {
                "name": out_path.name,
                "description": (
                    "Thanslator offline English->Simplified Chinese dictionary "
                    "built from ECDICT"
                ),
                "source_url": source_url,
                "license": SOURCE_LICENSE,
                "source_sha256": csv_sha256,
                "source_size_bytes": csv_size,
                "schema_version": SCHEMA_VERSION,
                "built_at": built_at,
                "row_counts": {
                    "entries": stats["entries"],
                    "forms": stats["forms"],
                    "meta": stats["meta"],
                },
                "database_file": out_path.name,
                "file_size_bytes": db_size,
                "sha256": db_sha256,
            }
            write_manifest(manifest_path, manifest)
            print(f"Manifest: {manifest_path}")

        return 0
    finally:
        if downloaded_by_default and downloaded_dir is not None:
            if args.keep_csv:
                print(f"Kept downloaded CSV: {downloaded_dir / 'ecdict.csv'}")
            else:
                shutil.rmtree(downloaded_dir, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
