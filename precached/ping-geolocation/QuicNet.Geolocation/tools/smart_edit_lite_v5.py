#!/usr/bin/env python3
"""Smart Edit Lite v5 — compact transactional code editor for AI coding agents.

Purpose:
  Apply a JSON list of safe code-file changes inside one workspace.

Included:
  write_file, replace, regex_replace, line_replace, insert, append, prepend,
  multi_edit, delete_path, mkdir, move, copy, dry-run, unified diff,
  rollback, optional syntax validation, JSON output, schema/example/self-test.

Excluded on purpose:
  network, RPC, archives, watch/search, binary editing, external validators,
  sandboxing, backup history, fuzzy matching, and platform-specific features.
"""

from __future__ import annotations

import argparse
import ast
import difflib
import json
import os
import re
import shutil
import sys
import tempfile
import tomllib
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable

VERSION = "5.0.0-lite"

AGENT_PROMPT = r"""# Smart Edit Lite v5 — instrucciones para agentes de código

Objetivo: hacer cambios correctos con el mínimo contexto, tokens y reescritura.

## Flujo recomendado
1. Inspecciona solo el fragmento relevante. No vuelques archivos grandes completos; usa búsqueda o lecturas por líneas desde tu entorno.
2. Prefiere `replace` con contexto suficiente y `expected: 1` para un cambio localizado.
3. Si hay varios cambios en el mismo archivo, agrúpalos en una sola operación `multi_edit`. Las ediciones se aplican en orden.
4. Usa `regex_replace` solo cuando el patrón sea claramente más preciso que un reemplazo literal. Incluye `expected` y un `count` explícito cuando quieras reemplazar más de una coincidencia (`count: 0` significa todas).
5. Usa `line_replace`, `insert`, `append` o `prepend` cuando la ubicación sea inequívoca.
6. Reserva `write_file` para archivos nuevos o para reemplazos completos deliberados. Para sobrescribir un archivo existente, especifica `overwrite: true`.
7. Agrupa operaciones de varios archivos en una sola especificación JSON; ordena las operaciones si dependen unas de otras.
8. Antes de una edición delicada, ejecuta `--dry-run --diff --json-output --compact-json`. Si el diff es correcto, repite la misma especificación sin `--dry-run`.
9. Usa `--validate` para comprobaciones de sintaxis compatibles. Ejecuta pruebas del proyecto cuando el cambio lo requiera; no afirmes que pasaron si no se ejecutaron.
10. Mantén las rutas relativas a `--root`; no uses `..` ni enlaces simbólicos. Ante un error, vuelve a leer solo el contexto necesario y corrige la operación concreta.

## Ejemplo de especificación
{
  "version": 5,
  "validate": true,
  "operations": [
    {"type": "replace", "file": "src/app.py",
     "old": "TIMEOUT = 1", "new": "TIMEOUT = 5", "expected": 1},
    {"type": "multi_edit", "file": "src/config.py",
     "edits": [
       {"type": "replace", "old": "DEBUG = True", "new": "DEBUG = False", "expected": 1},
       {"type": "append", "text": "\n# Updated by the migration\n"}
     ]}
  ]
}

## Salida económica
- Para respuestas de máquina, usa `--json-output --compact-json`.
- Usa `--summary` cuando solo necesites saber qué rutas cambiaron; combínalo con `--diff` solo si necesitas inspeccionar el parche.
- No pidas ni imprimas el archivo completo después de editarlo: comprueba el diff, los archivos afectados y las validaciones necesarias.
- Informa brevemente de los archivos modificados, validaciones ejecutadas y cualquier limitación real.
"""

TEXT_EDIT_TYPES = {
    "replace",
    "regex_replace",
    "line_replace",
    "insert",
    "append",
    "prepend",
}
ALL_TYPES = TEXT_EDIT_TYPES | {
    "write_file",
    "multi_edit",
    "delete_path",
    "mkdir",
    "move",
    "copy",
}


class MiniEditError(Exception):
    """Expected user/specification error."""


@dataclass
class TextFile:
    text: str
    newline: str = "\n"
    bom: bool = False


