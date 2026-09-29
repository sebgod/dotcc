"""Tracebacks: the traceback module formats a caught exception with 3.13's
position carets (which needs the compiler's location table and linecache
reading this file back), chained causes, and warnings. The file path is
reduced to its basename so the output does not depend on where it runs."""

import os
import traceback
import warnings


def inner(data):
    return data["key"] + 1


def outer():
    return inner({"key": "text"})


def show(exc):
    lines = traceback.format_exception(exc)
    here = os.path.basename(__file__)
    for line in lines:
        for piece in line.rstrip("\n").split("\n"):
            if 'File "' in piece:
                piece = piece[:piece.index('File "') + 6] + here + piece[piece.index('", line'):]
            print(piece)


try:
    outer()
except TypeError as e:
    show(e)

print("--")
try:
    try:
        {}["missing"]
    except KeyError as e:
        raise RuntimeError("lookup failed") from e
except RuntimeError as e:
    show(e)

print("--")
with warnings.catch_warnings(record=True) as caught:
    warnings.simplefilter("always")
    warnings.warn("careful", DeprecationWarning)
print([(w.category.__name__, str(w.message)) for w in caught])

print("--")
summary = traceback.extract_stack(limit=1)[0]
print(summary.name, summary.line)
