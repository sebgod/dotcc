"""A tour of the language: pattern matching, the class machinery (metaclasses,
descriptors, __slots__, __init_subclass__, properties), generators and
coroutines driven by hand, exception groups, the compiler at run time
(compile, exec, eval, ast), big integers and the built-in types' methods."""

import ast
import sys


def section(name):
    print(f"-- {name}")


section("match")


def describe(value):
    match value:
        case 0 | 1:
            return "bit"
        case int(n) if n < 0:
            return f"negative {n}"
        case [x, y, *rest]:
            return f"list x={x} y={y} rest={rest}"
        case {"kind": "point", "x": x, "y": y}:
            return f"point({x}, {y})"
        case str() as s if s.isupper():
            return f"shout {s}"
        case Point(x=0, y=y):
            return f"on the y axis at {y}"
        case _:
            return f"other {value!r}"


class Point:
    __match_args__ = ("x", "y")

    def __init__(self, x, y):
        self.x = x
        self.y = y


for v in [1, -5, [1, 2, 3, 4], {"kind": "point", "x": 3, "y": 4}, "HEY", Point(0, 7), 2.5]:
    print(describe(v))

section("classes")


class Registry(type):
    classes = []

    def __new__(mcls, name, bases, ns, **kw):
        cls = super().__new__(mcls, name, bases, ns, **kw)
        mcls.classes.append(name)
        return cls


class Typed:
    def __init__(self, kind):
        self.kind = kind

    def __set_name__(self, owner, name):
        self.name = "_" + name

    def __get__(self, obj, objtype=None):
        return self if obj is None else getattr(obj, self.name)

    def __set__(self, obj, value):
        if not isinstance(value, self.kind):
            raise TypeError(f"{self.name[1:]} must be {self.kind.__name__}")
        setattr(obj, self.name, value)


class Base(metaclass=Registry):
    subclasses = []

    def __init_subclass__(cls, tag="", **kw):
        super().__init_subclass__(**kw)
        Base.subclasses.append((cls.__name__, tag))


class Account(Base, tag="money"):
    __slots__ = ("_owner", "_balance")
    owner = Typed(str)
    balance = Typed(int)

    def __init__(self, owner, balance):
        self.owner = owner
        self.balance = balance

    @property
    def rich(self):
        return self.balance > 1000

    def __repr__(self):
        return f"Account({self.owner!r}, {self.balance})"

    def __eq__(self, other):
        return isinstance(other, Account) and (self.owner, self.balance) == (other.owner, other.balance)

    def __hash__(self):
        return hash((self.owner, self.balance))


a = Account("ada", 1500)
print(a, a.rich, Registry.classes, Base.subclasses)
try:
    a.balance = "lots"
except TypeError as e:
    print("TypeError:", e)
try:
    a.nickname = "x"
except AttributeError as e:
    print("AttributeError:", type(e).__name__)
print(len({Account("b", 1), Account("b", 1), Account("c", 1)}), Account.__mro__[1].__name__)

section("generators")


def averager():
    total = count = 0
    avg = None
    while True:
        try:
            value = yield avg
        except ValueError:
            print("  reset")
            total = count = 0
            continue
        total += value
        count += 1
        avg = total / count


g = averager()
next(g)
print(g.send(10), g.send(20))
g.throw(ValueError)
print(g.send(5))
g.close()


def delegate():
    result = yield from (x * x for x in range(4))
    return result


print(list(delegate()))


class Sleep:
    def __init__(self, label):
        self.label = label

    def __await__(self):
        got = yield self.label
        return got


async def task(name):
    first = await Sleep(f"{name}:1")
    second = await Sleep(f"{name}:2")
    return f"{name} got {first} and {second}"


coro = task("t")
print(coro.send(None))
print(coro.send("a"))
try:
    coro.send("b")
except StopIteration as stop:
    print(stop.value)


async def agen():
    for i in range(3):
        yield i * 10


