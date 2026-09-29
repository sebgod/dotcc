"""Paths: the interpreter's view of the file system is consistent with os.path.

On Windows the dotcc build is a POSIX CPython (posixpath) linked with
-fposix-paths, so it sees /c/Users/... where host CPython (ntpath) sees
C:\\Users\\...; the spellings differ, but every check here holds for both
(GH #254). The output is the same on every host.
"""
import os
import sys

here = os.path.dirname(os.path.abspath(__file__))
checks = [
    ("getcwd() is absolute", os.path.isabs(os.getcwd())),
    ("abspath('x') is getcwd()/x", os.path.abspath("x") == os.path.join(os.getcwd(), "x")),
    ("the script's directory is absolute", os.path.isabs(here)),
    ("the script's directory is listable", os.path.basename(__file__) in os.listdir(here)),
    ("the script is a file", os.path.isfile(os.path.join(here, os.path.basename(__file__)))),
    ("sys.executable is set", bool(sys.executable)),
    ("sys.executable is absolute", os.path.isabs(sys.executable)),
    ("sys.executable exists", os.path.exists(sys.executable)),
    ("sys.prefix is absolute", os.path.isabs(sys.prefix)),
    ("the stdlib is found", os.path.isfile(os.__file__)),
    ("TMPDIR, TEMP and TMP are unset or absolute",
     all(os.path.isabs(os.environ[k]) for k in ("TMPDIR", "TEMP", "TMP") if os.environ.get(k))),
]

# A round trip through a scratch directory beside the script (tempfile needs
# random, which needs math, not linked into the dotcc build yet: GH #252).
tmp = os.path.join(here, "paths.tmp")
os.makedirs(tmp, exist_ok=True)
name = os.path.join(tmp, "f.txt")
with open(name, "w", encoding="utf-8") as f:
    f.write("x")
checks.append(("a new file is listed", os.listdir(tmp) == ["f.txt"]))
checks.append(("its realpath is absolute", os.path.isabs(os.path.realpath(name))))
checks.append(("its size is 1", os.path.getsize(name) == 1))
os.remove(name)
os.rmdir(tmp)
checks.append(("it is gone", not os.path.exists(tmp)))

for label, ok in checks:
    print(f"{label}: {ok}")
