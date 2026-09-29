"""The object model under memory pressure: reference cycles and the cyclic
garbage collector, weak references and their callbacks, finalizers, and a
large number of short-lived objects (dicts, lists, strings, big ints)."""

import gc
import weakref

events = []


class Node:
    def __init__(self, name):
        self.name = name
        self.peer = None

    def __del__(self):
        events.append(f"del {self.name}")


def make_cycle(prefix):
    a, b = Node(prefix + "a"), Node(prefix + "b")
    a.peer, b.peer = b, a
    return weakref.ref(a, lambda r: events.append(f"callback {prefix}"))


gc.disable()
ref = make_cycle("x")
print("alive before collect:", ref() is not None)
found = gc.collect()
print("collected something:", found > 0, "alive after:", ref() is None)
print(sorted(events))
gc.enable()

events.clear()
n = Node("plain")
r = weakref.ref(n)
del n
print("refcount drop:", events, r())


class Cache:
    def __init__(self):
        self.items = weakref.WeakValueDictionary()


class Blob:
    def __init__(self, size):
        self.data = bytearray(size)


cache = Cache()
keep = [Blob(10) for _ in range(5)]
for i, b in enumerate(keep):
    cache.items[i] = b
for i in range(5, 10):
    cache.items[i] = Blob(10)
print("weak cache size:", len(cache.items))

total = 0
for i in range(200000):
    d = {"i": i, "s": str(i), "l": [i, i + 1]}
    total += len(d["s"]) + d["l"][1]
print("churn total:", total)

big = 1
for i in range(1, 400):
    big *= i
print("399! digits:", len(str(big)), "sum of digits:", sum(map(int, str(big))))

words = {}
for i in range(50000):
    key = f"k{i % 997}"
    words.setdefault(key, []).append(i)
print("buckets:", len(words), "longest:", max(len(v) for v in words.values()))
print("gc counts are ints:", all(isinstance(c, int) for c in gc.get_count()))