async def collect():
    return [x async for x in agen()]


c = collect()
try:
    c.send(None)
except StopIteration as stop:
    print("async comprehension:", stop.value)

section("exceptions")


def fail(kind):
    if kind == "v":
        raise ValueError("bad value")
    raise KeyError("missing")


try:
    raise ExceptionGroup("several", [ValueError("a"), KeyError("b"), ValueError("c")])
except* ValueError as eg:
    print("values:", [str(e) for e in eg.exceptions])
except* KeyError as eg:
    print("keys:", [str(e) for e in eg.exceptions])

try:
    try:
        fail("v")
    finally:
        print("finally ran")
except ValueError as e:
    print("caught", e, "context", e.__context__)


def depth(n):
    return depth(n + 1)


sys.setrecursionlimit(500)
try:
    depth(0)
except RecursionError as e:
    print("RecursionError:", e)
sys.setrecursionlimit(1000)

section("compiler")
ns = {}
exec(compile("def sq(x):\n    return x * x\nresult = [sq(i) for i in range(5)]\n", "<gen>", "exec"), ns)
print(ns["result"], eval("sq(12) + 1", ns))
tree = ast.parse("total = price * (1 + rate)")
print(ast.dump(tree.body[0].value, annotate_fields=False))
code = compile(tree, "<ast>", "exec")
env = {"price": 200, "rate": 0.25}
exec(code, env)
print(env["total"], ast.unparse(tree))
print((lambda *a, sep="-", **k: sep.join(map(str, a)) + str(sorted(k)))(1, 2, 3, z=1, a=2))


def positional(a, b, /, c, *, d):
    return a + b + c + d


print(positional(1, 2, c=3, d=4))
if (n := len("walrus")) > 3:
    print("walrus", n)

section("numbers")
print(2 ** 256, (2 ** 256).bit_length(), (-17) // 5, (-17) % 5, divmod(-17, 5))
print(int("ff", 16), int("-0b1011", 0), hex(255), oct(8), bin(10), format(1234567.891, ",.2f"))
print(10 ** 50 // 7 ** 20, pow(3, 200, 1000007), pow(2, -1, 7))
print(0.1 + 0.2 == 0.3, 1e16 + 1, 2.5.as_integer_ratio(), float.fromhex("0x1.8p1"), (1.5).hex())
print(round(0.5), round(1.5), round(2.675, 2), 7 / 2, 7 // 2, -7 / 2, int(-3.9), abs(-2 ** 63))
print(complex(1, 2) * complex(3, -1), abs(3 + 4j), sum(range(10 ** 6)))

section("builtins")
s = "The Quick Brown Fox"
print(s.split(), s.lower().title(), s[::-1], s.find("Brown"), s.partition(" ")[2], s.casefold())
print("|".join(sorted(s.split(), key=str.lower)), s.encode("utf-16-le")[:6], "a,b,,c".split(","))
b = bytearray(b"hello")
b[0] = ord("J")
b.extend(b"!!")
print(b, bytes(b).upper(), b.hex(), bytes.fromhex("48 69"), memoryview(b)[1:3].tobytes())
d = {"b": 2, "a": 1}
d |= {"c": 3}
print(d, sorted(d.items(), key=lambda kv: -kv[1]), {k: v for k, v in d.items() if v > 1})
fs = frozenset({1, 2, 3})
print(sorted(fs | {4}), sorted(fs & {2, 3, 9}), sorted(fs ^ {3, 4}), {fs: "hashable"}[frozenset([3, 2, 1])])
lst = [3, 1, 2]
lst.append(lst)
print(lst, sorted([("b", 2), ("a", 2), ("c", 1)], key=lambda t: t[1]))
print(list(zip("abc", range(3), strict=True)), list(reversed(range(5))), any([]), all([]))
print(isinstance(True, int), issubclass(bool, int), type(None).__name__, callable(len), repr(...))
