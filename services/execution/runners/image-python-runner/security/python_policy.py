#!/usr/bin/env python3
from __future__ import annotations

import ast
import pathlib
import sys

MAX_SOURCE_BYTES = 1 << 20
STANDARD_MODULES = {
    "math", "cmath", "statistics", "fractions", "decimal", "collections",
    "itertools", "functools", "heapq", "bisect", "array", "re", "string",
    "random", "typing", "dataclasses", "enum", "copy",
}
IMAGE_MODULES = STANDARD_MODULES | {"turtle", "tkinter", "matplotlib", "numpy", "PIL"}
FORBIDDEN_CALLS = {
    "eval", "exec", "compile", "open", "__import__", "breakpoint", "help",
    "getattr", "setattr", "delattr", "globals", "locals", "vars",
    "license", "credits", "copyright", "quit", "exit",
}
FORBIDDEN_NAMES = {
    "os", "sys", "subprocess", "socket", "ctypes", "importlib", "pathlib",
    "builtins", "inspect", "marshal", "pickle", "shelve", "runpy", "code",
    "codeop", "resource", "signal", "multiprocessing", "threading", "asyncio",
}
FORBIDDEN_ATTRIBUTES = {
    "__subclasses__", "__globals__", "__builtins__", "__code__", "__closure__",
    "__mro__", "__bases__", "__base__", "__loader__", "__spec__", "__dict__",
    "__class__", "__getattribute__", "__reduce__", "__reduce_ex__", "__self__",
    "__traceback__", "tb_frame", "tb_next", "f_back", "f_builtins", "f_code",
    "f_globals", "f_locals", "gi_frame", "cr_frame", "ag_frame", "func_globals",
    "im_func", "_os", "_sys", "_socket", "_ctypes", "_subprocess", "_builtins",
    "_importlib", "ctypeslib",
}
RESTRICTED_PATH_PARTS = (
    "/proc/", "/sys/", "/dev/", "/etc/", "/run/", "/root/", "/home/",
    "../", "..\\",
)
FORBIDDEN_FILE_READ_METHODS = {
    "fromfile", "memmap", "loadtxt", "genfromtxt", "imread",
    "read_csv", "read_table", "read_fwf", "read_pickle", "read_parquet",
    "read_feather", "read_hdf", "read_sql", "read_html", "read_xml",
    "read_excel",
}
FORBIDDEN_FILE_READ_CALLS = {
    "numpy.load", "numpy.lib.npyio.load", "PIL.Image.open",
    "matplotlib.image.imread", "matplotlib.pyplot.imread", "tkinter.PhotoImage",
    "tkinter.BitmapImage",
}
PATH_KEYWORDS = {"file", "filename", "fname", "path", "fileName"}


def fail() -> "NoReturn":
    print("TaskForge security policy rejected the Python submission.", file=sys.stderr)
    raise SystemExit(86)


def root_module(name: str | None) -> str:
    return (name or "").split(".", 1)[0]


def dotted_name(node: ast.AST) -> str:
    parts: list[str] = []
    current: ast.AST | None = node
    while isinstance(current, ast.Attribute):
        parts.append(current.attr)
        current = current.value
    if isinstance(current, ast.Name):
        parts.append(current.id)
    return ".".join(reversed(parts))


class PolicyVisitor(ast.NodeVisitor):
    def __init__(self, allowed_modules: set[str]) -> None:
        self.allowed_modules = allowed_modules
        self.bad = False

    def reject(self) -> None:
        self.bad = True

    def visit_Import(self, node: ast.Import) -> None:
        for alias in node.names:
            if root_module(alias.name) not in self.allowed_modules:
                self.reject()
        self.generic_visit(node)

    def visit_ImportFrom(self, node: ast.ImportFrom) -> None:
        if node.level or root_module(node.module) not in self.allowed_modules:
            self.reject()
        for alias in node.names:
            if alias.name.startswith("_") or alias.name in FORBIDDEN_NAMES or alias.name in FORBIDDEN_CALLS:
                self.reject()
        self.generic_visit(node)

    def visit_Call(self, node: ast.Call) -> None:
        name = dotted_name(node.func)
        root = name.split(".", 1)[0]
        leaf = name.rsplit(".", 1)[-1]
        if root in FORBIDDEN_NAMES or leaf in FORBIDDEN_CALLS:
            self.reject()
        if leaf in FORBIDDEN_FILE_READ_METHODS or name in FORBIDDEN_FILE_READ_CALLS:
            self.reject()
        for keyword in node.keywords:
            if keyword.arg in PATH_KEYWORDS:
                self.reject()
        self.generic_visit(node)

    def visit_Name(self, node: ast.Name) -> None:
        if node.id in FORBIDDEN_NAMES or node.id in FORBIDDEN_CALLS:
            self.reject()
        self.generic_visit(node)

    def visit_Attribute(self, node: ast.Attribute) -> None:
        if (
            node.attr in FORBIDDEN_ATTRIBUTES
            or node.attr in FORBIDDEN_NAMES
            or node.attr in FORBIDDEN_CALLS
            or node.attr.startswith("_")
        ):
            self.reject()
        name = dotted_name(node)
        if name.split(".", 1)[0] in FORBIDDEN_NAMES:
            self.reject()
        self.generic_visit(node)

    def visit_Constant(self, node: ast.Constant) -> None:
        if isinstance(node.value, str):
            lower = node.value.lower()
            if "__" in node.value or any(part in lower for part in RESTRICTED_PATH_PARTS):
                self.reject()
        self.generic_visit(node)

    def visit_Lambda(self, node: ast.Lambda) -> None:
        # Lambdas themselves are safe; keep visiting their body.
        self.generic_visit(node)


if len(sys.argv) != 3:
    fail()
path = pathlib.Path(sys.argv[1])
profile = sys.argv[2]
if profile not in {"standard", "image"}:
    fail()
try:
    data = path.read_bytes()
except OSError:
    fail()
if len(data) == 0 or len(data) > MAX_SOURCE_BYTES or b"\x00" in data:
    fail()
try:
    source = data.decode("utf-8")
    tree = ast.parse(source, filename=path.name, mode="exec", type_comments=True)
except (UnicodeDecodeError, SyntaxError, ValueError, MemoryError, RecursionError):
    fail()
visitor = PolicyVisitor(IMAGE_MODULES if profile == "image" else STANDARD_MODULES)
visitor.visit(tree)
if visitor.bad:
    fail()
