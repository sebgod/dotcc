"""Smoke test for the interpreter build.sh builds: core language features and
the pure-Python standard library the build can import. CI runs it through
run.sh and compares stdout with smoke.expected, which host CPython 3.13
prints too; nothing here depends on the platform, the hash seed or the clock.

The build has no threads (CPython's pthread stubs), so threading is imported
and its locks taken, but no thread is started. Importing it is itself a check:
its shutdown hook at exit spun forever while dotcc's 1-byte atomics were
4 bytes wide."""

import collections
import functools
import heapq
import io
import itertools
import json
import re
import textwrap
import threading
from enum import Enum


def fib(n):
    a, b = 0, 1
    for _ in range(n):
        a, b = b, a + b
    return a


print("fib:", [fib(n) for n in range(10)], fib(90))
print("int:", 2 ** 200 % 1_000_000_007, -7 // 2, -7 % 2, divmod(10 ** 30 + 7, 97), (255).to_bytes(2, "big"))
print("float:", 0.1 + 0.2, 1e308 * 10, round(2.675, 2), f"{3.14159:.3f}", 7 / 2, float("1e-7"), 2 ** 0.5)

words = "the quick brown fox jumps over the lazy dog the end".split()
print("sorted:", sorted(set(words), key=lambda w: (len(w), w)))
print("counter:", collections.Counter(words).most_common(2))
print("dict:", {w: len(w) for w in words[:4]}, dict(zip("abc", itertools.count(1))))


class Shape:
    def __init__(self, name):
        self.name = name

    def area(self):
        raise NotImplementedError

    def __repr__(self):
        return f"{type(self).__name__}({self.name!r}, area={self.area():.2f})"


class Square(Shape):
    def __init__(self, side):
        super().__init__("square")
        self.side = side

    def area(self):
        return self.side ** 2


class Circle(Shape):
    def __init__(self, r):
        super().__init__("circle")
        self.r = r

    def area(self):
        return 3.141592653589793 * self.r ** 2


print("classes:", sorted([Square(3), Circle(1), Square(1)], key=lambda s: s.area()))


@functools.lru_cache(maxsize=None)
def ways(n):
    return 1 if n < 2 else ways(n - 1) + ways(n - 2)


def counter():
    n = 0

    def inc():
        nonlocal n
        n += 1
        return n
    return inc


tick = counter()
tick()
tick()
print("functions:", ways(80), ways.cache_info().hits, tick(), functools.reduce(lambda a, b: a * b, range(1, 11)))
print("generators:", list(itertools.islice((x * x for x in itertools.count(1) if x % 3), 6)),
      heapq.nsmallest(3, [5, 1, 8, 3, 9, 2]), list(itertools.accumulate([1, 2, 3, 4])))

print("json:", json.dumps({"b": [1, 2.5, None, True], "a": "é"}, sort_keys=True), json.loads('{"x": [1, {"y": "z"}]}'))
print("re:", re.findall(r"(\w+)@(\w+)\.com", "a@b.com, c@d.com"), re.sub(r"\d+", lambda m: str(int(m.group()) * 2), "a1b22c333"))
print("str:", "Hello, World".swapcase(), "straße".upper(), "abc".center(9, "*"), "ü".encode("utf-8"), "%05.1f|%-4s|%x" % (3.14159, "ab", 255))
print("textwrap:", textwrap.wrap("the quick brown fox jumps over the lazy dog", 15))


class Color(Enum):
    RED = 1
    GREEN = 2


print("enum:", Color.GREEN, Color(1).name, list(Color))

try:
    1 / 0
except ZeroDivisionError as e:
    print("exception:", type(e).__name__, e)
try:
    try:
        raise ValueError("inner")
    except ValueError as e:
        raise KeyError("outer") from e
except KeyError as k:
    print("chained:", repr(k), repr(k.__cause__))

buf = io.StringIO()
print("to a buffer", end="!", file=buf)
print("stringio:", repr(buf.getvalue()))

lock = threading.Lock()
with lock:
    held = lock.locked()
print("threading:", threading.current_thread().name, held, lock.locked())