def _inside(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def safe_path(root: Path, value: Any) -> Path:
    if not isinstance(value, str) or not value.strip():
        raise MiniEditError("path must be a non-empty string")
    rel = Path(value)
    if rel.is_absolute() or ".." in rel.parts:
        raise MiniEditError(f"unsafe path: {value!r}")
    candidate = root.joinpath(rel)
    current = root
    for part in rel.parts:
        current = current / part
        if current.is_symlink():
            raise MiniEditError(f"symlinks are not supported: {value!r}")
    parent = candidate.parent.resolve()
    if not _inside(parent, root):
        raise MiniEditError(f"path escapes workspace: {value!r}")
    return candidate


def _detect_newline(raw: bytes) -> str:
    if b"\r\n" in raw:
        return "\r\n"
    if b"\r" in raw:
        return "\r"
    return "\n"


def read_text(path: Path) -> TextFile:
    if not path.is_file():
        raise MiniEditError(f"file does not exist: {path}")
    raw = path.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    try:
        text = raw.decode("utf-8-sig" if bom else "utf-8")
    except UnicodeDecodeError as exc:
        raise MiniEditError(f"file is not UTF-8 text: {path}") from exc
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    return TextFile(text=text, newline=_detect_newline(raw), bom=bom)


def encode_text(doc: TextFile) -> bytes:
    text = doc.text if doc.newline == "\n" else doc.text.replace("\n", doc.newline)
    raw = text.encode("utf-8")
    return (b"\xef\xbb\xbf" + raw) if doc.bom else raw


def atomic_write(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp_name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    tmp = Path(tmp_name)
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        if path.exists():
            shutil.copymode(path, tmp)
        os.replace(tmp, path)
    finally:
        tmp.unlink(missing_ok=True)


def require_string(op: dict[str, Any], key: str) -> str:
    value = op.get(key)
    if not isinstance(value, str):
        raise MiniEditError(f"{op.get('type')}.{key} must be a string")
    return value


def _expected_count(op: dict[str, Any], actual: int) -> None:
    expected = op.get("expected")
    if expected is None:
        if actual == 0:
            raise MiniEditError(f"{op['type']} found no matches")
        return
    if not isinstance(expected, int) or expected < 0:
        raise MiniEditError(f"{op['type']}.expected must be a non-negative integer")
    if actual != expected:
        raise MiniEditError(
            f"{op['type']} expected {expected} matches, found {actual}"
        )


def edit_replace(text: str, op: dict[str, Any]) -> str:
    old = require_string(op, "old")
    new = require_string(op, "new")
    if old == "":
        raise MiniEditError("replace.old cannot be empty")
    actual = text.count(old)
    _expected_count(op, actual)
    if "count" not in op and actual != 1:
        raise MiniEditError(
            f"replace is ambiguous: found {actual} matches; add more context or specify count/expected"
        )
    count = op.get("count", 1)
    if isinstance(count, bool) or not isinstance(count, int) or count < 0:
        raise MiniEditError("replace.count must be a non-negative integer")
    if count > actual:
        raise MiniEditError(f"replace.count={count} exceeds the {actual} available matches")
    return text.replace(old, new, count if count else -1)


def _regex_flags(value: Any) -> int:
    if value is None:
        return 0
    if not isinstance(value, str) or any(ch not in "imsx" for ch in value):
        raise MiniEditError("regex_replace.flags may only contain i, m, s, x")
    mapping = {"i": re.I, "m": re.M, "s": re.S, "x": re.X}
    flags = 0
    for ch in value:
        flags |= mapping[ch]
    return flags


def edit_regex(text: str, op: dict[str, Any]) -> str:
    pattern = require_string(op, "pattern")
    replacement = require_string(op, "replacement")
    try:
        regex = re.compile(pattern, _regex_flags(op.get("flags")))
    except re.error as exc:
        raise MiniEditError(f"invalid regular expression: {exc}") from exc
    actual = len(list(regex.finditer(text)))
    _expected_count(op, actual)
    if "count" not in op and actual != 1:
        raise MiniEditError(
            f"regex_replace is ambiguous: found {actual} matches; add expected/count explicitly"
        )
    count = op.get("count", 1)
    if isinstance(count, bool) or not isinstance(count, int) or count < 0:
        raise MiniEditError("regex_replace.count must be a non-negative integer")
    if count > actual:
        raise MiniEditError(f"regex_replace.count={count} exceeds the {actual} available matches")
    return regex.sub(replacement, text, count=count)


def edit_lines(text: str, op: dict[str, Any]) -> str:
    start = op.get("start")
    end = op.get("end", start)
    if not isinstance(start, int) or not isinstance(end, int) or start < 1 or end < start:
        raise MiniEditError("line_replace requires 1-based start/end with end >= start")
    lines = text.splitlines(keepends=True)
    if not lines and start == 1 and end == 1:
        lines = [""]
    if end > len(lines):
        raise MiniEditError(f"line_replace range {start}-{end} exceeds {len(lines)} lines")
    replacement = require_string(op, "text")
    replaced_had_newline = bool(lines[end - 1].endswith("\n"))
    if replacement and not replacement.endswith("\n") and (end < len(lines) or replaced_had_newline):
        replacement += "\n"
    lines[start - 1 : end] = [replacement] if replacement else []
    return "".join(lines)


def edit_insert(text: str, op: dict[str, Any]) -> str:
    line = op.get("line")
    if not isinstance(line, int) or line < 1:
        raise MiniEditError("insert.line must be a positive 1-based integer")
    lines = text.splitlines(keepends=True)
    max_line = len(lines) + 1
    if line > max_line:
        raise MiniEditError(f"insert.line {line} exceeds {max_line}")
    value = require_string(op, "text")
    if line == len(lines) + 1 and text and not text.endswith("\n"):
        value = "\n" + value
    elif value and not value.endswith("\n") and line <= len(lines):
        value += "\n"
    lines.insert(line - 1, value)
    return "".join(lines)


def edit_append(text: str, op: dict[str, Any]) -> str:
    return text + require_string(op, "text")


def edit_prepend(text: str, op: dict[str, Any]) -> str:
    return require_string(op, "text") + text


TEXT_HANDLERS: dict[str, Callable[[str, dict[str, Any]], str]] = {
    "replace": edit_replace,
    "regex_replace": edit_regex,
    "line_replace": edit_lines,
    "insert": edit_insert,
    "append": edit_append,
    "prepend": edit_prepend,
}


def apply_text_edit(doc: TextFile, op: dict[str, Any]) -> TextFile:
    kind = op.get("type")
    handler = TEXT_HANDLERS.get(kind)
    if handler is None:
        raise MiniEditError(f"unsupported text edit: {kind!r}")
    return TextFile(handler(doc.text, op), doc.newline, doc.bom)


def _remove(path: Path) -> None:
    if path.is_symlink() or path.is_file():
        path.unlink()
    elif path.is_dir():
        shutil.rmtree(path)


def _snapshot_roots(paths: list[Path]) -> list[Path]:
    unique = sorted(set(paths), key=lambda p: len(p.parts))
    roots: list[Path] = []
    for path in unique:
        if not any(path == root or _inside(path, root) for root in roots):
            roots.append(path)
    return roots


class Transaction:
    def __init__(self, root: Path, paths: list[Path]) -> None:
        self.root = root
        self.temp = Path(tempfile.mkdtemp(prefix="smart-edit-lite-"))
        self.records: list[tuple[Path, bool, Path | None]] = []
        self.missing_parents: set[Path] = set()
        for target in paths:
            parent = target.parent
            while parent != root and _inside(parent, root) and not parent.exists():
                self.missing_parents.add(parent)
                parent = parent.parent
        for index, path in enumerate(_snapshot_roots(paths)):
            exists = path.exists() or path.is_symlink()
            backup: Path | None = None
            if exists:
                backup = self.temp / str(index)
                if path.is_dir() and not path.is_symlink():
                    shutil.copytree(path, backup)
                else:
                    backup.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(path, backup, follow_symlinks=False)
            self.records.append((path, exists, backup))

    def rollback(self) -> None:
        for path, existed, backup in reversed(self.records):
            if path.exists() or path.is_symlink():
                _remove(path)
            if existed and backup is not None:
                path.parent.mkdir(parents=True, exist_ok=True)
                if backup.is_dir():
                    shutil.copytree(backup, path)
                else:
                    shutil.copy2(backup, path, follow_symlinks=False)
        for parent in sorted(self.missing_parents, key=lambda item: len(item.parts), reverse=True):
            try:
                if parent.is_dir() and not any(parent.iterdir()):
                    parent.rmdir()
            except OSError:
                pass

    def close(self) -> None:
        shutil.rmtree(self.temp, ignore_errors=True)


class MiniEditor:
    def __init__(self, root: Path) -> None:
        self.root = root.resolve()
        self.changed: set[Path] = set()
        self.before_text: dict[Path, str | None] = {}

    def path(self, value: Any) -> Path:
        return safe_path(self.root, value)

    def remember_text(self, path: Path) -> None:
        if path in self.before_text:
            return
        if path.is_file():
            try:
                self.before_text[path] = read_text(path).text
            except MiniEditError:
                self.before_text[path] = None
        else:
            self.before_text[path] = None

    def write_doc(self, path: Path, doc: TextFile) -> None:
        self.remember_text(path)
        atomic_write(path, encode_text(doc))
        self.changed.add(path)

    def text_operation(self, op: dict[str, Any]) -> None:
        path = self.path(op.get("file"))
        doc = read_text(path)
        updated = apply_text_edit(doc, op)
        if updated.text != doc.text:
            self.write_doc(path, updated)

    def write_file(self, op: dict[str, Any]) -> None:
        path = self.path(op.get("file"))
        overwrite = op.get("overwrite", False)
        if not isinstance(overwrite, bool):
            raise MiniEditError("write_file.overwrite must be boolean")
        if path.is_dir():
            raise MiniEditError(f"destination is a directory: {path}")
        if path.exists() and not overwrite:
            raise MiniEditError(f"destination already exists: {path}")
        content = require_string(op, "content")
        newline = op.get("newline", "lf")
        if newline not in {"lf", "crlf"}:
            raise MiniEditError("write_file.newline must be 'lf' or 'crlf'")
        doc = TextFile(content.replace("\r\n", "\n").replace("\r", "\n"), "\r\n" if newline == "crlf" else "\n")
        self.write_doc(path, doc)

    def multi_edit(self, op: dict[str, Any]) -> None:
        path = self.path(op.get("file"))
        edits = op.get("edits")
        if not isinstance(edits, list) or not edits:
            raise MiniEditError("multi_edit.edits must be a non-empty list")
        doc = read_text(path)
        for index, child in enumerate(edits):
            if not isinstance(child, dict):
                raise MiniEditError(f"multi_edit.edits[{index}] must be an object")
            child = dict(child)
            child.setdefault("type", "replace")
            if child["type"] not in TEXT_EDIT_TYPES:
                raise MiniEditError(f"multi_edit child type not allowed: {child['type']!r}")
            doc = apply_text_edit(doc, child)
        self.write_doc(path, doc)

    def delete_path(self, op: dict[str, Any]) -> None:
        path = self.path(op.get("path"))
        missing_ok = op.get("missing_ok", False)
        recursive = op.get("recursive", False)
        if not isinstance(missing_ok, bool) or not isinstance(recursive, bool):
            raise MiniEditError("delete_path flags must be boolean")
        if not path.exists() and not path.is_symlink():
            if missing_ok:
                return
            raise MiniEditError(f"path does not exist: {path}")
        if path.is_dir() and not recursive and any(path.iterdir()):
            raise MiniEditError("directory is not empty; set recursive=true")
        self.remember_text(path)
        _remove(path)
        self.changed.add(path)

    def mkdir(self, op: dict[str, Any]) -> None:
        path = self.path(op.get("path"))
        exist_ok = op.get("exist_ok", True)
        if not isinstance(exist_ok, bool):
            raise MiniEditError("mkdir.exist_ok must be boolean")
        if path.exists() and not exist_ok:
            raise MiniEditError(f"path already exists: {path}")
        path.mkdir(parents=True, exist_ok=exist_ok)
        self.changed.add(path)

    def transfer(self, op: dict[str, Any], move: bool) -> None:
        src = self.path(op.get("src"))
        dst = self.path(op.get("dst"))
        overwrite = op.get("overwrite", False)
        if not isinstance(overwrite, bool):
            raise MiniEditError(f"{op['type']}.overwrite must be boolean")
        if src == dst:
            raise MiniEditError("source and destination must be different")
        if not src.is_file():
            raise MiniEditError(f"source is not a file: {src}")
        if dst.is_dir():
            raise MiniEditError(f"destination is a directory: {dst}")
        self.remember_text(src)
        self.remember_text(dst)
        if dst.exists():
            if not overwrite:
                raise MiniEditError(f"destination already exists: {dst}")
            _remove(dst)
        dst.parent.mkdir(parents=True, exist_ok=True)
        (shutil.move if move else shutil.copy2)(src, dst)
        self.changed.update({src, dst} if move else {dst})

    def apply(self, op: dict[str, Any]) -> None:
        kind = op.get("type")
        if not isinstance(kind, str) or kind not in ALL_TYPES:
            raise MiniEditError(f"unsupported operation type: {kind!r}")
        if kind in TEXT_EDIT_TYPES:
            self.text_operation(op)
        elif kind == "write_file":
            self.write_file(op)
        elif kind == "multi_edit":
            self.multi_edit(op)
        elif kind == "delete_path":
            self.delete_path(op)
        elif kind == "mkdir":
            self.mkdir(op)
        elif kind == "move":
            self.transfer(op, move=True)
        elif kind == "copy":
            self.transfer(op, move=False)


def operation_paths(root: Path, op: dict[str, Any]) -> list[Path]:
    kind = op.get("type")
    if kind in TEXT_EDIT_TYPES | {"write_file", "multi_edit"}:
        return [safe_path(root, op.get("file"))]
    if kind in {"delete_path", "mkdir"}:
        return [safe_path(root, op.get("path"))]
    if kind in {"move", "copy"}:
        return [safe_path(root, op.get("src")), safe_path(root, op.get("dst"))]
    raise MiniEditError(f"unsupported operation type: {kind!r}")


def validate_file(path: Path) -> None:
    if not path.is_file():
        return
    suffix = path.suffix.lower()
    if suffix not in {".py", ".json", ".toml", ".xml", ".svg"}:
        return
    text = read_text(path).text
    try:
        if suffix == ".py":
            ast.parse(text, filename=str(path))
        elif suffix == ".json":
            json.loads(text)
        elif suffix == ".toml":
            tomllib.loads(text)
        else:
            ET.fromstring(text)
    except Exception as exc:
        raise MiniEditError(f"syntax validation failed for {path}: {exc}") from exc


def load_spec(source: str) -> dict[str, Any]:
    raw = sys.stdin.read() if source == "-" else Path(source).read_text(encoding="utf-8")
    try:
        value = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise MiniEditError(f"invalid JSON specification: {exc}") from exc
    if isinstance(value, list):
        value = {"version": 5, "operations": value}
    if not isinstance(value, dict):
        raise MiniEditError("specification must be an object or operation list")
    version = value.get("version", 5)
    if version not in {3, 4, 5, "mini-v3", "lite-v5"}:
        raise MiniEditError("version must be 3, 4, 5, 'mini-v3' or 'lite-v5'")
    if "operations" in value and "ops" in value:
        raise MiniEditError("use either 'operations' or 'ops', not both")
    operations = value.get("operations", value.get("ops"))
    if not isinstance(operations, list) or not operations:
        raise MiniEditError("operations (or ops) must be a non-empty list")
    value["operations"] = operations
    for index, op in enumerate(operations):
        if not isinstance(op, dict):
            raise MiniEditError(f"operations[{index}] must be an object")
    return value


def _diff_path(path: Path, root: Path) -> str:
    try:
        return path.relative_to(root).as_posix()
    except ValueError:
        return str(path)


def make_diff(editor: MiniEditor) -> str:
    chunks: list[str] = []
    for path in sorted(editor.before_text, key=lambda p: str(p)):
        before = editor.before_text[path]
        after: str | None
        try:
            after = read_text(path).text if path.is_file() else None
        except MiniEditError:
            after = None
        if before == after:
            continue
        name = _diff_path(path, editor.root)
        chunks.extend(
            difflib.unified_diff(
                [] if before is None else before.splitlines(keepends=True),
                [] if after is None else after.splitlines(keepends=True),
                fromfile=f"a/{name}",
                tofile=f"b/{name}",
            )
        )
    return "".join(chunks)


def run_spec(
    root: Path,
    spec: dict[str, Any],
    *,
    dry_run: bool = False,
    validate: bool = False,
    include_diff: bool = True,
) -> dict[str, Any]:
    root = root.resolve()
    if not root.is_dir():
        raise MiniEditError(f"workspace root is not a directory: {root}")
    operations: list[dict[str, Any]] = spec["operations"]
    touched = [path for op in operations for path in operation_paths(root, op)]
    transaction = Transaction(root, touched)
    editor = MiniEditor(root)
    try:
        for index, op in enumerate(operations):
            try:
                editor.apply(op)
            except Exception as exc:
                if isinstance(exc, MiniEditError):
                    raise MiniEditError(f"operation {index + 1}: {exc}") from exc
                raise
        if validate or spec.get("validate") is True:
            for path in editor.changed:
                validate_file(path)
        diff = make_diff(editor) if include_diff else ""
        changed = sorted(_diff_path(path, root) for path in editor.changed)
        if dry_run:
            transaction.rollback()
        return {
            "ok": True,
            "version": VERSION,
            "dry_run": dry_run,
            "operations": len(operations),
            "changed": changed,
            "diff": diff,
        }
    except Exception:
        transaction.rollback()
        raise
    finally:
        transaction.close()


SCHEMA = {
    "top_level": {
        "version": "5 (also accepts v3/v4 specs)",
        "validate": "optional boolean",
        "operations": "non-empty operation array",
    },
    "operations": {
        "write_file": {"file": "path", "content": "text", "overwrite": "bool=false; explicit true to replace an existing file", "newline": "lf|crlf"},
        "replace": {"file": "path", "old": "text", "new": "text", "count": "optional int; omitted requires unique match; 0=all", "expected": "optional exact match count"},
        "regex_replace": {"file": "path", "pattern": "regex", "replacement": "text", "flags": "imsx", "count": "optional int; omitted requires unique match; 0=all", "expected": "optional exact match count"},
        "line_replace": {"file": "path", "start": "1-based line", "end": "optional inclusive line", "text": "replacement; empty deletes"},
        "insert": {"file": "path", "line": "1-based insertion line", "text": "text"},
        "append": {"file": "path", "text": "text"},
        "prepend": {"file": "path", "text": "text"},
        "multi_edit": {"file": "path", "edits": "text-edit array; omitted child type means replace"},
        "delete_path": {"path": "path", "recursive": "bool=false", "missing_ok": "bool=false"},
        "mkdir": {"path": "path", "exist_ok": "bool=true"},
        "move": {"src": "file", "dst": "file", "overwrite": "bool=false"},
        "copy": {"src": "file", "dst": "file", "overwrite": "bool=false"},
    },
}

def self_test() -> dict[str, Any]:
    checks = 0
    with tempfile.TemporaryDirectory(prefix="smart-edit-mini-test-") as temp:
        root = Path(temp)
        spec = {
            "version": 5,
            "validate": True,
            "operations": [
                {"type": "mkdir", "path": "src"},
                {"type": "write_file", "file": "src/a.py", "content": "x = 1\n"},
                {
                    "type": "multi_edit",
                    "file": "src/a.py",
                    "edits": [
                        {"old": "x = 1", "new": "x = 2", "expected": 1},
                        {"type": "append", "text": "print(x)\n"},
                    ],
                },
                {"type": "copy", "src": "src/a.py", "dst": "src/b.py"},
                {"type": "regex_replace", "file": "src/b.py", "pattern": "x = \\d+", "replacement": "x = 3", "expected": 1},
                {"type": "insert", "file": "src/b.py", "line": 1, "text": "# generated"},
            ],
        }
        result = run_spec(root, spec, validate=True)
        assert result["ok"] and len(result["changed"]) >= 2
        checks += 1
        assert (root / "src/a.py").read_text() == "x = 2\nprint(x)\n"
        checks += 1
        assert (root / "src/b.py").read_text() == "# generated\nx = 3\nprint(x)\n"
        checks += 1

        dry = run_spec(root, {"operations": [{"type": "delete_path", "path": "src/a.py"}]}, dry_run=True)
        assert dry["dry_run"] and (root / "src/a.py").exists()
        checks += 1

        before = (root / "src/a.py").read_text()
        try:
            run_spec(
                root,
                {
                    "operations": [
                        {"type": "replace", "file": "src/a.py", "old": "x = 2", "new": "x = 9"},
                        {"type": "replace", "file": "missing.py", "old": "a", "new": "b"},
                    ]
                },
            )
        except MiniEditError:
            pass
        else:
            raise AssertionError("rollback test did not fail")
        assert (root / "src/a.py").read_text() == before
        checks += 1

        try:
            run_spec(root, {"operations": [{"type": "write_file", "file": "../escape.py", "content": ""}]})
        except MiniEditError:
            checks += 1
        else:
            raise AssertionError("path escape was accepted")

        try:
            run_spec(root, {"validate": True, "operations": [{"type": "write_file", "file": "bad.py", "content": "def broken(:\n"}]})
        except MiniEditError:
            checks += 1
        else:
            raise AssertionError("invalid Python was accepted")
        assert not (root / "bad.py").exists()
        checks += 1

    return {"ok": True, "checks": checks, "version": VERSION}


def parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        description="Small transactional code editor driven by a JSON specification."
    )
    p.add_argument("--root", default=".", help="workspace root (default: current directory)")
    p.add_argument("--spec", default="-", help="JSON spec file, or - for stdin")
    p.add_argument("--dry-run", action="store_true", help="apply, show result, then roll back")
    p.add_argument("--diff", action="store_true", help="print unified diff")
    p.add_argument("--validate", action="store_true", help="validate Python/JSON/TOML/XML/SVG")
    p.add_argument("--json-output", action="store_true", help="print machine-readable result")
    p.add_argument("--compact-json", action="store_true", help="minimize JSON whitespace for fewer output tokens")
    p.add_argument("--summary", action="store_true", help="return only status, operation count and changed paths (requires --json-output)")
    p.add_argument("--agent-prompt", action="store_true", help="print the built-in operating prompt for AI coding agents")
    p.add_argument("--schema", action="store_true", help="print the compact operation reference")
    p.add_argument("--self-test", action="store_true", help="run built-in tests")
    p.add_argument("--version", action="version", version=VERSION)
    return p


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    try:
        if args.agent_prompt:
            print(AGENT_PROMPT)
            return 0
        if args.summary and not args.json_output:
            raise MiniEditError("--summary requires --json-output")
        if args.schema:
            print(json.dumps(SCHEMA, indent=2, ensure_ascii=False))
            return 0
        if args.self_test:
            print(json.dumps(self_test(), indent=2))
            return 0
        spec = load_spec(args.spec)
        result = run_spec(Path(args.root), spec, dry_run=args.dry_run, validate=args.validate, include_diff=args.diff)
        if args.json_output:
            if args.summary:
                payload = {
                    "ok": result["ok"],
                    "version": result["version"],
                    "dry_run": result["dry_run"],
                    "operations": result["operations"],
                    "changed": result["changed"],
                }
                if args.diff and result.get("diff"):
                    payload["diff"] = result["diff"]
            else:
                payload = dict(result)
                if not args.diff:
                    payload.pop("diff", None)
            if args.compact_json:
                print(json.dumps(payload, ensure_ascii=False, separators=(",", ":")))
            else:
                print(json.dumps(payload, indent=2, ensure_ascii=False))
        else:
            print(f"ok: {result['operations']} operations, {len(result['changed'])} paths changed")
            for path in result["changed"]:
                print(f"  {path}")
            if args.diff and result["diff"]:
                print(result["diff"], end="" if result["diff"].endswith("\n") else "\n")
        return 0
    except MiniEditError as exc:
        if args.json_output:
            payload = {"ok": False, "error": str(exc)}
            if args.compact_json:
                print(json.dumps(payload, ensure_ascii=False, separators=(",", ":")))
            else:
                print(json.dumps(payload, ensure_ascii=False))
        else:
            print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
